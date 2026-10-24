using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Api.Fleet;
using OpenFleet.Api.Traffic;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Layout;

/// <summary>
/// 從資料庫載入執行期的狀態：路網、交通控制、目前佈局與車輛清單。啟動時與每次套用後呼叫。
/// 套用的交易已經提交，所以這裡讀到的一定是新的；記憶體裡的東西永遠和資料表一致。
/// </summary>
public sealed class LayoutRuntime(
    OpenFleetDbContext db,
    RoadNetwork network,
    TrafficController traffic,
    ActiveLayout active,
    FleetStateStore fleet)
{
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var layout = await db.FactoryLayouts.AsNoTracking().SingleOrDefaultAsync(l => l.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("No active factory layout.");
        var nodes = await db.RouteNodes.AsNoTracking().Select(n => new RoadNode(n.Code, n.X, n.Z)).ToListAsync(cancellationToken);
        var edges = await db.RouteEdges.AsNoTracking().Select(e => new { e.FromCode, e.ToCode }).ToListAsync(cancellationToken);
        var stations = await db.Stations.AsNoTracking().ToListAsync(cancellationToken);
        var vehicles = await db.Vehicles.AsNoTracking().ToListAsync(cancellationToken);

        network.Load(nodes, edges.Select(e => (e.FromCode, e.ToCode)));
        traffic.Reset();

        // 交接點與車位一定落在路網節點上（驗證保證），所以取最近的節點就是它。
        var stationNodes = stations.ToDictionary(s => s.Code, s => network.Nearest(s.HandoffX, s.HandoffZ).Code, StringComparer.Ordinal);
        var inLayout = vehicles.Where(v => v.InLayout).OrderBy(v => v.Id, StringComparer.Ordinal).ToList();
        var homeNodes = inLayout.ToDictionary(v => v.Id, v => network.Nearest(v.HomeX!.Value, v.HomeZ!.Value).Code, StringComparer.Ordinal);
        var retired = vehicles.Where(v => !v.InLayout).Select(v => v.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();

        var revision = new LayoutRevisionMessage
        {
            LayoutId = layout.Id,
            Name = layout.Name,
            AppliedAt = layout.AppliedAt ?? layout.UpdatedAt,
            Vehicles = inLayout
                .Select(v => new LayoutVehicle { VehicleId = v.Id, Type = v.Type, HomeX = v.HomeX!.Value, HomeZ = v.HomeZ!.Value })
                .ToList(),
        };

        active.Load(revision, stationNodes, homeNodes, retired);
        fleet.SetLayoutVehicles(inLayout.ToDictionary(v => v.Id, v => v.Type, StringComparer.Ordinal), retired);
    }
}
