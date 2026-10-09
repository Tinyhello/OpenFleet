using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OpenFleet.Contracts;
using OpenFleet.Messaging;
using OpenFleet.Simulator;

namespace OpenFleet.Tests;

/// <summary>模擬器的車輛行為：不需要 MQTT，直接推進模擬時間並檢查產生的回報。</summary>
public class SimulatedFleetTests
{
    private static readonly Position3 Pickup = new() { X = 2, Z = -1 };
    private static readonly Position3 Dropoff = new() { X = 8, Z = 5 };

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero));
    private readonly SimulatedFleet _fleet;

    public SimulatedFleetTests()
    {
        // 10 x 4 的矩形走道，一台車從 (0, 0) 出發，空車 1 m/s、滿載 30 件時 0.5 m/s，指令沒指定時交接 2 秒。
        _fleet = new SimulatedFleet(
            Options.Create(new SimulatorSettings
            {
                VehicleIds = ["agv"],
                Speed = 1,
                FullLoadSpeed = 0.5,
                LoadCapacity = 30,
                HandoverDuration = TimeSpan.FromSeconds(2),
                Route =
                [
                    new RoutePoint { X = 0, Z = 0 },
                    new RoutePoint { X = 10, Z = 0 },
                    new RoutePoint { X = 10, Z = 4 },
                    new RoutePoint { X = 0, Z = 4 },
                ],
            }),
            Options.Create(new MqttSettings { ProducerId = "simulator" }),
            _time);
    }

    [Fact]
    public void Vehicle_stays_still_until_it_gets_a_command()
    {
        var telemetry = Advance(5);

        Assert.Equal((0, 0, 0), (telemetry.Position.X, telemetry.Position.Z, telemetry.Speed));
        Assert.Empty(DrainEvents());
    }

    [Fact]
    public void Transport_reports_accepted_loaded_and_completed_as_the_vehicle_gets_there()
    {
        var command = Transport();
        Assert.False(_fleet.HandleCommand("agv", command));
        Assert.Equal(new[] { VehicleEventType.Accepted }, DrainEvents().Select(e => e.EventType));

        // 到取料點：沿走道 2 m + 離開走道 1 m = 3 m。抵達後還要 2 秒交接才算取料。
        var arriving = Advance(3);
        Assert.Equal((2, -1), (arriving.Position.X, arriving.Position.Z));
        Assert.Empty(DrainEvents());

        Advance(2);
        Assert.Equal(new[] { VehicleEventType.Loaded }, DrainEvents().Select(e => e.EventType));

        // 到卸料點：回走道 1 m、往東 8 m、轉角後往北 4 m、往西 2 m、離開走道 1 m = 16 m。
        Assert.Equal(1, Advance(15.5).Speed);
        var arrived = Advance(0.5);
        Assert.Equal(8, arrived.Position.X, 6);
        Assert.Equal(5, arrived.Position.Z, 6);
        Assert.Empty(DrainEvents());

        Advance(2);
        var completed = Assert.Single(DrainEvents());
        Assert.Equal((VehicleEventType.Completed, command.Payload.AssignmentId, command.Payload.CommandId),
            (completed.EventType, completed.AssignmentId, completed.CommandId));
        Assert.Equal(0, Advance(1).Speed);
    }

    [Fact]
    public void Redelivered_command_is_not_executed_again_and_resends_the_same_replies()
    {
        var command = Transport();
        _fleet.HandleCommand("agv", command);
        Assert.True(_fleet.TryPeekEvent(out _, out var first));
        _fleet.CompleteEvent();

        Assert.True(_fleet.HandleCommand("agv", command));

        Assert.True(_fleet.TryPeekEvent(out _, out var again));
        Assert.Equal(first.MessageId, again.MessageId);
        Assert.Equal(command.MessageId, again.CorrelationId);
    }

    [Fact]
    public void Busy_vehicle_rejects_another_transport_and_expired_commands_are_rejected()
    {
        _fleet.HandleCommand("agv", Transport());
        DrainEvents();

        _fleet.HandleCommand("agv", Transport());
        var busy = Assert.Single(DrainEvents());
        Assert.Equal((VehicleEventType.Rejected, "Busy"), (busy.EventType, busy.Reason));

        var late = Transport();
        _time.Advance(TimeSpan.FromSeconds(11));
        _fleet.HandleCommand("agv", late);
        var expired = Assert.Single(DrainEvents());
        Assert.Equal((VehicleEventType.Rejected, "Expired"), (expired.EventType, expired.Reason));
    }

    [Fact]
    public void Transport_takes_over_from_a_move_in_progress()
    {
        var move = Command(VehicleCommandType.MoveTo, destination: Dropoff);
        _fleet.HandleCommand("agv", move);
        Advance(1);
        DrainEvents();

        var transport = Transport();
        _fleet.HandleCommand("agv", transport);

        var events = DrainEvents();
        Assert.Equal(
            new[] { (move.Payload.CommandId, VehicleEventType.Failed), (transport.Payload.CommandId, VehicleEventType.Accepted) },
            events.Select(e => (e.CommandId, e.EventType)));
        Assert.Equal("Superseded", events[0].Reason);
    }

    [Fact]
    public void Move_ends_with_completed_at_the_destination()
    {
        _fleet.HandleCommand("agv", Command(VehicleCommandType.MoveTo, destination: new Position3 { X = 5, Z = 2 }));

        // 走道上最近的點是 (5, 0)：沿走道 5 m，再往內 2 m。
        var arrived = Advance(7);

        Assert.Equal((5, 2), (arrived.Position.X, arrived.Position.Z));
        Assert.Equal(new[] { VehicleEventType.Accepted, VehicleEventType.Completed }, DrainEvents().Select(e => e.EventType));
    }

    [Fact]
    public void Telemetry_carries_the_route_ahead_and_previews_the_next_leg_while_loading()
    {
        Assert.Empty(Advance(1).Route);

        _fleet.HandleCommand("agv", Transport(handoverSeconds: 2));

        // 往取料點：走道上 (2, 0) 再到 (2, -1)；走過的點會從路徑裡消失。
        Assert.Equal(new[] { (2d, 0d), (2d, -1d) }, Points(Advance(1)));
        Assert.Equal(new[] { (2d, -1d) }, Points(Advance(1.5)));

        // 上料中：預先顯示上料完成後要走到卸料點的路徑。
        var loading = Advance(1.5);
        Assert.Equal(VehicleActivity.Loading, loading.Activity);
        Assert.Equal(new[] { (2d, 0d), (10d, 0d), (10d, 4d), (8d, 4d), (8d, 5d) }, Points(loading));

        // 下料中與完成後沒有要去的地方。
        Advance(1 + 16);
        var unloading = Advance(1);
        Assert.Equal(VehicleActivity.Unloading, unloading.Activity);
        Assert.Empty(unloading.Route);
        Assert.Empty(Advance(2).Route);

        static IEnumerable<(double, double)> Points(VehicleTelemetry telemetry) => telemetry.Route.Select(p => (p.X, p.Z));
    }

    [Fact]
    public void Time_scale_speeds_up_driving_and_handover_together()
    {
        _fleet.TimeScale = 10;
        _fleet.HandleCommand("agv", Transport(handoverSeconds: 4));
        DrainEvents();

        // 到取料點 3 m 只要 0.3 秒；上料 4 秒只要 0.4 秒。回報的車速仍是 1 倍時的數值。
        var arrived = Advance(0.3);
        Assert.Equal((2, -1, 1d), (arrived.Position.X, arrived.Position.Z, arrived.Speed));

        var loading = Advance(0.2);
        Assert.Equal(VehicleActivity.Loading, loading.Activity);
        Assert.Equal(0.5, loading.ActivityProgress, 6);

        Advance(0.2);
        Assert.Equal(new[] { VehicleEventType.Loaded }, DrainEvents().Select(e => e.EventType));
    }

    [Fact]
    public void Loaded_vehicle_is_slower_and_reports_its_load_and_handover_progress()
    {
        // 15 件（半載）：車速 0.75 m/s；交接時間由指令指定為 4 秒。
        _fleet.HandleCommand("agv", Transport(loadQuantity: 15, handoverSeconds: 4));

        var empty = Advance(1);
        Assert.Equal((1d, VehicleActivity.Moving, 0), (empty.Speed, empty.Activity, empty.LoadQuantity));

        // 抵達取料點後上料：進度隨時間增加，交接完成前車上還沒有貨。
        Advance(2);
        var loading = Advance(1);
        Assert.Equal((VehicleActivity.Loading, 0, 0d), (loading.Activity, loading.LoadQuantity, loading.Speed));
        Assert.Equal(0.25, loading.ActivityProgress, 6);

        Advance(3);
        var loaded = Advance(1);
        Assert.Equal((VehicleActivity.Moving, 15, "upper-punch"), (loaded.Activity, loaded.LoadQuantity, loaded.LoadCode));
        Assert.Equal(0.75, loaded.Speed, 6);

        // 16 m 的路程以 0.75 m/s 行駛，約 21.3 秒；下料時貨還在車上，完成後才變回空車。
        Advance(21);
        var unloading = Advance(2);
        Assert.Equal((VehicleActivity.Unloading, 15), (unloading.Activity, unloading.LoadQuantity));

        var done = Advance(4);
        Assert.Equal((VehicleActivity.Idle, 0, null), (done.Activity, done.LoadQuantity, done.LoadCode));
        Assert.Equal(VehicleEventType.Completed, DrainEvents()[^1].EventType);
    }

    private MessageEnvelope<VehicleCommand> Transport(int loadQuantity = 0, double? handoverSeconds = null)
    {
        var command = Command(VehicleCommandType.Transport, Pickup, Dropoff, Guid.NewGuid());
        command.Payload.LoadQuantity = loadQuantity;
        command.Payload.LoadCode = loadQuantity > 0 ? "upper-punch" : null;
        command.Payload.HandoverSeconds = handoverSeconds;
        return command;
    }

    private MessageEnvelope<VehicleCommand> Command(VehicleCommandType type, Position3? pickup = null, Position3? destination = null, Guid? assignmentId = null) =>
        MessageEnvelope.Create("backend", new VehicleCommand
        {
            CommandId = Guid.NewGuid(),
            AssignmentId = assignmentId,
            CommandType = type,
            ExpiresAt = _time.GetUtcNow().AddSeconds(10),
            Pickup = pickup,
            Destination = destination,
        }, _time.GetUtcNow());

    /// <summary>以 0.1 秒為一步推進（和實際的遙測迴圈一樣是離散步進），回傳最後一筆遙測。</summary>
    private VehicleTelemetry Advance(double seconds)
    {
        VehicleTelemetry last = null!;
        for (var i = 0; i < (int)Math.Round(seconds * 10); i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(100));
            last = _fleet.Advance(TimeSpan.FromMilliseconds(100)).Single().Telemetry;
        }

        return last;
    }

    private List<VehicleEvent> DrainEvents()
    {
        var events = new List<VehicleEvent>();
        while (_fleet.TryPeekEvent(out _, out var envelope))
        {
            events.Add(envelope.Payload);
            _fleet.CompleteEvent();
        }

        return events;
    }
}
