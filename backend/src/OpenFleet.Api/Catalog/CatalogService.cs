using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenFleet.Api.Data;
using OpenFleet.Api.Production;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Catalog;

/// <summary>
/// 沖頭規格解析後的有效值：沖頭規格有覆寫就用覆寫，否則沿用桿徑（備料長度、一框支數）或製程設定（加工秒數）。
/// 之後開立工單時會把這一份存下來，修改規格或桿徑不影響已開立的工單。
/// </summary>
public sealed record ResolvedPunchSpec(
    string Code,
    string Name,
    string? Shape,
    int RodDiameterMm,
    int PiecesPerBin,
    int DieOuterDiameterMm,
    IReadOnlyDictionary<string, (int Value, bool Overridden)> BlankLengthMm,
    (double Value, bool Overridden) RoughSecondsPerPiece,
    (double Value, bool Overridden) FinishSecondsPerPiece);

/// <summary>規格、品項與 BOM（Scoped：跟著一個 DbContext）。</summary>
public sealed partial class CatalogService(OpenFleetDbContext db, TimeProvider time, ILogger<CatalogService> logger)
{
    /// <summary>中模外徑（使用者 2026-10-22：中模尺寸是 38，不跟桿徑走）。</summary>
    public const int DieOuterDiameterMm = 38;

    /// <summary>原料：Φ40 圓棒（使用者 2026-10-22），三種零件都從它切。</summary>
    public const string BarItemCode = "BAR-D40";
    public const int BarDiameterMm = 40;

    /// <summary>一根圓棒的長度與切口損耗是示範值（使用者只記得一根可切十幾支），有實際數字再換。</summary>
    public const int DemoBarLengthMm = 1500;
    public const int DemoKerfMm = 3;

    private const string RoughStation = "rough";
    private const string FinishStation = "fine";

    public static string PartItemCode(string specCode, string partCode) => $"{specCode}-{ProductParts.Letter(partCode)}";
    public static string BlankItemCode(string specCode, string partCode) => $"{PartItemCode(specCode, partCode)}-BLK";

    /// <summary>一根圓棒切幾支：圓棒長 ÷ (備料長 + 切口)，無條件捨去。</summary>
    public static int PiecesPerBar(int barLengthMm, int blankLengthMm, int kerfMm) => barLengthMm / (blankLengthMm + kerfMm);

    /// <summary>
    /// 一種沖頭規格要有的品項與第 1 版 BOM：整組（上沖、中模、下沖各 1）、三個零件（各用 1 支備料）、三種備料。
    /// 建立規格與啟動時補示範資料都用這一份。
    /// </summary>
    public static (List<Item> Items, List<BomVersion> Boms) BuildSpecRecords(PunchSpec spec, DateTimeOffset now)
    {
        var items = new List<Item>
        {
            new() { Code = spec.Code, Kind = ItemKind.Set, Name = $"沖模組 {spec.Code}", PunchSpecCode = spec.Code },
        };
        var setBom = NewBom(spec.Code, now);
        var boms = new List<BomVersion> { setBom };

        var sequence = 0;
        foreach (var part in ProductParts.All)
        {
            var partCode = PartItemCode(spec.Code, part);
            var blankCode = BlankItemCode(spec.Code, part);
            items.Add(new Item
            {
                Code = partCode, Kind = ItemKind.Part, Name = $"{ProductParts.Name(part)} {spec.Code}", PunchSpecCode = spec.Code, PartCode = part,
                DiameterMm = part == ProductParts.Die ? DieOuterDiameterMm : spec.RodDiameterMm,
            });
            items.Add(new Item
            {
                Code = blankCode, Kind = ItemKind.Blank, Name = $"備料 {ProductParts.Name(part)} {spec.Code}", PunchSpecCode = spec.Code, PartCode = part,
                DiameterMm = BarDiameterMm,
            });

            setBom.Lines.Add(new BomLine { Id = Guid.NewGuid(), BomVersionId = setBom.Id, Sequence = ++sequence, ChildItemCode = partCode, Quantity = 1 });
            var partBom = NewBom(partCode, now);
            partBom.Lines.Add(new BomLine { Id = Guid.NewGuid(), BomVersionId = partBom.Id, Sequence = 1, ChildItemCode = blankCode, Quantity = 1 });
            boms.Add(partBom);
        }

        return (items, boms);

        static BomVersion NewBom(string itemCode, DateTimeOffset now) =>
            new() { Id = Guid.NewGuid(), ItemCode = itemCode, Version = 1, CreatedAt = now };
    }

    public static ResolvedPunchSpec Resolve(PunchSpec spec, RodSize rod, double roughDefaultSeconds, double finishDefaultSeconds)
    {
        var blanks = ProductParts.All.ToDictionary(
            part => part,
            part => spec.BlankLengthOverrideFor(part) is { } value ? (value, true) : (rod.BlankLengthFor(part), false));
        return new ResolvedPunchSpec(
            spec.Code, spec.Name, spec.Shape, rod.DiameterMm, rod.PiecesPerBin, DieOuterDiameterMm, blanks,
            spec.RoughSecondsPerPiece is { } rough ? (rough, true) : (roughDefaultSeconds, false),
            spec.FinishSecondsPerPiece is { } finish ? (finish, true) : (finishDefaultSeconds, false));
    }

    public async Task<List<RodSizeDto>> GetRodSizesAsync(CancellationToken cancellationToken) =>
        (await db.RodSizes.AsNoTracking().OrderByDescending(r => r.DiameterMm).ToListAsync(cancellationToken))
        .Select(r => new RodSizeDto
        {
            DiameterMm = r.DiameterMm,
            PiecesPerBin = r.PiecesPerBin,
            BlankLengthMm = ProductParts.All.ToDictionary(part => part, r.BlankLengthFor),
        }).ToList();

    public async Task<List<PunchSpecDto>> GetSpecsAsync(CancellationToken cancellationToken)
    {
        var (rough, finish) = await StepDefaultsAsync(cancellationToken);
        var specs = await db.PunchSpecs.AsNoTracking().Include(s => s.Rod).OrderByDescending(s => s.RodDiameterMm).ThenBy(s => s.Code)
            .ToListAsync(cancellationToken);
        return specs.Select(s => ToDto(Resolve(s, s.Rod!, rough, finish))).ToList();
    }

    public async Task<PunchSpecDto?> GetSpecAsync(string code, CancellationToken cancellationToken) =>
        await ResolveAsync(code, cancellationToken) is { } resolved ? ToDto(resolved) : null;

    public async Task<ResolvedPunchSpec?> ResolveAsync(string code, CancellationToken cancellationToken)
    {
        var spec = await db.PunchSpecs.AsNoTracking().Include(s => s.Rod).SingleOrDefaultAsync(s => s.Code == code, cancellationToken);
        if (spec is null)
        {
            return null;
        }

        var (rough, finish) = await StepDefaultsAsync(cancellationToken);
        return Resolve(spec, spec.Rod!, rough, finish);
    }

    /// <summary>
    /// 建立一種沖頭規格，連同它的品項與第 1 版 BOM，在同一筆交易寫入。
    /// 代碼重複時回 409；兩個請求同時建立同一個代碼，由主鍵擋下後到的那一個。
    /// </summary>
    public async Task<PunchSpecDto> CreateSpecAsync(CreatePunchSpecRequest request, CancellationToken cancellationToken)
    {
        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (!SpecCodePattern().IsMatch(code))
        {
            throw new RequestValidationException("code", "Code must be 3-24 characters of A-Z, 0-9 and '-', starting with a letter or digit.");
        }

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 64)
        {
            throw new RequestValidationException("name", "Name is required and must be at most 64 characters.");
        }

        var shape = string.IsNullOrWhiteSpace(request.Shape) ? null : request.Shape.Trim();
        if (shape is { Length: > 64 })
        {
            throw new RequestValidationException("shape", "Shape must be at most 64 characters.");
        }

        var rod = await db.RodSizes.AsNoTracking().SingleOrDefaultAsync(r => r.DiameterMm == request.RodDiameterMm, cancellationToken);
        if (rod is null)
        {
            var known = await db.RodSizes.Select(r => r.DiameterMm).OrderBy(d => d).ToListAsync(cancellationToken);
            throw new RequestValidationException("rodDiameterMm", $"Rod diameter must be one of {string.Join(", ", known)} mm.");
        }

        ValidateBlankLength(request.UpperBlankLengthMm, "upperBlankLengthMm");
        ValidateBlankLength(request.DieBlankLengthMm, "dieBlankLengthMm");
        ValidateBlankLength(request.LowerBlankLengthMm, "lowerBlankLengthMm");
        ValidateSeconds(request.RoughSecondsPerPiece, "roughSecondsPerPiece");
        ValidateSeconds(request.FinishSecondsPerPiece, "finishSecondsPerPiece");

        if (await db.PunchSpecs.AnyAsync(s => s.Code == code, cancellationToken))
        {
            throw new ConflictException($"Punch spec {code} already exists.");
        }

        var now = time.GetUtcNow();
        var spec = new PunchSpec
        {
            Code = code,
            Name = name,
            Shape = shape,
            RodDiameterMm = rod.DiameterMm,
            UpperBlankLengthMm = request.UpperBlankLengthMm,
            DieBlankLengthMm = request.DieBlankLengthMm,
            LowerBlankLengthMm = request.LowerBlankLengthMm,
            RoughSecondsPerPiece = request.RoughSecondsPerPiece,
            FinishSecondsPerPiece = request.FinishSecondsPerPiece,
            CreatedAt = now,
        };
        var (items, boms) = BuildSpecRecords(spec, now);
        db.PunchSpecs.Add(spec);
        db.Items.AddRange(items);
        db.BomVersions.AddRange(boms);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException($"Punch spec {code} already exists.");
        }

        logger.LogInformation("Punch spec {Code} created on rod {Rod} mm with {Items} item(s).", code, rod.DiameterMm, items.Count);
        var (rough, finish) = await StepDefaultsAsync(cancellationToken);
        return ToDto(Resolve(spec, rod, rough, finish));
    }

    public async Task<List<ItemDto>> GetItemsAsync(ItemKind? kind, CancellationToken cancellationToken)
    {
        var query = db.Items.AsNoTracking();
        if (kind is { } k)
        {
            query = query.Where(i => i.Kind == k);
        }

        var items = await query.ToListAsync(cancellationToken);
        // 依規格分組，組內依整組、上沖、中模、下沖的順序。
        return items
            .OrderBy(i => i.Kind == ItemKind.Bar ? 1 : 0)
            .ThenBy(i => i.PunchSpecCode)
            .ThenBy(i => i.PartCode is null ? -1 : Array.IndexOf(ProductParts.All, i.PartCode))
            .ThenBy(i => i.Kind)
            .Select(ToDto)
            .ToList();
    }

    /// <summary>一個品項的 BOM；不指定版本時取最新一版。沒有 BOM 的品項（備料、圓棒）回傳 null。</summary>
    public async Task<BomDto?> GetBomAsync(string itemCode, int? version, CancellationToken cancellationToken)
    {
        var bom = await LoadBomAsync(itemCode, version, cancellationToken);
        if (bom is null)
        {
            return null;
        }

        var childCodes = bom.Lines.Select(l => l.ChildItemCode).ToList();
        var names = await db.Items.AsNoTracking()
            .Where(i => childCodes.Contains(i.Code))
            .ToDictionaryAsync(i => i.Code, i => i.Name, cancellationToken);
        return new BomDto
        {
            ItemCode = bom.ItemCode,
            Version = bom.Version,
            Lines = bom.Lines.OrderBy(l => l.Sequence).Select(l => new BomLineDto
            {
                ChildItemCode = l.ChildItemCode,
                ChildName = names.GetValueOrDefault(l.ChildItemCode, l.ChildItemCode),
                Quantity = l.Quantity,
            }).ToList(),
        };
    }

    /// <summary>
    /// 整組或單一零件展開到備料與圓棒：每種零件要幾支、備料多長、一根切幾支、全部從圓棒切要幾根、多出幾支。
    /// 還沒扣現有備料與圓棒庫存（庫存是下一步）。
    /// </summary>
    public async Task<ExplosionDto> ExplodeAsync(string itemCode, int quantity, CancellationToken cancellationToken)
    {
        if (quantity is < 1 or > ProductionService.MaxBatchQuantity)
        {
            throw new RequestValidationException("quantity", $"Quantity must be between 1 and {ProductionService.MaxBatchQuantity}.");
        }

        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Code == itemCode, cancellationToken)
            ?? throw new KeyNotFoundException($"Item {itemCode} not found.");
        if (item.Kind is not (ItemKind.Set or ItemKind.Part))
        {
            throw new RequestValidationException("itemCode", "Only a set or a part can be exploded.");
        }

        var bom = await LoadBomAsync(item.Code, null, cancellationToken)
            ?? throw new InvalidOperationException($"Item {item.Code} has no BOM.");
        var resolved = await ResolveAsync(item.PunchSpecCode!, cancellationToken)
            ?? throw new InvalidOperationException($"Punch spec {item.PunchSpecCode} not found.");
        var bar = await db.Items.AsNoTracking().SingleAsync(i => i.Code == BarItemCode, cancellationToken);
        var barLength = bar.LengthMm ?? DemoBarLengthMm;

        // 整組：BOM 的每一行是一種零件；單一零件：就是它自己。
        var parts = item.Kind == ItemKind.Set
            ? bom.Lines.OrderBy(l => l.Sequence).Select(l => (PartItemCode: l.ChildItemCode, PerSet: l.Quantity)).ToList()
            : [(item.Code, 1)];
        var partItemCodes = parts.Select(p => p.PartItemCode).ToList();
        var partItems = await db.Items.AsNoTracking()
            .Where(i => partItemCodes.Contains(i.Code))
            .ToDictionaryAsync(i => i.Code, cancellationToken);

        var lines = parts.Select(p =>
        {
            var partCode = partItems[p.PartItemCode].PartCode!;
            var needed = quantity * p.PerSet;
            var length = resolved.BlankLengthMm[partCode].Value;
            var perBar = PiecesPerBar(barLength, length, DemoKerfMm);
            var bars = (needed + perBar - 1) / perBar;
            return new ExplosionLineDto
            {
                PartCode = partCode,
                PartItemCode = p.PartItemCode,
                Quantity = needed,
                BlankItemCode = BlankItemCode(resolved.Code, partCode),
                BlankLengthMm = length,
                PiecesPerBar = perBar,
                Bars = bars,
                ExtraBlanks = bars * perBar - needed,
                RemnantMmPerBar = barLength - perBar * (length + DemoKerfMm),
            };
        }).ToList();

        return new ExplosionDto
        {
            ItemCode = item.Code,
            Quantity = quantity,
            BomVersion = bom.Version,
            BarItemCode = bar.Code,
            BarLengthMm = barLength,
            KerfMm = DemoKerfMm,
            TotalBars = lines.Sum(l => l.Bars),
            Lines = lines,
        };
    }

    private async Task<BomVersion?> LoadBomAsync(string itemCode, int? version, CancellationToken cancellationToken)
    {
        var query = db.BomVersions.AsNoTracking().Include(b => b.Lines).Where(b => b.ItemCode == itemCode);
        query = version is { } v ? query.Where(b => b.Version == v) : query.OrderByDescending(b => b.Version);
        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>沒有覆寫時沿用的加工秒數：製程設定裡粗加工、精加工的每件時間。</summary>
    private async Task<(double Rough, double Finish)> StepDefaultsAsync(CancellationToken cancellationToken)
    {
        var steps = await db.ProcessSteps.AsNoTracking()
            .Where(s => s.StationCode == RoughStation || s.StationCode == FinishStation)
            .ToDictionaryAsync(s => s.StationCode, s => s.SecondsPerPiece, cancellationToken);
        return (steps.GetValueOrDefault(RoughStation), steps.GetValueOrDefault(FinishStation));
    }

    private static void ValidateBlankLength(int? value, string field)
    {
        // 至少要能從一根圓棒切出一支。
        const int max = DemoBarLengthMm - DemoKerfMm;
        if (value is { } v && (v < 1 || v > max))
        {
            throw new RequestValidationException(field, $"Blank length must be between 1 and {max} mm.");
        }
    }

    private static void ValidateSeconds(double? value, string field)
    {
        if (value is { } v && (double.IsNaN(v) || v <= 0 || v > ProductionService.MaxSecondsPerPiece))
        {
            throw new RequestValidationException(field, $"Seconds per piece must be greater than 0 and at most {ProductionService.MaxSecondsPerPiece}.");
        }
    }

    private static PunchSpecDto ToDto(ResolvedPunchSpec s) => new()
    {
        Code = s.Code,
        Name = s.Name,
        Shape = s.Shape,
        RodDiameterMm = s.RodDiameterMm,
        PiecesPerBin = s.PiecesPerBin,
        DieOuterDiameterMm = s.DieOuterDiameterMm,
        BlankLengthMm = s.BlankLengthMm.ToDictionary(kv => kv.Key, kv => new ResolvedValueDto { Value = kv.Value.Value, Overridden = kv.Value.Overridden }),
        RoughSecondsPerPiece = new ResolvedValueDto { Value = s.RoughSecondsPerPiece.Value, Overridden = s.RoughSecondsPerPiece.Overridden },
        FinishSecondsPerPiece = new ResolvedValueDto { Value = s.FinishSecondsPerPiece.Value, Overridden = s.FinishSecondsPerPiece.Overridden },
    };

    private static ItemDto ToDto(Item i) => new()
    {
        Code = i.Code,
        Kind = i.Kind,
        Name = i.Name,
        PunchSpecCode = i.PunchSpecCode,
        PartCode = i.PartCode,
        DiameterMm = i.DiameterMm,
    };

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{2,23}$")]
    private static partial Regex SpecCodePattern();
}
