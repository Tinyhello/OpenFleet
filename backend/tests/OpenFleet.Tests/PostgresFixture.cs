using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using OpenFleet.Api.Data;
using OpenFleet.Api.Fleet;
using OpenFleet.Api.Production;
using OpenFleet.Api.Transport;
using OpenFleet.Contracts;
using OpenFleet.Messaging;
using Testcontainers.PostgreSql;

namespace OpenFleet.Tests;

/// <summary>
/// 整個測試集合共用一個 PostgreSQL 容器（需要 Docker），每個測試各自建立一個資料庫，互不影響。
/// 使用真正的 PostgreSQL 而不是記憶體資料庫，因為要驗證的正是唯一索引、xmin 並行控制與交易行為。
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>回傳一個全新資料庫的連線字串（尚未建立；第一次 Migrate 時才會建立）。</summary>
    public string NewConnectionString() =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = $"test_{Guid.NewGuid():N}" }.ConnectionString;
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>
/// 不經過 HTTP 與 MQTT、直接對資料庫測試服務用的環境。
/// 每次 <see cref="NewScope"/> 都是新的 DbContext，模擬「不同請求／不同工作者」各自的工作單元。
/// </summary>
public sealed class TestDatabase
{
    private readonly DbContextOptions<OpenFleetDbContext> _options;

    private TestDatabase(string connectionString)
    {
        _options = new DbContextOptionsBuilder<OpenFleetDbContext>().UseNpgsql(connectionString).Options;
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 9, 8, 0, 0, TimeSpan.Zero));

    public FleetOptions Fleet { get; } = new()
    {
        VehicleTypes = { ["agv-a"] = VehicleType.Large, ["agv-b"] = VehicleType.Large, ["agv-s"] = VehicleType.Small },
        DispatchVehicleIds = ["agv-a", "agv-b", "agv-s"],
    };

    public static async Task<TestDatabase> CreateAsync(PostgresFixture postgres)
    {
        var database = new TestDatabase(postgres.NewConnectionString());
        await using var db = database.NewContext();
        await db.Database.MigrateAsync();
        await DatabaseInitializer.SeedAsync(db, database.Fleet);
        return database;
    }

    public OpenFleetDbContext NewContext() => new(_options);

    public Scope NewScope()
    {
        var db = NewContext();
        var production = new ProductionService(db, Time, NullLogger<ProductionService>.Instance);
        var transport = new TransportService(db, production, Options.Create(Fleet),
            Options.Create(new MqttSettings { ProducerId = "backend", FactoryId = "demo" }), Time, NullLogger<TransportService>.Instance);
        return new Scope(db, production, transport, new VehicleEventProcessor(db, transport, Time));
    }

    public static VehicleState Online(string vehicleId, double x = 0, double z = 0) => new()
    {
        VehicleId = vehicleId,
        Connectivity = VehicleConnectivity.Online,
        Position = new Position3 { X = x, Z = z },
    };

    public static MessageEnvelope<VehicleEvent> Event(Assignment assignment, VehicleEventType type, string? reason = null) =>
        MessageEnvelope.Create("simulator", new VehicleEvent
        {
            CommandId = assignment.CommandId,
            AssignmentId = assignment.Id,
            EventType = type,
            Reason = reason,
        }, DateTimeOffset.UtcNow);

    public sealed record Scope(OpenFleetDbContext Db, ProductionService Production, TransportService Transport, VehicleEventProcessor Events) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
