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

    /// <summary>刀具壽命（件）：粗加工 500、精加工 250 是使用者提供的數值。</summary>
    public int ToolLifePieces { get; set; }

    /// <summary>整爐工序：累積到這麼多支才開爐，一爐最多 <see cref="BatchMaxPieces"/> 支。</summary>
    public int BatchMinPieces { get; set; }
    public int BatchMaxPieces { get; set; }

    /// <summary>整爐工序：第一段與第二段的時間（秒）。兩段在同一站連續進行；只有一段時第二段為 0。</summary>
    public double BatchSeconds { get; set; }
    public double BatchSecondStageSeconds { get; set; }

    /// <summary>兩段的名稱，顯示用（淬火／回火）。</summary>
    public string? StageName { get; set; }
    public string? SecondStageName { get; set; }

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
    public double SecondStageSeconds { get; private set; }
    public double RemainingSeconds { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset AccruedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsRunning => CompletedAt is null;

    /// <summary>已經進到第二段。</summary>
    public bool InSecondStage => SecondStageSeconds > 0 && RemainingSeconds <= SecondStageSeconds;

    /// <summary>目前這一段的進度，0～1。</summary>
    public double StageProgress
    {
        get
        {
            var firstStage = TotalSeconds - SecondStageSeconds;
            var (length, done) = InSecondStage
                ? (SecondStageSeconds, SecondStageSeconds - RemainingSeconds)
                : (firstStage, TotalSeconds - RemainingSeconds);
            return length <= 0 ? 1 : Math.Clamp(done / length, 0, 1);
        }
    }

    public static BatchRun Start(string stationCode, int stepSequence, int quantity, double seconds, double secondStageSeconds, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        StationCode = stationCode,
        StepSequence = stepSequence,
        Quantity = quantity,
        TotalSeconds = seconds + secondStageSeconds,
        SecondStageSeconds = secondStageSeconds,
        RemainingSeconds = seconds + secondStageSeconds,
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
/// 某一站機台的刀具狀態。快到壽命時預先叫小型車送一把新刀到機邊；到達壽命時新刀已到就開始換刀，
/// 還沒到就停機等刀。換刀期間機台暫停。所有時間點都存在資料庫，後端重啟後可以接續。
/// 「新刀送到」（搬運任務完成）與「換刀完成」（<see cref="ToolChangeRecord"/>）是兩件事，分開記錄。
/// </summary>
public sealed class StationTool
{
    public required string StationCode { get; set; }

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
    public ToolChangeRecord CompleteChange()
    {
        var record = new ToolChangeRecord
        {
            StationCode = StationCode,
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
    public int PiecesUsed { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}

/// <summary>可由面板修改的示範設定，全系統只有一列（Id = 1）。</summary>
public sealed class ProductionSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public int LotQuantity { get; set; }
    public required string PartCode { get; set; }
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

    /// <summary>某一站目前實際採用的倍率。</summary>
    public double SpeedOf(ProcessStep step) => TestMode ? step.TestSpeed : 1;
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
    public string PartCode { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public LotStatus Status { get; private set; }
    public string? StationCode { get; private set; }
    public string? VehicleId { get; private set; }

    /// <summary>目前所在或前往的工序；流程走完後為 null。</summary>
    public int? CurrentStepSequence { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>樂觀並行控制：對應 PostgreSQL 的 xmin，別人先改過這一列時 SaveChanges 會失敗。</summary>
    public uint Version { get; private set; }

    /// <summary>位置代碼，用在 <see cref="InventoryMovement"/>。</summary>
    public string Location => VehicleId is not null ? LocationCode.Vehicle(VehicleId) : LocationCode.Station(StationCode!);

    public static MaterialLot Receive(
        string partCode, int quantity, string stationCode, int? firstStep,
        string batchNumber, int binIndex, int binCount, int rodDiameterMm, DateTimeOffset now)
    {
        return new MaterialLot
        {
            Id = Guid.NewGuid(),
            LotNumber = $"{batchNumber}-{binIndex:00}",
            BatchNumber = batchNumber,
            BinIndex = binIndex,
            BinCount = binCount,
            RodDiameterMm = rodDiameterMm,
            PartCode = partCode,
            Quantity = quantity,
            Status = LotStatus.AwaitingTransport,
            StationCode = stationCode,
            CurrentStepSequence = firstStep,
            CreatedAt = now,
            UpdatedAt = now,
        };
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

/// <summary>批次位置異動紀錄（只新增、不修改）：收料、取料上車、卸料到站。</summary>
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
    /// 目前這一件預計完成的時間。<paramref name="speed"/> 是這一站目前的倍率（測試模式）；
    /// 倍率在這一件開始之後才改變時，從 <paramref name="speedChangedAt"/> 重新計時。
    /// </summary>
    public DateTimeOffset PieceDueAt(double speed, DateTimeOffset? speedChangedAt)
    {
        var from = speedChangedAt > PieceStartedAt!.Value ? speedChangedAt.Value : PieceStartedAt.Value;
        return from.AddSeconds((SecondsPerPiece!.Value + 2 * HandlingSeconds) / speed);
    }

    public void Start(double secondsPerPiece, double handlingSeconds, DateTimeOffset now)
    {
        Require(OperationStatus.Queued, nameof(Start));
        Status = OperationStatus.Running;
        SecondsPerPiece = secondsPerPiece;
        HandlingSeconds = handlingSeconds;
        StartedAt = now;
        PieceStartedAt = now;
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
