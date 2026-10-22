using OpenFleet.Contracts;

namespace OpenFleet.Api.Data;

/// <summary>搬運的起點／終點。座標是交接點（AGV 停靠處），不是機台中心。</summary>
public sealed class Station
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public double HandoffX { get; set; }
    public double HandoffZ { get; set; }

    /// <summary>待加工料容量（件）。null＝這個站的容量還沒建模（P2 的倉庫與暫存點），不代表無限。</summary>
    public int? InboundCapacity { get; set; }

    /// <summary>
    /// 完工件容量（件），目前只用來畫成品區的進度條。完工件容量依桿徑分 30／45 的對照還沒定，先用 30。
    /// </summary>
    public int? OutboundCapacity { get; set; }
}

/// <summary>
/// 逐件工序的機台（人工作業的品管、包裝是一個工位）。使用者 2026-10-22：粗加工、精加工各兩台車床，其他站一台；
/// 同一站的機台都能做所有規格。同一站的機台共用一個交接點、備料區與成品區：AGV 把框送到站上，
/// 哪一台空下來就由它接下一框（<see cref="Production.MachineAssignment"/>）。每台各有自己的刀具、換線狀態與加工進度。
/// 整爐工序（淬火、回火、電鍍）一站一座爐，不用這張表。
/// </summary>
public sealed class Machine
{
    /// <summary>站點代碼加編號，例如 rough-1。</summary>
    public required string Code { get; set; }
    public required string StationCode { get; set; }

    /// <summary>這一站的第幾台，從 1 起。</summary>
    public int Number { get; set; }

    /// <summary>例如「粗加工 1 號機」；一站只有一台時就是站名。</summary>
    public required string Name { get; set; }

    public static string CodeOf(string stationCode, int number) => $"{stationCode}-{number}";
}

/// <summary>
/// 製程路線：依 Sequence 順序加工，每一站一道工序。只有一條路線，產品與版本留到之後。
/// 工序分逐件與整爐兩種，各用各的欄位。
/// </summary>
public sealed class ProcessStep
{
    public int Sequence { get; set; }
    public required string StationCode { get; set; }
    public ProcessStepKind Kind { get; set; }

    /// <summary>逐件工序：每加工一件的時間（秒）。示範設定，可由面板修改。</summary>
    public double SecondsPerPiece { get; set; }

    /// <summary>這道工序有沒有刀具（壽命、換刀、配送）。拋光與整爐工序沒有。</summary>
    public bool UsesTool { get; set; }

    /// <summary>
    /// 這道逐件工序是品管檢驗：逐支檢驗（<see cref="SecondsPerPiece"/> 是每支的檢驗時間，沒有機台取放），
    /// 整框檢完後依結果分成合格、反工、報廢，而不是整框送往下一站。
    /// </summary>
    public bool IsInspection { get; set; }

    /// <summary>
    /// 這道逐件工序是包裝：逐支包裝（<see cref="SecondsPerPiece"/> 是每支的包裝時間，沒有機台取放）。
    /// 包完的框留在包裝站，同批次的框全部包完（或報廢）才併成一箱送去出貨。
    /// </summary>
    public bool IsPacking { get; set; }

    /// <summary>
    /// 這道逐件工序是切料（使用者 2026-10-22：要有切料站，整根圓棒切完、多出來的備料入庫；切一支約 5 分鐘）。
    /// 從圓棒切的框才經過這一站；用現成備料的框直接從下一道工序開始。
    /// </summary>
    public bool IsCutting { get; set; }

    /// <summary>人工作業的工序（品管、包裝）沒有機台取放一件的時間。</summary>
    public bool IsManual => IsInspection || IsPacking;

    /// <summary>刀具壽命（件）：粗加工 500、精加工 250 是使用者提供的數值。</summary>
    public int ToolLifePieces { get; set; }

    /// <summary>整爐工序：累積到這麼多支才開爐，一爐最多 <see cref="BatchMaxPieces"/> 支。</summary>
    public int BatchMinPieces { get; set; }
    public int BatchMaxPieces { get; set; }

    /// <summary>
    /// 整爐工序：桿徑 24 mm 的一爐上限；0 表示不分桿徑、只算支數（淬火、回火）。
    /// 有值時 <see cref="BatchMaxPieces"/> 是 30 mm 的上限，兩種桿徑可以混裝
    /// （使用者 2026-10-10：電鍍一爐 30 mm 最多夾 40 支、24 mm 最多夾 56 支，可以混夾）。
    /// </summary>
    public int BatchMaxPiecesRod24 { get; set; }

    /// <summary>整爐工序：一爐的時間（秒）。</summary>
    public double BatchSeconds { get; set; }

    /// <summary>整爐工序的名稱，顯示用（淬火、回火、電鍍）。</summary>
    public string? StageName { get; set; }

    /// <summary>
    /// 某個桿徑在這道整爐工序的一爐上限。混裝時每一框占「支數 ÷ 這個值」。
    /// 中模跟同組的下沖相同（使用者 2026-10-22）。
    /// </summary>
    public int BatchCapacityFor(int rodDiameterMm) =>
        rodDiameterMm == 24 && BatchMaxPiecesRod24 > 0 ? BatchMaxPiecesRod24 : BatchMaxPieces;

    /// <summary>
    /// 測試模式下這一站的專用倍率（使用者 2026-10-10：取消整體的時間加速，改成測試模式下每一站各自的倍率）。
    /// 這一站的加工、機台取放、換刀或整爐時間都除以這個值；測試模式關閉時不使用。
    /// </summary>
    public double TestSpeed { get; set; } = 1;
}

/// <summary>
/// 整爐工序的一爐。剩餘時間是原本的工序時間，累計扣減（每次檢查扣掉實際經過秒數 × 這一站當時的倍率），
/// 所以一爐長達數小時、測試模式或倍率中途改變也不會失真；後端重啟後從資料庫接續。
/// 爐裡有哪些框記在各框的 <see cref="OperationExecution.BatchRunId"/>。
/// </summary>
public sealed class BatchRun
{
    public Guid Id { get; private set; }
    public string StationCode { get; private set; } = string.Empty;
    public int StepSequence { get; private set; }
    public int Quantity { get; private set; }
    public double TotalSeconds { get; private set; }
    public double RemainingSeconds { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset AccruedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsRunning => CompletedAt is null;

    /// <summary>這一爐的進度，0～1。</summary>
    public double Progress => TotalSeconds <= 0 ? 1 : Math.Clamp((TotalSeconds - RemainingSeconds) / TotalSeconds, 0, 1);

    public static BatchRun Start(string stationCode, int stepSequence, int quantity, double seconds, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        StationCode = stationCode,
        StepSequence = stepSequence,
        Quantity = quantity,
        TotalSeconds = seconds,
        RemainingSeconds = seconds,
        StartedAt = now,
        AccruedAt = now,
    };

    public void Accrue(DateTimeOffset now, double speed)
    {
        if (now > AccruedAt)
        {
            RemainingSeconds = Math.Max(0, RemainingSeconds - (now - AccruedAt).TotalSeconds * speed);
            AccruedAt = now;
        }

        if (RemainingSeconds <= 0)
        {
            CompletedAt = now;
        }
    }
}

/// <summary>
/// 一台機台的刀具狀態（同一站的兩台車床各有一把，各自計壽命、各自叫刀與換刀）。
/// 快到壽命時預先叫小型車送一把新刀到機邊；到達壽命時新刀已到就開始換刀，
/// 還沒到就停機等刀。換刀期間機台暫停。所有時間點都存在資料庫，後端重啟後可以接續。
/// 「新刀送到」（搬運任務完成）與「換刀完成」（<see cref="ToolChangeRecord"/>）是兩件事，分開記錄。
/// </summary>
public sealed class MachineTool
{
    public required string MachineCode { get; set; }

    /// <summary>上次換刀後已加工的件數。</summary>
    public int PiecesSinceChange { get; private set; }
    public DateTimeOffset? ChangeStartedAt { get; private set; }
    public DateTimeOffset? ChangeDueAt { get; private set; }

    /// <summary>已叫車送新刀的時間；還沒叫時為 null。</summary>
    public DateTimeOffset? SpareRequestedAt { get; private set; }

    /// <summary>新刀送到機邊的時間；還沒到時為 null。</summary>
    public DateTimeOffset? SpareDeliveredAt { get; private set; }

    /// <summary>刀具已到壽命、新刀還沒到，機台從這個時間起停機等刀；沒有在等時為 null。</summary>
    public DateTimeOffset? WaitingSince { get; private set; }

    public bool IsChanging => ChangeDueAt is not null;

    public bool IsWaitingForSpare => WaitingSince is not null;

    /// <summary>機台因為刀具而不能加工：換刀中，或停機等刀。</summary>
    public bool IsStopped => IsChanging || IsWaitingForSpare;

    public void CountPiece() => PiecesSinceChange++;

    public void RequestSpare(DateTimeOffset now) => SpareRequestedAt = now;

    public void DeliverSpare(DateTimeOffset now) => SpareDeliveredAt = now;

    public void WaitForSpare(DateTimeOffset at) => WaitingSince = at;

    public void StartChange(DateTimeOffset now, double seconds)
    {
        WaitingSince = null;
        ChangeStartedAt = now;
        ChangeDueAt = now.AddSeconds(seconds);
    }

    /// <summary>換刀完成：計數歸零、備用的新刀用掉了，回傳這次換刀的紀錄。</summary>
    public ToolChangeRecord CompleteChange(string stationCode)
    {
        var record = new ToolChangeRecord
        {
            StationCode = stationCode,
            MachineCode = MachineCode,
            PiecesUsed = PiecesSinceChange,
            StartedAt = ChangeStartedAt!.Value,
            CompletedAt = ChangeDueAt!.Value,
        };
        PiecesSinceChange = 0;
        ChangeStartedAt = null;
        ChangeDueAt = null;
        SpareRequestedAt = null;
        SpareDeliveredAt = null;
        return record;
    }
}

/// <summary>換刀紀錄（只新增）。</summary>
public sealed class ToolChangeRecord
{
    public long Id { get; set; }
    public required string StationCode { get; set; }

    /// <summary>哪一台機台換刀。P6 以前的紀錄（一站一台）是那一站的 1 號機。</summary>
    public required string MachineCode { get; set; }
    public int PiecesUsed { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}

/// <summary>可由面板修改的示範設定，全系統只有一列（Id = 1）。</summary>
public sealed class ProductionSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    /// <summary>新批次的預設數量（組）。</summary>
    public int LotQuantity { get; set; }
    public required string SourceStationCode { get; set; }

    /// <summary>最後一道工序完成後送往的暫存站。</summary>
    public required string BufferStationCode { get; set; }

    /// <summary>新刀具的來源站（刀具庫）。</summary>
    public required string ToolStoreStationCode { get; set; }

    /// <summary>
    /// 機台取放一件的時間（秒）：加工前取料、加工後取出，各一次。使用者 2026-10-10 更正：
    /// 先前說的「上下料每件 5 秒」指的是機台的這兩個動作，不是 AGV。
    /// </summary>
    public double PieceHandlingSeconds { get; set; }

    /// <summary>AGV 與站點交接一框的時間（秒），和件數無關。使用者 2026-10-10：30 秒。</summary>
    public double AgvHandoverSeconds { get; set; }

    /// <summary>換刀時間（秒）。使用者 2026-10-10：90 秒。</summary>
    public double ToolChangeSeconds { get; set; }

    /// <summary>刀具剩餘件數低於（或等於）這個值就預先叫刀。示範值 50 件。</summary>
    public int ToolRequestThresholdPieces { get; set; }

    /// <summary>關閉時視為新刀永遠在機邊（P2 的行為）。</summary>
    public bool ToolDeliveryEnabled { get; set; }

    /// <summary>
    /// 測試模式：開啟時每一站依自己的 <see cref="ProcessStep.TestSpeed"/> 加快。車輛不加快。
    /// 和其他設定一樣存在資料庫，後端重啟後維持原狀。
    /// </summary>
    public bool TestMode { get; set; }

    /// <summary>
    /// 測試模式或任何一站的倍率最後一次變更的時間。逐件工序進行中的那一件從這個時間重新計時，
    /// 否則切換的瞬間會把「之前已經過的時間」也用新倍率計算，一口氣跳好幾件。
    /// </summary>
    public DateTimeOffset? SpeedChangedAt { get; set; }

    /// <summary>
    /// 機床換線時間（秒）。使用者 2026-10-10：24 mm 與 30 mm 用的工控程式不同，只要切換就要等，大約 10 分鐘；
    /// 也納入測試模式的加速（用該站的倍率）。
    /// </summary>
    public double ChangeoverSeconds { get; set; }

    /// <summary>測試模式下 AGV 交接加快的倍率。使用者 2026-10-10：40 倍。</summary>
    public double TestHandoverSpeed { get; set; } = 1;

    /// <summary>測試模式下空車車速的倍率。使用者 2026-10-10：空車可以再快 2 倍。</summary>
    public double TestEmptyVehicleSpeed { get; set; } = 1;

    /// <summary>測試模式下載貨時的車速倍率（使用者 2026-10-22：測試模式下空車與載貨都快 5 倍）。</summary>
    public double TestLoadedVehicleSpeed { get; set; } = 1;

    /// <summary>某一站目前實際採用的倍率。</summary>
    public double SpeedOf(ProcessStep step) => TestMode ? step.TestSpeed : 1;

    /// <summary>品管的不良率（0～1）。使用者 2026-10-10：示範用 10%。</summary>
    public double DefectRate { get; set; }

    /// <summary>同一支最多反工幾次，超過就報廢。使用者 2026-10-10：1 次。</summary>
    public int MaxReworkCount { get; set; }

    /// <summary>
    /// 鍍層不良的反工品累積到這麼多支才重新電鍍（使用者 2026-10-10：最少 30 支，只算反工品；
    /// 廠內沒有其他料會再進電鍍時，不足的尾料也放行）。
    /// </summary>
    public int ReworkBatchMinPieces { get; set; }

    /// <summary>
    /// 貨車多久來出貨站一次（秒）。使用者 2026-10-10：貨車定時來；間隔 1 小時是 Claude 挑的示範值。
    /// </summary>
    public double TruckIntervalSeconds { get; set; }

    /// <summary>測試模式下貨車間隔加快的倍率（Claude 挑的 60：1 小時 → 1 分鐘）。</summary>
    public double TestTruckSpeed { get; set; } = 1;

    /// <summary>貨車倒數目前實際採用的倍率。</summary>
    public double TruckSpeed => TestMode ? TestTruckSpeed : 1;

    /// <summary>現在派出的搬運，交接一框要多久。</summary>
    public double EffectiveHandoverSeconds => TestMode ? AgvHandoverSeconds / TestHandoverSpeed : AgvHandoverSeconds;
}

/// <summary>
/// 出貨站的貨車班次，全系統只有一列（Id = 1）。和整爐工序一樣用累計扣減：
/// 每次檢查扣掉實際經過秒數 × 當時的倍率，所以測試模式中途切換、後端重啟都能接續。
/// </summary>
public sealed class TruckSchedule
{
    public const int SingletonId = 1;

    public int Id { get; private set; } = SingletonId;

    /// <summary>距離下一班還有多久（秒，原本的間隔時間）。</summary>
    public double RemainingSeconds { get; private set; }

    /// <summary>上次扣減的時間；還沒開始計時為 null。</summary>
    public DateTimeOffset? AccruedAt { get; private set; }
    public DateTimeOffset? LastArrivedAt { get; private set; }

    public static TruckSchedule Create(double intervalSeconds) => new() { RemainingSeconds = intervalSeconds };

    /// <summary>扣掉這段時間；貨車到了回傳 true，並開始倒數下一班。後端停機很久也只算一班。</summary>
    public bool Accrue(DateTimeOffset now, double speed, double intervalSeconds)
    {
        if (AccruedAt is { } accrued && now > accrued)
        {
            RemainingSeconds -= (now - accrued).TotalSeconds * speed;
        }

        AccruedAt = now;
        if (RemainingSeconds > 0)
        {
            return false;
        }

        RemainingSeconds = intervalSeconds;
        LastArrivedAt = now;
        return true;
    }

    /// <summary>間隔被改了：從現在重新倒數。</summary>
    public void Restart(double intervalSeconds) => RemainingSeconds = intervalSeconds;
}

/// <summary>出貨紀錄（只新增）：某個批次某一種零件的出貨箱被貨車載走。</summary>
public sealed class Shipment
{
    public long Id { get; set; }
    public Guid LotId { get; set; }
    public required string BatchNumber { get; set; }
    public required string PartCode { get; set; }
    public required string LotNumber { get; set; }
    public int Quantity { get; set; }
    public DateTimeOffset ShippedAt { get; set; }
}

/// <summary>
/// 一框物料：搬運與加工的單位。一個批次（<see cref="BatchNumber"/>）依料框容量分成好幾框，每一框各自搬運、各自加工；
/// 訂單與批次本身的資料表留到下單階段（P5）。同一時間只在一個實體位置：某一站（<see cref="StationCode"/>）或某台車上（<see cref="VehicleId"/>）。
/// 狀態只能透過下面的方法改變，不合法的轉移會丟出例外，而不是默默寫進資料庫。
/// </summary>
public sealed class MaterialLot
{
    public Guid Id { get; private set; }
    public string LotNumber { get; private set; } = string.Empty;

    /// <summary>所屬批次，以及這是其中的第幾框（從 1 起）、共幾框。</summary>
    public string BatchNumber { get; private set; } = string.Empty;
    public int BinIndex { get; private set; }
    public int BinCount { get; private set; }

    /// <summary>桿徑（mm），決定一框裝幾支。</summary>
    public int RodDiameterMm { get; private set; }

    /// <summary>沖頭規格代碼（決定備料長度）；P5 以前建立的框沒有，也不經過切料。</summary>
    public string? SpecCode { get; private set; }

    /// <summary>屬於哪一張生產工單（補做的框屬於補做工單）；沒有訂單的批次是 null。</summary>
    public Guid? ProductionOrderId { get; private set; }

    /// <summary>出貨箱：屬於哪一張出貨單；沒有訂單的出貨箱與送成品倉的箱子是 null。</summary>
    public Guid? DeliveryId { get; private set; }
    public string PartCode { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public LotStatus Status { get; private set; }
    public string? StationCode { get; private set; }
    public string? VehicleId { get; private set; }

    /// <summary>目前所在或前往的工序；流程走完後為 null。</summary>
    public int? CurrentStepSequence { get; private set; }

    /// <summary>這一框的料已經反工過幾次。品管用它判斷還能不能再反工。</summary>
    public int ReworkCount { get; private set; }

    /// <summary>這一筆是整批包裝後的出貨箱（使用者 2026-10-10：整批一箱；每一種零件各一箱），不是料框。</summary>
    public bool IsBox { get; private set; }

    /// <summary>這一框是品管報廢後自動補做的料（使用者 2026-10-10：缺的自動補做）。爐前等的料裡有它時可以不滿下限就開爐。</summary>
    public bool IsMakeup { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>樂觀並行控制：對應 PostgreSQL 的 xmin，別人先改過這一列時 SaveChanges 會失敗。</summary>
    public uint Version { get; private set; }

    /// <summary>位置代碼，用在 <see cref="InventoryMovement"/>。</summary>
    public string Location => VehicleId is not null ? LocationCode.Vehicle(VehicleId) : LocationCode.Station(StationCode!);

    /// <summary>
    /// 在原料倉建立一框。<paramref name="awaitingMaterial"/> 為 true 時先等料（要切的圓棒還不夠），
    /// 料到了才由 <see cref="MaterialArrived"/> 改成等待搬運。
    /// </summary>
    public static MaterialLot Receive(
        string partCode, int quantity, string stationCode, int? firstStep,
        string batchNumber, int binIndex, int binCount, int rodDiameterMm, DateTimeOffset now,
        string? specCode = null, bool awaitingMaterial = false, Guid? productionOrderId = null)
    {
        return new MaterialLot
        {
            SpecCode = specCode,
            ProductionOrderId = productionOrderId,
            Id = Guid.NewGuid(),
            LotNumber = $"{batchNumber}-{ProductParts.Letter(partCode)}{binIndex:00}",
            BatchNumber = batchNumber,
            BinIndex = binIndex,
            BinCount = binCount,
            RodDiameterMm = rodDiameterMm,
            PartCode = partCode,
            Quantity = quantity,
            Status = awaitingMaterial ? LotStatus.AwaitingMaterial : LotStatus.AwaitingTransport,
            StationCode = stationCode,
            CurrentStepSequence = firstStep,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// 等料的框拿到料了：從 <paramref name="step"/> 開始（要切的從切料、用現成備料的從第一道加工），改成等待搬運。
    /// </summary>
    public void MaterialArrived(int step, DateTimeOffset now)
    {
        Require(LotStatus.AwaitingMaterial, nameof(MaterialArrived));
        CurrentStepSequence = step;
        Status = LotStatus.AwaitingTransport;
        UpdatedAt = now;
    }

    public void LoadOnto(string vehicleId, DateTimeOffset now)
    {
        Require(LotStatus.AwaitingTransport, nameof(LoadOnto));
        StationCode = null;
        VehicleId = vehicleId;
        Status = LotStatus.InTransit;
        UpdatedAt = now;
    }

    /// <summary>卸料：還有工序要做就排隊等加工，否則視為已到暫存位置。</summary>
    public void UnloadAt(string stationCode, DateTimeOffset now)
    {
        Require(LotStatus.InTransit, nameof(UnloadAt));
        VehicleId = null;
        StationCode = stationCode;
        Status = CurrentStepSequence is null ? LotStatus.Staged : LotStatus.Queued;
        UpdatedAt = now;
    }

    /// <summary>
    /// 人工恢復：搬運失敗後，有人把物料放回取料站。物料可能還在原站（還沒取走），也可能在車上。
    /// </summary>
    public void ReturnTo(string stationCode, DateTimeOffset now)
    {
        RequireOneOf(nameof(ReturnTo), LotStatus.AwaitingTransport, LotStatus.InTransit);
        VehicleId = null;
        StationCode = stationCode;
        Status = LotStatus.AwaitingTransport;
        UpdatedAt = now;
    }

    /// <summary>人工恢復：搬運失敗後，有人把物料送到了卸料站。之後和車輛卸料一樣：排隊等加工，或視為已暫存。</summary>
    public void PlaceAt(string stationCode, DateTimeOffset now)
    {
        RequireOneOf(nameof(PlaceAt), LotStatus.AwaitingTransport, LotStatus.InTransit);
        VehicleId = null;
        StationCode = stationCode;
        Status = CurrentStepSequence is null ? LotStatus.Staged : LotStatus.Queued;
        UpdatedAt = now;
    }

    public void StartProcessing(DateTimeOffset now)
    {
        Require(LotStatus.Queued, nameof(StartProcessing));
        Status = LotStatus.Processing;
        UpdatedAt = now;
    }

    /// <summary>整批加工完成才放行：改為等待搬運，並指向下一道工序（沒有下一道時為 null）。</summary>
    public void FinishProcessing(int? nextStep, DateTimeOffset now)
    {
        Require(LotStatus.Processing, nameof(FinishProcessing));
        CurrentStepSequence = nextStep;
        Status = LotStatus.AwaitingTransport;
        UpdatedAt = now;
    }

    /// <summary>
    /// 品管檢驗完，這一框留下 <paramref name="quantity"/> 支繼續流動，其餘的已經分到別的框或報廢。
    /// <paramref name="rework"/> 為 true 表示留下的是要反工的料，<paramref name="nextStep"/> 是返回的工序。
    /// </summary>
    public void FinishInspection(int quantity, int? nextStep, bool rework, DateTimeOffset now)
    {
        Require(LotStatus.Processing, nameof(FinishInspection));
        if (quantity < 1 || quantity > Quantity)
        {
            throw new InvalidOperationException($"Lot {LotNumber}: cannot keep {quantity} of {Quantity} piece(s) after inspection.");
        }

        Quantity = quantity;
        CurrentStepSequence = nextStep;
        ReworkCount += rework ? 1 : 0;
        Status = LotStatus.AwaitingTransport;
        UpdatedAt = now;
    }

    /// <summary>品管檢驗完，整框都報廢：留在原站，不再流動。數量保留，代表報廢了幾支。</summary>
    public void ScrapAll(DateTimeOffset now)
    {
        Require(LotStatus.Processing, nameof(ScrapAll));
        CurrentStepSequence = null;
        Status = LotStatus.Scrapped;
        UpdatedAt = now;
    }

    /// <summary>
    /// 品管把要反工的料從 <paramref name="source"/> 分出來另裝一框，在同一站等待搬運回 <paramref name="step"/> 重做。
    /// 批次編號相同，所以追蹤時仍看得出是同一個批次；框號加上 <paramref name="suffix"/>。
    /// </summary>
    public static MaterialLot SplitForRework(MaterialLot source, int quantity, string suffix, int step, int reworkCount, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        LotNumber = $"{source.LotNumber}-{suffix}",
        BatchNumber = source.BatchNumber,
        BinIndex = source.BinIndex,
        BinCount = source.BinCount,
        RodDiameterMm = source.RodDiameterMm,
        SpecCode = source.SpecCode,
        ProductionOrderId = source.ProductionOrderId,
        PartCode = source.PartCode,
        Quantity = quantity,
        Status = LotStatus.AwaitingTransport,
        StationCode = source.StationCode,
        CurrentStepSequence = step,
        ReworkCount = reworkCount,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>
    /// 品管報廢了 <paramref name="quantity"/> 支：在原料倉補投同樣數量的同一種零件，重走整條製程。
    /// 批次編號相同，所以這個批次要等補做的料包裝完才裝箱；框號是原本的框號加上 -M 與第幾次檢驗。
    /// 有規格的框先等料（<paramref name="awaitingMaterial"/>）：補做也要有實際的備料或圓棒，由備料分配決定從哪一站開始。
    /// 有工單的框屬於另開的補做工單（<paramref name="productionOrderId"/>），批次編號仍是主工單的號碼。
    /// </summary>
    public static MaterialLot ReceiveMakeup(
        MaterialLot source, int quantity, string stationCode, int? firstStep, DateTimeOffset now,
        bool awaitingMaterial = false, Guid? productionOrderId = null) => new()
    {
        ProductionOrderId = productionOrderId,
        Id = Guid.NewGuid(),
        LotNumber = $"{source.LotNumber}-M{source.ReworkCount + 1}",
        BatchNumber = source.BatchNumber,
        BinIndex = source.BinIndex,
        BinCount = source.BinCount,
        RodDiameterMm = source.RodDiameterMm,
        SpecCode = source.SpecCode,
        PartCode = source.PartCode,
        Quantity = quantity,
        Status = awaitingMaterial ? LotStatus.AwaitingMaterial : LotStatus.AwaitingTransport,
        StationCode = stationCode,
        CurrentStepSequence = firstStep,
        IsMakeup = true,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>這一框包裝完：留在包裝站，等同批次其他的框。</summary>
    public void FinishPacking(DateTimeOffset now)
    {
        Require(LotStatus.Processing, nameof(FinishPacking));
        CurrentStepSequence = null;
        Status = LotStatus.Packed;
        UpdatedAt = now;
    }

    /// <summary>這一框的料併進了出貨箱：料框空了，不再流動。數量保留，代表這一框包了幾支。</summary>
    public void MoveIntoBox(DateTimeOffset now)
    {
        Require(LotStatus.Packed, nameof(MoveIntoBox));
        Status = LotStatus.Boxed;
        UpdatedAt = now;
    }

    /// <summary>
    /// 同批次、同一種零件已包裝的框（<paramref name="packed"/>）併成一箱，在包裝站等待搬運到出貨站。
    /// 批次編號相同；箱號是批次編號加上零件的字母與 -BOX。
    /// </summary>
    public static MaterialLot PackBox(IReadOnlyList<MaterialLot> packed, DateTimeOffset now)
    {
        var first = packed[0];
        return new MaterialLot
        {
            Id = Guid.NewGuid(),
            LotNumber = $"{first.BatchNumber}-{ProductParts.Letter(first.PartCode)}-BOX",
            BatchNumber = first.BatchNumber,
            BinIndex = 1,
            BinCount = 1,
            RodDiameterMm = first.RodDiameterMm,
            SpecCode = first.SpecCode,
            PartCode = first.PartCode,
            Quantity = packed.Sum(l => l.Quantity),
            Status = LotStatus.AwaitingTransport,
            StationCode = first.StationCode,
            IsBox = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// 訂單的出貨箱（或送成品倉的箱子）：從包好的框裝 <paramref name="quantity"/> 支，在包裝站等待搬運。
    /// 箱號由呼叫端決定；<paramref name="deliveryId"/> 是出貨單（送成品倉的箱子沒有）。
    /// </summary>
    public static MaterialLot CreateBox(
        string lotNumber, string batchNumber, MaterialLot sample, int quantity, Guid? deliveryId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        LotNumber = lotNumber,
        BatchNumber = batchNumber,
        BinIndex = 1,
        BinCount = 1,
        RodDiameterMm = sample.RodDiameterMm,
        SpecCode = sample.SpecCode,
        PartCode = sample.PartCode,
        Quantity = quantity,
        Status = LotStatus.AwaitingTransport,
        StationCode = sample.StationCode,
        IsBox = true,
        DeliveryId = deliveryId,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>
    /// 分批出貨只出完整的組：從包好的框分出 <paramref name="quantity"/> 支另成一框（框號加 -P），剩下的留在原框繼續在包裝站等。
    /// 一張訂單只會分一次批，所以一框最多被分一次。
    /// </summary>
    public static MaterialLot SplitPacked(MaterialLot source, int quantity, DateTimeOffset now)
    {
        source.Require(LotStatus.Packed, nameof(SplitPacked));
        if (quantity <= 0 || quantity >= source.Quantity)
        {
            throw new InvalidOperationException($"Lot {source.LotNumber}: cannot split {quantity} of {source.Quantity}.");
        }

        source.Quantity -= quantity;
        source.UpdatedAt = now;
        return new MaterialLot
        {
            Id = Guid.NewGuid(),
            LotNumber = $"{source.LotNumber}-P",
            BatchNumber = source.BatchNumber,
            BinIndex = source.BinIndex,
            BinCount = source.BinCount,
            RodDiameterMm = source.RodDiameterMm,
            SpecCode = source.SpecCode,
            ProductionOrderId = source.ProductionOrderId,
            PartCode = source.PartCode,
            Quantity = quantity,
            Status = LotStatus.Packed,
            StationCode = source.StationCode,
            ReworkCount = source.ReworkCount,
            IsMakeup = source.IsMakeup,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>貨車把這一箱載走：留在出貨站的紀錄上，不再流動。</summary>
    public void Ship(DateTimeOffset now)
    {
        Require(LotStatus.Staged, nameof(Ship));
        Status = LotStatus.Shipped;
        UpdatedAt = now;
    }

    private void Require(LotStatus expected, string action) => RequireOneOf(action, expected);

    private void RequireOneOf(string action, params LotStatus[] expected)
    {
        if (!expected.Contains(Status))
        {
            throw new InvalidOperationException($"Lot {LotNumber}: {action} requires {string.Join(" or ", expected)}, but it is {Status}.");
        }
    }
}

public static class LocationCode
{
    public static string Station(string code) => $"station:{code}";

    public static string Vehicle(string id) => $"vehicle:{id}";
}

/// <summary>
/// 一框的品管結果（只新增）。合格、反工、報廢的支數相加等於檢驗的支數；
/// 三種原因的支數相加等於不良的支數。反工次數用完的不良會算在報廢，但原因照實記錄。
/// </summary>
public sealed class InspectionResult
{
    public long Id { get; set; }
    public Guid LotId { get; set; }
    public required string StationCode { get; set; }

    /// <summary>檢驗時這一框已經反工過幾次。</summary>
    public int ReworkRound { get; set; }
    public int Inspected { get; set; }
    public int Passed { get; set; }
    public int Reworked { get; set; }
    public int Scrapped { get; set; }
    public int SurfaceDefects { get; set; }
    public int PlatingDefects { get; set; }
    public int DimensionDefects { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}

/// <summary>批次位置異動紀錄（只新增、不修改）：收料、取料上車、卸料到站、品管分框與報廢。</summary>
public sealed class InventoryMovement
{
    public long Id { get; set; }
    public Guid LotId { get; set; }
    public int Quantity { get; set; }

    /// <summary>null 表示從系統外進來（收料）。</summary>
    public string? From { get; set; }
    public required string To { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>報廢品的去向代碼（不是實體站點）。</summary>
    public const string ScrapLocation = "scrap";

    /// <summary>出貨離廠的去向代碼（不是實體站點）。</summary>
    public const string ShippedLocation = "shipped";

    /// <summary>在批次移動「之後」呼叫：To 取批次的新位置。</summary>
    public static InventoryMovement Record(MaterialLot lot, string? from, string reason, DateTimeOffset now) => new()
    {
        LotId = lot.Id,
        Quantity = lot.Quantity,
        From = from,
        To = lot.Location,
        Reason = reason,
        OccurredAt = now,
    };
}

/// <summary>
/// 某批次在某道工序的一次加工：逐件計數，整批做完才完成。之後反工時會是同一工序的另一筆紀錄。
/// 進度以「目前這一件從何時開始」（<see cref="PieceStartedAt"/>）表示，存在資料庫，所以後端重啟後能接續。
/// </summary>
public sealed class OperationExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LotId { get; set; }
    public MaterialLot Lot { get; set; } = null!;
    public int StepSequence { get; set; }
    public required string StationCode { get; set; }

    /// <summary>
    /// 逐件工序：在哪一台機台加工。框在站上排隊時還不知道，哪一台空下來接手才決定（<see cref="Start"/>）；整爐工序是 null。
    /// </summary>
    public string? MachineCode { get; private set; }
    public OperationStatus Status { get; private set; } = OperationStatus.Queued;
    public int Quantity { get; set; }
    public int PiecesCompleted { get; private set; }

    /// <summary>開始加工時從 <see cref="ProcessStep"/> 複製過來；之後修改設定不影響已開始的加工。</summary>
    public double? SecondsPerPiece { get; private set; }

    /// <summary>機台取放一件的時間（秒），開始加工時從設定複製過來。每件的週期＝取料＋加工＋取出。</summary>
    public double HandlingSeconds { get; private set; }
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? PieceStartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>整爐工序：這一框在哪一爐裡；逐件工序為 null。</summary>
    public Guid? BatchRunId { get; private set; }

    /// <summary>
    /// 機床換線的起訖：這一框的桿徑和上一框不同時，先換線再做第一件。不需要換線時兩者都是 null。
    /// 換線時間在開始時就決定（和換刀一樣），之後改設定或倍率不影響。
    /// </summary>
    public DateTimeOffset? ChangeoverStartedAt { get; private set; }
    public DateTimeOffset? ChangeoverDueAt { get; private set; }

    /// <summary>
    /// 目前這一件預計完成的時間。<paramref name="speed"/> 是這一站目前的倍率（測試模式）；
    /// 倍率在這一件開始之後才改變時，從 <paramref name="speedChangedAt"/> 重新計時。
    /// </summary>
    public DateTimeOffset PieceDueAt(double speed, DateTimeOffset? speedChangedAt)
    {
        var from = speedChangedAt > PieceStartedAt!.Value ? speedChangedAt.Value : PieceStartedAt.Value;
        return from.AddSeconds((SecondsPerPiece!.Value + 2 * HandlingSeconds) / speed);
    }

    /// <summary>
    /// 由 <paramref name="machineCode"/> 這台機台開始加工。<paramref name="changeoverSeconds"/> 大於 0 時先換線，第一件從換線完成的時間開始。
    /// </summary>
    public void Start(string machineCode, double secondsPerPiece, double handlingSeconds, double changeoverSeconds, DateTimeOffset now)
    {
        Require(OperationStatus.Queued, nameof(Start));
        Status = OperationStatus.Running;
        MachineCode = machineCode;
        SecondsPerPiece = secondsPerPiece;
        HandlingSeconds = handlingSeconds;
        StartedAt = now;
        PieceStartedAt = now;
        if (changeoverSeconds > 0)
        {
            ChangeoverStartedAt = now;
            ChangeoverDueAt = now.AddSeconds(changeoverSeconds);
            PieceStartedAt = ChangeoverDueAt;
        }
    }

    /// <summary>整爐工序：這一框進了某一爐。進度由那一爐（<see cref="BatchRun"/>）決定。</summary>
    public void StartInBatch(Guid batchRunId, DateTimeOffset now)
    {
        Require(OperationStatus.Queued, nameof(StartInBatch));
        Status = OperationStatus.Running;
        BatchRunId = batchRunId;
        StartedAt = now;
    }

    /// <summary>整爐工序：那一爐完成，這一框的每一支都算做完。</summary>
    public void CompleteBatch(DateTimeOffset at)
    {
        Require(OperationStatus.Running, nameof(CompleteBatch));
        PiecesCompleted = Quantity;
        Status = OperationStatus.Completed;
        CompletedAt = at;
    }

    /// <summary>完成一件；做完最後一件時整筆加工完成。<paramref name="at"/> 是這一件實際完成的時間。</summary>
    public void CompletePiece(DateTimeOffset at)
    {
        Require(OperationStatus.Running, nameof(CompletePiece));
        PiecesCompleted++;
        PieceStartedAt = at;
        if (PiecesCompleted >= Quantity)
        {
            Status = OperationStatus.Completed;
            CompletedAt = at;
        }
    }

    /// <summary>機台暫停（等刀、換刀）結束後，目前這一件從 <paramref name="at"/> 重新開始計時。</summary>
    public void ResumeAt(DateTimeOffset at)
    {
        Require(OperationStatus.Running, nameof(ResumeAt));
        PieceStartedAt = at;
    }

    private void Require(OperationStatus expected, string action)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Operation {Id}: {action} requires {expected}, but it is {Status}.");
        }
    }
}
