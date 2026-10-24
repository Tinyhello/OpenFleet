using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OpenFleet.Contracts;
using OpenFleet.Messaging;
using OpenFleet.Simulator;

namespace OpenFleet.Tests;

/// <summary>
/// 模擬器跟著佈局走（P7 第 2 步 5.13）：車輛清單與車位來自後端發布的佈局訊息，不再由設定決定。
/// </summary>
public class SimulatedLayoutTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero));
    private readonly SimulatedFleet _fleet;

    public SimulatedLayoutTests()
    {
        _fleet = new SimulatedFleet(
            Options.Create(new SimulatorSettings()),
            Options.Create(new MqttSettings { ProducerId = "simulator" }),
            _time);
    }

    private static LayoutRevisionMessage Layout(Guid layoutId, DateTimeOffset appliedAt, params (string Id, double X, double Z)[] vehicles) => new()
    {
        LayoutId = layoutId,
        Name = "test",
        AppliedAt = appliedAt,
        Vehicles = vehicles.Select(v => new LayoutVehicle { VehicleId = v.Id, Type = VehicleType.Large, HomeX = v.X, HomeZ = v.Z }).ToList(),
    };

    private static VehicleClearance Clearance(Guid plan, params (double X, double Z)[] path) => new()
    {
        PlanId = plan,
        Revision = 1,
        Path = path.Select(p => new Position3 { X = p.X, Z = p.Z }).ToList(),
        ReleasedCount = path.Length,
    };

    [Fact]
    public void Vehicles_come_from_the_layout_and_start_at_their_bays()
    {
        _fleet.ApplyLayout(Layout(Guid.NewGuid(), _time.GetUtcNow(), ("agv-01", -10.3, 0.2), ("agv-02", -8, 0.2)));

        Assert.True(_fleet.Contains("agv-01"));
        Assert.True(_fleet.Contains("agv-02"));
        var positions = _fleet.Advance(TimeSpan.FromMilliseconds(100)).ToDictionary(t => t.VehicleId, t => t.Telemetry.Position);
        Assert.Equal((-10.3, 0.2), (positions["agv-01"].X, positions["agv-01"].Z));
        Assert.Equal((-8, 0.2), (positions["agv-02"].X, positions["agv-02"].Z));
    }

    [Fact]
    public void New_version_sends_every_vehicle_home_and_starts_a_new_session()
    {
        var applied = _time.GetUtcNow();
        _fleet.ApplyLayout(Layout(Guid.NewGuid(), applied, ("agv-01", -10.3, 0.2), ("agv-02", -8, 0.2)));
        var session = _fleet.SessionId;

        // agv-01 開出去一段（路徑給到 3 m 外）。
        _fleet.ApplyClearance("agv-01", Clearance(Guid.NewGuid(), (-10.3, 0.2), (-10.3, 3)));
        _fleet.Advance(TimeSpan.FromSeconds(1));

        // 新版本只剩 agv-01，位置也換了：agv-02 移除，agv-01 回到新車位。
        _fleet.ApplyLayout(Layout(Guid.NewGuid(), applied.AddMinutes(1), ("agv-01", -5, 0.2)));

        Assert.NotEqual(session, _fleet.SessionId);
        Assert.False(_fleet.Contains("agv-02"));
        var position = _fleet.Advance(TimeSpan.Zero).Single().Telemetry.Position;
        Assert.Equal((-5, 0.2), (position.X, position.Z));
    }

    [Fact]
    public void Same_version_only_adds_and_removes_and_keeps_positions()
    {
        var id = Guid.NewGuid();
        var applied = _time.GetUtcNow();
        _fleet.ApplyLayout(Layout(id, applied, ("agv-01", -10.3, 0.2)));
        var session = _fleet.SessionId;

        // 同一版本（重新連線時收到的 retained）：多一台；agv-01 的車位座標即使不同也不動它。
        _fleet.ApplyLayout(Layout(id, applied, ("agv-01", 99, 99), ("agv-03", -5.7, 0.2)));

        Assert.Equal(session, _fleet.SessionId);
        var positions = _fleet.Advance(TimeSpan.Zero).ToDictionary(t => t.VehicleId, t => t.Telemetry.Position);
        Assert.Equal((-10.3, 0.2), (positions["agv-01"].X, positions["agv-01"].Z));
        Assert.Equal((-5.7, 0.2), (positions["agv-03"].X, positions["agv-03"].Z));
    }

    // 測試與離線用的初始清單：兩台車各有指定的起點（離自己的車位都有一段距離）。
    private SimulatedFleet FleetWithInitialVehicles() => new(
        Options.Create(new SimulatorSettings
        {
            VehicleIds = ["agv-01", "agv-02"],
            StartPositions =
            {
                ["agv-01"] = new RoutePoint { X = -10.3, Z = 3 },
                ["agv-02"] = new RoutePoint { X = 4, Z = 9.4 },
            },
        }),
        Options.Create(new MqttSettings { ProducerId = "simulator" }),
        _time);

    [Fact]
    public void The_first_layout_keeps_the_start_of_vehicles_from_the_initial_list()
    {
        var fleet = FleetWithInitialVehicles();
        var session = fleet.SessionId;

        // 放行是後端在第一個佈局之前就給的：保留起點不代表保留工作與路徑，新 Session 之後都要清掉。
        fleet.ApplyClearance("agv-02", Clearance(Guid.NewGuid(), (4, 9.4), (4, 3)));
        fleet.ApplyLayout(Layout(Guid.NewGuid(), _time.GetUtcNow(), ("agv-01", -10.3, 0.2), ("agv-02", -8, 0.2), ("agv-03", -5.7, 0.2)));

        // 清單裡有的車留在起點；清單裡沒有的車（agv-03）從車位開始。
        Assert.NotEqual(session, fleet.SessionId);
        var telemetry = fleet.Advance(TimeSpan.FromMilliseconds(100)).ToDictionary(t => t.VehicleId, t => t.Telemetry);
        Assert.Equal((-10.3, 3), (telemetry["agv-01"].Position.X, telemetry["agv-01"].Position.Z));
        Assert.Equal((4, 9.4), (telemetry["agv-02"].Position.X, telemetry["agv-02"].Position.Z));
        Assert.Equal((-5.7, 0.2), (telemetry["agv-03"].Position.X, telemetry["agv-03"].Position.Z));
        Assert.Null(telemetry["agv-02"].RoutePlanId);
        Assert.All(telemetry.Values, t => Assert.Equal(fleet.SessionId, t.SessionId));
    }

    [Fact]
    public void A_later_version_sends_vehicles_from_the_initial_list_home()
    {
        var fleet = FleetWithInitialVehicles();
        var applied = _time.GetUtcNow();
        fleet.ApplyLayout(Layout(Guid.NewGuid(), applied, ("agv-01", -10.3, 0.2), ("agv-02", -8, 0.2)));
        var session = fleet.SessionId;
        Assert.Equal(4, fleet.Advance(TimeSpan.Zero).Single(t => t.VehicleId == "agv-02").Telemetry.Position.X);

        // 第二個版本（使用者換了佈局）：不管起點在哪裡，每一台都回到車位。
        fleet.ApplyLayout(Layout(Guid.NewGuid(), applied.AddMinutes(1), ("agv-01", -10.3, 0.2), ("agv-02", -8, 0.2)));

        Assert.NotEqual(session, fleet.SessionId);
        var positions = fleet.Advance(TimeSpan.Zero).ToDictionary(t => t.VehicleId, t => t.Telemetry.Position);
        Assert.Equal((-10.3, 0.2), (positions["agv-01"].X, positions["agv-01"].Z));
        Assert.Equal((-8, 0.2), (positions["agv-02"].X, positions["agv-02"].Z));
    }

    [Fact]
    public void Clearance_for_a_vehicle_that_left_the_layout_is_ignored()
    {
        _fleet.ApplyLayout(Layout(Guid.NewGuid(), _time.GetUtcNow(), ("agv-01", -10.3, 0.2)));

        _fleet.ApplyClearance("agv-02", Clearance(Guid.NewGuid(), (0, 0), (1, 0)));

        Assert.False(_fleet.Contains("agv-02"));
    }
}
