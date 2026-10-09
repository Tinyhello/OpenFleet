using System.Text.Json;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Tests;

public class ContractTests
{
    [Fact]
    public void Envelope_serializes_with_camelCase_fields_and_string_enums()
    {
        var envelope = MessageEnvelope.Create("simulator", new VehicleEvent
        {
            CommandId = Guid.NewGuid(),
            EventType = VehicleEventType.Completed,
        }, DateTimeOffset.UnixEpoch);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(envelope, JsonDefaults.Options));
        var root = json.RootElement;

        Assert.Equal(ContractVersion.Current, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("simulator", root.GetProperty("producerId").GetString());
        Assert.Equal("completed", root.GetProperty("payload").GetProperty("eventType").GetString());
    }

    [Fact]
    public void Envelope_round_trips()
    {
        var original = MessageEnvelope.Create("backend", new VehicleCommand
        {
            CommandId = Guid.NewGuid(),
            CommandType = VehicleCommandType.Ping,
            ExpiresAt = DateTimeOffset.UnixEpoch.AddMinutes(1),
        }, DateTimeOffset.UnixEpoch, correlationId: Guid.NewGuid());

        var json = JsonSerializer.Serialize(original, JsonDefaults.Options);
        var copy = JsonSerializer.Deserialize<MessageEnvelope<VehicleCommand>>(json, JsonDefaults.Options)!;

        Assert.Equal(original.MessageId, copy.MessageId);
        Assert.Equal(original.CorrelationId, copy.CorrelationId);
        Assert.Equal(original.Payload.CommandId, copy.Payload.CommandId);
        Assert.Equal(original.Payload.ExpiresAt, copy.Payload.ExpiresAt);
    }

    [Fact]
    public void Vehicle_topic_round_trips()
    {
        var topic = Topics.Vehicle("demo", "agv-01", VehicleChannel.Telemetry);

        Assert.Equal("openfleet/v1/demo/vehicles/agv-01/telemetry", topic);
        Assert.True(Topics.TryParseVehicle(topic, out var factory, out var vehicle, out var channel));
        Assert.Equal(("demo", "agv-01", VehicleChannel.Telemetry), (factory, vehicle, channel));
    }

    [Theory]
    [InlineData("openfleet/v1/demo/vehicles/agv-01")]
    [InlineData("openfleet/v1/demo/vehicles/agv-01/unknown")]
    [InlineData("openfleet/v2/demo/vehicles/agv-01/events")]
    [InlineData("openfleet/v1/demo/state/vehicles/agv-01")]
    [InlineData("openfleet/v1/demo/vehicles//events")]
    public void Invalid_vehicle_topics_are_rejected(string topic)
    {
        Assert.False(Topics.TryParseVehicle(topic, out _, out _, out _));
    }
}
