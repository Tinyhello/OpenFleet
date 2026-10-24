namespace OpenFleet.Api.Production;

/// <summary>
/// 同一道工序有好幾站時，替一框挑要送去哪一站（P7 第 1a 步）。派車時才呼叫，只看放得下的站。純計算，不碰資料庫。
///
/// 逐件工序（車床、拋光、品管、包裝、切料）：
/// 1. 換線與批次：<see cref="MachineAssignment"/> 的排名（站上機台最好的那一台），越小越好；
/// 2. 機台馬上能做的站優先（沒有在加工、等刀、換刀、成品台還卡著）；
/// 3. 路程：從取料站沿路網到這一站，近的優先；
/// 4. 站代碼（ordinal），最後決定順序。
///
/// 整爐工序（淬火、回火、電鍍）：
/// 1. 爐沒有在跑、而且爐前已經有料（排隊的加上已派車送來的）→ 先，料多的先（先湊滿一爐）；
///    其次是沒有在跑、爐前沒料的爐；最後是正在跑的爐，爐前料少的先；
/// 2. 路程；3. 站代碼。
/// </summary>
public static class StationSelector
{
    /// <summary>逐件工序的一站：<paramref name="Rank"/> 是站上機台的最好排名，<paramref name="MachineReady"/> 是有沒有一台馬上能做。</summary>
    public sealed record PieceCandidate(string StationCode, int Rank, bool MachineReady, double RouteMeters);

    /// <summary>整爐工序的一座爐：<paramref name="WaitingPieces"/> 是爐前已經排著或已派車送來的支數。</summary>
    public sealed record FurnaceCandidate(string StationCode, bool Running, int WaitingPieces, double RouteMeters);

    /// <summary>逐件工序挑一站；沒有候選站時回傳 null。</summary>
    public static string? PickPiece(IEnumerable<PieceCandidate> candidates) =>
        candidates
            .OrderBy(c => c.Rank)
            .ThenBy(c => c.MachineReady ? 0 : 1)
            .ThenBy(c => c.RouteMeters)
            .ThenBy(c => c.StationCode, StringComparer.Ordinal)
            .FirstOrDefault()?.StationCode;

    /// <summary>整爐工序挑一座爐；沒有候選爐時回傳 null。</summary>
    public static string? PickFurnace(IEnumerable<FurnaceCandidate> candidates) =>
        candidates
            .OrderBy(c => FurnaceTier(c))
            .ThenBy(c => FurnaceTier(c) switch
            {
                0 => -c.WaitingPieces,
                2 => c.WaitingPieces,
                _ => 0,
            })
            .ThenBy(c => c.RouteMeters)
            .ThenBy(c => c.StationCode, StringComparer.Ordinal)
            .FirstOrDefault()?.StationCode;

    /// <summary>0：沒有在跑、爐前有料；1：沒有在跑、爐前沒料；2：正在跑。</summary>
    private static int FurnaceTier(FurnaceCandidate candidate) =>
        candidate.Running ? 2 : candidate.WaitingPieces > 0 ? 0 : 1;
}
