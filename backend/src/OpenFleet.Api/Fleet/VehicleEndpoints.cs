using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using OpenFleet.Contracts;
using OpenFleet.Messaging;

namespace OpenFleet.Api.Fleet;

public sealed record CommandAcceptedResponse(Guid CommandId, Guid MessageId, DateTimeOffset ExpiresAt);

public static class VehicleEndpoints
{
    public static IEndpointRouteBuilder MapVehicleEndpoints(this IEndpointRouteBuilder app)
    {
        var vehicles = app.MapGroup("/api/vehicles");

        vehicles.MapGet("/", (FleetStateStore store) => store.Snapshot());

        vehicles.MapGet("/{vehicleId}", (string vehicleId, FleetStateStore store) =>
            store.Find(vehicleId) is { } state ? Results.Ok(state) : Results.NotFound());

        // P1 通訊驗證：送出 Ping，結果非同步回報；以 GET 查 lastCommandId／lastCommandEvent 確認。
        vehicles.MapPost("/{vehicleId}/ping", async (
            string vehicleId,
            FleetStateStore store,
            IMqttPublisher publisher,
            IOptions<FleetOptions> options,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            if (!store.Contains(vehicleId))
            {
                return Results.NotFound();
            }

            var now = time.GetUtcNow();
            var command = new VehicleCommand
            {
                CommandId = Guid.NewGuid(),
                CommandType = VehicleCommandType.Ping,
                ExpiresAt = now + options.Value.CommandTimeToLive,
            };
            var envelope = MessageEnvelope.Create(publisher.Settings.ProducerId, command, now);
            var topic = Topics.Vehicle(publisher.Settings.FactoryId, vehicleId, VehicleChannel.Commands);

            try
            {
                // 指令不可 retained：否則設備重連時會收到過期指令。
                await publisher.PublishAsync(topic, envelope, MqttQualityOfServiceLevel.AtLeastOnce, retain: false, cancellationToken);
            }
            catch (MqttNotConnectedException)
            {
                return Results.Problem("MQTT broker is not connected.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Accepted($"/api/vehicles/{vehicleId}", new CommandAcceptedResponse(command.CommandId, envelope.MessageId, command.ExpiresAt));
        });

        return app;
    }
}
