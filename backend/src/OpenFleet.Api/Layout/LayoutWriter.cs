using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Layout;

/// <summary>
/// 把推導結果寫進資料表：有的更新、沒有的新增、佈局裡已經不存在的刪除（P7 第 2 步 5.9）。
/// 不清空生產資料，那是 <see cref="DemoData.ClearProductionAsync"/> 的事。刪除站點與機台前，參照它們的生產資料必須已經清掉。
/// </summary>
public static class LayoutWriter
{
    /// <summary>
    /// 確保有一份預設佈局：文件每次都用 <paramref name="defaultDocument"/> 覆寫（預設佈局的內容由程式決定）。
    /// 沒有使用中的佈局時，讓它使用中並推導出資料表（第一次啟動，或舊資料庫第一次升級）。
    /// </summary>
    public static async Task EnsureDefaultAsync(OpenFleetDbContext db, FactoryLayoutDocument defaultDocument)
    {
        var now = DateTimeOffset.UtcNow;
        var layout = await db.FactoryLayouts.SingleOrDefaultAsync(l => l.IsDefault);
        if (layout is null)
        {
            layout = new FactoryLayoutEntity
            {
                Id = Guid.NewGuid(),
                Name = LayoutService.DefaultName,
                IsDefault = true,
                CreatedAt = now,
                UpdatedAt = now,
                Document = defaultDocument,
            };
            db.FactoryLayouts.Add(layout);
        }
        else
        {
            layout.Document = defaultDocument;
        }

        await db.SaveChangesAsync();
        if (await db.FactoryLayouts.AnyAsync(l => l.IsActive))
        {
            return;
        }

        await WriteAsync(db, defaultDocument, CancellationToken.None);
        layout.IsActive = true;
        layout.AppliedAt = now;
        layout.AppliedHash = LayoutJson.HashOf(defaultDocument);
        await db.SaveChangesAsync();
    }

    public static async Task WriteAsync(OpenFleetDbContext db, FactoryLayoutDocument document, CancellationToken cancellationToken)
    {
        var steps = await db.ProcessSteps.AsNoTracking().ToListAsync(cancellationToken);
        var derived = LayoutDeriver.Derive(document, steps);

        await WriteStationsAsync(db, derived, cancellationToken);
        await WriteMachinesAsync(db, derived, cancellationToken);
        await WriteRoutesAsync(db, derived, cancellationToken);
        await WriteVehiclesAsync(db, derived, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task WriteStationsAsync(OpenFleetDbContext db, DerivedLayout derived, CancellationToken cancellationToken)
    {
        var rows = await db.Stations.ToDictionaryAsync(s => s.Code, StringComparer.Ordinal, cancellationToken);
        foreach (var target in derived.Stations)
        {
            if (!rows.TryGetValue(target.Code, out var row))
            {
                db.Stations.Add(target);
                continue;
            }

            row.Name = target.Name;
            row.Kind = target.Kind;
            row.StepSequence = target.StepSequence;
            row.X = target.X;
            row.Z = target.Z;
            row.Rotation = target.Rotation;
            row.HandoffX = target.HandoffX;
            row.HandoffZ = target.HandoffZ;
            row.InboundCapacity = target.InboundCapacity;
            row.OutboundCapacity = target.OutboundCapacity;
        }

        var kept = derived.Stations.Select(s => s.Code).ToHashSet(StringComparer.Ordinal);
        db.Stations.RemoveRange(rows.Values.Where(s => !kept.Contains(s.Code)));
    }

    private static async Task WriteMachinesAsync(OpenFleetDbContext db, DerivedLayout derived, CancellationToken cancellationToken)
    {
        var rows = await db.Machines.ToDictionaryAsync(m => m.Code, StringComparer.Ordinal, cancellationToken);
        foreach (var target in derived.Machines)
        {
            if (!rows.TryGetValue(target.Code, out var row))
            {
                db.Machines.Add(target);
                continue;
            }

            row.StationCode = target.StationCode;
            row.Number = target.Number;
            row.Name = target.Name;
        }

        var kept = derived.Machines.Select(m => m.Code).ToHashSet(StringComparer.Ordinal);
        db.Machines.RemoveRange(rows.Values.Where(m => !kept.Contains(m.Code)));

        // 刀具狀態：有刀具的工序每台機台一筆；已經不需要刀具的機台，刀具紀錄一併刪掉。
        var tools = await db.MachineTools.ToDictionaryAsync(t => t.MachineCode, StringComparer.Ordinal, cancellationToken);
        foreach (var code in derived.ToolMachineCodes.Where(code => !tools.ContainsKey(code)))
        {
            db.MachineTools.Add(new MachineTool { MachineCode = code });
        }

        var toolKept = derived.ToolMachineCodes.ToHashSet(StringComparer.Ordinal);
        db.MachineTools.RemoveRange(tools.Values.Where(t => !toolKept.Contains(t.MachineCode)));
    }

    private static async Task WriteRoutesAsync(OpenFleetDbContext db, DerivedLayout derived, CancellationToken cancellationToken)
    {
        // 路網整份換掉：新舊節點代碼可能相同。ExecuteDelete 不經過 change tracker，所以先把同一個 DbContext
        // 裡已經追蹤的舊路網列分離，否則新增同代碼的節點會衝突（啟動時與測試都會在同一個 DbContext 裡寫兩次）。
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is RouteNode or RouteEdge).ToList())
        {
            entry.State = EntityState.Detached;
        }

        await db.RouteEdges.ExecuteDeleteAsync(cancellationToken);
        await db.RouteNodes.ExecuteDeleteAsync(cancellationToken);
        db.RouteNodes.AddRange(derived.RouteNodes);
        db.RouteEdges.AddRange(derived.RouteEdges);
    }

    private static async Task WriteVehiclesAsync(OpenFleetDbContext db, DerivedLayout derived, CancellationToken cancellationToken)
    {
        // 車輛不刪：歷史資料（派工、等待紀錄）可能參照它們。不在佈局裡的車只是退出派工。
        var rows = await db.Vehicles.ToDictionaryAsync(v => v.Id, StringComparer.Ordinal, cancellationToken);
        foreach (var target in derived.Vehicles)
        {
            if (!rows.TryGetValue(target.Id, out var row))
            {
                db.Vehicles.Add(target);
                continue;
            }

            row.Type = target.Type;
            row.DispatchEnabled = true;
            row.InLayout = true;
            row.HomeX = target.HomeX;
            row.HomeZ = target.HomeZ;
        }

        var inLayout = derived.Vehicles.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows.Values.Where(v => !inLayout.Contains(v.Id)))
        {
            row.InLayout = false;
            row.DispatchEnabled = false;
        }
    }
}
