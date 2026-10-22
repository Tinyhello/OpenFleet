using OpenFleet.Contracts;

namespace OpenFleet.Api.Data;

/// <summary>
/// 客戶訂單（使用者 2026-10-22：客戶訂單與生產工單分兩層）。一張訂單有好幾行明細，每一行是整組或單一零件。
/// 建立時帶的 Idempotency-Key 存在 <see cref="RequestKey"/>：同一個鍵重送只會有一張訂單。
/// </summary>
public sealed class SalesOrder
{
    public Guid Id { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public string Customer { get; private set; } = string.Empty;

    /// <summary>交期；只是記錄，系統不會因為交期提醒（使用者 2026-10-22）。</summary>
    public DateOnly? DueDate { get; private set; }
    public string? RequestKey { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>負責人同意分批出貨（使用者 2026-10-22：分批要負責人同意，最多分一次）。</summary>
    public string? PartialApprovedBy { get; private set; }
    public DateTimeOffset? PartialApprovedAt { get; private set; }

    /// <summary>樂觀並行控制（xmin）：兩個人同時按同意，只有一個會成功。</summary>
    public uint Version { get; private set; }
    public List<SalesOrderLine> Lines { get; private set; } = [];

    /// <summary>單號的後半段（日期與亂碼），工單號碼沿用它，看得出是同一張訂單。</summary>
    public string NumberSuffix => Number[3..];

    public static SalesOrder Create(string customer, DateOnly? dueDate, string? requestKey, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Number = $"SO-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}",
        Customer = customer,
        DueDate = dueDate,
        RequestKey = requestKey,
        CreatedAt = now,
    };

    public void ApprovePartial(string approvedBy, DateTimeOffset now)
    {
        if (PartialApprovedAt is not null)
        {
            throw new InvalidOperationException($"Partial shipment of {Number} was already approved.");
        }

        PartialApprovedBy = approvedBy;
        PartialApprovedAt = now;
    }

    public SalesOrderLine AddLine(string itemCode, int quantity)
    {
        var line = new SalesOrderLine
        {
            Id = Guid.NewGuid(),
            SalesOrderId = Id,
            LineNumber = Lines.Count + 1,
            ItemCode = itemCode,
            Quantity = quantity,
        };
        Lines.Add(line);
        return line;
    }
}

/// <summary>訂單明細：一個品項（整組沖模或單一零件）與數量（組或支，最多 300）。</summary>
public sealed class SalesOrderLine
{
    public Guid Id { get; set; }
    public Guid SalesOrderId { get; set; }
    public int LineNumber { get; set; }
    public required string ItemCode { get; set; }
    public int Quantity { get; set; }
}

/// <summary>
/// 生產工單：一行明細開一張主工單；品管報廢時另開補做工單，連回主工單（使用者 2026-10-22）。
/// 開立時把解析好的規格存一份（<see cref="Spec"/>），之後改規格或桿徑不影響已開立的工單；也記下用的是哪一版 BOM。
/// 工單的框以主工單的號碼當批次編號，補做的框也一樣，所以同一張主工單的料（含補做）一起裝箱。
/// </summary>
public sealed class ProductionOrder
{
    public Guid Id { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public ProductionOrderKind Kind { get; private set; }
    public Guid SalesOrderLineId { get; private set; }

    /// <summary>補做工單：主工單的 Id；主工單是 null。補做又報廢時再開的補做工單也連回主工單。</summary>
    public Guid? ParentId { get; private set; }
    public string ItemCode { get; private set; } = string.Empty;

    /// <summary>只做一種零件時是那一種（單一零件的明細、補做）；整組是 null。</summary>
    public string? PartCode { get; private set; }
    public int Quantity { get; private set; }

    /// <summary>主工單開工時每種零件多做的數量（<see cref="ExtraFor"/>）；補做工單是 0。報廢先用它抵。</summary>
    public int ExtraQuantity { get; private set; }
    public int BomVersion { get; private set; }
    public ProductionSpec Spec { get; private set; } = new();
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>這張工單要做哪幾種零件。</summary>
    public IReadOnlyList<string> Parts => PartCode is null ? ProductParts.All : [PartCode];

    /// <summary>
    /// 開工時多做幾組（使用者 2026-10-22：超過 30 組多做 1 組、超過 60 組多做 2 組）。
    /// 看的是這一行明細的數量；超過 90 組仍是 2 組；單一零件以支數套用同樣的門檻（這三點是 Claude 的解讀，待確認）。
    /// </summary>
    public static int ExtraFor(int quantity) => quantity > 60 ? 2 : quantity > 30 ? 1 : 0;

    public static ProductionOrder OpenMain(
        SalesOrder order, SalesOrderLine line, Item item, int bomVersion, ProductionSpec spec, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Number = $"WO-{order.NumberSuffix}-{line.LineNumber}",
        Kind = ProductionOrderKind.Main,
        SalesOrderLineId = line.Id,
        ItemCode = item.Code,
        PartCode = item.Kind == ItemKind.Part ? item.PartCode : null,
        Quantity = line.Quantity,
        ExtraQuantity = ExtraFor(line.Quantity),
        BomVersion = bomVersion,
        Spec = spec,
        CreatedAt = now,
    };

    /// <summary>補做工單：號碼是主工單號碼加上 -R 與第幾張補做；規格沿用主工單開立時的那一份。</summary>
    public static ProductionOrder OpenMakeup(
        ProductionOrder main, int sequence, string partCode, string partItemCode, int quantity, int bomVersion, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Number = $"{main.Number}-R{sequence}",
        Kind = ProductionOrderKind.Makeup,
        SalesOrderLineId = main.SalesOrderLineId,
        ParentId = main.Id,
        ItemCode = partItemCode,
        PartCode = partCode,
        Quantity = quantity,
        BomVersion = bomVersion,
        Spec = main.Spec.Copy(),
        CreatedAt = now,
    };
}

/// <summary>
/// 工單開立時解析好的規格（快照）。加工秒數只存覆寫值：沒有覆寫的沿用製程設定的現值，面板改了會跟著變。
/// </summary>
public sealed class ProductionSpec
{
    public string SpecCode { get; set; } = string.Empty;
    public string SpecName { get; set; } = string.Empty;
    public int RodDiameterMm { get; set; }
    public int PiecesPerBin { get; set; }
    public int DieOuterDiameterMm { get; set; }
    public int UpperBlankLengthMm { get; set; }
    public int DieBlankLengthMm { get; set; }
    public int LowerBlankLengthMm { get; set; }
    public double? RoughSecondsPerPiece { get; set; }
    public double? FinishSecondsPerPiece { get; set; }

    public int BlankLengthFor(string partCode) => partCode switch
    {
        ProductParts.UpperPunch => UpperBlankLengthMm,
        ProductParts.Die => DieBlankLengthMm,
        ProductParts.LowerPunch => LowerBlankLengthMm,
        _ => throw new ArgumentOutOfRangeException(nameof(partCode), partCode, "Unknown part code."),
    };

    /// <summary>從沖頭規格（要帶著桿徑）解析：沒有覆寫的備料長度沿用桿徑。</summary>
    public static ProductionSpec From(PunchSpec spec, int dieOuterDiameterMm)
    {
        var rod = spec.Rod ?? throw new InvalidOperationException($"Punch spec {spec.Code} was loaded without its rod size.");
        return new ProductionSpec
        {
            SpecCode = spec.Code,
            SpecName = spec.Name,
            RodDiameterMm = rod.DiameterMm,
            PiecesPerBin = rod.PiecesPerBin,
            DieOuterDiameterMm = dieOuterDiameterMm,
            UpperBlankLengthMm = spec.UpperBlankLengthMm ?? rod.UpperBlankLengthMm,
            DieBlankLengthMm = spec.DieBlankLengthMm ?? rod.DieBlankLengthMm,
            LowerBlankLengthMm = spec.LowerBlankLengthMm ?? rod.LowerBlankLengthMm,
            RoughSecondsPerPiece = spec.RoughSecondsPerPiece,
            FinishSecondsPerPiece = spec.FinishSecondsPerPiece,
        };
    }

    public ProductionSpec Copy() => (ProductionSpec)MemberwiseClone();
}

/// <summary>
/// 報廢註記：品管在哪一張工單、哪一框報廢了幾支，記在主工單上。先用開工時多做的抵（<see cref="CoveredByExtra"/>），
/// 不夠抵的才開補做工單，並連到那一張。同一框的同一次檢驗只會有一筆（(框, 第幾次檢驗) 唯一），避免重複補做。
/// </summary>
public sealed class ScrapRecord
{
    public Guid Id { get; private set; }
    public Guid MainOrderId { get; private set; }
    public Guid SourceOrderId { get; private set; }

    /// <summary>多做的不夠抵時開的補做工單；全部抵掉時是 null。</summary>
    public Guid? MakeupOrderId { get; private set; }
    public Guid LotId { get; private set; }
    public string LotNumber { get; private set; } = string.Empty;

    /// <summary>這一框檢驗時已經反工過幾次（第幾次檢驗）。</summary>
    public int InspectionRound { get; private set; }
    public string PartCode { get; private set; } = string.Empty;
    public int Quantity { get; private set; }

    /// <summary>其中由開工時多做的抵掉的數量。</summary>
    public int CoveredByExtra { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static ScrapRecord Create(
        ProductionOrder main, ProductionOrder source, ProductionOrder? makeup, MaterialLot lot, int quantity, int coveredByExtra, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        MainOrderId = main.Id,
        SourceOrderId = source.Id,
        MakeupOrderId = makeup?.Id,
        CoveredByExtra = coveredByExtra,
        LotId = lot.Id,
        LotNumber = lot.LotNumber,
        InspectionRound = lot.ReworkCount,
        PartCode = lot.PartCode,
        Quantity = quantity,
        CreatedAt = now,
    };
}
