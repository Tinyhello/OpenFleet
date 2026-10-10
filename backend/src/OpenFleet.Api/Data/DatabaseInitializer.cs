using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenFleet.Api.Fleet;
using OpenFleet.Api.Traffic;
using OpenFleet.Contracts;

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
    /// 示範製程。逐件工序的每件加工時間（粗加工 30 秒、精加工 60 秒）與刀具壽命（500／250 件）、
    /// 淬火 4 小時＋回火 8 小時、滿 150 支開爐最多 300 支、電鍍滿 100 支開槽最多 150 支，都是使用者提供的數值；
    /// 電鍍一槽 1 小時、拋光每支 20 秒是 Claude 提議、使用者同意先用的示範值。
    /// 測試模式的專用倍率（TestSpeed）是 Claude 挑的示範值，目標是每一站做一框或一爐約 1～4 分鐘：
    /// 粗加工 10、精加工 20、淬火／回火 200（12 小時 → 3.6 分鐘）、電鍍 60（1 小時 → 1 分鐘）、拋光 10。面板可以改。
    /// </summary>
    private static readonly ProcessStep[] DemoSteps =
    [
        new() { Sequence = 1, StationCode = "rough", Kind = ProcessStepKind.Piece, SecondsPerPiece = 30, UsesTool = true, ToolLifePieces = 500, TestSpeed = 10 },
        new() { Sequence = 2, StationCode = "fine", Kind = ProcessStepKind.Piece, SecondsPerPiece = 60, UsesTool = true, ToolLifePieces = 250, TestSpeed = 20 },
        new()
        {
            Sequence = 3, StationCode = "heat", Kind = ProcessStepKind.Batch, BatchMinPieces = 150, BatchMaxPieces = 300,
            BatchSeconds = 4 * 3600, BatchSecondStageSeconds = 8 * 3600, StageName = "淬火", SecondStageName = "回火", TestSpeed = 200,
        },
        new()
        {
            Sequence = 4, StationCode = "plating", Kind = ProcessStepKind.Batch, BatchMinPieces = 100, BatchMaxPieces = 150,
            BatchSeconds = 3600, StageName = "電鍍", TestSpeed = 60,
        },
        new() { Sequence = 5, StationCode = "polish", Kind = ProcessStepKind.Piece, SecondsPerPiece = 20, TestSpeed = 10 },
    ];

    /// <summary>最後一道工序（拋光）完成後送到品管站入口暫存；品管之後的流程還沒做。</summary>
    private const string DemoBufferStation = "qc";

    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenFleetDbContext>();
        var fleet = scope.ServiceProvider.GetRequiredService<IOptions<FleetOptions>>().Value;

        await db.Database.MigrateAsync();
        await SeedAsync(db, fleet);

        // 路網不會在執行中改變：啟動時從資料庫讀進記憶體，之後規劃路徑不必再查資料庫。
        var nodes = await db.RouteNodes.AsNoTracking().Select(n => new RoadNode(n.Code, n.X, n.Z)).ToListAsync();
        var edges = await db.RouteEdges.AsNoTracking().Select(e => new { e.FromCode, e.ToCode }).ToListAsync();
        app.Services.GetRequiredService<RoadNetwork>().Load(nodes, edges.Select(e => (e.FromCode, e.ToCode)));
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

        if (!await db.RouteNodes.AnyAsync())
        {
            var (nodes, edges) = DemoRoadNetwork.Build();
            db.RouteNodes.AddRange(nodes.Select(n => new RouteNode { Code = n.Code, X = n.X, Z = n.Z }));
            db.RouteEdges.AddRange(edges.Select(e => new RouteEdge { FromCode = e.From, ToCode = e.To }));
        }

        // 補上缺少的工序（舊資料庫只有粗加工、精加工）；已經有的不動，面板改過的數值會保留。
        var steps = await db.ProcessSteps.Select(s => s.Sequence).ToListAsync();
        db.ProcessSteps.AddRange(DemoSteps.Where(demo => !steps.Contains(demo.Sequence)).Select(demo => new ProcessStep
        {
            Sequence = demo.Sequence,
            StationCode = demo.StationCode,
            Kind = demo.Kind,
            SecondsPerPiece = demo.SecondsPerPiece,
            UsesTool = demo.UsesTool,
            ToolLifePieces = demo.ToolLifePieces,
            BatchMinPieces = demo.BatchMinPieces,
            BatchMaxPieces = demo.BatchMaxPieces,
            BatchSeconds = demo.BatchSeconds,
            BatchSecondStageSeconds = demo.BatchSecondStageSeconds,
            StageName = demo.StageName,
            SecondStageName = demo.SecondStageName,
            TestSpeed = demo.TestSpeed,
        }));

        // 有刀具的工序各一筆刀具狀態。
        var tools = await db.StationTools.Select(t => t.StationCode).ToListAsync();
        db.StationTools.AddRange(DemoSteps.Where(s => s.UsesTool && !tools.Contains(s.StationCode)).Select(s => new StationTool { StationCode = s.StationCode }));

        if (!await db.ProductionSettings.AnyAsync())
        {
            db.ProductionSettings.Add(new ProductionSettings
            {
                LotQuantity = 30,
                PartCode = "upper-punch",
                SourceStationCode = "raw-store",
                BufferStationCode = DemoBufferStation,
                ToolStoreStationCode = "tool-store",
                // 使用者 2026-10-10：機台取放每件 5 秒（取料、取出各一次）、AGV 交接一框 30 秒、換刀 90 秒。
                // 叫刀門檻 50 件是示範值。
                PieceHandlingSeconds = 5,
                AgvHandoverSeconds = 30,
                ToolChangeSeconds = 90,
                ToolRequestThresholdPieces = 50,
                ToolDeliveryEnabled = true,
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
