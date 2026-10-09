using OpenFleet.Messaging;

namespace OpenFleet.Simulator;

/// <summary>組裝模擬器的 Host；Program 與整合測試共用同一套註冊。</summary>
public static class SimulatorApp
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddOptions<SimulatorSettings>()
            .Bind(builder.Configuration.GetSection(SimulatorSettings.SectionName))
            .Validate(s => s.VehicleIds.Count > 0, "Simulator:VehicleIds must contain at least one vehicle.")
            .Validate(s => s.Route.Count >= 2 && LoopRoute.MeasureLength(s.Route) > 0, "Simulator:Route needs at least two distinct points.")
            .Validate(s => s.Speed > 0 && s.TelemetryInterval > TimeSpan.Zero, "Simulator settings are out of range.")
            .ValidateOnStart();

        builder.Services.AddSingleton<SimulatedFleet>();
        builder.Services.AddSingleton<EventSender>();
        builder.Services.AddOpenFleetMqtt(builder.Configuration);
        builder.Services.AddMqttHandler<CommandHandler>();
        builder.Services.AddHostedService<TelemetryLoop>();

        return builder;
    }
}
