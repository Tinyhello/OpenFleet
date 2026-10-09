using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenFleet.Api.Fleet;

namespace OpenFleet.Api.Data;

/// <summary>
/// 啟動時套用 Migration 並補上示範資料。只新增缺少的列：面板改過的設定不會在重啟時被蓋掉。
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// 暫定佈局的交接點（與 Unity 的 FactoryLayout 相同：站點中心往走道方向 3.4 m）。
    /// 待加工料容量 30 件是已確認的數值；倉庫與暫存點（淬火站入口）的容量規則還沒定，所以留空。
    /// </summary>
    private static readonly Station[] DemoStations =
    [
        new() { Code = "raw-store", Name = "原料倉", HandoffX = -16, HandoffZ = -7.6 },
        new() { Code = "rough", Name = "粗加工", HandoffX = -8, HandoffZ = -7.6, InboundCapacity = 30 },
        new() { Code = "fine", Name = "精加工", HandoffX = 0, HandoffZ = -7.6, InboundCapacity = 30 },
        new() { Code = "heat", Name = "淬火／回火", HandoffX = 8, HandoffZ = -7.6 },
        new() { Code = "plating", Name = "電鍍", HandoffX = 16, HandoffZ = -7.6 },
        new() { Code = "polish", Name = "拋光", HandoffX = 16, HandoffZ = 7.6 },
        new() { Code = "qc", Name = "品管", HandoffX = 8, HandoffZ = 7.6 },
        new() { Code = "packing", Name = "包裝", HandoffX = 0, HandoffZ = 7.6 },
        new() { Code = "shipping", Name = "出貨", HandoffX = -8, HandoffZ = 7.6 },
        new() { Code = "tool-store", Name = "刀具庫", HandoffX = -16, HandoffZ = 7.6 },
    ];

    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenFleetDbContext>();
        var fleet = scope.ServiceProvider.GetRequiredService<IOptions<FleetOptions>>().Value;

        await db.Database.MigrateAsync();
        await SeedAsync(db, fleet);
    }

    public static async Task SeedAsync(OpenFleetDbContext db, FleetOptions fleet)
    {
        var existingStations = await db.Stations.Select(s => s.Code).ToListAsync();
        db.Stations.AddRange(DemoStations
            .Where(s => !existingStations.Contains(s.Code))
            .Select(s => new Station { Code = s.Code, Name = s.Name, HandoffX = s.HandoffX, HandoffZ = s.HandoffZ, InboundCapacity = s.InboundCapacity }));

        if (!await db.ProcessSteps.AnyAsync())
        {
            // 示範設定（使用者 2026-10-09 同意暫用）：每站整批固定加工 10 秒。
            db.ProcessSteps.Add(new ProcessStep { Sequence = 1, StationCode = "rough", DurationSeconds = 10 });
            db.ProcessSteps.Add(new ProcessStep { Sequence = 2, StationCode = "fine", DurationSeconds = 10 });
        }

        if (!await db.ProductionSettings.AnyAsync())
        {
            db.ProductionSettings.Add(new ProductionSettings
            {
                LotQuantity = 30,
                PartCode = "upper-punch",
                SourceStationCode = "raw-store",
                BufferStationCode = "heat",
            });
        }

        var vehicles = await db.Vehicles.ToDictionaryAsync(v => v.Id);
        foreach (var (id, type) in fleet.VehicleTypes)
        {
            if (!vehicles.TryGetValue(id, out var vehicle))
            {
                vehicle = new Vehicle { Id = id };
                db.Vehicles.Add(vehicle);
            }

            vehicle.Type = type;
            vehicle.DispatchEnabled = fleet.DispatchVehicleIds.Contains(id);
        }

        await db.SaveChangesAsync();
    }
}
