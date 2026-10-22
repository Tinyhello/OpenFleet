using System.Collections.Generic;

namespace OpenFleet.Contracts
{
    // P5 規格與 BOM 的 HTTP 契約。

    /// <summary>品項的種類：整組沖模、零件、備料、原料圓棒。</summary>
    public enum ItemKind
    {
        Set,
        Part,
        Blank,
        Bar,
    }

    /// <summary>桿徑：沖頭規格的基底，放共同的預設值。</summary>
    public sealed class RodSizeDto
    {
        public int DiameterMm { get; set; }

        /// <summary>一框幾支；中模跟同組的上下沖相同。</summary>
        public int PiecesPerBin { get; set; }

        /// <summary>各零件的備料長度（mm），依 <see cref="ProductParts.All"/> 的代碼。</summary>
        public Dictionary<string, int> BlankLengthMm { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>一個值，以及它是沖頭規格自己覆寫的，還是沿用來的。</summary>
    public sealed class ResolvedValueDto
    {
        public double Value { get; set; }
        public bool Overridden { get; set; }
    }

    /// <summary>沖頭規格與解析後的有效值。</summary>
    public sealed class PunchSpecDto
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Shape { get; set; }
        public int RodDiameterMm { get; set; }
        public int PiecesPerBin { get; set; }

        /// <summary>中模外徑（mm），不跟桿徑走。</summary>
        public int DieOuterDiameterMm { get; set; }

        /// <summary>各零件的備料長度（mm）。沒有覆寫的沿用桿徑。</summary>
        public Dictionary<string, ResolvedValueDto> BlankLengthMm { get; set; } = new Dictionary<string, ResolvedValueDto>();

        /// <summary>粗加工、精加工每支的秒數。沒有覆寫的沿用製程設定。</summary>
        public ResolvedValueDto RoughSecondsPerPiece { get; set; } = new ResolvedValueDto();
        public ResolvedValueDto FinishSecondsPerPiece { get; set; } = new ResolvedValueDto();
    }

    /// <summary>建立沖頭規格（POST /api/catalog/specs）。覆寫欄位留空就沿用桿徑或製程設定。</summary>
    public sealed class CreatePunchSpecRequest
    {
        public string Code { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Shape { get; set; }
        public int RodDiameterMm { get; set; }
        public int? UpperBlankLengthMm { get; set; }
        public int? DieBlankLengthMm { get; set; }
        public int? LowerBlankLengthMm { get; set; }
        public double? RoughSecondsPerPiece { get; set; }
        public double? FinishSecondsPerPiece { get; set; }
    }

    public sealed class ItemDto
    {
        public string Code { get; set; } = string.Empty;
        public ItemKind Kind { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? PunchSpecCode { get; set; }
        public string? PartCode { get; set; }
        public int? DiameterMm { get; set; }
    }

    public sealed class BomLineDto
    {
        public string ChildItemCode { get; set; } = string.Empty;
        public string ChildName { get; set; } = string.Empty;
        public int Quantity { get; set; }
    }

    public sealed class BomDto
    {
        public string ItemCode { get; set; } = string.Empty;
        public int Version { get; set; }
        public List<BomLineDto> Lines { get; set; } = new List<BomLineDto>();
    }

    /// <summary>某一種零件展開到備料與圓棒的結果。</summary>
    public sealed class ExplosionLineDto
    {
        public string PartCode { get; set; } = string.Empty;
        public string PartItemCode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public string BlankItemCode { get; set; } = string.Empty;
        public int BlankLengthMm { get; set; }

        /// <summary>一根圓棒切幾支：圓棒長 ÷ (備料長 + 切口)，無條件捨去。</summary>
        public int PiecesPerBar { get; set; }

        /// <summary>全部從圓棒切要幾根（還沒扣現有備料）。</summary>
        public int Bars { get; set; }

        /// <summary>整根切完多出來、會入庫的備料。</summary>
        public int ExtraBlanks { get; set; }

        /// <summary>每根切完剩下、不夠再切一支的長度（mm）。</summary>
        public int RemnantMmPerBar { get; set; }
    }

    /// <summary>GET /api/catalog/items/{code}/explosion：整組或單一零件展開到圓棒。</summary>
    public sealed class ExplosionDto
    {
        public string ItemCode { get; set; } = string.Empty;
        public int Quantity { get; set; }
        public int BomVersion { get; set; }
        public string BarItemCode { get; set; } = string.Empty;
        public int BarLengthMm { get; set; }
        public int KerfMm { get; set; }
        public int TotalBars { get; set; }
        public List<ExplosionLineDto> Lines { get; set; } = new List<ExplosionLineDto>();
    }
}
