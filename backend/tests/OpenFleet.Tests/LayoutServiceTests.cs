using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFleet.Api.Data;
using OpenFleet.Api.Fleet;
using OpenFleet.Api.Layout;
using OpenFleet.Api.Production;
using OpenFleet.Api.Traffic;
using OpenFleet.Contracts;
using OpenFleet.Layout;
using Xunit;

namespace OpenFleet.Tests;

/// <summary>
/// 佈局的存檔、套用與啟動檢查（需要資料庫）。測試資料庫的預設佈局是 <see cref="TestLayouts.Default"/>（車號 agv-a…），
/// 要套用的佈局用正式的 <see cref="DefaultLayout"/>（車號 agv-01…）。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LayoutServiceTests(PostgresFixture postgres)
{
    private sealed class Harness : IAsyncDisposable
    {
        public required TestDatabase Database { get; init; }
        public required OpenFleetDbContext Db { get; init; }
        public required LayoutService Service { get; init; }
        public required ActiveLayout Active { get; init; }
        public required RoadNetwork Network { get; init; }
        public required TrafficController Traffic { get; init; }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private async Task<Harness> CreateAsync()
    {
        var database = await TestDatabase.CreateAsync(postgres);
        var db = database.NewContext();
        var network = new RoadNetwork();
        var active = new ActiveLayout();
        var traffic = new TrafficController(network, new TrafficOptions(), database.Time, NullLogger<TrafficController>.Instance);
        var fleet = new FleetStateStore(database.Time, new StateChangeQueue(), active);
        var runtime = new LayoutRuntime(db, network, traffic, active, fleet);
        var service = new LayoutService(db, new LayoutGate(), runtime, database.Time, NullLogger<LayoutService>.Instance);
        await runtime.LoadAsync(CancellationToken.None);
        return new Harness { Database = database, Db = db, Service = service, Active = active, Network = network, Traffic = traffic };
    }

    private static SaveLayoutRequest Request(string name, FactoryLayoutDocument? document = null) =>
        new() { Name = name, Document = document ?? DefaultLayout.Build() };

    [Fact]
    public async Task Apply_while_work_is_in_progress_is_refused_and_changes_nothing()
    {
        await using var h = await CreateAsync();
        await h.Database.OccupyAsync("agv-a");
        var saved = await h.Service.CreateAsync(Request("標準"), CancellationToken.None);

        var result = await h.Service.ApplyAsync(saved.Layout.Id, resetDemo: false, CancellationToken.None);

        Assert.Equal(ApplyStatus.Busy, result.Status);
        Assert.Contains("1 筆搬運進行中", result.Busy!.Message);
        Assert.True((await h.Service.GetActiveAsync(CancellationToken.None))!.IsDefault);
        Assert.Contains(h.Active.Revision!.Vehicles, v => v.VehicleId == "agv-a");
    }

    [Fact]
    public async Task Apply_with_reset_clears_the_work_and_moves_the_vehicles_to_the_new_layout()
    {
        await using var h = await CreateAsync();
        await h.Database.OccupyAsync("agv-a");
        var saved = await h.Service.CreateAsync(Request("標準"), CancellationToken.None);

        var result = await h.Service.ApplyAsync(saved.Layout.Id, resetDemo: true, CancellationToken.None);

        Assert.Equal(ApplyStatus.Applied, result.Status);
        Assert.True(result.Layout!.IsActive);

        await using var check = h.Database.NewContext();
        Assert.Empty(await check.TransportTasks.ToListAsync());
        Assert.Empty(await check.Lots.ToListAsync());

        // 新佈局的五台在、舊的四台退出派工（列還在，歷史紀錄可能參照）。
        var vehicles = await check.Vehicles.ToListAsync();
        Assert.Equal(new[] { "agv-01", "agv-02", "agv-03", "agv-04", "agv-05" }, vehicles.Where(v => v.InLayout).Select(v => v.Id).Order());
        Assert.Equal(new[] { "agv-a", "agv-b", "agv-c", "agv-s" }, vehicles.Where(v => !v.InLayout).Select(v => v.Id).Order());
        Assert.All(vehicles.Where(v => !v.InLayout), v => Assert.False(v.DispatchEnabled));

        // 記憶體跟著資料表換掉：路網、車輛清單與交通控制的預約。
        Assert.Equal(100, h.Network.Nodes.Count);
        Assert.Equal(5, h.Active.Revision!.Vehicles.Count);
        Assert.Empty(h.Traffic.ReservedNodes());
    }

    [Fact]
    public async Task Invalid_layout_can_be_saved_but_not_applied()
    {
        await using var h = await CreateAsync();
        var broken = DefaultLayout.Build();
        broken.Modules.RemoveAll(m => m.Kind == ModuleKind.QuenchFurnace);

        var saved = await h.Service.CreateAsync(Request("缺淬火", broken), CancellationToken.None);
        Assert.Contains(saved.Validation.Issues, i => i.Code == "step-missing");

        var result = await h.Service.ApplyAsync(saved.Layout.Id, resetDemo: true, CancellationToken.None);

        Assert.Equal(ApplyStatus.Invalid, result.Status);
        Assert.Contains(result.Validation!.Issues, i => i.Code == "step-missing");
        Assert.True((await h.Service.GetActiveAsync(CancellationToken.None))!.IsDefault);
    }

    [Fact]
    public async Task Save_rules_check_names_limits_and_protect_the_default_and_the_active_layout()
    {
        await using var h = await CreateAsync();
        var ct = CancellationToken.None;

        await Assert.ThrowsAsync<RequestValidationException>(() => h.Service.CreateAsync(Request("   "), ct));
        await Assert.ThrowsAsync<RequestValidationException>(() => h.Service.CreateAsync(Request(new string('長', 41)), ct));

        var first = await h.Service.CreateAsync(Request("甲"), ct);
        await Assert.ThrowsAsync<RequestValidationException>(() => h.Service.CreateAsync(Request("甲"), ct));

        // 預設佈局排第一，不能改、不能刪。
        var defaultLayout = (await h.Service.ListAsync(ct)).First();
        Assert.True(defaultLayout.IsDefault);
        await Assert.ThrowsAsync<RequestValidationException>(() => h.Service.UpdateAsync(defaultLayout.Id, Request("改"), ct));
        await Assert.ThrowsAsync<RequestValidationException>(() => h.Service.DeleteAsync(defaultLayout.Id, ct));

        // 使用中的佈局不能刪；套用另一份之後，原來那份才能刪。
        await h.Service.ApplyAsync(first.Layout.Id, resetDemo: true, ct);
        await Assert.ThrowsAsync<RequestValidationException>(() => h.Service.DeleteAsync(first.Layout.Id, ct));
        var second = await h.Service.CreateAsync(Request("乙"), ct);
        await h.Service.ApplyAsync(second.Layout.Id, resetDemo: true, ct);
        await h.Service.DeleteAsync(first.Layout.Id, ct);
        Assert.DoesNotContain(await h.Service.ListAsync(ct), l => l.Id == first.Layout.Id);
    }

    [Fact]
    public async Task A_save_without_a_document_is_refused_with_a_validation_error()
    {
        await using var h = await CreateAsync();

        await Assert.ThrowsAsync<RequestValidationException>(() => h.Service.CreateAsync(new SaveLayoutRequest { Name = "空", Document = null! }, CancellationToken.None));
    }

    // 文件結構壞掉（欄位是 null、清單有空項目、種類不在目錄裡）：驗證與套用都沒辦法處理，存檔要在寫入之前就擋下來。
    private static IEnumerable<(string Name, FactoryLayoutDocument Document)> BrokenDocuments()
    {
        var noModules = DefaultLayout.Build();
        noModules.Modules = null!;
        yield return ("modules is null", noModules);

        var noFloor = DefaultLayout.Build();
        noFloor.Floor = null!;
        yield return ("floor is null", noFloor);

        var unknownKind = DefaultLayout.Build();
        unknownKind.Modules.Single(m => m.Code == "rough-1").Kind = (ModuleKind)99;
        yield return ("kind 99", unknownKind);

        var nullItem = DefaultLayout.Build();
        nullItem.RoadNodes[0] = null!;
        yield return ("a null road node", nullItem);

        var noCode = DefaultLayout.Build();
        noCode.Modules.Single(m => m.Code == "rough-1").Code = null!;
        yield return ("a null module code", noCode);
    }

    [Fact]
    public async Task A_save_with_a_broken_document_is_refused_and_nothing_is_written()
    {
        await using var h = await CreateAsync();
        var ct = CancellationToken.None;
        var before = await h.Db.FactoryLayouts.CountAsync(ct);

        foreach (var (name, document) in BrokenDocuments())
        {
            var error = await Assert.ThrowsAsync<RequestValidationException>(() => h.Service.CreateAsync(Request("壞的", document), ct));
            Assert.True(error.Errors.ContainsKey("document"), name);
        }

        // 用另一個 DbContext 數：確認是資料庫裡沒有多一筆，不是這個 DbContext 沒追蹤。
        await using var check = h.Database.NewContext();
        Assert.Equal(before, await check.FactoryLayouts.CountAsync(ct));
        Assert.DoesNotContain(await check.FactoryLayouts.ToListAsync(ct), l => l.Name == "壞的");
    }

    [Fact]
    public async Task An_update_with_a_broken_document_is_refused_and_the_saved_layout_stays_as_it_was()
    {
        await using var h = await CreateAsync();
        var ct = CancellationToken.None;
        var saved = await h.Service.CreateAsync(Request("甲"), ct);

        foreach (var (_, document) in BrokenDocuments())
        {
            await Assert.ThrowsAsync<RequestValidationException>(() => h.Service.UpdateAsync(saved.Layout.Id, Request("乙", document), ct));
        }

        await using var check = h.Database.NewContext();
        var stored = await check.FactoryLayouts.SingleAsync(l => l.Id == saved.Layout.Id, ct);
        Assert.Equal("甲", stored.Name);
        Assert.Equal(DefaultLayout.Build().Modules.Count, stored.Document.Modules.Count);
    }

    [Fact]
    public async Task A_stored_layout_with_a_broken_document_cannot_be_applied()
    {
        await using var h = await CreateAsync();
        var ct = CancellationToken.None;

        // 這份文件是舊版本存下來的（現在的存檔已經擋掉了），套用走驗證器，回報無效而不是丟例外。
        var id = Guid.NewGuid();
        await using (var db = h.Database.NewContext())
        {
            var broken = DefaultLayout.Build();
            broken.RoadEdges = null!;
            var now = h.Database.Time.GetUtcNow();
            db.FactoryLayouts.Add(new FactoryLayoutEntity { Id = id, Name = "舊的壞檔", Document = broken, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync(ct);
        }

        var result = await h.Service.ApplyAsync(id, resetDemo: true, ct);

        Assert.Equal(ApplyStatus.Invalid, result.Status);
        Assert.Contains(result.Validation!.Issues, i => i.Code == "document-invalid");
        Assert.True((await h.Service.GetActiveAsync(ct))!.IsDefault);
    }

    [Fact]
    public async Task At_most_twenty_layouts_can_be_saved()
    {
        await using var h = await CreateAsync();
        var ct = CancellationToken.None;

        // 已經有預設佈局（測試資料庫的種子），再存 19 份就到上限。
        for (var i = 2; i <= LayoutService.MaxLayouts; i++)
        {
            await h.Service.CreateAsync(Request($"佈局{i}"), ct);
        }

        await Assert.ThrowsAsync<RequestValidationException>(() => h.Service.CreateAsync(Request("第 21 份"), ct));
    }

    [Fact]
    public async Task Startup_reapplies_the_default_layout_when_its_code_changed()
    {
        await using var h = await CreateAsync();
        var ct = CancellationToken.None;

        // 模擬程式改了預設佈局：種子用新的預設文件覆寫，使用中的預設佈局的雜湊就和文件不同了。
        await using (var db = h.Database.NewContext())
        {
            await DatabaseInitializer.SeedAsync(db, DefaultLayout.Build());
        }

        await h.Service.ReconcileOnStartupAsync(ct);

        await using var check = h.Database.NewContext();
        var defaultLayout = await check.FactoryLayouts.SingleAsync(l => l.IsDefault);
        Assert.True(defaultLayout.IsActive);
        Assert.Equal(LayoutJson.HashOf(DefaultLayout.Build()), defaultLayout.AppliedHash);
        Assert.Equal(new[] { "agv-01", "agv-02", "agv-03", "agv-04", "agv-05" }, h.Active.Revision!.Vehicles.Select(v => v.VehicleId).Order());
    }

    [Fact]
    public async Task Startup_falls_back_to_the_default_when_the_active_layout_became_invalid()
    {
        await using var h = await CreateAsync();
        var ct = CancellationToken.None;

        // 預設佈局改用正式的文件（測試種子的車號不符合代碼規則，不能當作退回的目標）。
        await using (var db = h.Database.NewContext())
        {
            await DatabaseInitializer.SeedAsync(db, DefaultLayout.Build());
        }

        var saved = await h.Service.CreateAsync(Request("標準"), ct);
        await h.Service.ApplyAsync(saved.Layout.Id, resetDemo: true, ct);

        // 程式改了目錄（例如拋光的種類不再合法）：使用中的「標準」連工廠正在跑的快照都變成不合法。
        await using (var db = h.Database.NewContext())
        {
            var layout = await db.FactoryLayouts.SingleAsync(l => l.Id == saved.Layout.Id);
            var broken = DefaultLayout.Build();
            broken.Modules.RemoveAll(m => m.Kind == ModuleKind.Polisher);
            layout.Document = broken;
            layout.AppliedDocument = broken;
            await db.SaveChangesAsync();
        }

        // 啟動時是新的 DbContext；這裡清掉變更追蹤，讓服務重新讀取資料庫裡的文件。
        h.Db.ChangeTracker.Clear();
        await h.Service.ReconcileOnStartupAsync(ct);

        Assert.True((await h.Service.GetActiveAsync(ct))!.IsDefault);
    }

    private static FactoryLayoutDocument MovedCarrier()
    {
        var document = DefaultLayout.Build();
        document.Modules.Single(m => m.Code == "carrier-1").X += 2;
        return document;
    }

    [Fact]
    public async Task Saving_the_active_layout_keeps_the_applied_snapshot_until_applied_again()
    {
        await using var h = await CreateAsync();
        var ct = CancellationToken.None;
        var original = DefaultLayout.Build();
        var originalX = original.Modules.Single(m => m.Code == "carrier-1").X;
        var saved = await h.Service.CreateAsync(Request("標準", original), ct);
        var applied = await h.Service.ApplyAsync(saved.Layout.Id, resetDemo: true, ct);
        Assert.False(applied.Layout!.HasUnappliedChanges);

        await using (var check = h.Database.NewContext())
        {
            var stored = await check.FactoryLayouts.SingleAsync(l => l.Id == saved.Layout.Id, ct);
            Assert.Equal(stored.AppliedHash, LayoutJson.HashOf(stored.AppliedDocument!));
            Assert.Equal(LayoutJson.HashOf(original), stored.AppliedHash);
        }

        var update = await h.Service.UpdateAsync(saved.Layout.Id, Request("標準", MovedCarrier()), ct);
        Assert.True(update.Layout.HasUnappliedChanges);

        // 工廠正在跑的還是舊的位置；可編輯的版本是新的位置。
        var active = (await h.Service.GetActiveAsync(ct))!;
        Assert.True(active.HasUnappliedChanges);
        Assert.Equal(originalX, active.Document.Modules.Single(m => m.Code == "carrier-1").X);
        var editable = (await h.Service.GetAsync(saved.Layout.Id, ct))!;
        Assert.Equal(originalX + 2, editable.Document.Modules.Single(m => m.Code == "carrier-1").X);
        Assert.True(editable.HasUnappliedChanges);
        var list = await h.Service.ListAsync(ct);
        Assert.True(list.Single(l => l.Id == saved.Layout.Id).HasUnappliedChanges);
        Assert.All(list.Where(l => l.Id != saved.Layout.Id), l => Assert.False(l.HasUnappliedChanges));

        // 再套用一次：快照換成新的，旗標消失。
        var again = await h.Service.ApplyAsync(saved.Layout.Id, resetDemo: true, ct);
        Assert.False(again.Layout!.HasUnappliedChanges);
        var after = (await h.Service.GetActiveAsync(ct))!;
        Assert.False(after.HasUnappliedChanges);
        Assert.Equal(originalX + 2, after.Document.Modules.Single(m => m.Code == "carrier-1").X);
        Assert.All(await h.Service.ListAsync(ct), l => Assert.False(l.HasUnappliedChanges));
    }

    [Fact]
    public async Task Startup_ignores_errors_that_exist_only_in_the_saved_version_of_the_active_layout()
    {
        await using var h = await CreateAsync();
        var ct = CancellationToken.None;
        await using (var db = h.Database.NewContext())
        {
            await DatabaseInitializer.SeedAsync(db, DefaultLayout.Build());
        }

        var saved = await h.Service.CreateAsync(Request("標準"), ct);
        await h.Service.ApplyAsync(saved.Layout.Id, resetDemo: true, ct);

        // 存了一份有錯誤的版本（沙盒常存半成品），沒有套用。
        var broken = DefaultLayout.Build();
        broken.Modules.RemoveAll(m => m.Kind == ModuleKind.Polisher);
        await h.Service.UpdateAsync(saved.Layout.Id, Request("標準", broken), ct);
        var appliedAt = (await h.Service.GetActiveAsync(ct))!.AppliedAt;

        h.Db.ChangeTracker.Clear();
        await h.Service.ReconcileOnStartupAsync(ct);

        var active = (await h.Service.GetActiveAsync(ct))!;
        Assert.Equal(saved.Layout.Id, active.Id);
        Assert.False(active.IsDefault);
        Assert.True(active.HasUnappliedChanges);
        Assert.Equal(appliedAt, active.AppliedAt);
        Assert.Contains(active.Document.Modules, m => m.Kind == ModuleKind.Polisher);
    }
}
