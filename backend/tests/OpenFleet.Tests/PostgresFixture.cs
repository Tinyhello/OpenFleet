using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using OpenFleet.Api.Data;
using OpenFleet.Api.Fleet;
using OpenFleet.Api.Inventory;
using OpenFleet.Api.Production;
using OpenFleet.Api.Traffic;
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
    // 每個測試各有一個資料庫與自己的連線池，閒置連線會一直留著；預設上限 100 不夠整個測試集合用。
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").WithCommand("-c", "max_connections=500").Build();

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

    /// <summary>品管每一支的檢驗結果由測試指定；沒有指定的都是合格。</summary>
    public ScriptedDefects Defects { get; } = new();

    /// <summary>測試佈局的路網（和 <see cref="TestLayouts.Default"/> 寫入資料庫的是同一份）。</summary>
    public RoadNetwork Network { get; } = TestLayouts.Network(TestLayouts.Default());

    public FleetOptions Fleet { get; } = new();

    /// <summary>
    /// <paramref name="plentyOfBlanks"/>：每種備料都放很多現成的，建立批次時不會經過切料站（P5 以前的測試看的是後面的工序）。
    /// 測試切料與庫存的測試傳 false，用示範的初始庫存。
    /// </summary>
    public static async Task<TestDatabase> CreateAsync(PostgresFixture postgres, bool plentyOfBlanks = true)
    {
        var database = new TestDatabase(postgres.NewConnectionString());
        await using var db = database.NewContext();
        await db.Database.MigrateAsync();
        await DatabaseInitializer.SeedAsync(db, TestLayouts.Default());
        if (plentyOfBlanks)
        {
            var inventory = new InventoryService(db, database.Time, NullLogger<InventoryService>.Instance);
            var blanks = await db.Items.Where(i => i.Kind == ItemKind.Blank).Select(i => i.Code).ToListAsync();
            await inventory.RunAsync(async () =>
            {
                foreach (var blank in blanks)
                {
                    await inventory.AddStockAsync(blank, InventoryService.StoreStation, 100_000, StockMovementReason.Adjustment, "test", default);
                }

                return 0;
            }, default);
        }

        return database;
    }

    public OpenFleetDbContext NewContext() => new(_options);

    /// <summary>
    /// 測試用的簡化流程：只有粗加工、精加工兩道工序，做完送到淬火站入口暫存（P2 的範圍）；
    /// 每件 2 秒、機台取放不花時間、換刀 20 秒、新刀視為已在機邊（不配送刀具）。
    /// 示範值（30／60 秒、取放 5 秒、換刀 90 秒、小型車送刀、後段製程）數字太大，時間點不好算；
    /// 刀具配送與後段製程另有專門的測試。
    /// </summary>
    public async Task UseSimpleMachiningAsync()
    {
        await using var db = NewContext();
        await db.ProcessSteps.Where(s => s.Sequence > 2).ExecuteDeleteAsync();
        await db.ProcessSteps.ExecuteUpdateAsync(s => s.SetProperty(step => step.SecondsPerPiece, 2d));
        await db.ProductionSettings.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.BufferStationCode, "heat-1")
            .SetProperty(x => x.PieceHandlingSeconds, 0d)
            .SetProperty(x => x.ToolChangeSeconds, 20d)
            .SetProperty(x => x.ToolDeliveryEnabled, false));
    }

    /// <summary>讓一台車一直處於忙碌：給它一筆和示範流程無關的有效派工（包裝 → 出貨）。</summary>
    public async Task OccupyAsync(string vehicleId)
    {
        await using var db = NewContext();
        var now = Time.GetUtcNow();
        var lot = MaterialLot.Receive("occupied", 1, "packing-1", null, $"OCC-{Guid.NewGuid():N}"[..20], 1, 1, 30, now);
        var task = TransportTask.Create(lot, "packing-1", "shipping", TransportTaskKind.WipTransfer, now);
        db.Lots.Add(lot);
        db.TransportTasks.Add(task);
        db.Assignments.Add(task.Assign(vehicleId, 1, now, TimeSpan.FromHours(1)));
        await db.SaveChangesAsync();
    }

    /// <summary>結束 <see cref="OccupyAsync"/> 給的那筆派工，車輛恢復可用。</summary>
    public async Task ReleaseAsync(string vehicleId)
    {
        await using var db = NewContext();
        await db.Assignments.Where(a => a.VehicleId == vehicleId && a.Task.FromStationCode == "packing-1").ExecuteDeleteAsync();
    }

    /// <summary>車輛目前那筆剛派出的派工，走完接受、取料、卸料；過程中時間不前進。</summary>
    public async Task CompleteDeliveryAsync(string vehicleId)
    {
        Assignment assignment;
        await using (var db = NewContext())
        {
            assignment = await db.Assignments.AsNoTracking().SingleAsync(a => a.VehicleId == vehicleId && a.Status == AssignmentStatus.Dispatched);
        }

        foreach (var type in new[] { VehicleEventType.Accepted, VehicleEventType.Loaded, VehicleEventType.Completed })
        {
            await using var scope = NewScope();
            if (!await scope.Events.ProcessAsync(vehicleId, Event(assignment, type), default))
            {
                throw new InvalidOperationException($"Event {type} for {vehicleId} was not processed.");
            }
        }
    }

    public Scope NewScope()
    {
        var db = NewContext();
        var inventory = new InventoryService(db, Time, NullLogger<InventoryService>.Instance);
        var production = new ProductionService(db, inventory, Defects, Time, NullLogger<ProductionService>.Instance);
        var shortage = new VehicleShortageService(db, Time, NullLogger<VehicleShortageService>.Instance);
        var transport = new TransportService(db, production, shortage, Network, Options.Create(Fleet),
            Options.Create(new MqttSettings { ProducerId = "backend", FactoryId = "demo" }), Time, NullLogger<TransportService>.Instance);
        return new Scope(db, production, transport, shortage, new VehicleEventProcessor(db, transport, Time));
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

    public sealed record Scope(
        OpenFleetDbContext Db, ProductionService Production, TransportService Transport, VehicleShortageService Shortage, VehicleEventProcessor Events) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}

/// <summary>
/// 測試用的檢驗結果：依序回傳事先排好的結果（null＝合格），排完之後都是合格。不看不良率。
/// </summary>
public sealed class ScriptedDefects : OpenFleet.Api.Production.IDefectSource
{
    private readonly Queue<DefectReason?> _results = new();

    public void Enqueue(params DefectReason?[] results)
    {
        foreach (var result in results)
        {
            _results.Enqueue(result);
        }
    }

    public DefectReason? Inspect(double defectRate) => _results.TryDequeue(out var result) ? result : null;
}
