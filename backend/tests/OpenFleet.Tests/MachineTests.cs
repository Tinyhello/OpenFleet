using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenFleet.Api.Data;
using OpenFleet.Api.Fleet;
using OpenFleet.Contracts;
using static OpenFleet.Tests.TestDatabase;

namespace OpenFleet.Tests;

/// <summary>
/// P6 第 1 步：機台。粗加工、精加工各兩台車床，其他逐件工序的站一台（使用者 2026-10-22）；
/// 同一站的機台共用備料區與交接點，刀具、換線、加工進度與缺車停機都是每台各自的。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MachineTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Demo_factory_has_two_lathes_at_rough_and_fine_and_one_machine_at_the_other_piece_stations()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        await using (var db = database.NewContext())
        {
            var machines = await db.Machines.OrderBy(m => m.Code).Select(m => new { m.Code, m.StationCode, m.Number, m.Name }).ToListAsync();
            Assert.Equal(
                new[]
                {
                    ("cutting-1", "cutting", 1, "切料"), ("fine-1", "fine", 1, "精加工 1 號機"), ("fine-2", "fine", 2, "精加工 2 號機"),
                    ("packing-1", "packing", 1, "包裝"), ("polish-1", "polish", 1, "拋光"), ("qc-1", "qc", 1, "品管"),
                    ("rough-1", "rough", 1, "粗加工 1 號機"), ("rough-2", "rough", 2, "粗加工 2 號機"),
                },
                machines.Select(m => (m.Code, m.StationCode, m.Number, m.Name)));

            // 有刀具的只有車床，每台一把。
            Assert.Equal(new[] { "fine-1", "fine-2", "rough-1", "rough-2" },
                await db.MachineTools.OrderBy(t => t.MachineCode).Select(t => t.MachineCode).ToListAsync());

            // 一台車床的備料台 30 支：兩台車床的站共 60 支，切料站一台 30 支。
            var capacities = await db.Stations.Where(s => s.InboundCapacity != null).OrderBy(s => s.Code)
                .Select(s => new { s.Code, s.InboundCapacity }).ToListAsync();
            Assert.Equal(new[] { ("cutting", 30), ("fine", 60), ("rough", 60) }, capacities.Select(c => (c.Code, c.InboundCapacity!.Value)));
        }

        // 站點狀態：每台機台各一筆，依編號排列；整爐工序沒有機台。
        await using var scope = database.NewScope();
        var stations = await scope.Production.GetStationStatusAsync(default);
        Assert.Equal(new[] { "粗加工 1 號機", "粗加工 2 號機" }, stations.Single(s => s.Code == "rough").Machines.Select(m => m.Name));
        Assert.All(stations.Single(s => s.Code == "rough").Machines, m => Assert.Equal((0, 500), (m.Tool!.PiecesUsed, m.Tool.LifePieces)));
        Assert.Null(stations.Single(s => s.Code == "polish").Machine().Tool);
        Assert.Empty(stations.Single(s => s.Code == "heat").Machines);
        Assert.Empty(stations.Single(s => s.Code == "raw-store").Machines);
    }

    [Fact]
    public async Task Each_lathe_counts_its_own_tool_life_and_the_spare_goes_to_the_lathe_that_asked_for_it()
    {
        // 每件 2 秒、刀具壽命 10 件、剩 4 件時叫刀、換刀 20 秒。第一框在粗加工 1 號機，4 秒後第二框在 2 號機。
        var database = await TestDatabase.CreateAsync(postgres);
        await database.UseSimpleMachiningAsync();
        await ConfigureAsync(database, s =>
        {
            s.ToolDeliveryEnabled = true;
            s.ToolRequestThresholdPieces = 4;
            s.Steps[1].ToolLifePieces = 10;
        });
        await CreateAndDeliverAsync(database, "agv-a");
        await AdvanceAsync(database, 4);
        await CreateAndDeliverAsync(database, "agv-b");

        // 第 12 秒：1 號機做了 6 件（剩 4 件）要叫刀；2 號機做了 4 件，還不用。
        await AdvanceAsync(database, 8);
        var rough = await RoughAsync(database);
        Assert.Equal((6, 4), (rough.Machine(1).Machining!.PiecesCompleted, rough.Machine(2).Machining!.PiecesCompleted));
        Assert.Equal((ToolSpareStatus.Requested, ToolSpareStatus.None), (rough.Machine(1).Tool!.Spare, rough.Machine(2).Tool!.Spare));
        await using (var scope = database.NewScope())
        {
            var task = await scope.Db.TransportTasks.SingleAsync(t => t.Kind == TransportTaskKind.ToolDelivery);
            Assert.Equal(("tool-store", "rough", "rough-1"), (task.FromStationCode, task.ToStationCode, task.MachineCode));
        }

        // 小型車送到：新刀是給 1 號機的。
        await using (var scope = database.NewScope())
        {
            Assert.Equal(1, await scope.Transport.DispatchPendingAsync([Online("agv-s")], default));
        }

        await database.CompleteDeliveryAsync("agv-s");
        rough = await RoughAsync(database);
        Assert.Equal((ToolSpareStatus.Delivered, ToolSpareStatus.None), (rough.Machine(1).Tool!.Spare, rough.Machine(2).Tool!.Spare));

        // 第 20 秒：1 號機做滿 10 件、新刀已到，換刀；2 號機做了 8 件，換它叫刀。
        await AdvanceAsync(database, 8);
        rough = await RoughAsync(database);
        Assert.Equal((true, false), (rough.Machine(1).Tool!.Changing, rough.Machine(2).Tool!.Changing));
        Assert.Equal((8, ToolSpareStatus.Requested), (rough.Machine(2).Tool!.PiecesUsed, rough.Machine(2).Tool!.Spare));
        await using (var scope = database.NewScope())
        {
            Assert.Equal(new[] { "rough-1", "rough-2" },
                await scope.Db.TransportTasks.Where(t => t.Kind == TransportTaskKind.ToolDelivery).OrderBy(t => t.CreatedAt).Select(t => t.MachineCode!).ToListAsync());
        }

        // 換刀 20 秒後完成，紀錄記在 1 號機。2 號機的刀在第 24 秒到壽命、它的新刀還沒送：只有 2 號機停機等刀。
        await AdvanceAsync(database, 20);
        rough = await RoughAsync(database);
        Assert.Equal((false, 0, false), (rough.Machine(1).Tool!.Changing, rough.Machine(1).Tool!.PiecesUsed, rough.Machine(1).Tool!.WaitingForTool));
        Assert.Equal((true, 10), (rough.Machine(2).Tool!.WaitingForTool, rough.Machine(2).Machining!.PiecesCompleted));
        await using (var scope = database.NewScope())
        {
            var change = await scope.Db.ToolChanges.SingleAsync();
            Assert.Equal(("rough", "rough-1", 10), (change.StationCode, change.MachineCode, change.PiecesUsed));
        }
    }

    [Fact]
    public async Task Upgrading_an_old_database_turns_each_station_into_machine_one_and_adds_the_second_lathes()
    {
        var options = new DbContextOptionsBuilder<OpenFleetDbContext>().UseNpgsql(postgres.NewConnectionString()).Options;
        await using (var db = new OpenFleetDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261022110627_DemoSpeedsAndLoadedVehicleSpeed");

            // P5 的資料：粗加工的刀用了 123 件、已經叫刀，換過一次刀；有一筆送刀任務在等車、一段進行中的缺車停機。
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Stations" ("Code", "Name", "HandoffX", "HandoffZ", "InboundCapacity", "OutboundCapacity") VALUES
                    ('rough', '粗加工', -12, -9.4, 30, 30), ('fine', '精加工', -4, -9.4, 30, 30), ('polish', '拋光', 20, 9.4, NULL, NULL),
                    ('heat', '淬火', 4, -9.4, NULL, NULL), ('tool-store', '刀具庫', 0, -3.2, NULL, NULL);
                INSERT INTO "ProcessSteps" ("Sequence", "StationCode", "Kind", "SecondsPerPiece", "UsesTool", "IsInspection", "IsPacking", "IsCutting",
                    "ToolLifePieces", "BatchMinPieces", "BatchMaxPieces", "BatchMaxPiecesRod24", "BatchSeconds", "StageName", "TestSpeed") VALUES
                    (1, 'rough', 'Piece', 30, TRUE, FALSE, FALSE, FALSE, 500, 0, 0, 0, 0, NULL, 1),
                    (2, 'fine', 'Piece', 60, TRUE, FALSE, FALSE, FALSE, 250, 0, 0, 0, 0, NULL, 1),
                    (3, 'heat', 'Batch', 0, FALSE, FALSE, FALSE, FALSE, 0, 150, 300, 0, 14400, '淬火', 1),
                    (6, 'polish', 'Piece', 20, FALSE, FALSE, FALSE, FALSE, 0, 0, 0, 0, 0, NULL, 1);
                INSERT INTO "StationTools" ("StationCode", "PiecesSinceChange", "SpareRequestedAt") VALUES
                    ('rough', 123, '2026-10-09 08:00:00+00'), ('fine', 7, NULL);
                INSERT INTO "ToolChanges" ("StationCode", "PiecesUsed", "StartedAt", "CompletedAt") VALUES
                    ('rough', 500, '2026-10-09 07:00:00+00', '2026-10-09 07:01:30+00');
                INSERT INTO "TransportTasks" ("Id", "FromStationCode", "ToStationCode", "Quantity", "Kind", "Status", "CreatedAt") VALUES
                    ('11111111-1111-1111-1111-111111111111', 'tool-store', 'rough', 1, 'ToolDelivery', 'Pending', '2026-10-09 08:00:00+00');
                INSERT INTO "MachineDelays" ("StationCode", "StepSequence", "Cause", "Reason", "TaskId", "StartedAt", "AccruedAt", "SimulatedSeconds") VALUES
                    ('rough', 1, 'WaitingForTool', 'VehiclesBusy', '11111111-1111-1111-1111-111111111111', '2026-10-09 08:00:00+00', '2026-10-09 08:00:05+00', 5);
                """);

            await db.Database.MigrateAsync();
        }

        await using (var db = new OpenFleetDbContext(options))
        {
            // 原本那一台變成 1 號機；粗加工、精加工多一台 2 號機（新刀）；容量變成兩台的 60 件。
            Assert.Equal(new[] { "fine-1", "fine-2", "polish-1", "rough-1", "rough-2" }, await db.Machines.OrderBy(m => m.Code).Select(m => m.Code).ToListAsync());
            var tools = await db.MachineTools.OrderBy(t => t.MachineCode).ToListAsync();
            Assert.Equal(new[] { ("fine-1", 7), ("fine-2", 0), ("rough-1", 123), ("rough-2", 0) }, tools.Select(t => (t.MachineCode, t.PiecesSinceChange)));
            Assert.NotNull(tools.Single(t => t.MachineCode == "rough-1").SpareRequestedAt);
            Assert.Equal(new int?[] { 60, 60 }, await db.Stations.Where(s => s.Code == "rough" || s.Code == "fine").Select(s => s.InboundCapacity).ToListAsync());

            // 換刀紀錄、送刀任務、缺車停機都接到 1 號機。
            Assert.Equal("rough-1", (await db.ToolChanges.SingleAsync()).MachineCode);
            Assert.Equal("rough-1", (await db.TransportTasks.SingleAsync()).MachineCode);
            Assert.Equal("rough-1", (await db.MachineDelays.SingleAsync()).MachineCode);

            // 啟動時的示範資料補上其他站與機台，並把名稱改成「粗加工 1 號機」這樣的命名。
            await DatabaseInitializer.SeedAsync(db, new FleetOptions());
        }

        await using (var db = new OpenFleetDbContext(options))
        {
            Assert.Equal("粗加工 2 號機", (await db.Machines.SingleAsync(m => m.Code == "rough-2")).Name);
            Assert.Equal(8, await db.Machines.CountAsync());
        }
    }

    private static async Task CreateAndDeliverAsync(TestDatabase database, string vehicleId)
    {
        await using (var scope = database.NewScope())
        {
            await scope.Production.CreateBatchAsync(null, null, [ProductParts.UpperPunch], default);
            Assert.Equal(1, await scope.Transport.DispatchPendingAsync([Online(vehicleId)], default));
        }

        await database.CompleteDeliveryAsync(vehicleId);
        await AdvanceAsync(database, 0);
    }

    private static async Task AdvanceAsync(TestDatabase database, double seconds)
    {
        database.Time.Advance(TimeSpan.FromSeconds(seconds));
        await using var scope = database.NewScope();
        await scope.Production.AdvanceOperationsAsync(default);
    }

    private static async Task ConfigureAsync(TestDatabase database, Action<ProductionSettingsDto> change)
    {
        await using var scope = database.NewScope();
        var settings = await scope.Production.GetSettingsAsync(default);
        change(settings);
        await scope.Production.UpdateSettingsAsync(settings, default);
    }

    private static async Task<StationStatusDto> RoughAsync(TestDatabase database)
    {
        await using var scope = database.NewScope();
        return (await scope.Production.GetStationStatusAsync(default)).Single(s => s.Code == "rough");
    }
}
