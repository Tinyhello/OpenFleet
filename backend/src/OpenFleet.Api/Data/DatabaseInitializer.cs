using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenFleet.Api.Catalog;
using OpenFleet.Api.Fleet;
using OpenFleet.Api.Inventory;
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
    /// 下排由左到右是切料、原料倉到電鍍（淬火與回火相鄰；切料站在原料倉左邊，使用者 2026-10-22 選擇把廠房往西加寬），
    /// 上排由右到左是拋光、（雜物倉庫）、品管、包裝、成品倉、出貨，
    /// 括號裡的兩個只是場景、不是站點；
    /// 刀具庫在走道內側的島區中央（使用者 2026-10-12 指定的相鄰關係；實際座標是 Claude 排的）。
    /// 待加工料容量 30 件是已確認的數值；倉庫與暫存點（淬火站入口）的容量規則還沒定，所以留空。
    /// </summary>
    private static readonly Station[] DemoStations =
    [
        new() { Code = "cutting", Name = "切料", HandoffX = -28, HandoffZ = -DemoRoadNetwork.HandoffZ, InboundCapacity = 30, OutboundCapacity = 30 },
        new() { Code = "raw-store", Name = "原料倉", HandoffX = -20, HandoffZ = -DemoRoadNetwork.HandoffZ },
        new() { Code = "rough", Name = "粗加工", HandoffX = -12, HandoffZ = -DemoRoadNetwork.HandoffZ, InboundCapacity = 30, OutboundCapacity = 30 },
        new() { Code = "fine", Name = "精加工", HandoffX = -4, HandoffZ = -DemoRoadNetwork.HandoffZ, InboundCapacity = 30, OutboundCapacity = 30 },
        new() { Code = "heat", Name = "淬火", HandoffX = 4, HandoffZ = -DemoRoadNetwork.HandoffZ },
        new() { Code = "temper", Name = "回火", HandoffX = 12, HandoffZ = -DemoRoadNetwork.HandoffZ },
        new() { Code = "plating", Name = "電鍍", HandoffX = 20, HandoffZ = -DemoRoadNetwork.HandoffZ },
        new() { Code = "polish", Name = "拋光", HandoffX = 20, HandoffZ = DemoRoadNetwork.HandoffZ },
        new() { Code = "qc", Name = "品管", HandoffX = 4, HandoffZ = DemoRoadNetwork.HandoffZ },
        new() { Code = "packing", Name = "包裝", HandoffX = -4, HandoffZ = DemoRoadNetwork.HandoffZ },
        // 成品倉：P4 只是場景，P5 變成真正的站點（使用者 2026-10-22：多出來的零件依規格與客戶收進成品倉）。
        new() { Code = "finished-store", Name = "成品倉", HandoffX = -12, HandoffZ = DemoRoadNetwork.HandoffZ },
        new() { Code = "shipping", Name = "出貨", HandoffX = -20, HandoffZ = DemoRoadNetwork.HandoffZ },
        new() { Code = "tool-store", Name = "刀具庫", HandoffX = DemoRoadNetwork.IslandX, HandoffZ = DemoRoadNetwork.IslandHandoffZ },
    ];

    /// <summary>
    /// 示範製程。逐件工序的每件加工時間（粗加工 30 秒、精加工 60 秒）與刀具壽命（500／250 件）、
    /// 淬火 4 小時、回火 8 小時（兩座爐，中間要搬運）、滿 150 支開爐最多 300 支、
    /// 電鍍一爐 30 mm 最多 40 支／24 mm 最多 56 支可以混夾，都是使用者提供的數值；
    /// 電鍍一爐 1 小時、拋光每支 20 秒是 Claude 提議、使用者同意先用的示範值。
    /// 回火的開爐條件沿用淬火的 150～300 支、電鍍有料就開（下限 1 支），是 Claude 的推定，待使用者確認。
    /// 品管逐支全檢、每支 60 秒是使用者提供的數值（2026-10-10）。
    /// 包裝逐支、每支 60 秒是使用者提供的數值（2026-10-10）。
    /// 測試模式的專用倍率（TestSpeed）：P5 起每一站都是 <see cref="DemoTestSpeed"/>（使用者 2026-10-22：同其他機台的加速比例，讓一個流程約 2～3 分鐘看完）；
    /// P4 時是粗加工與精加工 80、淬火與回火 250、電鍍 60、拋光 10、品管與包裝 20。面板可以逐站改。
    /// </summary>
    private static readonly ProcessStep[] DemoSteps =
    [
        // 切料：使用者 2026-10-22 要有切料站、切一支約 5 分鐘。排在第 0 道，只有從圓棒切的框會經過；
        // 待加工與完工容量 30 比照車床，是 Claude 挑的。
        new() { Sequence = 0, StationCode = "cutting", Kind = ProcessStepKind.Piece, SecondsPerPiece = 300, IsCutting = true, TestSpeed = DemoTestSpeed },
        new() { Sequence = 1, StationCode = "rough", Kind = ProcessStepKind.Piece, SecondsPerPiece = 30, UsesTool = true, ToolLifePieces = 500, TestSpeed = DemoTestSpeed },
        new() { Sequence = 2, StationCode = "fine", Kind = ProcessStepKind.Piece, SecondsPerPiece = 60, UsesTool = true, ToolLifePieces = 250, TestSpeed = DemoTestSpeed },
        new()
        {
            Sequence = 3, StationCode = "heat", Kind = ProcessStepKind.Batch, BatchMinPieces = 150, BatchMaxPieces = 300,
            BatchSeconds = 4 * 3600, StageName = "淬火", TestSpeed = DemoTestSpeed,
        },
        new()
        {
            Sequence = 4, StationCode = "temper", Kind = ProcessStepKind.Batch, BatchMinPieces = 150, BatchMaxPieces = 300,
            BatchSeconds = 8 * 3600, StageName = "回火", TestSpeed = DemoTestSpeed,
        },
        new()
        {
            Sequence = 5, StationCode = "plating", Kind = ProcessStepKind.Batch, BatchMinPieces = 1, BatchMaxPieces = 40, BatchMaxPiecesRod24 = 56,
            BatchSeconds = 3600, StageName = "電鍍", TestSpeed = DemoTestSpeed,
        },
        new() { Sequence = 6, StationCode = "polish", Kind = ProcessStepKind.Piece, SecondsPerPiece = 20, TestSpeed = DemoTestSpeed },
        new() { Sequence = 7, StationCode = "qc", Kind = ProcessStepKind.Piece, SecondsPerPiece = 60, IsInspection = true, TestSpeed = DemoTestSpeed },
        new() { Sequence = 8, StationCode = "packing", Kind = ProcessStepKind.Piece, SecondsPerPiece = 60, IsPacking = true, TestSpeed = DemoTestSpeed },
    ];

    /// <summary>
    /// 測試模式下每一站與貨車的倍率（使用者 2026-10-22：同其他機台的加速比例，讓一個流程約 2～3 分鐘看完）。
    /// 量測過：各站 1000 倍時，面板預設的 30 組要 199 秒，其中淬火與回火占 44 秒；3000 倍時兩爐約 15 秒。面板可以逐站修改。
    /// </summary>
    public const double DemoTestSpeed = 3000;

    /// <summary>測試模式下車輛的倍率：空車與載貨都快 5 倍（使用者 2026-10-22；5 是交通控制的上限）。</summary>
    public const double DemoTestVehicleSpeed = 5;

    /// <summary>包好的出貨箱送到出貨站等貨車。</summary>
    private const string DemoBufferStation = "shipping";

    /// <summary>貨車每小時來一次（Claude 挑的示範值，待使用者確認）；測試模式下和各站一樣用 <see cref="DemoTestSpeed"/>。</summary>
    private const double DemoTruckIntervalSeconds = 3600;

    /// <summary>
    /// 桿徑：一框 45／30 支是使用者確認的數值；各零件的備料長度是示範值（使用者還沒有實際數字），
    /// 讓一根 1.5 m 的圓棒切出十幾支（使用者 2026-10-22：一根原棒可以切十幾個）。
    /// </summary>
    private static readonly RodSize[] DemoRodSizes =
    [
        new() { DiameterMm = 30, PiecesPerBin = 30, UpperBlankLengthMm = 110, DieBlankLengthMm = 90, LowerBlankLengthMm = 95 },
        new() { DiameterMm = 24, PiecesPerBin = 45, UpperBlankLengthMm = 100, DieBlankLengthMm = 85, LowerBlankLengthMm = 88 },
    ];

    /// <summary>示範的沖頭規格。名稱、形狀與覆寫值都是 Claude 編的示範資料，用來展示「沿用」與「覆寫」。</summary>
    private static readonly PunchSpec[] DemoSpecs =
    [
        new() { Code = "P30-RD12", Name = "圓形 12", Shape = "圓形", RodDiameterMm = 30 },
        new() { Code = "P30-HX10", Name = "六角 10", Shape = "六角形", RodDiameterMm = 30, FinishSecondsPerPiece = 90 },
        new() { Code = "P24-SQ08", Name = "方形 8", Shape = "方形", RodDiameterMm = 24 },
        new() { Code = "P24-SK01", Name = "鍵形 K1", Shape = "特殊形狀", RodDiameterMm = 24, UpperBlankLengthMm = 115, FinishSecondsPerPiece = 120 },
    ];

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

                // 佈局不是面板能改的設定：舊資料庫的交接點跟著目前的示範佈局。
                station.HandoffX = demo.HandoffX;
                station.HandoffZ = demo.HandoffZ;
            }
        }

        // 路網整份跟著目前的示範佈局：補上缺少的節點與路段，舊佈局留下來的刪掉。
        var (nodes, edges) = DemoRoadNetwork.Build();
        var demoNodes = nodes.Select(n => n.Code).ToHashSet(StringComparer.Ordinal);
        var demoEdges = edges.Select(e => RoadNetwork.EdgeKey(e.From, e.To)).ToHashSet();
        var storedNodes = await db.RouteNodes.ToListAsync();
        var storedEdges = await db.RouteEdges.ToListAsync();
        db.RouteEdges.RemoveRange(storedEdges.Where(e => !demoEdges.Contains(RoadNetwork.EdgeKey(e.FromCode, e.ToCode))));
        db.RouteNodes.RemoveRange(storedNodes.Where(n => !demoNodes.Contains(n.Code)));
        var knownNodes = storedNodes.Select(n => n.Code).ToHashSet(StringComparer.Ordinal);
        var knownEdges = storedEdges.Select(e => RoadNetwork.EdgeKey(e.FromCode, e.ToCode)).ToHashSet();
        db.RouteNodes.AddRange(nodes.Where(n => !knownNodes.Contains(n.Code)).Select(n => new RouteNode { Code = n.Code, X = n.X, Z = n.Z }));
        db.RouteEdges.AddRange(edges.Where(e => !knownEdges.Contains(RoadNetwork.EdgeKey(e.From, e.To)))
            .Select(e => new RouteEdge { FromCode = e.From, ToCode = e.To }));

        // 補上缺少的工序（舊資料庫沒有後段工序或回火）；已經有的不動，面板改過的數值會保留。
        var steps = await db.ProcessSteps.Select(s => s.Sequence).ToListAsync();
        db.ProcessSteps.AddRange(DemoSteps.Where(demo => !steps.Contains(demo.Sequence)).Select(demo => new ProcessStep
        {
            Sequence = demo.Sequence,
            StationCode = demo.StationCode,
            Kind = demo.Kind,
            SecondsPerPiece = demo.SecondsPerPiece,
            UsesTool = demo.UsesTool,
            IsInspection = demo.IsInspection,
            IsPacking = demo.IsPacking,
            IsCutting = demo.IsCutting,
            ToolLifePieces = demo.ToolLifePieces,
            BatchMinPieces = demo.BatchMinPieces,
            BatchMaxPieces = demo.BatchMaxPieces,
            BatchMaxPiecesRod24 = demo.BatchMaxPiecesRod24,
            BatchSeconds = demo.BatchSeconds,
            StageName = demo.StageName,
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
                // 使用者 2026-10-10：機床換線約 10 分鐘。測試模式下 AGV 交接和各站同一個倍率、車速 5 倍（使用者 2026-10-22）。
                ChangeoverSeconds = 600,
                TestHandoverSpeed = DemoTestSpeed,
                TestEmptyVehicleSpeed = DemoTestVehicleSpeed,
                TestLoadedVehicleSpeed = DemoTestVehicleSpeed,
                // 使用者 2026-10-10：示範用不良率 10%，同一支最多反工 1 次。
                DefectRate = 0.1,
                MaxReworkCount = 1,
                // 使用者 2026-10-10：鍍層不良的反工品最少 30 支才重新電鍍。
                ReworkBatchMinPieces = 30,
                TruckIntervalSeconds = DemoTruckIntervalSeconds,
                TestTruckSpeed = DemoTestSpeed,
            });
        }

        if (!await db.TruckSchedules.AnyAsync())
        {
            var interval = await db.ProductionSettings.Select(s => (double?)s.TruckIntervalSeconds).SingleOrDefaultAsync() ?? DemoTruckIntervalSeconds;
            db.TruckSchedules.Add(TruckSchedule.Create(interval));
        }

        await SeedCatalogAsync(db);
        await SeedInventoryAsync(db);

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
    /// <summary>
    /// 示範的初始庫存：Φ40 圓棒 40 根（安全庫存 30 根）、P30-RD12 三種備料各 40 支。
    /// 數量是 Claude 挑的示範值：一般的單切完會低於安全庫存，下 300 組會看到圓棒不夠。
    /// </summary>
    public const int DemoBarSafetyStock = 30;

    private static readonly (string ItemCode, int Quantity)[] DemoStock =
    [
        (CatalogService.BarItemCode, 40),
        ("P30-RD12-U-BLK", 40),
        ("P30-RD12-D-BLK", 40),
        ("P30-RD12-L-BLK", 40),
    ];

    /// <summary>補上缺少的初始庫存（只補還沒有庫存列的品項）。啟動與示範重設都呼叫；呼叫端負責存檔。</summary>
    public static async Task SeedInventoryAsync(OpenFleetDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var stocked = await db.StockBalances.Select(b => b.ItemCode).ToListAsync();
        foreach (var (itemCode, quantity) in DemoStock.Where(s => !stocked.Contains(s.ItemCode)))
        {
            var balance = StockBalance.Create(itemCode, InventoryService.StoreStation, now);
            balance.Add(quantity, now);
            db.StockBalances.Add(balance);
            db.StockMovements.Add(new StockMovement
            {
                ItemCode = itemCode, StationCode = InventoryService.StoreStation, Quantity = quantity,
                Reason = StockMovementReason.Seed, OnHandAfter = balance.OnHand, At = now,
            });
        }
    }

    /// <summary>補上缺少的桿徑、圓棒與示範規格；已經有的不動（之後可能被改過）。</summary>
    private static async Task SeedCatalogAsync(OpenFleetDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var rods = await db.RodSizes.Select(r => r.DiameterMm).ToListAsync();
        db.RodSizes.AddRange(DemoRodSizes.Where(r => !rods.Contains(r.DiameterMm)).Select(r => new RodSize
        {
            DiameterMm = r.DiameterMm,
            PiecesPerBin = r.PiecesPerBin,
            UpperBlankLengthMm = r.UpperBlankLengthMm,
            DieBlankLengthMm = r.DieBlankLengthMm,
            LowerBlankLengthMm = r.LowerBlankLengthMm,
        }));

        var bar = await db.Items.SingleOrDefaultAsync(i => i.Code == CatalogService.BarItemCode);
        if (bar is null)
        {
            db.Items.Add(new Item
            {
                Code = CatalogService.BarItemCode, Kind = ItemKind.Bar, Name = "Φ40 圓棒",
                DiameterMm = CatalogService.BarDiameterMm, LengthMm = CatalogService.DemoBarLengthMm, SafetyStock = DemoBarSafetyStock,
            });
        }
        else
        {
            // 舊資料庫升級：安全庫存是後來才加的。
            bar.SafetyStock ??= DemoBarSafetyStock;
        }

        var specs = await db.PunchSpecs.Select(s => s.Code).ToListAsync();
        foreach (var demo in DemoSpecs.Where(d => !specs.Contains(d.Code)))
        {
            var spec = new PunchSpec
            {
                Code = demo.Code, Name = demo.Name, Shape = demo.Shape, RodDiameterMm = demo.RodDiameterMm,
                UpperBlankLengthMm = demo.UpperBlankLengthMm, DieBlankLengthMm = demo.DieBlankLengthMm, LowerBlankLengthMm = demo.LowerBlankLengthMm,
                RoughSecondsPerPiece = demo.RoughSecondsPerPiece, FinishSecondsPerPiece = demo.FinishSecondsPerPiece, CreatedAt = now,
            };
            var (items, boms) = CatalogService.BuildSpecRecords(spec, now);
            db.PunchSpecs.Add(spec);
            db.Items.AddRange(items);
            db.BomVersions.AddRange(boms);
        }
    }
}
