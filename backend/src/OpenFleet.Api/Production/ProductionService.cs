using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Catalog;
using OpenFleet.Api.Data;
using OpenFleet.Api.Inventory;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Production;

/// <summary>輸入不符合規則；端點會轉成 HTTP 400（ValidationProblem）。</summary>
public sealed class RequestValidationException(string field, string message) : Exception(message)
{
    public IDictionary<string, string[]> Errors { get; } = new Dictionary<string, string[]> { [field] = [message] };
}

/// <summary>
/// 批次、製程設定與加工進度（Scoped：跟著一個 DbContext）。
/// 一個批次依料框容量分成好幾框，每一框各自搬運、各自加工；系統裡的 <see cref="MaterialLot"/> 就是一框。
/// 加工是後端內的簡化模擬，工序有兩種：
/// 逐件（車床、拋光）：每站一台、一次一框、逐件計數、整框做完才放行；有刀具的站快到壽命時預先叫刀，到壽命時換刀或停機等刀；
/// 車床接著做的那一框桿徑不同時要先換線。
/// 整爐（淬火、回火、電鍍）：站內累積到下限才開爐，不同批次可以混在一起，時間到整爐一起放行。
/// 品管（逐件工序的一種）：逐支檢驗，整框檢完後在品管站分框，合格的往下走、可反工的送回去重做、其餘報廢。
/// 包裝（逐件工序的一種）：逐支包裝，同批次同一種零件的框全部包完才併成一箱送去出貨站；貨車定時來，把整批到齊的箱子載走。
/// 一個批次是若干組，每一組是上沖、中模、下沖各一（<see cref="ProductParts"/>）；三種零件各自分框、走同一條製程。
/// 品管報廢的料會自動從原料倉補投同樣數量，補齊之後整批才裝箱。
/// 測試模式開啟時，每一站依自己的專用倍率加快（<see cref="ProductionSettings.SpeedOf"/>）。
/// </summary>
public sealed partial class ProductionService(
    OpenFleetDbContext db, InventoryService inventory, IDefectSource defects, TimeProvider time, ILogger<ProductionService> logger)
{
    public const double MaxSecondsPerPiece = 600;
    public const double MaxPieceHandlingSeconds = 60;
    public const double MaxAgvHandoverSeconds = 600;
    public const double MaxToolChangeSeconds = 3600;
    public const int MaxToolLifePieces = 100_000;

    /// <summary>一個批次最多幾組。使用者 2026-10-10：訂單最大 300 組；訂單還沒做，先以批次數量代表。</summary>
    public const int MaxBatchQuantity = 300;
    public const int MaxBatchRunPieces = 1_000;
    public const double MaxBatchRunSeconds = 48 * 3600;
    public const double MaxTestSpeed = 1000;
    public const double MaxChangeoverSeconds = 4 * 3600;

    /// <summary>空車倍率的上限：再快交通控制的放行會跟不上。</summary>
    public const double MaxTestEmptyVehicleSpeed = 5;

    public const int MaxReworkLimit = 3;
    public const double MinTruckIntervalSeconds = 60;
    public const double MaxTruckIntervalSeconds = 24 * 3600;

    /// <summary>
    /// 不良原因 → 回哪一站重做；null 表示不能反工，直接報廢（使用者 2026-10-10 選定的三種原因）。
    /// 回電鍍的料之後照路線再經過拋光與品管。
    /// </summary>
    private static readonly (DefectReason Reason, string? StationCode, string Suffix)[] ReworkRoutes =
    [
        (DefectReason.Surface, "polish", "RS"),
        (DefectReason.Plating, "plating", "RP"),
        (DefectReason.Dimension, null, string.Empty),
    ];

    /// <summary>沒有指定規格時用的示範規格（每個桿徑一個沒有覆寫的規格）。</summary>
    public static readonly IReadOnlyDictionary<int, string> DefaultSpecs = new Dictionary<int, string> { [30] = "P30-RD12", [24] = "P24-SQ08" };

    /// <summary>目前支援的桿徑（mm）。</summary>
    public static readonly int[] RodDiameters = [24, 30];
    public const int DefaultRodDiameter = 30;

    /// <summary>
    /// 一框可以裝幾支，由桿徑決定：24 mm 一框 45 支、30 mm 一框 30 支（使用者 2026-10-10 確認）。
    /// 中模外徑是 38 mm，但和同組的上下沖配套，一框支數相同（使用者 2026-10-22）。
    /// </summary>
    public static int BinCapacity(int rodDiameterMm) => rodDiameterMm == 24 ? 45 : 30;

    public async Task<ProductionSettingsDto> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await db.ProductionSettings.AsNoTracking().SingleAsync(cancellationToken);
        var stations = await db.Stations.AsNoTracking().ToDictionaryAsync(s => s.Code, cancellationToken);
        var steps = await db.ProcessSteps.AsNoTracking().OrderBy(s => s.Sequence).ToListAsync(cancellationToken);

        return new ProductionSettingsDto
        {
            LotQuantity = settings.LotQuantity,
            MaxLotQuantity = MaxBatchQuantity,
            SourceStationCode = settings.SourceStationCode,
            BufferStationCode = settings.BufferStationCode,
            PieceHandlingSeconds = settings.PieceHandlingSeconds,
            AgvHandoverSeconds = settings.AgvHandoverSeconds,
            ToolChangeSeconds = settings.ToolChangeSeconds,
            ToolRequestThresholdPieces = settings.ToolRequestThresholdPieces,
            ToolDeliveryEnabled = settings.ToolDeliveryEnabled,
            TestMode = settings.TestMode,
            ChangeoverSeconds = settings.ChangeoverSeconds,
            TestHandoverSpeed = settings.TestHandoverSpeed,
            TestEmptyVehicleSpeed = settings.TestEmptyVehicleSpeed,
            DefectRate = settings.DefectRate,
            MaxReworkCount = settings.MaxReworkCount,
            ReworkBatchMinPieces = settings.ReworkBatchMinPieces,
            TruckIntervalSeconds = settings.TruckIntervalSeconds,
            TestTruckSpeed = settings.TestTruckSpeed,
            Steps = steps.Select(s => new ProcessStepDto
            {
                Sequence = s.Sequence,
                StationCode = s.StationCode,
                StationName = stations[s.StationCode].Name,
                Kind = s.Kind,
                SecondsPerPiece = s.SecondsPerPiece,
                UsesTool = s.UsesTool,
                IsInspection = s.IsInspection,
                IsPacking = s.IsPacking,
                IsCutting = s.IsCutting,
                ToolLifePieces = s.ToolLifePieces,
                BatchMinPieces = s.BatchMinPieces,
                BatchMaxPieces = s.BatchMaxPieces,
                BatchMaxPiecesRod24 = s.BatchMaxPiecesRod24,
                BatchSeconds = s.BatchSeconds,
                StageName = s.StageName,
                TestSpeed = s.TestSpeed,
            }).ToList(),
        };
    }

    /// <summary>
    /// 修改示範設定。已開始的加工與已派出的搬運不受影響；之後開始的加工、之後派出的搬運採用新值。
    /// 例外是測試模式與各站的倍率：立刻生效，進行中的加工從現在起用新的速度。
    /// </summary>
    public async Task<ProductionSettingsDto> UpdateSettingsAsync(ProductionSettingsDto request, CancellationToken cancellationToken)
    {
        var settings = await db.ProductionSettings.SingleAsync(cancellationToken);
        var steps = await db.ProcessSteps.OrderBy(s => s.Sequence).ToListAsync(cancellationToken);

        ValidateQuantity(request.LotQuantity, MaxBatchQuantity, "lotQuantity");
        // 0 代表瞬間完成（測試用）；負數或太大的值視為輸入錯誤。
        ValidateRange(request.PieceHandlingSeconds, 0, MaxPieceHandlingSeconds, "pieceHandlingSeconds");
        ValidateRange(request.AgvHandoverSeconds, 0, MaxAgvHandoverSeconds, "agvHandoverSeconds");
        ValidateRange(request.ToolChangeSeconds, 0, MaxToolChangeSeconds, "toolChangeSeconds");
        ValidateRange(request.ToolRequestThresholdPieces, 0, MaxToolLifePieces, "toolRequestThresholdPieces");
        ValidateRange(request.ChangeoverSeconds, 0, MaxChangeoverSeconds, "changeoverSeconds");
        ValidateRange(request.TestHandoverSpeed, 1, MaxTestSpeed, "testHandoverSpeed");
        ValidateRange(request.TestEmptyVehicleSpeed, 1, MaxTestEmptyVehicleSpeed, "testEmptyVehicleSpeed");
        ValidateRange(request.DefectRate, 0, 1, "defectRate");
        ValidateRange(request.MaxReworkCount, 0, MaxReworkLimit, "maxReworkCount");
        ValidateRange(request.ReworkBatchMinPieces, 1, MaxBatchRunPieces, "reworkBatchMinPieces");
        ValidateRange(request.TruckIntervalSeconds, MinTruckIntervalSeconds, MaxTruckIntervalSeconds, "truckIntervalSeconds");
        ValidateRange(request.TestTruckSpeed, 1, MaxTestSpeed, "testTruckSpeed");

        settings.LotQuantity = request.LotQuantity;
        settings.PieceHandlingSeconds = request.PieceHandlingSeconds;
        settings.AgvHandoverSeconds = request.AgvHandoverSeconds;
        settings.ToolChangeSeconds = request.ToolChangeSeconds;
        settings.ToolRequestThresholdPieces = request.ToolRequestThresholdPieces;
        settings.ToolDeliveryEnabled = request.ToolDeliveryEnabled;
        settings.ChangeoverSeconds = request.ChangeoverSeconds;
        settings.TestHandoverSpeed = request.TestHandoverSpeed;
        settings.TestEmptyVehicleSpeed = request.TestEmptyVehicleSpeed;
        settings.DefectRate = request.DefectRate;
        settings.MaxReworkCount = request.MaxReworkCount;
        settings.ReworkBatchMinPieces = request.ReworkBatchMinPieces;
        settings.TestTruckSpeed = request.TestTruckSpeed;
        if (settings.TruckIntervalSeconds != request.TruckIntervalSeconds)
        {
            // 間隔改了：下一班從現在用新的間隔重新倒數。
            settings.TruckIntervalSeconds = request.TruckIntervalSeconds;
            (await db.TruckSchedules.SingleAsync(cancellationToken)).Restart(request.TruckIntervalSeconds);
        }

        var speedsBefore = steps.Select(settings.SpeedOf).ToList();
        settings.TestMode = request.TestMode;

        foreach (var requested in request.Steps ?? [])
        {
            var step = steps.SingleOrDefault(s => s.Sequence == requested.Sequence)
                ?? throw new RequestValidationException("steps", $"Step {requested.Sequence} does not exist.");

            // 寫成「不在範圍內」的否定，NaN 也會被擋下。
            if (!(requested.TestSpeed >= 1 && requested.TestSpeed <= MaxTestSpeed))
            {
                throw new RequestValidationException("steps", $"Step {requested.Sequence}: testSpeed must be between 1 and {MaxTestSpeed}.");
            }

            step.TestSpeed = requested.TestSpeed;

            // 工序的種類、站點、是否用刀具是製程定義，不能從這裡改；只接受各自的數值。
            if (step.Kind == ProcessStepKind.Batch)
            {
                if (requested.BatchMinPieces < 1 || requested.BatchMaxPieces < requested.BatchMinPieces || requested.BatchMaxPieces > MaxBatchRunPieces)
                {
                    throw new RequestValidationException("steps",
                        $"Step {requested.Sequence}: batchMinPieces must be at least 1 and batchMaxPieces between batchMinPieces and {MaxBatchRunPieces}.");
                }

                // 分不分桿徑是製程定義（0＝不分），不能從這裡切換；有分的工序才採用這個值。
                if (step.BatchMaxPiecesRod24 > 0 && requested.BatchMaxPiecesRod24 is < 1 or > MaxBatchRunPieces)
                {
                    throw new RequestValidationException("steps", $"Step {requested.Sequence}: batchMaxPiecesRod24 must be between 1 and {MaxBatchRunPieces}.");
                }

                if (!(requested.BatchSeconds > 0 && requested.BatchSeconds <= MaxBatchRunSeconds))
                {
                    throw new RequestValidationException("steps", $"Step {requested.Sequence}: batchSeconds must be greater than 0 and at most {MaxBatchRunSeconds}.");
                }

                step.BatchMinPieces = requested.BatchMinPieces;
                step.BatchMaxPieces = requested.BatchMaxPieces;
                step.BatchSeconds = requested.BatchSeconds;
                if (step.BatchMaxPiecesRod24 > 0)
                {
                    step.BatchMaxPiecesRod24 = requested.BatchMaxPiecesRod24;
                }

                continue;
            }

            if (!(requested.SecondsPerPiece > 0 && requested.SecondsPerPiece <= MaxSecondsPerPiece))
            {
                throw new RequestValidationException("steps", $"Step {requested.Sequence}: secondsPerPiece must be greater than 0 and at most {MaxSecondsPerPiece}.");
            }

            step.SecondsPerPiece = requested.SecondsPerPiece;
            if (step.UsesTool)
            {
                if (requested.ToolLifePieces is < 1 or > MaxToolLifePieces)
                {
                    throw new RequestValidationException("steps", $"Step {requested.Sequence}: toolLifePieces must be between 1 and {MaxToolLifePieces}.");
                }

                step.ToolLifePieces = requested.ToolLifePieces;
            }
        }

        if (!steps.Select(settings.SpeedOf).SequenceEqual(speedsBefore))
        {
            settings.SpeedChangedAt = time.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Production settings updated: batch {Quantity}, piece handling {Handling}s, AGV handover {Handover}s, tool change {ToolChange}s, test mode {TestMode}.",
            settings.LotQuantity, settings.PieceHandlingSeconds, settings.AgvHandoverSeconds, settings.ToolChangeSeconds, settings.TestMode);
        return await GetSettingsAsync(cancellationToken);
    }

    /// <summary>
    /// 模擬收料：在原料倉建立一個沒有訂單的批次，<paramref name="quantity"/> 是組數；一組裡的每一種零件各做這麼多
    /// （<paramref name="partCodes"/> 可以指定只做其中幾種）。面板與 API 下單改走客戶訂單（OrderService）；這裡留給測試與內部使用。
    /// 框怎麼建立見 <see cref="ReceiveLotsAsync"/>。所有的框、庫存異動、搬運任務在同一筆交易寫入；庫存被別人同時改動時整個重來。
    /// </summary>
    public Task<List<MaterialLot>> CreateBatchAsync(
        int? quantity, int? rodDiameterMm, IReadOnlyList<string>? partCodes, CancellationToken cancellationToken) =>
        CreateBatchAsync(quantity, rodDiameterMm, partCodes, null, cancellationToken);

    public async Task<List<MaterialLot>> CreateBatchAsync(
        int? quantity, int? rodDiameterMm, IReadOnlyList<string>? partCodes, string? specCode, CancellationToken cancellationToken)
    {
        var settings = await db.ProductionSettings.AsNoTracking().SingleAsync(cancellationToken);
        var total = quantity ?? settings.LotQuantity;
        ValidateQuantity(total, MaxBatchQuantity, "quantity");
        var (spec, diameter) = await ResolveSpecAsync(specCode, rodDiameterMm, cancellationToken);
        var parts = ValidateParts(partCodes);

        var batchNumber = $"B-{time.GetUtcNow():yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        var bins = await inventory.RunAsync(
            () => ReceiveLotsAsync(spec, diameter, parts, total, batchNumber, null, cancellationToken), cancellationToken);

        logger.LogInformation(
            "Batch {BatchNumber} received at {Station}: {Quantity} set(s) of {Parts} ({Spec}, rod {Diameter} mm) in {Bins} bin(s), {Waiting} waiting for bars.",
            batchNumber, settings.SourceStationCode, total, string.Join("+", parts), spec?.SpecCode ?? "no spec", diameter, bins.Count,
            bins.Count(b => b.Status == LotStatus.AwaitingMaterial));
        return bins;
    }

    /// <summary>
    /// 規格：有指定就要存在、而且和指定的桿徑相符；沒有指定就用該桿徑的示範規格（找不到時是 null，料不經過切料）。
    /// </summary>
    public async Task<(ProductionSpec? Spec, int Diameter)> ResolveSpecAsync(string? specCode, int? rodDiameterMm, CancellationToken cancellationToken)
    {
        if (specCode is not null)
        {
            var spec = await db.PunchSpecs.AsNoTracking().Include(s => s.Rod).SingleOrDefaultAsync(s => s.Code == specCode, cancellationToken)
                ?? throw new RequestValidationException("specCode", $"Punch spec {specCode} does not exist.");
            if (rodDiameterMm is { } rod && rod != spec.RodDiameterMm)
            {
                throw new RequestValidationException("rodDiameterMm", $"Punch spec {spec.Code} is for {spec.RodDiameterMm} mm rods.");
            }

            return (ProductionSpec.From(spec, CatalogService.DieOuterDiameterMm), spec.RodDiameterMm);
        }

        var diameter = rodDiameterMm ?? DefaultRodDiameter;
        if (!RodDiameters.Contains(diameter))
        {
            throw new RequestValidationException("rodDiameterMm", $"Rod diameter must be one of {string.Join(", ", RodDiameters)} mm.");
        }

        var defaultCode = DefaultSpecs[diameter];
        var fallback = await db.PunchSpecs.AsNoTracking().Include(s => s.Rod).SingleOrDefaultAsync(s => s.Code == defaultCode, cancellationToken);
        return (fallback is null ? null : ProductionSpec.From(fallback, CatalogService.DieOuterDiameterMm), diameter);
    }

    /// <summary>依一組的固定順序（上沖、中模、下沖），不照輸入的順序；重複的只算一次。不認得的零件是輸入錯誤。</summary>
    public static string[] ValidateParts(IReadOnlyList<string>? partCodes)
    {
        var parts = partCodes is null ? ProductParts.All : ProductParts.All.Where(partCodes.Contains).ToArray();
        if (parts.Length == 0 || partCodes?.Any(code => !ProductParts.All.Contains(code)) == true)
        {
            throw new RequestValidationException("partCodes", $"Part codes must be one or more of {string.Join(", ", ProductParts.All)}.");
        }

        return parts;
    }

    /// <summary>
    /// 開立工單：在原料倉建立這張工單的框（含開工時多做的），批次編號就是工單號碼。規格用工單開立時存下的那一份。
    /// 不存檔：由呼叫端（建立訂單）在同一筆交易裡一起存。
    /// </summary>
    public Task<List<MaterialLot>> ReleaseAsync(ProductionOrder order, CancellationToken cancellationToken) =>
        ReceiveLotsAsync(
            order.Spec, order.Spec.RodDiameterMm, order.Parts, order.Quantity + order.ExtraQuantity, order.Number, order.Id, cancellationToken);

    /// <summary>
    /// 在原料倉建立一批框，每一種零件各自依料框容量分框（不存檔）。
    /// 有規格時先用原料倉現成的備料（直接送第一道加工），不夠的從圓棒切（先送切料站）：
    /// 圓棒依框的順序保留，保留不到的框先在原料倉等料，到貨後才送去切（使用者 2026-10-22：不等料，有多少先做多少）。
    /// </summary>
    private async Task<List<MaterialLot>> ReceiveLotsAsync(
        ProductionSpec? spec, int diameter, IReadOnlyList<string> parts, int total, string batchNumber, Guid? productionOrderId,
        CancellationToken cancellationToken)
    {
        var settings = await db.ProductionSettings.AsNoTracking().SingleAsync(cancellationToken);
        var steps = await db.ProcessSteps.AsNoTracking().OrderBy(s => s.Sequence).ToListAsync(cancellationToken);
        var cutting = steps.FirstOrDefault(s => s.IsCutting);
        var first = steps.FirstOrDefault(s => !s.IsCutting);
        var bar = spec is null || cutting is null
            ? null
            : await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Code == CatalogService.BarItemCode, cancellationToken);

        var now = time.GetUtcNow();
        var bins = new List<MaterialLot>();
        foreach (var part in parts)
        {
            if (spec is null || cutting is null || bar is null)
            {
                // 沒有規格（或沒有切料站）：料直接從原料倉送第一道工序。
                var sizes = SplitIntoBins(total, BinCapacity(diameter));
                for (var i = 0; i < sizes.Count; i++)
                {
                    bins.Add(ReceiveBin(part, sizes[i], first, batchNumber, i + 1, sizes.Count, diameter, null, productionOrderId, settings, now));
                }

                continue;
            }

            bins.AddRange(await ReceivePartAsync(
                spec, part, total, cutting, first, bar, batchNumber, productionOrderId, settings, now, cancellationToken));
        }

        return bins;
    }

    /// <summary>
    /// 一種零件：先保留並領用現成的備料（裝成直接送第一道加工的框），不夠的從圓棒切。一框幾支依規格的桿徑。
    /// 整根切完、一根只切一種長度（使用者 2026-10-22）：圓棒根數＝要切的支數 ÷ 一根切幾支（無條件進位），多出來的切完入庫。
    /// 圓棒依累計支數分到各框（第 k 框用到第 ⌈累計支數 ÷ 一根切幾支⌉ 根為止），多出來的算在最後一框。
    /// </summary>
    private async Task<List<MaterialLot>> ReceivePartAsync(
        ProductionSpec spec, string part, int total, ProcessStep cutting, ProcessStep? first, Item bar,
        string batchNumber, Guid? productionOrderId, ProductionSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var capacity = spec.PiecesPerBin;
        var blankItem = CatalogService.BlankItemCode(spec.SpecCode, part);
        var blankLength = spec.BlankLengthFor(part);
        var reference = $"{batchNumber}-{ProductParts.Letter(part)}";

        var fromStock = 0;
        if (await inventory.ReserveAsync(blankItem, InventoryService.StoreStation, total, reference, cancellationToken) is { } blanks)
        {
            fromStock = blanks.Quantity;
            await inventory.ConsumeAsync(blanks, fromStock, cancellationToken);
        }

        var toCut = total - fromStock;
        var stockBins = SplitIntoBins(fromStock, capacity);
        var cutBins = SplitIntoBins(toCut, capacity);
        var binCount = stockBins.Count + cutBins.Count;
        var lots = new List<MaterialLot>();
        var index = 0;
        foreach (var size in stockBins)
        {
            lots.Add(ReceiveBin(part, size, first, batchNumber, ++index, binCount, spec.RodDiameterMm, spec.SpecCode, productionOrderId, settings, now));
        }

        if (toCut == 0)
        {
            return lots;
        }

        var perBar = CatalogService.PiecesPerBar(bar.LengthMm ?? CatalogService.DemoBarLengthMm, blankLength, CatalogService.DemoKerfMm);
        var totalBars = (toCut + perBar - 1) / perBar;
        var cumulative = 0;
        var barsSoFar = 0;
        for (var i = 0; i < cutBins.Count; i++)
        {
            cumulative += cutBins[i];
            var barsThrough = (cumulative + perBar - 1) / perBar;
            var extra = i == cutBins.Count - 1 ? totalBars * perBar - toCut : 0;
            var lot = MaterialLot.Receive(part, cutBins[i], settings.SourceStationCode, cutting.Sequence, batchNumber, ++index, binCount,
                spec.RodDiameterMm, now, spec.SpecCode, awaitingMaterial: true, productionOrderId);
            db.Lots.Add(lot);
            db.InventoryMovements.Add(InventoryMovement.Record(lot, from: null, "Received", now));
            var job = CuttingJob.Plan(lot, blankItem, bar.Code, blankLength, perBar, barsThrough - barsSoFar, extra, now);
            db.CuttingJobs.Add(job);
            barsSoFar = barsThrough;
            await ReserveBarsAsync(lot, job, cutting, settings, now, cancellationToken);
            lots.Add(lot);
        }

        return lots;
    }

    private MaterialLot ReceiveBin(
        string part, int size, ProcessStep? first, string batchNumber, int index, int binCount, int diameter, string? specCode,
        Guid? productionOrderId, ProductionSettings settings, DateTimeOffset now)
    {
        var bin = MaterialLot.Receive(
            part, size, settings.SourceStationCode, first?.Sequence, batchNumber, index, binCount, diameter, now, specCode,
            productionOrderId: productionOrderId);
        db.Lots.Add(bin);
        db.InventoryMovements.Add(InventoryMovement.Record(bin, from: null, "Received", now));
        db.TransportTasks.Add(TransportTask.Create(
            bin, settings.SourceStationCode, first?.StationCode ?? settings.BufferStationCode, TransportTaskKind.Replenishment, now));
        return bin;
    }

    /// <summary>替一框保留還缺的圓棒；保留到夠了，這一框就從等料改成送去切料。</summary>
    private async Task ReserveBarsAsync(
        MaterialLot lot, CuttingJob job, ProcessStep cutting, ProductionSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (job.BarsMissing > 0
            && await inventory.ReserveAsync(job.BarItemCode, InventoryService.StoreStation, job.BarsMissing, lot.LotNumber, cancellationToken) is { } reserved)
        {
            job.AddReserved(reserved.Quantity);
        }

        if (job.IsReady)
        {
            lot.MaterialArrived(cutting.Sequence, now);
            db.TransportTasks.Add(TransportTask.Create(lot, settings.SourceStationCode, cutting.StationCode, TransportTaskKind.Replenishment, now));
        }
    }

    /// <summary>前面的框裝滿，最後一框裝剩下的。</summary>
    private static List<int> SplitIntoBins(int total, int capacity)
    {
        var sizes = new List<int>();
        for (var left = total; left > 0; left -= capacity)
        {
            sizes.Add(Math.Min(capacity, left));
        }

        return sizes;
    }

    /// <summary>
    /// 等料的框（圓棒不夠，或補做的料還沒分配）依建立順序再試一次：
    /// 補做的框先看現成的備料夠不夠整框，夠就直接送第一道加工；不夠就排切料、保留圓棒。
    /// 保留到夠了就送去切料。採購單收貨或盤點補進圓棒後，下一輪就會接上。
    /// 和其他寫入庫存的請求同時改到同一列時，這一輪放棄，下一輪重來。
    /// </summary>
    private async Task<bool> AllocateWaitingMaterialAsync(
        IReadOnlyList<ProcessStep> steps, ProductionSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutting = steps.FirstOrDefault(s => s.IsCutting);
        if (cutting is null || !await db.Lots.AnyAsync(l => l.Status == LotStatus.AwaitingMaterial, cancellationToken))
        {
            return false;
        }

        var waiting = await db.Lots.Where(l => l.Status == LotStatus.AwaitingMaterial)
            .OrderBy(l => l.CreatedAt).ThenBy(l => l.LotNumber)
            .ToListAsync(cancellationToken);
        var first = steps.FirstOrDefault(s => !s.IsCutting);
        var lotIds = waiting.Select(l => l.Id).ToList();
        var jobs = await db.CuttingJobs.Where(j => lotIds.Contains(j.LotId)).ToDictionaryAsync(j => j.LotId, cancellationToken);
        // 有工單的框用工單開立時存下的規格；沒有工單的用規格的現值。
        var orderIds = waiting.Select(l => l.ProductionOrderId).OfType<Guid>().Distinct().ToList();
        var orderSpecs = await db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id))
            .ToDictionaryAsync(o => o.Id, o => o.Spec, cancellationToken);
        var specCodes = waiting.Where(l => l.ProductionOrderId is null).Select(l => l.SpecCode).OfType<string>().Distinct().ToList();
        var liveSpecs = (await db.PunchSpecs.AsNoTracking().Include(s => s.Rod).Where(s => specCodes.Contains(s.Code)).ToListAsync(cancellationToken))
            .ToDictionary(s => s.Code, s => ProductionSpec.From(s, CatalogService.DieOuterDiameterMm));
        var bar = await db.Items.AsNoTracking().SingleAsync(i => i.Code == CatalogService.BarItemCode, cancellationToken);

        foreach (var lot in waiting)
        {
            if (!jobs.TryGetValue(lot.Id, out var job))
            {
                var spec = lot.ProductionOrderId is { } orderId ? orderSpecs[orderId] : liveSpecs[lot.SpecCode!];
                var blankItem = CatalogService.BlankItemCode(spec.SpecCode, lot.PartCode);
                var stock = await inventory.GetOrCreateBalanceAsync(blankItem, InventoryService.StoreStation, cancellationToken);
                if (stock.Available >= lot.Quantity)
                {
                    var blanks = (await inventory.ReserveAsync(blankItem, InventoryService.StoreStation, lot.Quantity, lot.LotNumber, cancellationToken))!;
                    await inventory.ConsumeAsync(blanks, lot.Quantity, cancellationToken);
                    lot.MaterialArrived(first!.Sequence, now);
                    db.TransportTasks.Add(TransportTask.Create(lot, settings.SourceStationCode, first.StationCode, TransportTaskKind.Replenishment, now));
                    logger.LogInformation("Lot {LotNumber}: {Quantity} blank(s) taken from stock.", lot.LotNumber, lot.Quantity);
                    continue;
                }

                var blankLength = spec.BlankLengthFor(lot.PartCode);
                var perBar = CatalogService.PiecesPerBar(bar.LengthMm ?? CatalogService.DemoBarLengthMm, blankLength, CatalogService.DemoKerfMm);
                var bars = (lot.Quantity + perBar - 1) / perBar;
                job = CuttingJob.Plan(lot, blankItem, bar.Code, blankLength, perBar, bars, bars * perBar - lot.Quantity, now);
                db.CuttingJobs.Add(job);
            }

            await ReserveBarsAsync(lot, job, cutting, settings, now, cancellationToken);
            if (lot.Status == LotStatus.AwaitingTransport)
            {
                logger.LogInformation("Lot {LotNumber}: {Bars} bar(s) reserved; transport to cutting requested.", lot.LotNumber, job.Bars);
            }
        }

        if (!db.ChangeTracker.HasChanges())
        {
            return false;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            logger.LogInformation("Material allocation conflicted with another stock update; retrying next cycle.");
            return false;
        }
    }

    /// <summary>
    /// 切完一框：領用這一框保留的圓棒，多出來的備料放回原料倉（Claude 選的存放位置，待使用者確認）。
    /// 多出來的備料直接算入庫，沒有另外的搬運。
    /// </summary>
    private async Task FinishCuttingAsync(OperationExecution operation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lot = operation.Lot;
        var job = await db.CuttingJobs.SingleAsync(j => j.LotId == lot.Id, cancellationToken);
        var reservations = await db.StockReservations
            .Where(r => r.Reference == lot.LotNumber && r.ItemCode == job.BarItemCode && r.Remaining > 0)
            .ToListAsync(cancellationToken);
        foreach (var reservation in reservations)
        {
            await inventory.ConsumeAsync(reservation, reservation.Remaining, cancellationToken);
        }

        if (job.ExtraBlanks > 0)
        {
            await inventory.AddStockAsync(
                job.BlankItemCode, InventoryService.StoreStation, job.ExtraBlanks, StockMovementReason.Production, lot.LotNumber, cancellationToken);
        }

        job.Complete(now);
        logger.LogInformation("Lot {LotNumber} cut: {Bars} bar(s) used, {Pieces} piece(s) cut, {Extra} extra blank(s) into stock.",
            lot.LotNumber, job.Bars, job.PiecesToCut, job.ExtraBlanks);
    }

    /// <summary>一框剛卸到某站：還有工序就建立一筆排隊中的加工紀錄。由搬運完成事件在同一筆交易內呼叫。</summary>
    public void OnLotArrived(MaterialLot lot, DateTimeOffset now)
    {
        if (lot.Status == LotStatus.Queued)
        {
            db.Operations.Add(new OperationExecution
            {
                LotId = lot.Id,
                Lot = lot,
                StepSequence = lot.CurrentStepSequence!.Value,
                StationCode = lot.StationCode!,
                Quantity = lot.Quantity,
                QueuedAt = now,
            });
        }
    }

    /// <summary>新刀送到機邊（刀具配送任務完成）。由搬運完成事件在同一筆交易內呼叫；換刀要等刀具到壽命才開始。</summary>
    public async Task OnToolDeliveredAsync(string stationCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tool = await db.StationTools.SingleAsync(t => t.StationCode == stationCode, cancellationToken);
        tool.DeliverSpare(now);
        logger.LogInformation("Station {Station}: spare tool delivered.", stationCode);
    }

    /// <summary>
    /// 把加工推進到現在：逐件工序逐件完成、到達刀具壽命時換刀（或停機等刀）；整爐工序扣掉經過的時間、時間到整爐放行；
    /// 做完的框建立下一趟搬運，最後讓空閒的機台開始下一框、累積夠量的爐開爐。
    /// 逐件工序的每一件與每次換刀的時間點都由資料庫裡的時間推算，所以不管多久呼叫一次（包含後端停機之後）結果都一樣。
    /// 回傳這一輪是否有任何變更。
    /// </summary>
    public async Task<bool> AdvanceOperationsAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var steps = await db.ProcessSteps.AsNoTracking().OrderBy(s => s.Sequence).ToListAsync(cancellationToken);
        var settings = await db.ProductionSettings.AsNoTracking().SingleAsync(cancellationToken);
        var allocated = await AllocateWaitingMaterialAsync(steps, settings, now, cancellationToken);
        var tools = await db.StationTools.ToDictionaryAsync(t => t.StationCode, cancellationToken);
        var running = await db.Operations.Include(o => o.Lot)
            .Where(o => o.Status == OperationStatus.Running)
            .ToListAsync(cancellationToken);

        foreach (var operation in running.Where(o => o.BatchRunId is null))
        {
            // 沒有刀具的工序（拋光）不計刀具壽命，也不會換刀。
            var tool = tools.GetValueOrDefault(operation.StationCode);
            var step = steps.Single(s => s.Sequence == operation.StepSequence);
            var speed = settings.SpeedOf(step);

            while (operation.Status == OperationStatus.Running)
            {
                if (tool is not null)
                {
                    // 停機等刀：新刀送到的那一刻才開始換刀。
                    if (tool.IsWaitingForSpare && !TryStartChangeAfterWaiting(tool, settings, speed))
                    {
                        break;
                    }

                    if (tool.ChangeDueAt is { } changeDue)
                    {
                        if (changeDue > now)
                        {
                            break;
                        }

                        // 換刀結束：目前這一件從換刀完成的時間重新開始。
                        FinishToolChange(tool);
                        operation.ResumeAt(changeDue);
                    }
                }

                var pieceDue = operation.PieceDueAt(speed, settings.SpeedChangedAt);
                if (pieceDue > now)
                {
                    break;
                }

                operation.CompletePiece(pieceDue);
                if (tool is null)
                {
                    continue;
                }

                tool.CountPiece();
                if (tool.PiecesSinceChange >= step.ToolLifePieces)
                {
                    if (!settings.ToolDeliveryEnabled || tool.SpareDeliveredAt is not null)
                    {
                        tool.StartChange(pieceDue, settings.ToolChangeSeconds / speed);
                        logger.LogInformation("Station {Station}: tool reached {Pieces} piece(s); changing for {Seconds}s.",
                            tool.StationCode, tool.PiecesSinceChange, settings.ToolChangeSeconds);
                    }
                    else
                    {
                        tool.WaitForSpare(pieceDue);
                        logger.LogWarning("Station {Station}: tool reached {Pieces} piece(s) but the spare has not arrived; machine stopped.",
                            tool.StationCode, tool.PiecesSinceChange);
                    }
                }
            }

            if (operation.Status == OperationStatus.Completed)
            {
                if (step.IsInspection)
                {
                    await InspectAsync(operation, steps, settings, now, cancellationToken);
                }
                else if (step.IsCutting)
                {
                    await FinishCuttingAsync(operation, now, cancellationToken);
                    Release(operation, steps, settings, now);
                }
                else if (step.IsPacking)
                {
                    // 包完的框留在包裝站，等同批次到齊（BoxPackedBatchesAsync）。
                    operation.Lot.FinishPacking(now);
                    logger.LogInformation("Lot {LotNumber} packed at {Station}; waiting for the rest of batch {Batch}.",
                        operation.Lot.LotNumber, operation.StationCode, operation.Lot.BatchNumber);
                }
                else
                {
                    Release(operation, steps, settings, now);
                }
            }
        }

        await AdvanceBatchRunsAsync(running, steps, settings, now, cancellationToken);

        // 沒有在加工的機台，等刀與換刀也要往前走（上一框最後一件剛好用完刀具的情況）。
        foreach (var tool in tools.Values)
        {
            if (tool.IsWaitingForSpare)
            {
                TryStartChangeAfterWaiting(tool, settings, settings.SpeedOf(steps.Single(s => s.StationCode == tool.StationCode)));
            }

            if (tool.ChangeDueAt <= now)
            {
                FinishToolChange(tool);
            }
        }

        if (settings.ToolDeliveryEnabled)
        {
            RequestSpareTools(steps, tools, settings, now);
        }

        var changed = db.ChangeTracker.HasChanges();
        await db.SaveChangesAsync(cancellationToken);
        changed |= await BoxPackedBatchesAsync(settings, now, cancellationToken);
        changed |= await BoxOrdersAsync(settings, now, cancellationToken);
        changed |= await ReceiveSurplusAsync(now, cancellationToken);
        changed |= await AdvanceTruckAsync(settings, now, cancellationToken);
        var started = await StartQueuedOperationsAsync(steps, tools, settings, now, cancellationToken);
        return await StartBatchRunsAsync(steps, settings, now, cancellationToken) || started || changed || allocated;
    }

    /// <summary>
    /// 整批一箱（使用者 2026-10-10），每一種零件各一箱：同批次同一種零件的每一框都包完了（或已經報廢），
    /// 就把包好的併成一箱，建立送往出貨站的搬運。還有框在前面的工序（包含反工中、補做中的）就繼續等。
    /// 報廢的料會自動補做，所以裝箱時這一種零件的數量會回到批次原本的組數。
    /// </summary>
    private async Task<bool> BoxPackedBatchesAsync(ProductionSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // 有訂單的框照訂單出貨（BoxOrdersAsync）；這裡只處理沒有訂單的批次。
        var groups = await db.Lots.Where(l => l.Status == LotStatus.Packed && l.ProductionOrderId == null)
            .Select(l => new { l.BatchNumber, l.PartCode }).Distinct().ToListAsync(cancellationToken);
        var boxed = false;
        foreach (var group in groups)
        {
            var batch = group.BatchNumber;
            // 還在流動的框只查有沒有、不載入：它們同時會被搬運事件修改，載入後留在這個 DbContext 裡的會是舊狀態。
            var outstanding = await db.Lots.AnyAsync(
                l => l.BatchNumber == batch && l.PartCode == group.PartCode && !l.IsBox
                    && l.Status != LotStatus.Packed && l.Status != LotStatus.Scrapped && l.Status != LotStatus.Boxed,
                cancellationToken);
            if (outstanding)
            {
                continue;
            }

            var packed = await db.Lots
                .Where(l => l.BatchNumber == batch && l.PartCode == group.PartCode && l.Status == LotStatus.Packed)
                .OrderBy(l => l.LotNumber)
                .ToListAsync(cancellationToken);
            var box = MaterialLot.PackBox(packed, now);
            var station = box.StationCode!;
            db.Lots.Add(box);
            db.InventoryMovements.Add(InventoryMovement.Record(box, LocationCode.Station(station), "Boxed", now));
            foreach (var bin in packed)
            {
                bin.MoveIntoBox(now);
            }

            db.TransportTasks.Add(TransportTask.Create(box, station, settings.BufferStationCode, TransportTaskKind.WipTransfer, now));
            boxed = true;
            logger.LogInformation("Batch {Batch}: {Bins} packed bin(s) boxed as {LotNumber} ({Quantity} piece(s)); transport to {Destination} requested.",
                batch, packed.Count, box.LotNumber, box.Quantity, settings.BufferStationCode);
        }

        await db.SaveChangesAsync(cancellationToken);
        return boxed;
    }

    /// <summary>
    /// 貨車定時來（使用者 2026-10-10）：倒數到了就把出貨站上整批到齊的出貨箱載走，寫入出貨紀錄與位置異動。
    /// 整批到齊才發貨（使用者 2026-10-10）：同批次每一種零件的箱子都到了出貨站才載，缺任何一種就留到下一班。
    /// 沒有可以載的箱子時貨車空車離開，照樣開始倒數下一班。
    /// </summary>
    private async Task<bool> AdvanceTruckAsync(ProductionSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var truck = await db.TruckSchedules.SingleAsync(cancellationToken);
        var shipped = false;
        if (truck.Accrue(now, settings.TruckSpeed, settings.TruckIntervalSeconds))
        {
            var boxes = await ReadyBoxes(db.Lots, settings.BufferStationCode).ToListAsync(cancellationToken);
            boxes.AddRange(await ShipReadyDeliveriesAsync(settings, now, cancellationToken));
            foreach (var box in boxes)
            {
                box.Ship(now);
                db.Shipments.Add(new Shipment
                {
                    LotId = box.Id, BatchNumber = box.BatchNumber, PartCode = box.PartCode, LotNumber = box.LotNumber, Quantity = box.Quantity, ShippedAt = now,
                });
                db.InventoryMovements.Add(new InventoryMovement
                {
                    LotId = box.Id,
                    Quantity = box.Quantity,
                    From = box.Location,
                    To = InventoryMovement.ShippedLocation,
                    Reason = "Shipped",
                    OccurredAt = now,
                });
            }

            shipped = boxes.Count > 0;
            logger.LogInformation("Truck arrived at {Station}: {Boxes} box(es), {Quantity} piece(s) shipped.",
                settings.BufferStationCode, boxes.Count, boxes.Sum(b => b.Quantity));
        }

        await db.SaveChangesAsync(cancellationToken);
        return shipped;
    }

    /// <summary>
    /// 在出貨站、而且整批已經到齊的出貨箱：同批次沒有任何一筆還在製程中、在車上，或包好了還沒送到出貨站。
    /// 也就是批次裡除了這些箱子以外，只剩下已裝箱（空了）的料框與報廢的框。
    /// </summary>
    private static IQueryable<MaterialLot> ReadyBoxes(IQueryable<MaterialLot> lots, string shippingStation) =>
        lots.Where(box => box.IsBox && box.DeliveryId == null && box.Status == LotStatus.Staged && box.StationCode == shippingStation
            && !lots.Any(other => other.BatchNumber == box.BatchNumber
                && other.Status != LotStatus.Boxed && other.Status != LotStatus.Scrapped && other.Status != LotStatus.Shipped
                && !(other.IsBox && other.Status == LotStatus.Staged && other.StationCode == shippingStation)));

    /// <summary>整框（或整爐）完成才放行：改為等待搬運，並建立送往下一站（或暫存點）的任務。</summary>
    private void Release(OperationExecution operation, IReadOnlyList<ProcessStep> steps, ProductionSettings settings, DateTimeOffset now)
    {
        var next = steps.FirstOrDefault(s => s.Sequence > operation.StepSequence);
        var destination = next?.StationCode ?? settings.BufferStationCode;
        operation.Lot.FinishProcessing(next?.Sequence, now);
        db.TransportTasks.Add(TransportTask.Create(operation.Lot, operation.StationCode, destination, TransportTaskKind.WipTransfer, now));
        logger.LogInformation("Lot {LotNumber} finished step {Step} at {Station}; transport to {Next} requested.",
            operation.Lot.LotNumber, operation.StepSequence, operation.StationCode, destination);
    }

    /// <summary>
    /// 品管檢完一框：逐支判定，然後在品管站分框。
    /// 合格的往下一站（或暫存點）；表面不良回拋光、鍍層不良回電鍍，各自另裝一框；尺寸不良報廢。
    /// 這一框已經反工到上限的話，再被判不良一律報廢（避免無限循環）。同一筆交易內寫入檢驗結果、分出來的框、位置異動與搬運任務。
    /// 原本的框盡量留給合格的料；沒有合格的就留給第一種要反工的料；全部報廢時整框標記為報廢。
    /// 缺的自動補做（使用者 2026-10-10）：報廢幾支，當下就在原料倉補投同樣數量的同一種零件，重走整條製程。
    /// </summary>
    private async Task InspectAsync(
        OperationExecution operation, IReadOnlyList<ProcessStep> steps, ProductionSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lot = operation.Lot;
        var station = operation.StationCode;
        var counts = ReworkRoutes.ToDictionary(r => r.Reason, _ => 0);
        for (var piece = 0; piece < operation.Quantity; piece++)
        {
            if (defects.Inspect(settings.DefectRate) is { } reason)
            {
                counts[reason]++;
            }
        }

        // 每種原因要回到哪一道工序；不能反工的原因、反工次數用完、或路線上沒有那一站，都是報廢。
        var canRework = lot.ReworkCount < settings.MaxReworkCount;
        var rework = new List<(int Quantity, ProcessStep Step, string Suffix)>();
        var scrapped = 0;
        foreach (var (reason, stationCode, suffix) in ReworkRoutes)
        {
            var target = stationCode is null ? null : steps.FirstOrDefault(s => s.StationCode == stationCode);
            if (counts[reason] == 0)
            {
                continue;
            }

            if (canRework && target is not null)
            {
                rework.Add((counts[reason], target, suffix));
            }
            else
            {
                scrapped += counts[reason];
            }
        }

        var defective = counts.Values.Sum();
        var passed = operation.Quantity - defective;
        db.InspectionResults.Add(new InspectionResult
        {
            LotId = lot.Id,
            StationCode = station,
            ReworkRound = lot.ReworkCount,
            Inspected = operation.Quantity,
            Passed = passed,
            Reworked = defective - scrapped,
            Scrapped = scrapped,
            SurfaceDefects = counts[DefectReason.Surface],
            PlatingDefects = counts[DefectReason.Plating],
            DimensionDefects = counts[DefectReason.Dimension],
            CompletedAt = now,
        });

        if (scrapped > 0)
        {
            db.InventoryMovements.Add(new InventoryMovement
            {
                LotId = lot.Id,
                Quantity = scrapped,
                From = LocationCode.Station(station),
                To = InventoryMovement.ScrapLocation,
                Reason = "Scrapped",
                OccurredAt = now,
            });

            // 有規格的料：補做也要有實際的備料或圓棒，先在原料倉等料，下一輪由備料分配決定從哪一站開始。
            var cutting = steps.FirstOrDefault(s => s.IsCutting);
            var first = steps.FirstOrDefault(s => !s.IsCutting);
            var needsMaterial = lot.SpecCode is not null && cutting is not null;
            // 有工單的料：在主工單上註記報廢；先用開工時多做的抵，不夠抵的才另開補做工單（使用者 2026-10-22），補做的框屬於補做工單。
            var (makeupOrder, makeupQuantity) = lot.ProductionOrderId is { } orderId
                ? await RecordScrapAsync(orderId, lot, scrapped, now, cancellationToken)
                : (null, scrapped);
            if (makeupQuantity > 0)
            {
                var makeup = MaterialLot.ReceiveMakeup(
                    lot, makeupQuantity, settings.SourceStationCode, needsMaterial ? cutting!.Sequence : first?.Sequence, now,
                    awaitingMaterial: needsMaterial, productionOrderId: makeupOrder?.Id);
                db.Lots.Add(makeup);
                db.InventoryMovements.Add(InventoryMovement.Record(makeup, from: null, "MakeupReceived", now));
                if (!needsMaterial)
                {
                    db.TransportTasks.Add(TransportTask.Create(
                        makeup, settings.SourceStationCode, first?.StationCode ?? settings.BufferStationCode, TransportTaskKind.Replenishment, now));
                }

                logger.LogInformation("Lot {LotNumber}: {Scrapped} piece(s) scrapped; make-up lot {Makeup} of {Quantity} received at {Station}.",
                    lot.LotNumber, scrapped, makeup.LotNumber, makeupQuantity, settings.SourceStationCode);
            }
        }

        var reworkRound = lot.ReworkCount + 1;
        var next = steps.FirstOrDefault(s => s.Sequence > operation.StepSequence);
        var keptByLot = false;
        if (passed > 0)
        {
            lot.FinishInspection(passed, next?.Sequence, rework: false, now);
            db.TransportTasks.Add(TransportTask.Create(lot, station, next?.StationCode ?? settings.BufferStationCode, TransportTaskKind.WipTransfer, now));
            keptByLot = true;
        }

        foreach (var (quantity, target, suffix) in rework)
        {
            var bin = lot;
            if (keptByLot)
            {
                bin = MaterialLot.SplitForRework(lot, quantity, suffix, target.Sequence, reworkRound, now);
                db.Lots.Add(bin);
                db.InventoryMovements.Add(InventoryMovement.Record(bin, LocationCode.Station(station), "ReworkSplit", now));
            }
            else
            {
                lot.FinishInspection(quantity, target.Sequence, rework: true, now);
                keptByLot = true;
            }

            db.TransportTasks.Add(TransportTask.Create(bin, station, target.StationCode, TransportTaskKind.WipTransfer, now));
        }

        if (!keptByLot)
        {
            lot.ScrapAll(now);
        }

        logger.LogInformation("Lot {LotNumber} inspected at {Station}: {Passed} passed, {Reworked} to rework, {Scrapped} scrapped.",
            lot.LotNumber, station, passed, defective - scrapped, scrapped);
    }

    /// <summary>
    /// 報廢了 <paramref name="scrapped"/> 支：在主工單註記。先用主工單開工時多做的抵（每種零件各自算），
    /// 不夠抵的才另開一張補做工單（號碼是主工單號碼加 -R 與第幾張）。補做工單裡又報廢時，一樣記在主工單、需要時再開一張，
    /// 連回主工單（使用者 2026-10-22）。補做工單的規格沿用主工單開立時存下的那一份；BOM 用那個零件目前的版本。
    /// 回傳開的補做工單（全部抵掉時是 null）與要補做的數量。
    /// </summary>
    private async Task<(ProductionOrder? Makeup, int Quantity)> RecordScrapAsync(
        Guid orderId, MaterialLot lot, int scrapped, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var source = await db.ProductionOrders.SingleAsync(o => o.Id == orderId, cancellationToken);
        var main = source.ParentId is { } parentId ? await db.ProductionOrders.SingleAsync(o => o.Id == parentId, cancellationToken) : source;

        // 這張主工單、這種零件已經用掉多少多做的（含同一輪還沒存檔的）。
        var used = await db.ScrapRecords.Where(r => r.MainOrderId == main.Id && r.PartCode == lot.PartCode)
                .SumAsync(r => r.CoveredByExtra, cancellationToken)
            + db.ChangeTracker.Entries<ScrapRecord>()
                .Where(e => e.State == EntityState.Added && e.Entity.MainOrderId == main.Id && e.Entity.PartCode == lot.PartCode)
                .Sum(e => e.Entity.CoveredByExtra);
        var covered = Math.Min(scrapped, Math.Max(0, main.ExtraQuantity - used));
        var makeupQuantity = scrapped - covered;

        ProductionOrder? makeup = null;
        if (makeupQuantity > 0)
        {
            // 同一輪可能已經替同一張主工單開過補做工單（還沒存檔），一起算進編號。
            var saved = await db.ProductionOrders.CountAsync(o => o.ParentId == main.Id, cancellationToken);
            var pending = db.ChangeTracker.Entries<ProductionOrder>().Count(e => e.State == EntityState.Added && e.Entity.ParentId == main.Id);
            var partItem = CatalogService.PartItemCode(main.Spec.SpecCode, lot.PartCode);
            var bomVersion = await db.BomVersions.Where(b => b.ItemCode == partItem).MaxAsync(b => (int?)b.Version, cancellationToken) ?? 1;
            makeup = ProductionOrder.OpenMakeup(main, saved + pending + 1, lot.PartCode, partItem, makeupQuantity, bomVersion, now);
            db.ProductionOrders.Add(makeup);
        }

        db.ScrapRecords.Add(ScrapRecord.Create(main, source, makeup, lot, scrapped, covered, now));
        logger.LogInformation("Work order {Main}: {Scrapped} piece(s) of {Part} scrapped in {Lot}; {Covered} covered by extra, make-up order {Makeup}.",
            main.Number, scrapped, lot.PartCode, lot.LotNumber, covered, makeup?.Number ?? "none");
        return (makeup, makeupQuantity);
    }

    /// <summary>
    /// 每件加工時間：粗加工與精加工如果工單的規格有覆寫（例如六角的精加工 90 秒），用工單開立時存下的值；
    /// 沒有覆寫的、或沒有工單的框，用製程設定的現值。
    /// </summary>
    private async Task<double> SecondsPerPieceAsync(ProcessStep step, MaterialLot lot, CancellationToken cancellationToken)
    {
        var isRough = step.StationCode == CatalogService.RoughStationCode;
        if (lot.ProductionOrderId is not { } orderId || !(isRough || step.StationCode == CatalogService.FinishStationCode))
        {
            return step.SecondsPerPiece;
        }

        var spec = await db.ProductionOrders.AsNoTracking().Where(o => o.Id == orderId)
            .Select(o => new { o.Spec.RoughSecondsPerPiece, o.Spec.FinishSecondsPerPiece })
            .SingleAsync(cancellationToken);
        return (isRough ? spec.RoughSecondsPerPiece : spec.FinishSecondsPerPiece) ?? step.SecondsPerPiece;
    }

    /// <summary>
    /// 進行中的爐：扣掉這段時間走掉的工序時間（實際秒數 × 這一站的倍率）。用累計的方式，
    /// 因為一爐長達數小時，倍率中途改變時不能像逐件工序那樣從頭計時。時間到就整爐完成，裡面的每一框各自放行。
    /// </summary>
    private async Task AdvanceBatchRunsAsync(
        IReadOnlyList<OperationExecution> running, IReadOnlyList<ProcessStep> steps, ProductionSettings settings,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var runs = await db.BatchRuns.Where(r => r.CompletedAt == null).ToListAsync(cancellationToken);
        foreach (var run in runs)
        {
            run.Accrue(now, settings.SpeedOf(steps.Single(s => s.Sequence == run.StepSequence)));
            if (run.IsRunning)
            {
                continue;
            }

            foreach (var operation in running.Where(o => o.BatchRunId == run.Id))
            {
                operation.CompleteBatch(now);
                Release(operation, steps, settings, now);
            }

            logger.LogInformation("Station {Station}: batch run of {Quantity} piece(s) finished.", run.StationCode, run.Quantity);
        }
    }

    /// <summary>
    /// 整爐工序開爐：這一站沒有在跑，而且排隊中的支數達到下限。照到站順序裝進去，裝到上限為止；
    /// 一框不拆開，放不下的那一框留到下一爐。不同批次可以混在同一爐（使用者 2026-10-10），出爐後仍是各自的框。
    /// 上限依桿徑不同的工序（電鍍）：每一框占「支數 ÷ 該桿徑的上限」，加起來不超過一爐。
    /// 湊不到下限就一直等，不設逾時（使用者 2026-10-10）。兩個例外（使用者 2026-10-10）：
    /// 補做的料：等的料裡有補做品、而且已經沒有其他料會再來這一站（前面的工序都空了），不滿下限也開爐。
    /// 反工品（回來重鍍的）：另外累積，滿 <see cref="ProductionSettings.ReworkBatchMinPieces"/> 支才放行；
    /// 廠內已經沒有其他料會再進電鍍（品管之前的工序除了這些反工品都空了）時，不足的尾料也放行。放行後和一般的料一樣裝爐。
    /// </summary>
    private async Task<bool> StartBatchRunsAsync(
        IReadOnlyList<ProcessStep> steps, ProductionSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var batchSteps = steps.Where(s => s.Kind == ProcessStepKind.Batch).ToList();
        if (batchSteps.Count == 0)
        {
            return false;
        }

        var stationCodes = batchSteps.Select(s => s.StationCode).ToList();
        var busy = await db.BatchRuns.Where(r => r.CompletedAt == null).Select(r => r.StationCode).ToListAsync(cancellationToken);
        var queued = await db.Operations.Include(o => o.Lot)
            .Where(o => o.Status == OperationStatus.Queued && stationCodes.Contains(o.StationCode))
            .OrderBy(o => o.QueuedAt)
            .ToListAsync(cancellationToken);
        var started = false;
        if (queued.Count == 0)
        {
            return false;
        }

        // 還在製程中的每一框目前在哪一道工序（走完的、報廢的、出貨箱沒有工序）。
        var active = await db.Lots.AsNoTracking()
            .Where(l => l.CurrentStepSequence != null)
            .Select(l => new { l.Id, Step = l.CurrentStepSequence!.Value })
            .ToListAsync(cancellationToken);
        var inspection = steps.FirstOrDefault(s => s.IsInspection)?.Sequence ?? int.MaxValue;

        foreach (var step in batchSteps.Where(s => !busy.Contains(s.StationCode)))
        {
            var arrived = queued.Where(o => o.StepSequence == step.Sequence).ToList();
            var arrivedIds = arrived.Select(o => o.LotId).ToHashSet();

            // 反工品先扣住，累積夠了或沒有其他料會再進來才放行。
            var rework = arrived.Where(o => o.Lot.ReworkCount > 0).ToList();
            var reworkIds = rework.Select(o => o.LotId).ToHashSet();
            var reworkReleased = rework.Sum(o => o.Quantity) >= settings.ReworkBatchMinPieces
                || !active.Any(l => l.Step <= inspection && !reworkIds.Contains(l.Id));
            var waiting = reworkReleased ? arrived : arrived.Except(rework).ToList();

            var moreComing = active.Any(l => l.Step <= step.Sequence && !arrivedIds.Contains(l.Id));
            var enough = waiting.Sum(o => o.Quantity) >= step.BatchMinPieces
                || (waiting.Any(o => o.Lot.IsMakeup) && !moreComing);
            if (!enough)
            {
                continue;
            }

            var load = new List<OperationExecution>();
            var used = 0d;
            foreach (var operation in waiting)
            {
                var share = (double)operation.Quantity / step.BatchCapacityFor(operation.Lot.RodDiameterMm);
                if (used + share <= 1 + 1e-9)
                {
                    load.Add(operation);
                    used += share;
                }
            }

            if (load.Count == 0)
            {
                continue;
            }

            var pieces = load.Sum(o => o.Quantity);
            var run = BatchRun.Start(step.StationCode, step.Sequence, pieces, step.BatchSeconds, now);
            db.BatchRuns.Add(run);
            foreach (var operation in load)
            {
                operation.StartInBatch(run.Id, now);
                operation.Lot.StartProcessing(now);
            }

            started = true;
            logger.LogInformation("Station {Station}: batch run started with {Quantity} piece(s) in {Bins} bin(s), {Seconds}s.",
                step.StationCode, pieces, load.Count, run.TotalSeconds);
        }

        await db.SaveChangesAsync(cancellationToken);
        return started;
    }

    /// <summary>新刀已送到就從送到的時間開始換刀（不會早於開始等待的時間）；還沒到回傳 false。</summary>
    private bool TryStartChangeAfterWaiting(StationTool tool, ProductionSettings settings, double speed)
    {
        if (tool.SpareDeliveredAt is not { } delivered || tool.WaitingSince is not { } waitingSince)
        {
            return false;
        }

        tool.StartChange(delivered > waitingSince ? delivered : waitingSince, settings.ToolChangeSeconds / speed);
        logger.LogInformation("Station {Station}: spare tool arrived; changing for {Seconds}s.", tool.StationCode, settings.ToolChangeSeconds);
        return true;
    }

    /// <summary>
    /// 刀具剩餘件數低於門檻、而且還沒叫過刀的機台：建立一筆刀具配送任務（刀具庫 → 機邊），由小型車執行。
    /// 一把刀只叫一次；換刀完成後備用刀用掉了，下一輪再依門檻判斷。
    /// </summary>
    private void RequestSpareTools(
        IReadOnlyList<ProcessStep> steps, IReadOnlyDictionary<string, StationTool> tools, ProductionSettings settings, DateTimeOffset now)
    {
        foreach (var step in steps.Where(s => s.UsesTool))
        {
            var tool = tools[step.StationCode];
            var remaining = step.ToolLifePieces - tool.PiecesSinceChange;
            if (tool.SpareRequestedAt is not null || tool.IsChanging || remaining > settings.ToolRequestThresholdPieces)
            {
                continue;
            }

            tool.RequestSpare(now);
            db.TransportTasks.Add(TransportTask.CreateToolDelivery(settings.ToolStoreStationCode, step.StationCode, now));
            logger.LogInformation("Station {Station}: {Remaining} piece(s) of tool life left; spare tool requested from {Store}.",
                step.StationCode, remaining, settings.ToolStoreStationCode);
        }
    }

    /// <summary>逐件工序：空閒的機台開始下一框。</summary>
    private async Task<bool> StartQueuedOperationsAsync(
        IReadOnlyList<ProcessStep> steps, IReadOnlyDictionary<string, StationTool> tools, ProductionSettings settings,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pieceStations = steps.Where(s => s.Kind == ProcessStepKind.Piece).Select(s => s.StationCode).ToList();
        var queued = await db.Operations.Include(o => o.Lot)
            .Where(o => o.Status == OperationStatus.Queued && pieceStations.Contains(o.StationCode))
            .OrderBy(o => o.QueuedAt)
            .ToListAsync(cancellationToken);
        if (queued.Count == 0)
        {
            return false;
        }

        var stationCodes = queued.Select(o => o.StationCode).Distinct().ToList();
        var busyStations = await db.Operations
            .Where(o => o.Status == OperationStatus.Running)
            .Select(o => o.StationCode)
            .ToListAsync(cancellationToken);

        // 做完的那一框還沒被搬走的機台不開始下一框：成品區只放一框。
        var blockedStations = await db.Lots
            .Where(l => l.Status == LotStatus.AwaitingTransport && stationCodes.Contains(l.StationCode!))
            .Select(l => l.StationCode!)
            .ToListAsync(cancellationToken);

        var unavailable = busyStations
            .Concat(blockedStations)
            .Concat(tools.Values.Where(t => t.IsStopped).Select(t => t.StationCode))
            .ToHashSet(StringComparer.Ordinal);
        var started = false;

        foreach (var station in stationCodes.Where(code => !unavailable.Contains(code)))
        {
            // 這一站最近加工的是哪個批次的哪一種零件：同批次同零件的框優先接著做，做完的才會放在一起
            // （使用者 2026-10-10 對拋光的要求，其他逐件工序也適用）。
            // 接下來機床（有刀具的工序）先找不用換線的（同零件同桿徑），其他站先找同批次的；最後照到站順序。
            var last = await db.Operations.AsNoTracking()
                .Where(o => o.StationCode == station && o.StartedAt != null)
                .OrderByDescending(o => o.StartedAt)
                .Select(o => new { o.Lot.BatchNumber, o.Lot.PartCode, o.Lot.RodDiameterMm })
                .FirstOrDefaultAsync(cancellationToken);
            var waiting = queued.Where(o => o.StationCode == station).ToList();
            var isLathe = steps.Single(s => s.StationCode == station).UsesTool;
            bool SameSetup(OperationExecution o) => o.Lot.PartCode == last?.PartCode && o.Lot.RodDiameterMm == last?.RodDiameterMm;
            bool SameBatch(OperationExecution o) => o.Lot.BatchNumber == last?.BatchNumber;
            var operation = waiting.FirstOrDefault(o => SameBatch(o) && SameSetup(o))
                ?? waiting.FirstOrDefault(isLathe ? SameSetup : SameBatch)
                ?? waiting.FirstOrDefault(isLathe ? SameBatch : SameSetup)
                ?? waiting.FirstOrDefault(o => o.Lot.RodDiameterMm == last?.RodDiameterMm)
                ?? waiting[0];

            // 機床換線（使用者 2026-10-10）：24 mm 與 30 mm 的工控程式不同，桿徑和上一框不同就要先換；
            // 上沖、中模、下沖之間切換也要換線。
            // 「機床」以有刀具的工序判斷（粗加工、精加工）；拋光不換線。這一站的第一框不用換。
            var step = steps.Single(s => s.Sequence == operation.StepSequence);
            if (step.IsCutting && await db.CuttingJobs.AsNoTracking().SingleOrDefaultAsync(j => j.LotId == operation.LotId, cancellationToken) is { } job)
            {
                // 切料站切的是整根圓棒：最後一框連多出來的一起切。
                operation.Quantity = job.PiecesToCut;
            }

            var changeover = step.UsesTool && last is not null && !SameSetup(operation)
                ? settings.ChangeoverSeconds / settings.SpeedOf(step)
                : 0;
            var secondsPerPiece = await SecondsPerPieceAsync(step, operation.Lot, cancellationToken);
            // 品管與包裝是人工逐支作業，沒有機台取放的時間。
            operation.Start(secondsPerPiece, step.IsManual ? 0 : settings.PieceHandlingSeconds, changeover, now);
            operation.Lot.StartProcessing(now);
            started = true;
            logger.LogInformation("Lot {LotNumber} started step {Step} at {Station}: {Quantity} piece(s), {Seconds}s each, changeover {Changeover:0.#}s.",
                operation.Lot.LotNumber, operation.StepSequence, operation.StationCode, operation.Quantity, secondsPerPiece, changeover);
        }

        await db.SaveChangesAsync(cancellationToken);
        return started;
    }

    /// <summary>
    /// 每個站目前的物料與設備狀態，供顯示端畫備料區、成品區、加工、整爐與刀具的進度條。唯讀查詢。
    /// </summary>
    public async Task<List<StationStatusDto>> GetStationStatusAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var stations = await db.Stations.AsNoTracking().OrderBy(s => s.Code).ToListAsync(cancellationToken);
        var steps = await db.ProcessSteps.AsNoTracking().ToDictionaryAsync(s => s.StationCode, cancellationToken);
        var tools = await db.StationTools.AsNoTracking().ToDictionaryAsync(t => t.StationCode, cancellationToken);
        var running = await db.Operations.AsNoTracking()
            .Where(o => o.Status == OperationStatus.Running && o.BatchRunId == null)
            .Select(o => new { o.StationCode, o.Lot.LotNumber, o.PiecesCompleted, o.Quantity, o.ChangeoverStartedAt, o.ChangeoverDueAt })
            .ToDictionaryAsync(o => o.StationCode, cancellationToken);
        var runs = await db.BatchRuns.AsNoTracking()
            .Where(r => r.CompletedAt == null)
            .ToDictionaryAsync(r => r.StationCode, cancellationToken);
        var lots = await db.Lots.AsNoTracking()
            .Where(l => l.StationCode != null && l.Status != LotStatus.Processing)
            .GroupBy(l => new { l.StationCode, l.Status })
            .Select(g => new { g.Key.StationCode, g.Key.Status, Quantity = g.Sum(l => l.Quantity) })
            .ToListAsync(cancellationToken);

        var shortages = await db.MachineDelays.AsNoTracking()
            .Where(d => d.EndedAt == null)
            .ToDictionaryAsync(d => d.StationCode, d => new { d.Cause, d.Reason }, cancellationToken);
        var settings = await db.ProductionSettings.AsNoTracking().SingleAsync(cancellationToken);
        var truck = await db.TruckSchedules.AsNoTracking().SingleAsync(cancellationToken);

        int Sum(string station, LotStatus status) =>
            lots.Where(l => l.StationCode == station && l.Status == status).Sum(l => l.Quantity);

        return stations.Select(station =>
        {
            var step = steps.GetValueOrDefault(station.Code);
            var machining = running.GetValueOrDefault(station.Code);
            var status = new StationStatusDto
            {
                Code = station.Code,
                Name = station.Name,
                IsMachine = step is not null,
                IsInspection = step is { IsInspection: true },
                IsPacking = step is { IsPacking: true },
                // 加工中的那一框：還沒做的件數在備料區，做完的件數在成品區。爐裡的不算在兩邊。
                InboundQuantity = Sum(station.Code, LotStatus.Queued) + Sum(station.Code, LotStatus.Staged)
                    + (machining is null ? 0 : machining.Quantity - machining.PiecesCompleted),
                InboundCapacity = station.InboundCapacity,
                // 包裝站：包好、在等同批次到齊的框也算在成品區。
                OutboundQuantity = Sum(station.Code, LotStatus.AwaitingTransport) + Sum(station.Code, LotStatus.Packed) + (machining?.PiecesCompleted ?? 0),
                OutboundCapacity = station.OutboundCapacity,
                Machining = machining is null ? null : new MachiningStatusDto
                {
                    LotNumber = machining.LotNumber,
                    PiecesCompleted = machining.PiecesCompleted,
                    Quantity = machining.Quantity,
                    ChangingOver = machining.ChangeoverDueAt > now,
                    ChangeoverProgress = machining.ChangeoverDueAt > now ? Progress(machining.ChangeoverStartedAt, machining.ChangeoverDueAt, now) : 0,
                },
                ShortageCause = shortages.GetValueOrDefault(station.Code)?.Cause,
                ShortageReason = shortages.GetValueOrDefault(station.Code)?.Reason,
            };

            if (step is { Kind: ProcessStepKind.Batch })
            {
                var run = runs.GetValueOrDefault(station.Code);
                status.Batch = new BatchStatusDto
                {
                    MinPieces = step.BatchMinPieces,
                    MaxPieces = step.BatchMaxPieces,
                    Running = run is not null,
                    Quantity = run?.Quantity ?? 0,
                    StageName = run is null ? null : step.StageName,
                    StageProgress = run?.Progress ?? 0,
                    RemainingSeconds = run?.RemainingSeconds ?? 0,
                };
            }

            if (station.Code == settings.BufferStationCode)
            {
                status.Truck = new TruckStatusDto
                {
                    RemainingSeconds = truck.RemainingSeconds,
                    Progress = settings.TruckIntervalSeconds <= 0 ? 0 : Math.Clamp(1 - truck.RemainingSeconds / settings.TruckIntervalSeconds, 0, 1),
                };
            }

            if (step is { UsesTool: true } && tools.TryGetValue(station.Code, out var tool))
            {
                status.Tool = new ToolStatusDto
                {
                    PiecesUsed = tool.PiecesSinceChange,
                    LifePieces = step.ToolLifePieces,
                    Changing = tool.IsChanging,
                    ChangeProgress = Progress(tool.ChangeStartedAt, tool.ChangeDueAt, now),
                    Spare = tool.SpareDeliveredAt is not null ? ToolSpareStatus.Delivered
                        : tool.SpareRequestedAt is not null ? ToolSpareStatus.Requested
                        : ToolSpareStatus.None,
                    WaitingForTool = tool.IsWaitingForSpare,
                };
            }

            return status;
        }).ToList();
    }

    public async Task<InspectionSummaryDto> GetInspectionSummaryAsync(CancellationToken cancellationToken)
    {
        var summary = await db.InspectionResults.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new InspectionSummaryDto
            {
                Inspected = g.Sum(r => r.Inspected),
                Passed = g.Sum(r => r.Passed),
                Reworked = g.Sum(r => r.Reworked),
                Scrapped = g.Sum(r => r.Scrapped),
                SurfaceDefects = g.Sum(r => r.SurfaceDefects),
                PlatingDefects = g.Sum(r => r.PlatingDefects),
                DimensionDefects = g.Sum(r => r.DimensionDefects),
            })
            .SingleOrDefaultAsync(cancellationToken);
        return summary ?? new InspectionSummaryDto();
    }

    public async Task<ShippingSummaryDto> GetShippingSummaryAsync(CancellationToken cancellationToken)
    {
        var settings = await db.ProductionSettings.AsNoTracking().SingleAsync(cancellationToken);
        var truck = await db.TruckSchedules.AsNoTracking().SingleAsync(cancellationToken);
        var waiting = await db.Lots.AsNoTracking()
            .Where(l => l.IsBox && l.Status == LotStatus.Staged && l.StationCode == settings.BufferStationCode)
            .Select(l => l.Quantity)
            .ToListAsync(cancellationToken);
        return new ShippingSummaryDto
        {
            TruckRemainingSeconds = truck.RemainingSeconds,
            TruckIntervalSeconds = settings.TruckIntervalSeconds,
            PackedPieces = await db.Lots.AsNoTracking().Where(l => l.Status == LotStatus.Packed).SumAsync(l => l.Quantity, cancellationToken),
            WaitingBoxes = waiting.Count,
            WaitingPieces = waiting.Sum(),
            ReadyBoxes = await ReadyBoxes(db.Lots.AsNoTracking(), settings.BufferStationCode).CountAsync(cancellationToken)
                + await CountReadyDeliveryBoxesAsync(settings, cancellationToken),
            ShippedBoxes = await db.Shipments.AsNoTracking().CountAsync(cancellationToken),
            ShippedPieces = await db.Shipments.AsNoTracking().SumAsync(s => s.Quantity, cancellationToken),
            Shipments = await db.Shipments.AsNoTracking().OrderByDescending(s => s.Id).Take(20)
                .Select(s => new ShipmentDto { BatchNumber = s.BatchNumber, PartCode = s.PartCode, LotNumber = s.LotNumber, Quantity = s.Quantity, ShippedAt = s.ShippedAt })
                .ToListAsync(cancellationToken),
        };
    }

    private void FinishToolChange(StationTool tool)
    {
        var record = tool.CompleteChange();
        db.ToolChanges.Add(record);
        logger.LogInformation("Station {Station}: tool changed after {Pieces} piece(s).", record.StationCode, record.PiecesUsed);
    }

    /// <summary>一段有起訖時間的作業（換刀、換線）做到哪裡，0～1；沒有在進行時為 0。</summary>
    private static double Progress(DateTimeOffset? startedAt, DateTimeOffset? dueAt, DateTimeOffset now)
    {
        if (startedAt is not { } started || dueAt is not { } due)
        {
            return 0;
        }

        var total = (due - started).TotalSeconds;
        return total <= 0 ? 1 : Math.Clamp((now - started).TotalSeconds / total, 0, 1);
    }

    internal static void ValidateQuantity(int quantity, int max, string field)
    {
        if (quantity < 1 || quantity > max)
        {
            throw new RequestValidationException(field, $"Quantity must be between 1 and {max}.");
        }
    }

    private static void ValidateRange(double value, double min, double max, string field)
    {
        // 寫成「不在範圍內」的否定，NaN 也會被擋下。
        if (!(value >= min && value <= max))
        {
            throw new RequestValidationException(field, $"Value must be between {min} and {max}.");
        }
    }
}
