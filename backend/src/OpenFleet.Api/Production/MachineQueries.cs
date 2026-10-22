using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Production;

/// <summary>加工推進與缺車統計共用的機台查詢（唯讀）。</summary>
internal static class MachineQueries
{
    /// <summary>
    /// 做完的那一框還沒被搬走的機台（機邊的成品台只放一框，搬走之前不接下一框），只看 <paramref name="stationCodes"/> 這幾站。
    /// 一站只有一台時，站上任何一框在等搬走都算它的（品管分出來的反工框、包裝站的出貨箱也是）；
    /// 有好幾台時，看那一框最後是在哪一台做完的。
    /// </summary>
    public static async Task<List<string>> BlockedMachinesAsync(
        OpenFleetDbContext db, IReadOnlyCollection<string> stationCodes, IEnumerable<Machine> machines, CancellationToken cancellationToken)
    {
        var waiting = await db.Lots.AsNoTracking()
            .Where(l => l.Status == LotStatus.AwaitingTransport && stationCodes.Contains(l.StationCode!))
            .Select(l => new { l.Id, Station = l.StationCode! })
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0)
        {
            return [];
        }

        var byStation = machines.GroupBy(m => m.StationCode).ToDictionary(g => g.Key, g => g.ToList());
        var finishedOn = await FinishedOnAsync(db, waiting.Select(l => l.Id).ToList(), cancellationToken);
        var blocked = new List<string>();
        foreach (var lot in waiting)
        {
            if (!byStation.TryGetValue(lot.Station, out var atStation))
            {
                continue;
            }

            if (atStation.Count == 1)
            {
                blocked.Add(atStation[0].Code);
            }
            else if (finishedOn.TryGetValue(lot.Id, out var machine) && atStation.Any(m => m.Code == machine))
            {
                blocked.Add(machine);
            }
        }

        return blocked;
    }

    /// <summary>每一框最後是在哪一台機台做完的（只看有機台、已完成的逐件加工）；沒有做過的框不在結果裡。</summary>
    public static async Task<Dictionary<Guid, string>> FinishedOnAsync(
        OpenFleetDbContext db, IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken)
    {
        var finished = await db.Operations.AsNoTracking()
            .Where(o => lotIds.Contains(o.LotId) && o.MachineCode != null && o.CompletedAt != null)
            .Select(o => new { o.LotId, MachineCode = o.MachineCode!, CompletedAt = o.CompletedAt!.Value })
            .ToListAsync(cancellationToken);
        return finished.GroupBy(o => o.LotId).ToDictionary(g => g.Key, g => g.MaxBy(o => o.CompletedAt)!.MachineCode);
    }
}
