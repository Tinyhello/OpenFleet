using OpenFleet.Api.Data;
using OpenFleet.Contracts;
using OpenFleet.Layout;

namespace OpenFleet.Api.Layout;

/// <summary>由佈局文件推導出的資料表列（純計算，不碰資料庫；寫入由 <see cref="LayoutWriter"/> 負責）。</summary>
public sealed class DerivedLayout
{
    public List<Station> Stations { get; } = [];
    public List<Machine> Machines { get; } = [];

    /// <summary>需要刀具的機台代碼（工序 UsesTool 的機台）。</summary>
    public List<string> ToolMachineCodes { get; } = [];
    public List<RouteNode> RouteNodes { get; } = [];
    public List<RouteEdge> RouteEdges { get; } = [];

    /// <summary>佈局裡的車：每個 AGV 車位一台。</summary>
    public List<Vehicle> Vehicles { get; } = [];
}

/// <summary>
/// 佈局 → 資料表（P7 第 2 步 5.8）：站點、機台、刀具、路網與車輛。站名、編號與交接點在這裡算好，
/// 之後的派工與畫面只讀資料表，不再認識佈局文件。
/// </summary>
public static class LayoutDeriver
{
    public static DerivedLayout Derive(FactoryLayoutDocument document, IEnumerable<ProcessStep> steps)
    {
        var stepByCode = steps.ToDictionary(s => s.Code, StringComparer.Ordinal);
        var derived = new DerivedLayout();

        foreach (var module in document.Modules.Where(m => ModuleCatalog.IsStation(m.Kind)))
        {
            var spec = ModuleCatalog.Get(module.Kind);
            var handoff = LayoutGeometry.HandoffPoint(module)
                ?? throw new InvalidOperationException($"Station {module.Code} has no handoff point.");
            var step = spec.ProcessCode is null ? null : stepByCode[spec.ProcessCode];
            var name = ModuleNaming.DisplayName(document, module);

            derived.Stations.Add(new Station
            {
                Code = module.Code,
                Name = name,
                Kind = module.Kind,
                StepSequence = step?.Sequence,
                X = module.X,
                Z = module.Z,
                Rotation = module.Rotation,
                HandoffX = handoff.X,
                HandoffZ = handoff.Z,
                InboundCapacity = spec.InboundCapacity,
                OutboundCapacity = spec.OutboundCapacity,
            });

            // 逐件工序的站：每一站就是一台機台（代碼相同），有刀具的工序另外記一筆刀具狀態。
            // 整爐工序（淬火、回火、電鍍）的爐不是機台（和第 1b 步相同，見第 4 節）。
            if (step is null || step.Kind != ProcessStepKind.Piece)
            {
                continue;
            }

            // 編號是「這一站的第幾台」，每站一台所以一律是 1（和第 1b 步相同）。不用代碼的數字：
            // 派工平手時會用它排序（MachineAssignment），改了會改變派工結果。站名的號數由 ModuleNaming 另外算。
            derived.Machines.Add(new Machine
            {
                Code = module.Code,
                StationCode = module.Code,
                Number = 1,
                Name = name,
            });

            if (step.UsesTool)
            {
                derived.ToolMachineCodes.Add(module.Code);
            }
        }

        foreach (var module in document.Modules.Where(m => ModuleCatalog.IsBay(m.Kind)))
        {
            var home = LayoutGeometry.HandoffPoint(module)
                ?? throw new InvalidOperationException($"AGV bay {module.Code} has no handoff point.");
            derived.Vehicles.Add(new Vehicle
            {
                Id = module.Code,
                Type = module.Kind == ModuleKind.AgvBayLarge ? VehicleType.Large : VehicleType.Small,
                DispatchEnabled = true,
                InLayout = true,
                HomeX = home.X,
                HomeZ = home.Z,
            });
        }

        derived.RouteNodes.AddRange(document.RoadNodes.Select(n => new RouteNode { Code = n.Code, X = n.X, Z = n.Z }));
        derived.RouteEdges.AddRange(document.RoadEdges.Select(e => new RouteEdge { FromCode = e.From, ToCode = e.To }));
        return derived;
    }
}
