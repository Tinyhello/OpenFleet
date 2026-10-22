using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OpenFleet.Contracts;
using OpenFleet.Messaging;
using OpenFleet.Simulator;

namespace OpenFleet.Tests;

/// <summary>
/// 模擬器的車輛行為：不需要 MQTT，直接給放行與指令、推進模擬時間，檢查位置、遙測與回報。
/// 車輛自己不規劃路線，所以每個測試都要先給放行，車才會動。
/// </summary>
public class SimulatedFleetTests
{
    private static readonly Position3 Pickup = new() { X = 2, Z = -1 };
    private static readonly Position3 Dropoff = new() { X = 8, Z = 5 };

    // 取料點在 (2, -1)：先沿走道到 (2, 0) 再進去，共 3 m。
    private static readonly (double, double)[] ToPickup = [(0, 0), (2, 0), (2, -1)];

    // 卸料點在 (8, 5)：出來、往東 6 m、往北 4 m、再進去 1 m，共 12 m。
    private static readonly (double, double)[] ToDropoff = [(2, -1), (2, 0), (8, 0), (8, 4), (8, 5)];

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero));
    private readonly SimulatedFleet _fleet;

    public SimulatedFleetTests()
    {
        // 一台車從 (0, 0) 出發，空車 1 m/s、滿載 30 件時 0.5 m/s，指令沒指定時交接 2 秒。
        _fleet = new SimulatedFleet(
            Options.Create(new SimulatorSettings
            {
                VehicleIds = ["agv"],
                StartPositions = { ["agv"] = new RoutePoint { X = 0, Z = 0 } },
                Speed = 1,
                FullLoadSpeed = 0.5,
                LoadCapacity = 30,
                HandoverDuration = TimeSpan.FromSeconds(2),
            }),
            Options.Create(new MqttSettings { ProducerId = "simulator" }),
            _time);
    }

    [Fact]
    public void Vehicle_does_not_move_without_a_clearance_even_after_accepting_a_transport()
    {
        _fleet.HandleCommand("agv", Transport());

        var telemetry = Advance(5);

        Assert.Equal((0, 0, 0, VehicleActivity.Idle), (telemetry.Position.X, telemetry.Position.Z, telemetry.Speed, telemetry.Activity));
        Assert.Null(telemetry.RoutePlanId);
        Assert.Equal(new[] { VehicleEventType.Accepted }, DrainEvents().Select(e => e.EventType));
    }

    [Fact]
    public void Vehicle_drives_only_as_far_as_it_has_been_cleared_and_reports_its_progress()
    {
        var plan = Guid.NewGuid();

        // 路徑有 4 個點，只放行到第 2 個：開到 (4, 0) 就停。
        _fleet.ApplyClearance("agv", Clearance(plan, revision: 1, released: 2, (0, 0), (4, 0), (8, 0), (8, 4)));
        var moving = Advance(2);
        Assert.Equal(2, moving.Position.X, 6);
        Assert.Equal((1d, VehicleActivity.Moving), (moving.Speed, moving.Activity));
        Assert.Equal((plan, 1L, 1), (moving.RoutePlanId, moving.RouteRevision, moving.RouteReachedCount));

        var waiting = Advance(5);
        Assert.Equal((4d, 0d, 0d), (waiting.Position.X, waiting.Position.Z, waiting.Speed));
        Assert.Equal(2, waiting.RouteReachedCount);

        // 剩下的路徑照樣回報（畫面可以顯示），但標明其中沒有任何一點已放行。
        Assert.Equal(new[] { (8d, 0d), (8d, 4d) }, Points(waiting));
        Assert.Equal(0, waiting.RouteReleasedCount);

        // 放行延伸到終點：繼續開，走過轉角。
        _fleet.ApplyClearance("agv", Clearance(plan, revision: 2, released: 4, (0, 0), (4, 0), (8, 0), (8, 4)));
        Assert.Equal(2, Advance(1).RouteReleasedCount);
        var arrived = Advance(7);
        Assert.Equal((8d, 4d, 4), (arrived.Position.X, arrived.Position.Z, arrived.RouteReachedCount));
        Assert.Empty(arrived.Route);
    }

    [Fact]
    public void Stale_clearances_are_ignored_and_a_reroute_keeps_the_progress_already_made()
    {
        var plan = Guid.NewGuid();
        _fleet.ApplyClearance("agv", Clearance(plan, revision: 2, released: 2, (0, 0), (4, 0), (8, 0)));
        Advance(4);

        // 重送或亂序的舊版本不能把放行範圍或路徑改回去。
        _fleet.ApplyClearance("agv", Clearance(plan, revision: 1, released: 1, (0, 0), (4, 0), (4, 4)));
        Assert.Equal(new[] { (8d, 0d) }, Points(Advance(1)));

        // 改道：已放行、已走過的 (0,0)、(4,0) 不變，只換掉後面；車從 (4,0) 繼續走新的路。
        _fleet.ApplyClearance("agv", Clearance(plan, revision: 3, released: 3, (0, 0), (4, 0), (4, 4)));
        var rerouted = Advance(4);
        Assert.Equal((4d, 4d, 3), (rerouted.Position.X, rerouted.Position.Z, rerouted.RouteReachedCount));

        // 換了一段新的路（新的 PlanId）：進度從頭算。
        var next = Guid.NewGuid();
        _fleet.ApplyClearance("agv", Clearance(next, revision: 1, released: 2, (4, 4), (0, 4)));
        var second = Advance(1);
        Assert.Equal((next, 1), (second.RoutePlanId, second.RouteReachedCount));
        Assert.Equal(3, second.Position.X, 6);
    }

    [Fact]
    public void Transport_reports_accepted_loaded_and_completed_as_the_vehicle_gets_there()
    {
        var command = Transport();
        Assert.False(_fleet.HandleCommand("agv", command));
        Assert.Equal(new[] { VehicleEventType.Accepted }, DrainEvents().Select(e => e.EventType));

        // 3 m 到取料點；抵達後還要 2 秒交接才算取料。
        _fleet.ApplyClearance("agv", Clearance(Guid.NewGuid(), 1, 3, ToPickup));
        var arriving = Advance(3);
        Assert.Equal((2, -1), (arriving.Position.X, arriving.Position.Z));
        Assert.Empty(DrainEvents());

        Assert.Equal(VehicleActivity.Loading, Advance(1).Activity);
        Advance(1);
        Assert.Equal(new[] { VehicleEventType.Loaded }, DrainEvents().Select(e => e.EventType));

        // 上料完成後也要等放行才會出發。
        var held = Advance(2);
        Assert.Equal((2d, -1d), (held.Position.X, held.Position.Z));

        // 12 m 到卸料點。
        _fleet.ApplyClearance("agv", Clearance(Guid.NewGuid(), 1, 5, ToDropoff));
        Assert.Equal(1, Advance(11.5).Speed);
        var arrived = Advance(0.5);
        Assert.Equal(8, arrived.Position.X, 6);
        Assert.Equal(5, arrived.Position.Z, 6);
        Assert.Empty(DrainEvents());

        Advance(2);
        var completed = Assert.Single(DrainEvents());
        Assert.Equal((VehicleEventType.Completed, command.Payload.AssignmentId, command.Payload.CommandId),
            (completed.EventType, completed.AssignmentId, completed.CommandId));
        Assert.Equal(VehicleActivity.Idle, Advance(1).Activity);
    }

    [Fact]
    public void Loading_starts_immediately_when_the_vehicle_is_already_at_the_pickup_point()
    {
        _fleet.ApplyClearance("agv", Clearance(Guid.NewGuid(), 1, 3, ToPickup));
        Advance(3);

        _fleet.HandleCommand("agv", Transport());

        Assert.Equal(VehicleActivity.Loading, Advance(0.2).Activity);
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
    public void Empty_and_loaded_vehicles_drive_faster_by_their_own_factors_in_the_clearance()
    {
        // 測試模式：後端在放行裡指定空車 2 倍。空車 1 m/s 變 2 m/s；超出設備上限或不合理的值不採用。
        _fleet.HandleCommand("agv", Transport(loadQuantity: 15, handoverSeconds: 1));
        var toPickup = Clearance(Guid.NewGuid(), 1, 3, ToPickup);
        toPickup.EmptySpeedFactor = 2;
        _fleet.ApplyClearance("agv", toPickup);
        var empty = Advance(0.5);
        Assert.Equal((2d, 1d), (empty.Speed, empty.Position.X));

        // 取料後載著 15 件（半載）：0.75 m/s。空車倍率不影響載貨，載貨有自己的倍率（使用者 2026-10-22：載貨也加快）。
        Advance(1);
        Advance(1.5);
        var toDropoff = Clearance(Guid.NewGuid(), 1, 5, ToDropoff);
        toDropoff.EmptySpeedFactor = 2;
        _fleet.ApplyClearance("agv", toDropoff);
        var loaded = Advance(1);
        Assert.Equal((15, 0.75), (loaded.LoadQuantity, loaded.Speed));

        var faster = Clearance(Guid.NewGuid(), 1, 5, ToDropoff);
        faster.LoadedSpeedFactor = 3;
        _fleet.ApplyClearance("agv", faster);
        Assert.Equal(2.25, Advance(0.1).Speed, 6);

        // 超過上限 5 倍的只用到 5 倍。
        var tooFast = Clearance(Guid.NewGuid(), 1, 5, ToDropoff);
        tooFast.LoadedSpeedFactor = 50;
        _fleet.ApplyClearance("agv", tooFast);
        Assert.Equal(3.75, Advance(0.1).Speed, 6);
    }

    [Fact]
    public void Loaded_vehicle_is_slower_and_reports_its_load_and_handover_progress()
    {
        // 15 件（半載）：車速 0.75 m/s；交接時間由指令指定為 4 秒。
        _fleet.HandleCommand("agv", Transport(loadQuantity: 15, handoverSeconds: 4));
        _fleet.ApplyClearance("agv", Clearance(Guid.NewGuid(), 1, 3, ToPickup));

        var empty = Advance(1);
        Assert.Equal((1d, VehicleActivity.Moving, 0), (empty.Speed, empty.Activity, empty.LoadQuantity));

        // 抵達取料點後上料：進度隨時間增加，交接完成前車上還沒有貨。
        Advance(2);
        var loading = Advance(1);
        Assert.Equal((VehicleActivity.Loading, 0, 0d), (loading.Activity, loading.LoadQuantity, loading.Speed));
        Assert.Equal(0.25, loading.ActivityProgress, 6);

        Advance(3);
        _fleet.ApplyClearance("agv", Clearance(Guid.NewGuid(), 1, 5, ToDropoff));
        var loaded = Advance(1);
        Assert.Equal((VehicleActivity.Moving, 15, "upper-punch"), (loaded.Activity, loaded.LoadQuantity, loaded.LoadCode));
        Assert.Equal(0.75, loaded.Speed, 6);

        // 12 m 的路程以 0.75 m/s 行駛要 16 秒；下料時貨還在車上，完成後才變回空車。
        Advance(15);
        var unloading = Advance(2);
        Assert.Equal((VehicleActivity.Unloading, 15), (unloading.Activity, unloading.LoadQuantity));

        var done = Advance(4);
        Assert.Equal((VehicleActivity.Idle, 0, null), (done.Activity, done.LoadQuantity, done.LoadCode));
        Assert.Equal(VehicleEventType.Completed, DrainEvents()[^1].EventType);
    }

    private static IEnumerable<(double, double)> Points(VehicleTelemetry telemetry) => telemetry.Route.Select(p => (p.X, p.Z));

    private static VehicleClearance Clearance(Guid planId, long revision, int released, params (double X, double Z)[] path) => new()
    {
        PlanId = planId,
        Revision = revision,
        ReleasedCount = released,
        Path = path.Select(p => new Position3 { X = p.X, Z = p.Z }).ToList(),
    };

    private MessageEnvelope<VehicleCommand> Transport(int loadQuantity = 0, double? handoverSeconds = null) =>
        MessageEnvelope.Create("backend", new VehicleCommand
        {
            CommandId = Guid.NewGuid(),
            AssignmentId = Guid.NewGuid(),
            CommandType = VehicleCommandType.Transport,
            ExpiresAt = _time.GetUtcNow().AddSeconds(10),
            Pickup = Pickup,
            Destination = Dropoff,
            LoadQuantity = loadQuantity,
            LoadCode = loadQuantity > 0 ? "upper-punch" : null,
            HandoverSeconds = handoverSeconds,
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
