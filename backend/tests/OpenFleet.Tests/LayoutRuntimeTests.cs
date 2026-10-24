using Microsoft.Extensions.Time.Testing;
using OpenFleet.Api.Fleet;
using OpenFleet.Api.Layout;
using OpenFleet.Api.Traffic;
using OpenFleet.Contracts;
using OpenFleet.Layout;
using Xunit;

namespace OpenFleet.Tests;

/// <summary>
/// 套用佈局時用到的記憶體元件（不需要資料庫）：背景工作的閘門、交通控制的重置、退出佈局的車輛、佈局的 MQTT Topic。
/// </summary>
public class LayoutRuntimeTests
{
    [Fact]
    public async Task Gate_waits_for_the_running_round_then_blocks_new_rounds_until_resumed()
    {
        var gate = new LayoutGate();
        Assert.True(gate.TryEnter());

        var pause = gate.PauseAsync(CancellationToken.None);
        Assert.False(pause.IsCompleted);     // 還有一輪在跑，暫停還沒完成
        Assert.False(gate.TryEnter());      // 新的一輪被擋住
        gate.Exit();

        using var resume = await pause;
        Assert.False(gate.TryEnter());
        resume.Dispose();

        Assert.True(gate.TryEnter());
        gate.Exit();
    }

    [Fact]
    public void Traffic_reset_forgets_every_reservation()
    {
        var network = TestLayouts.Network(DefaultLayout.Build());
        var controller = new TrafficController(network, new TrafficOptions(), new FakeTimeProvider());
        controller.Update([new TrafficInput("agv", true, Guid.NewGuid(), -10.3, 0.2, null, 0, 0, "-20,-9.4", true)]);
        Assert.NotEmpty(controller.ReservedNodes());

        controller.Reset();

        Assert.Empty(controller.ReservedNodes());
    }

    [Fact]
    public void Retired_vehicle_is_ignored_until_it_is_back_in_the_layout()
    {
        var store = new FleetStateStore(new FakeTimeProvider(), new StateChangeQueue(), new ActiveLayout());
        var session = Guid.NewGuid();
        VehicleTelemetry Telemetry(long sequence) => new() { SessionId = session, Sequence = sequence, Position = new Position3() };

        Assert.Equal(TelemetryOutcome.Applied, store.ApplyTelemetry("agv-x", Telemetry(1)));

        store.Retire(["agv-x"]);
        Assert.False(store.Contains("agv-x"));
        Assert.Equal(TelemetryOutcome.Ignored, store.ApplyTelemetry("agv-x", Telemetry(2)));

        store.Unretire(["agv-x"]);
        Assert.Equal(TelemetryOutcome.Applied, store.ApplyTelemetry("agv-x", Telemetry(3)));
    }

    [Fact]
    public void Layout_topic_is_scoped_to_the_factory()
    {
        Assert.Equal("openfleet/v1/demo/layout", Topics.Layout("demo"));
    }
}
