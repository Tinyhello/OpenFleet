using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFleet.Api;
using OpenFleet.Api.Catalog;
using OpenFleet.Api.Data;
using OpenFleet.Api.Production;
using OpenFleet.Contracts;

namespace OpenFleet.Tests;

/// <summary>
/// P5 第 1 步：桿徑當基底、沖頭規格只覆寫自己的欄位；建立規格時產生品項與第 1 版 BOM；整組或單一零件展開到圓棒。
/// 備料長度、圓棒 1.5 m、切口 3 mm 都是示範值。
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CatalogTests(PostgresFixture postgres) : IAsyncLifetime
{
    private TestDatabase _database = null!;

    public async Task InitializeAsync() => _database = await TestDatabase.CreateAsync(postgres);

    public Task DisposeAsync() => Task.CompletedTask;

    private CatalogService NewCatalog(OpenFleetDbContext db) => new(db, _database.Time, NullLogger<CatalogService>.Instance);

    [Fact]
    public async Task Spec_inherits_from_its_rod_and_overrides_only_its_own_fields()
    {
        await using var db = _database.NewContext();
        var catalog = NewCatalog(db);

        // 六角 10：只覆寫精加工秒數，其他沿用 Φ30 與製程設定（粗加工 30 秒）。
        var hex = (await catalog.GetSpecAsync("P30-HX10", default))!;
        Assert.Equal((30, 30, 38), (hex.RodDiameterMm, hex.PiecesPerBin, hex.DieOuterDiameterMm));
        Assert.Equal((90d, true), (hex.FinishSecondsPerPiece.Value, hex.FinishSecondsPerPiece.Overridden));
        Assert.Equal((30d, false), (hex.RoughSecondsPerPiece.Value, hex.RoughSecondsPerPiece.Overridden));
        Assert.Equal([110d, 90d, 95d], ProductParts.All.Select(p => hex.BlankLengthMm[p].Value));
        Assert.All(hex.BlankLengthMm.Values, v => Assert.False(v.Overridden));

        // 鍵形：上沖備料覆寫成 115，中模與下沖沿用 Φ24。
        var key = (await catalog.GetSpecAsync("P24-SK01", default))!;
        Assert.Equal(45, key.PiecesPerBin);
        Assert.Equal((115d, true), (key.BlankLengthMm[ProductParts.UpperPunch].Value, key.BlankLengthMm[ProductParts.UpperPunch].Overridden));
        Assert.Equal((85d, false), (key.BlankLengthMm[ProductParts.Die].Value, key.BlankLengthMm[ProductParts.Die].Overridden));
        Assert.Equal((88d, false), (key.BlankLengthMm[ProductParts.LowerPunch].Value, key.BlankLengthMm[ProductParts.LowerPunch].Overridden));
    }

    [Fact]
    public async Task Changing_a_rod_default_reaches_specs_that_do_not_override_it()
    {
        await using (var db = _database.NewContext())
        {
            await db.RodSizes.Where(r => r.DiameterMm == 24).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.UpperBlankLengthMm, 105)
                .SetProperty(r => r.DieBlankLengthMm, 80));
        }

        await using var read = _database.NewContext();
        var catalog = NewCatalog(read);
        var square = (await catalog.GetSpecAsync("P24-SQ08", default))!;
        var key = (await catalog.GetSpecAsync("P24-SK01", default))!;

        Assert.Equal(105, square.BlankLengthMm[ProductParts.UpperPunch].Value);
        Assert.Equal(80, square.BlankLengthMm[ProductParts.Die].Value);
        // 自己覆寫的不受影響。
        Assert.Equal(115, key.BlankLengthMm[ProductParts.UpperPunch].Value);
        Assert.Equal(80, key.BlankLengthMm[ProductParts.Die].Value);
    }

    [Fact]
    public async Task Creating_a_spec_creates_the_set_three_parts_three_blanks_and_the_first_bom()
    {
        await using (var db = _database.NewContext())
        {
            var created = await NewCatalog(db).CreateSpecAsync(new CreatePunchSpecRequest
            {
                Code = "p30-tr06", Name = "三角 6", Shape = "三角形", RodDiameterMm = 30, LowerBlankLengthMm = 100,
            }, default);
            Assert.Equal("P30-TR06", created.Code);
            Assert.Equal((100d, true), (created.BlankLengthMm[ProductParts.LowerPunch].Value, created.BlankLengthMm[ProductParts.LowerPunch].Overridden));
        }

        await using var read = _database.NewContext();
        var catalog = NewCatalog(read);
        var items = await read.Items.AsNoTracking().Where(i => i.PunchSpecCode == "P30-TR06").ToDictionaryAsync(i => i.Code);
        Assert.Equal(7, items.Count);
        Assert.Equal(ItemKind.Set, items["P30-TR06"].Kind);
        Assert.Equal((ItemKind.Part, 30), (items["P30-TR06-U"].Kind, items["P30-TR06-U"].DiameterMm!.Value));
        Assert.Equal((ItemKind.Part, 38), (items["P30-TR06-D"].Kind, items["P30-TR06-D"].DiameterMm!.Value));
        Assert.Equal((ItemKind.Blank, 40), (items["P30-TR06-L-BLK"].Kind, items["P30-TR06-L-BLK"].DiameterMm!.Value));

        // 一組＝一支上沖、一支中模、一支下沖。
        var setBom = (await catalog.GetBomAsync("P30-TR06", null, default))!;
        Assert.Equal(1, setBom.Version);
        Assert.Equal(["P30-TR06-U", "P30-TR06-D", "P30-TR06-L"], setBom.Lines.Select(l => l.ChildItemCode));
        Assert.All(setBom.Lines, l => Assert.Equal(1, l.Quantity));

        var partBom = (await catalog.GetBomAsync("P30-TR06-D", null, default))!;
        Assert.Equal(("P30-TR06-D-BLK", 1), (partBom.Lines.Single().ChildItemCode, partBom.Lines.Single().Quantity));
        Assert.Null(await catalog.GetBomAsync("P30-TR06-D-BLK", null, default));
    }

    [Fact]
    public async Task Invalid_or_duplicate_specs_are_rejected()
    {
        await using var db = _database.NewContext();
        var catalog = NewCatalog(db);

        await Assert.ThrowsAsync<ConflictException>(() =>
            catalog.CreateSpecAsync(new CreatePunchSpecRequest { Code = "P30-HX10", Name = "重複", RodDiameterMm = 30 }, default));
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            catalog.CreateSpecAsync(new CreatePunchSpecRequest { Code = "P25-X1", Name = "沒有這種桿徑", RodDiameterMm = 25 }, default));
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            catalog.CreateSpecAsync(new CreatePunchSpecRequest { Code = "P 30", Name = "代碼有空白", RodDiameterMm = 30 }, default));
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            catalog.CreateSpecAsync(new CreatePunchSpecRequest { Code = "P30-LONG", Name = "備料比圓棒長", RodDiameterMm = 30, UpperBlankLengthMm = 2000 }, default));
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            catalog.CreateSpecAsync(new CreatePunchSpecRequest { Code = "P30-ZERO", Name = "秒數為 0", RodDiameterMm = 30, FinishSecondsPerPiece = 0 }, default));

        Assert.Equal(4, await db.PunchSpecs.CountAsync());
    }

    [Fact]
    public async Task Two_requests_creating_the_same_code_at_once_produce_one_spec()
    {
        var request = new CreatePunchSpecRequest { Code = "P24-RACE", Name = "同時建立", RodDiameterMm = 24 };

        async Task<bool> CreateAsync()
        {
            await using var db = _database.NewContext();
            try
            {
                await NewCatalog(db).CreateSpecAsync(request, default);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(CreateAsync(), CreateAsync());

        Assert.Single(results, created => created);
        await using var read = _database.NewContext();
        Assert.Equal(7, await read.Items.CountAsync(i => i.PunchSpecCode == "P24-RACE"));
        Assert.Equal(4, await read.BomVersions.CountAsync(b => b.ItemCode.StartsWith("P24-RACE")));
    }

    [Fact]
    public async Task Set_explodes_into_parts_in_set_order_with_bars_and_extra_blanks()
    {
        await using var db = _database.NewContext();

        var explosion = await NewCatalog(db).ExplodeAsync("P30-HX10", 120, default);

        // 圓棒 1500 mm、切口 3 mm：上沖 110 → 一根 13 支，中模 90 → 16 支，下沖 95 → 15 支。
        Assert.Equal((1500, 3, 1), (explosion.BarLengthMm, explosion.KerfMm, explosion.BomVersion));
        Assert.Equal([ProductParts.UpperPunch, ProductParts.Die, ProductParts.LowerPunch], explosion.Lines.Select(l => l.PartCode));
        Assert.All(explosion.Lines, l => Assert.Equal(120, l.Quantity));
        Assert.Equal([13, 16, 15], explosion.Lines.Select(l => l.PiecesPerBar));
        Assert.Equal([10, 8, 8], explosion.Lines.Select(l => l.Bars));
        Assert.Equal([10, 8, 0], explosion.Lines.Select(l => l.ExtraBlanks));
        Assert.Equal([31, 12, 30], explosion.Lines.Select(l => l.RemnantMmPerBar));
        Assert.Equal(26, explosion.TotalBars);
    }

    [Fact]
    public async Task Single_part_explodes_into_that_part_only()
    {
        await using var db = _database.NewContext();
        var catalog = NewCatalog(db);

        // 鍵形上沖覆寫成 115 mm：一根 1500 ÷ 118 = 12 支，50 支要 5 根、多 10 支。
        var line = (await catalog.ExplodeAsync("P24-SK01-U", 50, default)).Lines.Single();
        Assert.Equal((ProductParts.UpperPunch, "P24-SK01-U-BLK", 115, 12, 5, 10),
            (line.PartCode, line.BlankItemCode, line.BlankLengthMm, line.PiecesPerBar, line.Bars, line.ExtraBlanks));

        await Assert.ThrowsAsync<RequestValidationException>(() => catalog.ExplodeAsync("P24-SK01", 0, default));
        await Assert.ThrowsAsync<RequestValidationException>(() => catalog.ExplodeAsync("P24-SK01", 301, default));
        await Assert.ThrowsAsync<RequestValidationException>(() => catalog.ExplodeAsync("P24-SK01-U-BLK", 10, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => catalog.ExplodeAsync("NOPE", 10, default));
    }

    [Fact]
    public async Task Die_follows_its_set_for_bin_size_and_plating_capacity()
    {
        // 使用者 2026-10-22：中模一框支數跟同組的上下沖相同、電鍍一爐支數跟下沖相同。
        await using var scope = _database.NewScope();
        var bins = await scope.Production.CreateBatchAsync(90, 24, [ProductParts.Die], default);
        Assert.Equal([45, 45], bins.Select(b => b.Quantity));

        await using var db = _database.NewContext();
        var plating = await db.ProcessSteps.AsNoTracking().SingleAsync(s => s.StationCode == "plating");
        Assert.Equal((56, 40), (plating.BatchCapacityFor(24), plating.BatchCapacityFor(30)));
    }
}
