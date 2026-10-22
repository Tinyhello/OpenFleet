using OpenFleet.Contracts;

namespace OpenFleet.Api.Data;

/// <summary>
/// 桿徑：沖頭規格的基底（使用者 2026-10-22：桿徑大多只有 24 與 30 兩種，沖頭規格繼承桿徑後覆寫自己的部分）。
/// 放和機台有關的共同值；沖頭規格沒有覆寫的欄位就沿用這裡。
/// </summary>
public sealed class RodSize
{
    public int DiameterMm { get; set; }

    /// <summary>一框幾支（24 mm 45 支、30 mm 30 支；中模跟同組的上下沖相同）。</summary>
    public int PiecesPerBin { get; set; }

    /// <summary>各零件的備料長度（mm）。示範值，使用者還沒有實際數字。</summary>
    public int UpperBlankLengthMm { get; set; }
    public int DieBlankLengthMm { get; set; }
    public int LowerBlankLengthMm { get; set; }

    public int BlankLengthFor(string partCode) => partCode switch
    {
        ProductParts.UpperPunch => UpperBlankLengthMm,
        ProductParts.Die => DieBlankLengthMm,
        ProductParts.LowerPunch => LowerBlankLengthMm,
        _ => throw new ArgumentOutOfRangeException(nameof(partCode), partCode, "Unknown part code."),
    };
}

/// <summary>
/// 沖頭規格：指向一個桿徑，只填要覆寫的欄位，null 表示沿用。
/// 用資料表達繼承（不是 C# 類別繼承），新增一種規格只是多一筆資料，不用改程式。
/// 中模也跟著沖頭規格（內孔配合沖頭），外徑固定 38 mm。
/// </summary>
public sealed class PunchSpec
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? Shape { get; set; }
    public int RodDiameterMm { get; set; }
    public RodSize? Rod { get; set; }

    public int? UpperBlankLengthMm { get; set; }
    public int? DieBlankLengthMm { get; set; }
    public int? LowerBlankLengthMm { get; set; }

    /// <summary>粗加工、精加工每支的秒數；null 沿用製程設定。</summary>
    public double? RoughSecondsPerPiece { get; set; }
    public double? FinishSecondsPerPiece { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public int? BlankLengthOverrideFor(string partCode) => partCode switch
    {
        ProductParts.UpperPunch => UpperBlankLengthMm,
        ProductParts.Die => DieBlankLengthMm,
        ProductParts.LowerPunch => LowerBlankLengthMm,
        _ => throw new ArgumentOutOfRangeException(nameof(partCode), partCode, "Unknown part code."),
    };
}

/// <summary>
/// 品項：整組、零件、備料、圓棒。每建立一種沖頭規格，就產生 1 個整組、3 個零件、3 種備料；圓棒只有 Φ40 一種。
/// </summary>
public sealed class Item
{
    public required string Code { get; set; }
    public ItemKind Kind { get; set; }
    public required string Name { get; set; }
    public string? PunchSpecCode { get; set; }

    /// <summary>零件與備料：屬於哪一種零件（<see cref="ProductParts"/>）。</summary>
    public string? PartCode { get; set; }

    /// <summary>零件：上下沖是桿徑、中模是外徑 38；備料與圓棒：40。</summary>
    public int? DiameterMm { get; set; }

    /// <summary>圓棒：一根多長（mm，示範值）。</summary>
    public int? LengthMm { get; set; }

    /// <summary>安全庫存：只有圓棒有設（使用者 2026-10-22：安全庫存看圓棒，常用規格的備料也是從圓棒切）。</summary>
    public int? SafetyStock { get; set; }
}

/// <summary>
/// 一個品項的一版 BOM。改組成時新增一版，不改舊版；工單會記住開立時用的版本。
/// 整組 → 三種零件各 1；零件 → 備料 1。備料 → 圓棒不是固定用量，由切料規則算（一根切幾支）。
/// </summary>
public sealed class BomVersion
{
    public Guid Id { get; set; }
    public required string ItemCode { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<BomLine> Lines { get; set; } = [];
}

public sealed class BomLine
{
    public Guid Id { get; set; }
    public Guid BomVersionId { get; set; }

    /// <summary>顯示順序（整組依上沖、中模、下沖）。</summary>
    public int Sequence { get; set; }
    public required string ChildItemCode { get; set; }
    public int Quantity { get; set; }
}
