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
}

/// <summary>P2 的製程路線：依 Sequence 順序加工。只有一條路線，產品與版本留到之後。</summary>
public sealed class ProcessStep
{
    public int Sequence { get; set; }
    public required string StationCode { get; set; }

    /// <summary>整批固定加工時間（秒）。示範設定，可由面板修改。</summary>
    public int DurationSeconds { get; set; }
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
}

/// <summary>
/// 物料批次。同一時間只在一個實體位置：某一站（<see cref="StationCode"/>）或某台車上（<see cref="VehicleId"/>）。
/// 狀態只能透過下面的方法改變，不合法的轉移會丟出例外，而不是默默寫進資料庫。
/// </summary>
public sealed class MaterialLot
{
    public Guid Id { get; private set; }
    public string LotNumber { get; private set; } = string.Empty;
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

    public static MaterialLot Receive(string partCode, int quantity, string stationCode, int? firstStep, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        return new MaterialLot
        {
            Id = id,
            LotNumber = $"LOT-{now:yyyyMMdd}-{id.ToString("N")[..6].ToUpperInvariant()}",
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

    private void Require(LotStatus expected, string action)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Lot {LotNumber}: {action} requires {expected}, but it is {Status}.");
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

/// <summary>某批次在某道工序的一次加工。之後反工時會是同一工序的另一筆紀錄。</summary>
public sealed class OperationExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LotId { get; set; }
    public MaterialLot Lot { get; set; } = null!;
    public int StepSequence { get; set; }
    public required string StationCode { get; set; }
    public OperationStatus Status { get; private set; } = OperationStatus.Queued;

    /// <summary>開始加工時從 <see cref="ProcessStep"/> 複製過來；之後修改設定不影響已開始的加工。</summary>
    public int? DurationSeconds { get; private set; }
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? DueAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public void Start(int durationSeconds, DateTimeOffset now)
    {
        if (Status != OperationStatus.Queued)
        {
            throw new InvalidOperationException($"Operation {Id} cannot start from {Status}.");
        }

        Status = OperationStatus.Running;
        DurationSeconds = durationSeconds;
        StartedAt = now;
        DueAt = now.AddSeconds(durationSeconds);
    }

    public void Complete(DateTimeOffset now)
    {
        if (Status != OperationStatus.Running)
        {
            throw new InvalidOperationException($"Operation {Id} cannot complete from {Status}.");
        }

        Status = OperationStatus.Completed;
        CompletedAt = now;
    }
}
