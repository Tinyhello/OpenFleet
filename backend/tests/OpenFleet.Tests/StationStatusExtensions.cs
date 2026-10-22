using OpenFleet.Contracts;

namespace OpenFleet.Tests;

/// <summary>測試讀站點狀態的捷徑。</summary>
internal static class StationStatusExtensions
{
    /// <summary>這一站的第 <paramref name="number"/> 號機；一站只有一台時就是那一台。</summary>
    public static MachineStatusDto Machine(this StationStatusDto station, int number = 1) =>
        station.Machines.Single(m => m.Number == number);
}
