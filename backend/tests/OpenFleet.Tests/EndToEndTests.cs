using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using OpenFleet.Api.Fleet;
using OpenFleet.Contracts;
using OpenFleet.Messaging;
using OpenFleet.Simulator;

namespace OpenFleet.Tests;

/// <summary>
/// 端對端驗收：程式內嵌 MQTT broker + 後端 API + 模擬器 + PostgreSQL（Testcontainers，需要 Docker），走完整路徑。
/// 與真正的 Mosquitto 的手動驗證步驟寫在 backend/README.md。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EndToEndTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly int _port = GetFreePort();
    private MqttServer _broker = null!;
    private WebApplicationFactory<Program> _api = null!;
    private HttpClient _http = null!;
    private IHost _simulator = null!;

    public async Task InitializeAsync()
    {
        _broker = await StartBrokerAsync(_port);

        _api = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Mqtt:Port", _port.ToString());
            b.UseSetting("Mqtt:ClientId", "test-backend");
            b.UseSetting("Mqtt:ReconnectDelay", "00:00:00.200");
            b.UseSetting("Fleet:OfflineAfter", "00:00:01");
            b.UseSetting("Fleet:LivenessCheckInterval", "00:00:00.200");
            b.UseSetting("Fleet:VehicleTypes:agv-t1", "Large");
            b.UseSetting("Fleet:VehicleTypes:agv-t2", "Small");
            b.UseSetting("ConnectionStrings:OpenFleet", postgres.NewConnectionString());
            b.UseSetting("Fleet:DispatchVehicleIds:0", "agv-t1");
            b.UseSetting("Fleet:DispatchInterval", "00:00:00.100");
            b.UseSetting("Fleet:OutboxPollInterval", "00:00:00.100");
            b.UseSetting("Production:TickInterval", "00:00:00.100");
            b.UseSetting("Fleet:Parking:agv-t2:X", "3");
            b.UseSetting("Fleet:Parking:agv-t2:Z", "2");
        });
        _http = _api.CreateClient();

        var simulator = SimulatorApp.CreateBuilder([]);
        // 不讀 appsettings.json：車輛清單與路線只用下面的測試設定，避免示範用的車輛混進來。
        foreach (var json in simulator.Configuration.Sources.OfType<JsonConfigurationSource>().ToList())
        {
            simulator.Configuration.Sources.Remove(json);
        }

        simulator.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mqtt:Port"] = _port.ToString(),
            ["Mqtt:ClientId"] = "test-simulator",
            ["Mqtt:ProducerId"] = "simulator",
            ["Mqtt:ReconnectDelay"] = "00:00:00.200",
            ["Simulator:VehicleIds:0"] = "agv-t1",
            ["Simulator:VehicleIds:1"] = "agv-t2",
            ["Simulator:TelemetryInterval"] = "00:00:00.100",
            ["Simulator:Route:0:X"] = "0",
            ["Simulator:Route:0:Z"] = "0",
            ["Simulator:Route:1:X"] = "10",
            ["Simulator:Route:1:Z"] = "0",
            // 加快車速與交接，讓整個物料流程在幾秒內跑完。
            ["Simulator:Speed"] = "40",
            ["Simulator:HandoverDuration"] = "00:00:00.100",
        });
        _simulator = simulator.Build();
        await _simulator.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _simulator.StopAsync();
        _simulator.Dispose();
        _http.Dispose();
        await _api.DisposeAsync();
        await _broker.StopAsync();
        _broker.Dispose();
    }

    [Fact]
    public async Task Ping_round_trip_reaches_simulator_and_result_is_visible_over_http()
    {
        await EventuallyAsync(async () => (await GetVehiclesAsync()).Count(v => v.Connectivity == VehicleConnectivity.Online) == 2);

        // 車型由後端設定決定，並透過快照（JSON 字串 enum）傳給顯示端。
        Assert.Equal(VehicleType.Large, (await GetVehicleAsync("agv-t1")).VehicleType);
        Assert.Equal(VehicleType.Small, (await GetVehicleAsync("agv-t2")).VehicleType);

        var response = await _http.PostAsync("/api/vehicles/agv-t1/ping", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<CommandAcceptedResponse>(JsonDefaults.Options))!;

        await EventuallyAsync(async () =>
        {
            var state = await GetVehicleAsync("agv-t1");
            return state.LastCommandId == accepted.CommandId && state.LastCommandEvent == VehicleEventType.Completed;
        });
    }

    [Fact]
    public async Task Lot_travels_from_raw_store_through_both_machines_to_the_buffer()
    {
        await EventuallyAsync(async () => (await GetVehiclesAsync()).Count(v => v.Connectivity == VehicleConnectivity.Online) == 2);

        // 經由面板用的 API 把兩站的加工時間改成 1 秒、批量改成 20。
        var settings = (await _http.GetFromJsonAsync<ProductionSettingsDto>("/api/settings", JsonDefaults.Options))!;
        settings.LotQuantity = 20;
        settings.Steps.ForEach(step => step.DurationSeconds = 1);
        (await _http.PutAsJsonAsync("/api/settings", settings, JsonDefaults.Options)).EnsureSuccessStatusCode();

        var created = await _http.PostAsJsonAsync("/api/lots", new CreateLotRequest(), JsonDefaults.Options);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var lot = (await created.Content.ReadFromJsonAsync<LotDto>(JsonDefaults.Options))!;
        Assert.Equal(20, lot.Quantity);

        await EventuallyAsync(async () => (await GetTraceAsync(lot.Id)).Lot.Status == LotStatus.Staged);

        var trace = await GetTraceAsync(lot.Id);
        Assert.Equal("heat", trace.Lot.StationCode);
        Assert.Equal(
            new[] { "station:raw-store", "vehicle:agv-t1", "station:rough", "vehicle:agv-t1", "station:fine", "vehicle:agv-t1", "station:heat" },
            trace.Movements.Select(m => m.To));
        Assert.Equal(new[] { "rough", "fine" }, trace.Operations.Select(o => o.StationCode));
        Assert.All(trace.Operations, o => Assert.Equal(Contracts.OperationStatus.Completed, o.Status));
        Assert.Equal(3, trace.Tasks.Count);
        Assert.All(trace.Tasks, t => Assert.Equal((TransportTaskStatus.Completed, "agv-t1", 1), (t.Status, t.VehicleId, t.Attempts)));

        // 任務歷史：待派工 → 已派工 → 執行中 → 完成。
        var events = (await _http.GetFromJsonAsync<List<TaskEventDto>>($"/api/transport-tasks/{trace.Tasks[0].Id}/events", JsonDefaults.Options))!;
        Assert.Equal(
            new[] { TransportTaskStatus.Pending, TransportTaskStatus.Assigned, TransportTaskStatus.Executing, TransportTaskStatus.Completed },
            events.Select(e => e.ToStatus));
    }

    [Fact]
    public async Task Idle_vehicle_is_sent_to_its_parking_slot()
    {
        await EventuallyAsync(async () =>
        {
            var state = await GetVehicleAsync("agv-t2");
            return state.Speed == 0 && Math.Abs(state.Position.X - 3) < 0.01 && Math.Abs(state.Position.Z - 2) < 0.01;
        });

        // 沒有設定車位的車留在原地。
        Assert.Equal(0, (await GetVehicleAsync("agv-t1")).Position.Z);
    }

    [Fact]
    public async Task Invalid_settings_are_rejected_with_400()
    {
        var settings = (await _http.GetFromJsonAsync<ProductionSettingsDto>("/api/settings", JsonDefaults.Options))!;
        settings.LotQuantity = settings.MaxLotQuantity + 1;

        var response = await _http.PutAsJsonAsync("/api/settings", settings, JsonDefaults.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("lotQuantity", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_vehicle_returns_404()
    {
        var response = await _http.PostAsync("/api/vehicles/nope/ping", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_event_delivery_changes_state_only_once()
    {
        using var client = await ConnectTestClientAsync();
        var envelope = MessageEnvelope.Create("external-device", new VehicleEvent
        {
            CommandId = Guid.NewGuid(),
            EventType = VehicleEventType.Completed,
        }, DateTimeOffset.UtcNow);
        var topic = Topics.Vehicle("demo", "agv-dup", VehicleChannel.Events);

        await PublishAsync(client, topic, envelope);
        await EventuallyAsync(() => Task.FromResult(_api.Services.GetRequiredService<FleetStateStore>().Find("agv-dup") is not null));
        var versionAfterFirst = _api.Services.GetRequiredService<FleetStateStore>().Find("agv-dup")!.Version;

        await PublishAsync(client, topic, envelope);
        // 送一則不同的訊息當作「前一則已處理完」的標記（同一連線、同一 topic 保證順序）。
        var marker = MessageEnvelope.Create("external-device", new VehicleEvent { CommandId = Guid.NewGuid(), EventType = VehicleEventType.Accepted }, DateTimeOffset.UtcNow);
        await PublishAsync(client, topic, marker);
        await EventuallyAsync(() => Task.FromResult(_api.Services.GetRequiredService<FleetStateStore>().Find("agv-dup")!.LastCommandId == marker.Payload.CommandId));

        Assert.Equal(versionAfterFirst + 1, _api.Services.GetRequiredService<FleetStateStore>().Find("agv-dup")!.Version);
    }

    [Fact]
    public async Task Backend_publishes_retained_camelCase_state_for_visualisation_clients()
    {
        await EventuallyAsync(async () => (await GetVehiclesAsync()).Count == 2);

        var received = new ConcurrentQueue<string>();
        using var client = await ConnectTestClientAsync();
        client.ApplicationMessageReceivedAsync += e =>
        {
            received.Enqueue(e.ApplicationMessage.ConvertPayloadToString());
            return Task.CompletedTask;
        };
        await client.SubscribeAsync(Topics.AllVehicleStates("demo"), MqttQualityOfServiceLevel.AtLeastOnce);

        await EventuallyAsync(() => Task.FromResult(!received.IsEmpty));
        Assert.True(received.TryPeek(out var json));

        using var doc = JsonDocument.Parse(json);
        var payload = doc.RootElement.GetProperty("payload");
        Assert.StartsWith("agv-t", payload.GetProperty("vehicleId").GetString());
        Assert.Equal("online", payload.GetProperty("connectivity").GetString());
    }

    [Fact]
    public async Task Services_recover_after_broker_restart()
    {
        await EventuallyAsync(async () => (await GetVehiclesAsync()).Count(v => v.Connectivity == VehicleConnectivity.Online) == 2);

        await _broker.StopAsync();
        _broker.Dispose();

        await EventuallyAsync(async () => !(await GetHealthAsync()));
        await EventuallyAsync(async () => (await GetVehiclesAsync()).All(v => v.Connectivity == VehicleConnectivity.Offline));
        var sequenceWhileDown = (await GetVehicleAsync("agv-t1")).TelemetrySequence;

        var offline = await _http.PostAsync("/api/vehicles/agv-t1/ping", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, offline.StatusCode);

        _broker = await StartBrokerAsync(_port);

        await EventuallyAsync(GetHealthAsync);
        await EventuallyAsync(async () =>
        {
            var state = await GetVehicleAsync("agv-t1");
            return state.Connectivity == VehicleConnectivity.Online && state.TelemetrySequence > sequenceWhileDown;
        });

        var response = await _http.PostAsync("/api/vehicles/agv-t1/ping", null);
        var accepted = (await response.Content.ReadFromJsonAsync<CommandAcceptedResponse>(JsonDefaults.Options))!;
        await EventuallyAsync(async () => (await GetVehicleAsync("agv-t1")).LastCommandId == accepted.CommandId);
    }

    private async Task<List<VehicleState>> GetVehiclesAsync() =>
        (await _http.GetFromJsonAsync<List<VehicleState>>("/api/vehicles", JsonDefaults.Options))!;

    private async Task<VehicleState> GetVehicleAsync(string id) =>
        (await _http.GetFromJsonAsync<VehicleState>($"/api/vehicles/{id}", JsonDefaults.Options))!;

    private async Task<LotTraceDto> GetTraceAsync(Guid lotId) =>
        (await _http.GetFromJsonAsync<LotTraceDto>($"/api/lots/{lotId}", JsonDefaults.Options))!;

    private async Task<bool> GetHealthAsync()
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync("/health"));
        return doc.RootElement.GetProperty("mqttConnected").GetBoolean();
    }

    private async Task<IMqttClient> ConnectTestClientAsync()
    {
        var client = new MqttClientFactory().CreateMqttClient();
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", _port).WithClientId($"test-{Guid.NewGuid():N}").Build());
        return client;
    }

    private static Task PublishAsync<T>(IMqttClient client, string topic, T message) =>
        client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(JsonSerializer.SerializeToUtf8Bytes(message, JsonDefaults.Options))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build());

    private static async Task<MqttServer> StartBrokerAsync(int port)
    {
        var options = new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port).Build();
        var server = new MqttServerFactory().CreateMqttServer(options);
        await server.StartAsync();
        return server;
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Condition not met within {Timeout}.");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
