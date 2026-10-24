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
            .Validate(s => s.Speed > 0 && s.FullLoadSpeed > 0 && s.LoadCapacity > 0 && s.TelemetryInterval > TimeSpan.Zero, "Simulator settings are out of range.")
            .ValidateOnStart();

        builder.Services.AddSingleton<SimulatedFleet>();
        builder.Services.AddSingleton<EventSender>();
        builder.Services.AddOpenFleetMqtt(builder.Configuration);
        builder.Services.AddMqttHandler<CommandHandler>();
        builder.Services.AddMqttHandler<ClearanceHandler>();
        builder.Services.AddMqttHandler<LayoutHandler>();
        builder.Services.AddHostedService<TelemetryLoop>();

        return builder;
    }
}
