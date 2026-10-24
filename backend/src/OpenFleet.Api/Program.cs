using OpenFleet.Api.Catalog;
using OpenFleet.Api.Inventory;
using OpenFleet.Api.Orders;
using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Api.Fleet;
using OpenFleet.Api.Layout;
using OpenFleet.Api.Production;
using OpenFleet.Api.Traffic;
using OpenFleet.Api.Transport;
using OpenFleet.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => JsonDefaults.Configure(o.SerializerOptions));
builder.Services.AddSingleton(TimeProvider.System);

// DbContext 預設是 Scoped：每個請求／每個手動建立的 scope 一個實例。
builder.Services.AddDbContext<OpenFleetDbContext>(o => o.UseNpgsql(
    builder.Configuration.GetConnectionString("OpenFleet")
        ?? throw new InvalidOperationException("ConnectionStrings:OpenFleet is required.")));

builder.Services.AddOptions<FleetOptions>().Bind(builder.Configuration.GetSection(FleetOptions.SectionName));
builder.Services.AddOptions<ProductionOptions>().Bind(builder.Configuration.GetSection(ProductionOptions.SectionName));
builder.Services.AddSingleton<StateChangeQueue>();
builder.Services.AddSingleton<FleetStateStore>();

// 佈局（P7 第 2 步）：使用中的佈局與背景工作的閘門都在記憶體；套用時暫停閘門、換掉路網與車輛清單。
builder.Services.AddSingleton<ActiveLayout>();
builder.Services.AddSingleton<LayoutGate>();

// 路網與交通控制的狀態都在記憶體：路網啟動時從資料庫載入，預約狀況由車輛的遙測重建。
builder.Services.AddOptions<TrafficOptions>().Bind(builder.Configuration.GetSection(TrafficOptions.SectionName));
builder.Services.AddSingleton<RoadNetwork>();
builder.Services.AddSingleton(sp => new TrafficController(
    sp.GetRequiredService<RoadNetwork>(),
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TrafficOptions>>().Value,
    sp.GetRequiredService<TimeProvider>()));

// 用到 DbContext 的服務也必須是 Scoped（Singleton 不能依賴 Scoped）。
builder.Services.AddSingleton<IDefectSource, RandomDefectSource>();
builder.Services.AddScoped<ProductionService>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<CatalogService>();
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddScoped<VehicleShortageService>();
builder.Services.AddScoped<TransportService>();
builder.Services.AddScoped<VehicleEventProcessor>();
builder.Services.AddScoped<LayoutService>();
builder.Services.AddScoped<LayoutRuntime>();

builder.Services.AddOpenFleetMqtt(builder.Configuration);
builder.Services.AddMqttHandler<VehicleEventHandler>();
builder.Services.AddMqttHandler<VehicleTelemetryHandler>();

builder.Services.AddHostedService<VehicleStatePublisher>();
builder.Services.AddHostedService<VehicleLivenessMonitor>();
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddHostedService<DispatchWorker>();
builder.Services.AddHostedService<TrafficWorker>();
builder.Services.AddHostedService<ProductionWorker>();
builder.Services.AddHostedService<LayoutPublisher>();

var app = builder.Build();

// 資料庫連不上就直接啟動失敗：沒有資料庫時後端無法保證任何業務狀態，不像 MQTT 可以先降級再重連。
await app.InitializeDatabaseAsync();

app.MapGet("/health", (IMqttPublisher mqtt) => Results.Ok(new
{
    Status = mqtt.IsConnected ? "ok" : "degraded",
    MqttConnected = mqtt.IsConnected,
}));

app.MapVehicleEndpoints();
app.MapProductionEndpoints();
app.MapCatalogEndpoints();
app.MapInventoryEndpoints();
app.MapOrderEndpoints();
app.MapTransportEndpoints();
app.MapLayoutEndpoints();

app.Run();

// 讓整合測試的 WebApplicationFactory<Program> 可以參照。
public partial class Program;
