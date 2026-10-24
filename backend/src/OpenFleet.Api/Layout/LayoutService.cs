using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Api.Production;
using OpenFleet.Contracts;
using OpenFleet.Layout;

namespace OpenFleet.Api.Layout;

public enum ApplyStatus
{
    Applied,
    NotFound,
    Invalid,
    Busy,
}

public sealed record ApplyResult(
    ApplyStatus Status,
    LayoutDto? Layout = null,
    LayoutValidationDto? Validation = null,
    LayoutBusyDto? Busy = null);

/// <summary>
/// 佈局的存檔、驗證與套用（Scoped：跟著一個 DbContext）。有錯誤的佈局可以存，但不能套用。
/// 套用時先暫停背景工作，再用一筆交易清空生產資料並寫入推導結果，交易提交後才重新載入記憶體。
/// </summary>
public sealed class LayoutService(
    OpenFleetDbContext db,
    LayoutGate gate,
    LayoutRuntime runtime,
    TimeProvider time,
    ILogger<LayoutService> logger)
{
    public const string DefaultName = "預設佈局";
    public const int MaxLayouts = 20;
    public const int MaxNameLength = 40;

    public async Task<List<LayoutSummaryDto>> ListAsync(CancellationToken cancellationToken)
    {
        var layouts = await db.FactoryLayouts.AsNoTracking().ToListAsync(cancellationToken);
        return layouts
            .OrderByDescending(l => l.IsDefault)
            .ThenBy(l => l.Name, StringComparer.Ordinal)
            .Select(l => new LayoutSummaryDto
            {
                Id = l.Id,
                Name = l.Name,
                IsActive = l.IsActive,
                IsDefault = l.IsDefault,
                UpdatedAt = l.UpdatedAt,
                AppliedAt = l.AppliedAt,
            })
            .ToList();
    }

    public async Task<LayoutDto?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.FactoryLayouts.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, cancellationToken) is { } layout ? ToDto(layout) : null;

    public async Task<LayoutDto?> GetActiveAsync(CancellationToken cancellationToken) =>
        await db.FactoryLayouts.AsNoTracking().SingleOrDefaultAsync(l => l.IsActive, cancellationToken) is { } layout ? ToDto(layout) : null;

    public async Task<SaveLayoutResponse> CreateAsync(SaveLayoutRequest request, CancellationToken cancellationToken)
    {
        var validation = RequireWellFormed(request);
        var name = await ValidNameAsync(request.Name, null, cancellationToken);
        if (await db.FactoryLayouts.CountAsync(cancellationToken) >= MaxLayouts)
        {
            throw new RequestValidationException("name", $"最多只能存 {MaxLayouts} 份佈局");
        }

        var now = time.GetUtcNow();
        var layout = new FactoryLayoutEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            Document = request.Document,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.FactoryLayouts.Add(layout);
        await db.SaveChangesAsync(cancellationToken);
        return Response(layout, validation);
    }

    public async Task<SaveLayoutResponse> UpdateAsync(Guid id, SaveLayoutRequest request, CancellationToken cancellationToken)
    {
        var validation = RequireWellFormed(request);
        var layout = await FindAsync(id, cancellationToken);
        if (layout.IsDefault)
        {
            throw new RequestValidationException("id", "預設佈局不能修改，請另存");
        }

        layout.Name = await ValidNameAsync(request.Name, id, cancellationToken);
        layout.Document = request.Document;
        layout.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return Response(layout, validation);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var layout = await FindAsync(id, cancellationToken);
        if (layout.IsDefault)
        {
            throw new RequestValidationException("id", "預設佈局不能刪除");
        }

        if (layout.IsActive)
        {
            throw new RequestValidationException("id", "使用中的佈局不能刪除，請先套用另一份");
        }

        db.FactoryLayouts.Remove(layout);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>套用佈局（P7 第 2 步 5.9）。<paramref name="resetDemo"/> 為 true 時，有在製品也照樣清空。</summary>
    public async Task<ApplyResult> ApplyAsync(Guid id, bool resetDemo, CancellationToken cancellationToken)
    {
        var layout = await db.FactoryLayouts.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (layout is null)
        {
            return new ApplyResult(ApplyStatus.NotFound);
        }

        var validation = LayoutValidator.Validate(layout.Document);
        if (!validation.IsValid)
        {
            return new ApplyResult(ApplyStatus.Invalid, Validation: validation);
        }

        // 先暫停背景工作再檢查：檢查與清空之間不會有新的訂單或搬運冒出來。
        using var _ = await gate.PauseAsync(cancellationToken);

        if (!resetDemo && await BusyAsync(cancellationToken) is { } busy)
        {
            return new ApplyResult(ApplyStatus.Busy, Busy: busy);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await DemoData.ClearProductionAsync(db, cancellationToken);

        // 這兩張表的列參照被刪的機台與站點，清空後才能重建站點與機台。
        await db.Database.ExecuteSqlRawAsync("""TRUNCATE "MachineDelays", "VehicleWaits" """, cancellationToken);
        await LayoutWriter.WriteAsync(db, layout.Document, cancellationToken);
        await DatabaseInitializer.SeedInventoryAsync(db);

        // 同時只能有一份使用中：先把其他的關掉（部分唯一索引不允許兩份同時為 true）。
        await db.Database.ExecuteSqlAsync(
            $"""UPDATE "FactoryLayouts" SET "IsActive" = FALSE WHERE "IsActive" AND "Id" <> {layout.Id}""", cancellationToken);

        // 同一個 DbContext 裡已經追蹤的其他佈局，旗標要跟著資料庫（否則之後的刪除會以為它還在使用中）。
        foreach (var other in db.FactoryLayouts.Local.Where(l => l.Id != layout.Id).ToList())
        {
            await db.Entry(other).ReloadAsync(cancellationToken);
        }

        layout.IsActive = true;
        layout.AppliedAt = time.GetUtcNow();
        layout.AppliedHash = LayoutJson.HashOf(layout.Document);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await runtime.LoadAsync(cancellationToken);
        logger.LogInformation("Factory layout {Name} applied (reset demo: {ResetDemo}).", layout.Name, resetDemo);
        return new ApplyResult(ApplyStatus.Applied, Layout: ToDto(layout));
    }

    /// <summary>啟動時檢查使用中的佈局（P7 第 2 步 5.10）：驗證失敗就改用預設佈局；預設佈局的程式改了就重新套用。有在製品時只記錄警告。</summary>
    public async Task ReconcileOnStartupAsync(CancellationToken cancellationToken)
    {
        var active = await db.FactoryLayouts.SingleAsync(l => l.IsActive, cancellationToken);
        var validation = LayoutValidator.Validate(active.Document);
        if (!validation.IsValid)
        {
            logger.LogWarning("Active layout {Name} has {Count} error(s) after a code change; switching to the default layout.", active.Name, validation.Issues.Count);
            var fallback = await db.FactoryLayouts.SingleAsync(l => l.IsDefault, cancellationToken);
            await ApplyAtStartupAsync(fallback.Id, cancellationToken);
            return;
        }

        if (active.AppliedHash == LayoutJson.HashOf(active.Document))
        {
            return;
        }

        if (active.IsDefault)
        {
            logger.LogInformation("The default layout changed in code; re-applying it.");
            await ApplyAtStartupAsync(active.Id, cancellationToken);
            return;
        }

        // 自訂佈局的文件改過但還沒套用：只提醒，不替使用者套用。
        logger.LogWarning("Layout {Name} has saved changes that are not applied; the factory keeps the applied version.", active.Name);
    }

    private async Task ApplyAtStartupAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await ApplyAsync(id, resetDemo: false, cancellationToken);
        switch (result.Status)
        {
            case ApplyStatus.Busy:
                logger.LogWarning("Layout not applied at startup: {Message} Keeping the existing data.", result.Busy?.Message);
                break;
            case ApplyStatus.Invalid:
                logger.LogError("Layout not applied at startup: the layout has errors.");
                break;
        }
    }

    private async Task<LayoutBusyDto?> BusyAsync(CancellationToken cancellationToken)
    {
        // 完成的框：Scrapped、Boxed、Shipped，以及在成品倉的 Staged（P7 第 2 步 5.9）。
        var finished = ModuleCatalog.Get(ModuleKind.FinishedGoodsStore).FixedCode;
        var lots = await db.Lots.CountAsync(
            l => l.Status != LotStatus.Scrapped
                && l.Status != LotStatus.Boxed
                && l.Status != LotStatus.Shipped
                && !(l.Status == LotStatus.Staged && l.StationCode == finished),
            cancellationToken);
        var tasks = await db.TransportTasks.CountAsync(
            t => t.Status == TransportTaskStatus.Pending
                || t.Status == TransportTaskStatus.Assigned
                || t.Status == TransportTaskStatus.Executing,
            cancellationToken);
        var batches = await db.BatchRuns.CountAsync(b => b.CompletedAt == null, cancellationToken);
        if (lots == 0 && tasks == 0 && batches == 0)
        {
            return null;
        }

        var message = $"工廠還有在製品：{lots} 框還在流程中、{tasks} 筆搬運進行中" + (batches > 0 ? $"、{batches} 爐還在加工" : string.Empty);
        return new LayoutBusyDto { Message = message, LotsInProgress = lots, ActiveTasks = tasks };
    }

    // 存檔前的結構檢查，在碰資料庫之前做：文件有空欄位、空項目或未定義的種類，驗證與套用都沒辦法處理，
    // 先寫入再驗證的話資料已經存進去才發現，所以直接回 400、不寫入。
    // 其他錯誤（重疊、缺工序…）照規格仍然可以存，沙盒常存半成品。回傳的驗證結果給存檔後的回應用，不必再驗一次。
    private static LayoutValidationDto RequireWellFormed(SaveLayoutRequest request)
    {
        var validation = LayoutValidator.Validate(request.Document);
        var broken = validation.Issues.Where(i => i.Code == LayoutValidator.DocumentInvalid).Select(i => i.Message).ToList();
        if (broken.Count > 0)
        {
            throw new RequestValidationException("document", string.Join("；", broken));
        }

        return validation;
    }

    private async Task<FactoryLayoutEntity> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.FactoryLayouts.SingleOrDefaultAsync(l => l.Id == id, cancellationToken)
        ?? throw new KeyNotFoundException("找不到這份佈局");

    private async Task<string> ValidNameAsync(string? raw, Guid? exceptId, CancellationToken cancellationToken)
    {
        var name = (raw ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new RequestValidationException("name", "佈局名稱不能是空白");
        }

        if (name.Length > MaxNameLength)
        {
            throw new RequestValidationException("name", $"佈局名稱最多 {MaxNameLength} 個字");
        }

        var excluded = exceptId ?? Guid.Empty;
        if (await db.FactoryLayouts.AnyAsync(l => l.Name == name && l.Id != excluded, cancellationToken))
        {
            throw new RequestValidationException("name", $"已經有叫「{name}」的佈局");
        }

        return name;
    }

    private static SaveLayoutResponse Response(FactoryLayoutEntity layout, LayoutValidationDto validation) => new()
    {
        Layout = ToDto(layout),
        Validation = validation,
    };

    private static LayoutDto ToDto(FactoryLayoutEntity layout) => new()
    {
        Id = layout.Id,
        Name = layout.Name,
        IsActive = layout.IsActive,
        IsDefault = layout.IsDefault,
        UpdatedAt = layout.UpdatedAt,
        AppliedAt = layout.AppliedAt,
        Document = layout.Document,
    };
}
