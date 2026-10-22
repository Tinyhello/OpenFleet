namespace OpenFleet.Api.Data;

/// <summary>
/// 一框的切料：在切料站用幾根圓棒、切出幾支，多出來的備料切完後入庫（使用者 2026-10-22：整根切完、一根只切一種長度）。
/// 圓棒在建立時先保留（保留的來源是框號）；保留到 <see cref="Bars"/> 根才算備齊、框才送去切料。切完時領用保留的圓棒。
/// </summary>
public sealed class CuttingJob
{
    public Guid Id { get; private set; }
    public Guid LotId { get; private set; }
    public string LotNumber { get; private set; } = string.Empty;

    /// <summary>切出來的備料品項（決定長度）。</summary>
    public string BlankItemCode { get; private set; } = string.Empty;
    public string BarItemCode { get; private set; } = string.Empty;
    public int BlankLengthMm { get; private set; }
    public int PiecesPerBar { get; private set; }

    /// <summary>這一框要用幾根圓棒，已經保留了幾根。</summary>
    public int Bars { get; private set; }
    public int BarsReserved { get; private set; }

    /// <summary>實際要切幾支（框裡的支數加上多出來的）、多出幾支會入庫。</summary>
    public int PiecesToCut { get; private set; }
    public int ExtraBlanks { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsReady => BarsReserved >= Bars;
    public int BarsMissing => Bars - BarsReserved;

    public static CuttingJob Plan(
        MaterialLot lot, string blankItemCode, string barItemCode, int blankLengthMm, int piecesPerBar, int bars, int extraBlanks, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        LotId = lot.Id,
        LotNumber = lot.LotNumber,
        BlankItemCode = blankItemCode,
        BarItemCode = barItemCode,
        BlankLengthMm = blankLengthMm,
        PiecesPerBar = piecesPerBar,
        Bars = bars,
        PiecesToCut = lot.Quantity + extraBlanks,
        ExtraBlanks = extraBlanks,
        CreatedAt = now,
    };

    public void AddReserved(int bars)
    {
        if (bars < 0 || BarsReserved + bars > Bars)
        {
            throw new InvalidOperationException($"Cutting job for {LotNumber}: cannot reserve {bars} more bar(s); {BarsReserved}/{Bars} already.");
        }

        BarsReserved += bars;
    }

    public void Complete(DateTimeOffset now)
    {
        if (!IsReady || CompletedAt is not null)
        {
            throw new InvalidOperationException($"Cutting job for {LotNumber} is not ready or already completed.");
        }

        CompletedAt = now;
    }
}
