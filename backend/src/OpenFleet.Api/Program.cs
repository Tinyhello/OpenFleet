using OpenFleet.Api.Fleet;
using OpenFleet.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Configure(o.SerializerOptions));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddOptions<FleetOptions>().Bind(builder.Configuration.GetSection(FleetOptions.SectionName));
builder.Services.AddSingleton<StateChangeQueue>();
builder.Services.AddSingleton<FleetStateStore>();
builder.Services.AddSingleton<InboxDeduplicator>();

builder.Services.AddOpenFleetMqtt(builder.Configuration);
builder.Services.AddMqttHandler<VehicleEventHandler>();
builder.Services.AddMqttHandler<VehicleTelemetryHandler>();

builder.Services.AddHostedService<VehicleStatePublisher>();
builder.Services.AddHostedService<VehicleLivenessMonitor>();

var app = builder.Build();

app.MapGet("/health", (IMqttPublisher mqtt) => Results.Ok(new
{
    Status = mqtt.IsConnected ? "ok" : "degraded",
    MqttConnected = mqtt.IsConnected,
}));

app.MapVehicleEndpoints();

app.Run();

// 讓整合測試的 WebApplicationFactory<Program> 可以參照。
public partial class Program;
