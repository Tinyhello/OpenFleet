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
        new() { Code = "rough", Name = "粗加工", HandoffX = -8, HandoffZ = -7.6, InboundCapacity = 30, OutboundCapacity = 30 },
        new() { Code = "fine", Name = "精加工", HandoffX = 0, HandoffZ = -7.6, InboundCapacity = 30, OutboundCapacity = 30 },
        new() { Code = "heat", Name = "淬火／回火", HandoffX = 8, HandoffZ = -7.6 },
        new() { Code = "plating", Name = "電鍍", HandoffX = 16, HandoffZ = -7.6 },
        new() { Code = "polish", Name = "拋光", HandoffX = 16, HandoffZ = 7.6 },
        new() { Code = "qc", Name = "品管", HandoffX = 8, HandoffZ = 7.6 },
        new() { Code = "packing", Name = "包裝", HandoffX = 0, HandoffZ = 7.6 },
        new() { Code = "shipping", Name = "出貨", HandoffX = -8, HandoffZ = 7.6 },
        new() { Code = "tool-store", Name = "刀具庫", HandoffX = -16, HandoffZ = 7.6 },
    ];

    /// <summary>
    /// 示範製程。每件加工 2 秒是暫定值；刀具壽命（粗加工 500 件、精加工 250 件）是使用者 2026-10-09 提供的數值。
    /// </summary>
    private static readonly ProcessStep[] DemoSteps =
    [
        new() { Sequence = 1, StationCode = "rough", SecondsPerPiece = 2, ToolLifePieces = 500 },
        new() { Sequence = 2, StationCode = "fine", SecondsPerPiece = 2, ToolLifePieces = 250 },
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
        var stations = await db.Stations.ToDictionaryAsync(s => s.Code);
        foreach (var demo in DemoStations)
        {
            if (!stations.TryGetValue(demo.Code, out var station))
            {
                db.Stations.Add(new Station
                {
                    Code = demo.Code,
                    Name = demo.Name,
                    HandoffX = demo.HandoffX,
                    HandoffZ = demo.HandoffZ,
                    InboundCapacity = demo.InboundCapacity,
                    OutboundCapacity = demo.OutboundCapacity,
                });
            }
            else
            {
                // 舊資料庫升級：欄位是後來才加的，沒有值就補上示範值。
                station.OutboundCapacity ??= demo.OutboundCapacity;
            }
        }

        var steps = await db.ProcessSteps.ToDictionaryAsync(s => s.Sequence);
        foreach (var demo in DemoSteps)
        {
            if (steps.Count == 0)
            {
                db.ProcessSteps.Add(new ProcessStep
                {
                    Sequence = demo.Sequence,
                    StationCode = demo.StationCode,
                    SecondsPerPiece = demo.SecondsPerPiece,
                    ToolLifePieces = demo.ToolLifePieces,
                });
            }
            else if (steps.TryGetValue(demo.Sequence, out var step) && step.ToolLifePieces <= 0)
            {
                step.ToolLifePieces = demo.ToolLifePieces;
            }
        }

        // 每個加工站一筆刀具狀態。
        var tools = await db.StationTools.Select(t => t.StationCode).ToListAsync();
        var machineStations = steps.Count == 0 ? DemoSteps.Select(s => s.StationCode) : steps.Values.Select(s => s.StationCode);
        db.StationTools.AddRange(machineStations.Where(code => !tools.Contains(code)).Select(code => new StationTool { StationCode = code }));

        if (!await db.ProductionSettings.AnyAsync())
        {
            db.ProductionSettings.Add(new ProductionSettings
            {
                LotQuantity = 30,
                PartCode = "upper-punch",
                SourceStationCode = "raw-store",
                BufferStationCode = "heat",
                // 上下料每件 5 秒是使用者 2026-10-09 的決定（人工約每件 10 秒）；換刀 20 秒是暫定值。
                HandoverSecondsPerPiece = 5,
                ToolChangeSeconds = 20,
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
