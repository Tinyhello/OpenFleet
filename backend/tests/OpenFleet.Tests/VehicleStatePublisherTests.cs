using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MQTTnet.Protocol;
using OpenFleet.Api.Fleet;
using OpenFleet.Api.Layout;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Tests;

public class VehicleStatePublisherTests
{
    [Fact]
    public async Task State_queued_before_the_vehicle_was_retired_is_not_published()
    {
        var changes = new StateChangeQueue();
        var store = new FleetStateStore(new FakeTimeProvider(), changes, new ActiveLayout());
        var publisher = new RecordingPublisher();
        var service = new VehicleStatePublisher(changes, store, publisher, NullLogger<VehicleStatePublisher>.Instance);

        VehicleTelemetry Telemetry() => new() { SessionId = Guid.NewGuid(), Sequence = 1, Position = new Position3() };

        // 先讓發布迴圈卡在「完整快照」的第一筆，這段時間進佇列的狀態才能確定是在車輛退出前排進去的。
        store.ApplyTelemetry("agv-sentinel", Telemetry());
        publisher.BlockNextPublish();
        await service.StartAsync(CancellationToken.None);
        await publisher.WaitUntilBlocked();

        // 退出的車先排進佇列：發布迴圈依序處理，等到 agv-keep 發出時，agv-retired 一定已經處理過了。
        store.ApplyTelemetry("agv-retired", Telemetry());
        store.ApplyTelemetry("agv-keep", Telemetry());
        store.Retire(["agv-retired"]);

        publisher.Release();
        await publisher.WaitForTopic(Topics.VehicleState(publisher.Settings.FactoryId, "agv-keep"));
        await service.StopAsync(CancellationToken.None);

        var topics = publisher.Topics;
        Assert.Contains(Topics.VehicleState(publisher.Settings.FactoryId, "agv-sentinel"), topics);
        Assert.Contains(Topics.VehicleState(publisher.Settings.FactoryId, "agv-keep"), topics);
        Assert.DoesNotContain(Topics.VehicleState(publisher.Settings.FactoryId, "agv-retired"), topics);
    }

    private sealed class RecordingPublisher : IMqttPublisher
    {
        private readonly ConcurrentQueue<string> _topics = new();
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _block;

        public bool IsConnected => true;

        public MqttSettings Settings { get; } = new() { FactoryId = "demo", ProducerId = "test" };

        public IReadOnlyCollection<string> Topics => _topics.ToArray();

        public void BlockNextPublish() => _block = true;

        public Task WaitUntilBlocked() => _blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Release() => _release.SetResult();

        public async Task WaitForTopic(string topic)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!_topics.Contains(topic) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }

        public async Task PublishAsync<T>(string topic, T message, MqttQualityOfServiceLevel qos, bool retain, CancellationToken cancellationToken)
        {
            if (_block)
            {
                _block = false;
                _blocked.SetResult();
                await _release.Task;
            }

            _topics.Enqueue(topic);
        }

        public Task PublishRawAsync(string topic, ReadOnlyMemory<byte> payload, MqttQualityOfServiceLevel qos, bool retain, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
