namespace OpenFleet.Api.Production;

/// <summary>
/// 逐件工序的站：空下來的機台接哪一框（P6）。同一站的機台共用備料區，框在站上排隊，哪一台空下來就由它接手
/// （使用者 2026-10-22：同一站的機台都能做所有規格）。
/// 先看框的優先順序（<see cref="SchedulePriority"/>：急單、交期早的先做，使用者 2026-10-22）；
/// 優先順序一樣時，每台機台各自看自己上一框做的是什麼（P4 的規則，改成每台各自算）：
/// 1. 同批次、不用換線 → 2. 機床：不用換線；其他站：同批次 → 3. 機床：同批次；其他站：同零件同桿徑 → 4. 同桿徑 → 5. 到站順序。
/// 機台還沒做過任何一框時不用換線。
/// 同一站同時有好幾台空著時，每次先配排名最好的（機台, 框）組合，所以不用換線的框會交給上一框相同的那一台；
/// 排名相同時先到站的框優先，再來是編號小的機台。
/// </summary>
public static class MachineAssignment
{
    /// <summary>機台上一框的批次、零件與桿徑。</summary>
    public sealed record Setup(string BatchNumber, string PartCode, int RodDiameterMm);

    /// <summary>空著的機台；<see cref="Last"/> 是它上一框做的東西，還沒做過時為 null。</summary>
    public sealed record FreeMachine(string Code, int Number, Setup? Last);

    /// <summary>
    /// 機床換線（使用者 2026-10-10）：24 mm 與 30 mm 的工控程式不同，上沖、中模、下沖之間切換也要換。
    /// 零件或桿徑和這台機台的上一框不同就要換；機台的第一框不用。
    /// </summary>
    public static bool NeedsChangeover(Setup? last, string partCode, int rodDiameterMm) =>
        last is not null && (last.PartCode != partCode || last.RodDiameterMm != rodDiameterMm);

    /// <summary>
    /// 把排隊的框（<paramref name="waiting"/>，依到站順序）配給空著的機台，回傳配好的組合；機台或框用完就停。
    /// <paramref name="isLathe"/>：有刀具的工序（粗加工、精加工）才會換線，排序先看換線，其他站先看同批次。
    /// <paramref name="priority"/>：每一框的優先順序；沒給時都一樣。
    /// <paramref name="canPair"/>：這台機台能不能接這一框（例如框已經排在另一座站前就不能給這台）；沒給時都能接。
    /// </summary>
    public static List<(FreeMachine Machine, T Item)> Assign<T>(
        IReadOnlyList<FreeMachine> machines, IReadOnlyList<T> waiting, Func<T, Setup> describe, bool isLathe,
        Func<T, SchedulePriority>? priority = null, Func<FreeMachine, T, bool>? canPair = null)
    {
        var free = machines.ToList();
        var queue = waiting
            .Select((item, index) => (Item: item, Index: index, Setup: describe(item), Priority: priority?.Invoke(item) ?? SchedulePriority.None))
            .ToList();
        var assigned = new List<(FreeMachine, T)>();
        while (free.Count > 0 && queue.Count > 0)
        {
            var pairs = free.SelectMany(machine => queue
                    .Where(entry => canPair is null || canPair(machine, entry.Item))
                    .Select(entry => (Machine: machine, Entry: entry)))
                .ToList();
            if (pairs.Count == 0)
            {
                break;
            }

            var best = pairs
                .OrderBy(pair => pair.Entry.Priority)
                .ThenBy(pair => Rank(pair.Machine.Last, pair.Entry.Setup, isLathe))
                .ThenBy(pair => pair.Entry.Index)
                .ThenBy(pair => pair.Machine.Number)
                .First();
            assigned.Add((best.Machine, best.Entry.Item));
            free.Remove(best.Machine);
            queue.Remove(best.Entry);
        }

        return assigned;
    }

    /// <summary>
    /// 越小越優先，對應類別說明的 1～5。同一站有好幾台機台時，站的排名取其中最好的（<see cref="StationSelector"/> 用）。
    /// </summary>
    internal static int Rank(Setup? last, Setup bin, bool isLathe)
    {
        var sameSetup = isLathe
            ? !NeedsChangeover(last, bin.PartCode, bin.RodDiameterMm)
            : last is not null && last.PartCode == bin.PartCode && last.RodDiameterMm == bin.RodDiameterMm;
        var sameBatch = last is not null && last.BatchNumber == bin.BatchNumber;
        if (sameBatch && sameSetup)
        {
            return 0;
        }

        if (isLathe ? sameSetup : sameBatch)
        {
            return 1;
        }

        if (isLathe ? sameBatch : sameSetup)
        {
            return 2;
        }

        return last is not null && last.RodDiameterMm == bin.RodDiameterMm ? 3 : 4;
    }
}
