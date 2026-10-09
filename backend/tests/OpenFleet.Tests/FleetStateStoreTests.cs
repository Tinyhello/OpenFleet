using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OpenFleet.Api.Fleet;
using OpenFleet.Contracts;

namespace OpenFleet.Tests;

public class FleetStateStoreTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-09-26T00:00:00Z"));
    private readonly StateChangeQueue _changes = new();
    private readonly FleetStateStore _store;

    private static readonly IOptions<FleetOptions> Options = Microsoft.Extensions.Options.Options.Create(new FleetOptions
    {
        VehicleTypes = { ["agv-large"] = VehicleType.Large, ["agv-small"] = VehicleType.Small },
    });

    public FleetStateStoreTests() => _store = new FleetStateStore(_time, _changes, Options);

    private static VehicleTelemetry Telemetry(Guid session, long sequence, double x = 0) =>
        new() { SessionId = session, Sequence = sequence, Position = new Position3 { X = x } };

    [Fact]
    public void Older_or_repeated_sequence_in_same_session_is_ignored()
    {
        var session = Guid.NewGuid();

        Assert.Equal(TelemetryOutcome.Applied, _store.ApplyTelemetry("agv", Telemetry(session, 5, x: 5)));
        Assert.Equal(TelemetryOutcome.Ignored, _store.ApplyTelemetry("agv", Telemetry(session, 5, x: 99)));
        Assert.Equal(TelemetryOutcome.Ignored, _store.ApplyTelemetry("agv", Telemetry(session, 3, x: 99)));

        var state = _store.Find("agv")!;
        Assert.Equal(5, state.TelemetrySequence);
        Assert.Equal(5, state.Position.X);
        Assert.Equal(1, state.Version);
    }

    [Fact]
    public void Vehicle_type_comes_from_fleet_options_and_unlisted_vehicles_are_unknown()
    {
        _store.ApplyTelemetry("agv-large", Telemetry(Guid.NewGuid(), 1));
        _store.ApplyTelemetry("agv-small", Telemetry(Guid.NewGuid(), 1));
        _store.ApplyTelemetry("agv-other", Telemetry(Guid.NewGuid(), 1));

        Assert.Equal(VehicleType.Large, _store.Find("agv-large")!.VehicleType);
        Assert.Equal(VehicleType.Small, _store.Find("agv-small")!.VehicleType);
        Assert.Equal(VehicleType.Unknown, _store.Find("agv-other")!.VehicleType);
    }

    [Fact]
    public void States_carry_the_store_epoch_and_a_restarted_store_gets_a_new_one()
    {
        _store.ApplyTelemetry("agv", Telemetry(Guid.NewGuid(), 1));
        Assert.NotEqual(Guid.Empty, _store.StateEpoch);
        Assert.Equal(_store.StateEpoch, _store.Find("agv")!.StateEpoch);

        // 重啟後 Version 會從頭算，顯示端要靠 Epoch 分辨，而不是比 Version 大小。
        var restarted = new FleetStateStore(_time, new StateChangeQueue(), Options);
        restarted.ApplyTelemetry("agv", Telemetry(Guid.NewGuid(), 1));
        Assert.NotEqual(_store.StateEpoch, restarted.Find("agv")!.StateEpoch);
    }

    [Fact]
    public void New_session_is_accepted_and_late_packets_from_old_session_are_dropped()
    {
        var oldSession = Guid.NewGuid();
        var newSession = Guid.NewGuid();

        _store.ApplyTelemetry("agv", Telemetry(oldSession, 100));
        Assert.Equal(TelemetryOutcome.Applied, _store.ApplyTelemetry("agv", Telemetry(newSession, 1)));
        Assert.Equal(TelemetryOutcome.Ignored, _store.ApplyTelemetry("agv", Telemetry(oldSession, 101)));

        var state = _store.Find("agv")!;
        Assert.Equal(newSession, state.TelemetrySessionId);
        Assert.Equal(1, state.TelemetrySequence);
    }

    [Fact]
    public void Vehicle_goes_offline_after_timeout_and_back_online_on_next_report()
    {
        var session = Guid.NewGuid();
        _store.ApplyTelemetry("agv", Telemetry(session, 1));

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, _store.MarkStaleVehiclesOffline(TimeSpan.FromSeconds(3)));

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, _store.MarkStaleVehiclesOffline(TimeSpan.FromSeconds(3)));
        Assert.Equal(VehicleConnectivity.Offline, _store.Find("agv")!.Connectivity);

        _store.ApplyTelemetry("agv", Telemetry(session, 2));
        Assert.Equal(VehicleConnectivity.Online, _store.Find("agv")!.Connectivity);
    }

    [Fact]
    public void Every_change_is_queued_as_an_independent_copy()
    {
        _store.ApplyTelemetry("agv", Telemetry(Guid.NewGuid(), 1, x: 1));

        Assert.True(_changes.Reader.TryRead(out var queued));
        queued.Position.X = 42;
        queued.Route.Add(new Position3 { X = 9 });

        Assert.Equal(1, _store.Find("agv")!.Position.X);
        Assert.Empty(_store.Find("agv")!.Route);
    }
}
