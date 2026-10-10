using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Production;

public static class ProductionEndpoints
{
    public static IEndpointRouteBuilder MapProductionEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter<ValidationFilter>();

        api.MapGet("/stations", async (OpenFleetDbContext db, CancellationToken ct) =>
            await db.Stations.AsNoTracking().OrderBy(s => s.Code).Select(s => new StationDto
            {
                Code = s.Code,
                Name = s.Name,
                HandoffX = s.HandoffX,
                HandoffZ = s.HandoffZ,
                InboundCapacity = s.InboundCapacity,
            }).ToListAsync(ct));

        // 備料區、成品區、加工與刀具的即時狀態；顯示端每秒輪詢數次。
        api.MapGet("/stations/status", (ProductionService production, CancellationToken ct) => production.GetStationStatusAsync(ct));

        api.MapGet("/settings", (ProductionService production, CancellationToken ct) => production.GetSettingsAsync(ct));

        api.MapPut("/settings", (ProductionSettingsDto request, ProductionService production, CancellationToken ct) =>
            production.UpdateSettingsAsync(request, ct));

        api.MapGet("/lots", async (OpenFleetDbContext db, CancellationToken ct) =>
            (await db.Lots.AsNoTracking().OrderByDescending(l => l.CreatedAt).ThenBy(l => l.BinIndex).Take(200).ToListAsync(ct)).Select(ToDto));

        // 追蹤一個批次：目前位置、每次位置異動、每道工序的加工紀錄、每一趟搬運。
        api.MapGet("/lots/{id:guid}", async (Guid id, OpenFleetDbContext db, CancellationToken ct) =>
        {
            var lot = await db.Lots.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, ct);
            if (lot is null)
            {
                return Results.NotFound();
            }

            var trace = new LotTraceDto
            {
                Lot = ToDto(lot),
                Movements = await db.InventoryMovements.AsNoTracking().Where(m => m.LotId == id).OrderBy(m => m.Id)
                    .Select(m => new InventoryMovementDto { From = m.From, To = m.To, Quantity = m.Quantity, Reason = m.Reason, OccurredAt = m.OccurredAt })
                    .ToListAsync(ct),
                Operations = await db.Operations.AsNoTracking().Where(o => o.LotId == id).OrderBy(o => o.QueuedAt)
                    .Select(o => new OperationDto
                    {
                        StepSequence = o.StepSequence,
                        StationCode = o.StationCode,
                        Status = o.Status,
                        Quantity = o.Quantity,
                        PiecesCompleted = o.PiecesCompleted,
                        SecondsPerPiece = o.SecondsPerPiece,
                        QueuedAt = o.QueuedAt,
                        StartedAt = o.StartedAt,
                        CompletedAt = o.CompletedAt,
                    })
                    .ToListAsync(ct),
                Tasks = await Transport.TransportEndpoints.QueryTasks(db).Where(t => t.LotId == id).OrderBy(t => t.CreatedAt).ToListAsync(ct),
            };
            return Results.Ok(trace);
        });

        // 缺車統計：各工序機台因缺車停下來的時間、任務等車的累計；車輛全部離線的重大事故另外列出。
        api.MapGet("/shortage", (Transport.VehicleShortageService shortage, Fleet.FleetStateStore fleet, CancellationToken ct) =>
            shortage.GetSummaryAsync(fleet.Snapshot(), ct));

        api.MapGet("/shortage/waits", async (OpenFleetDbContext db, CancellationToken ct) =>
            await (from wait in db.VehicleWaits.AsNoTracking()
                   join task in db.TransportTasks.AsNoTracking() on wait.TaskId equals task.Id
                   orderby wait.Id descending
                   select new VehicleWaitDto
                   {
                       TaskId = task.Id,
                       Reason = wait.Reason,
                       LotNumber = task.Lot!.LotNumber,
                       Kind = task.Kind,
                       FromStationCode = task.FromStationCode,
                       ToStationCode = task.ToStationCode,
                       StartedAt = wait.StartedAt,
                       EndedAt = wait.EndedAt,
                       SimulatedSeconds = wait.SimulatedSeconds,
                   }).Take(100).ToListAsync(ct));

        api.MapGet("/shortage/delays", async (OpenFleetDbContext db, CancellationToken ct) =>
            await db.MachineDelays.AsNoTracking().OrderByDescending(d => d.Id).Take(100).Select(d => new MachineDelayDto
            {
                StationCode = d.StationCode,
                StepSequence = d.StepSequence,
                Cause = d.Cause,
                Reason = d.Reason,
                TaskId = d.TaskId,
                StartedAt = d.StartedAt,
                EndedAt = d.EndedAt,
                SimulatedSeconds = d.SimulatedSeconds,
            }).ToListAsync(ct));

        // 建立一個批次；回應是它分成的每一框。
        api.MapPost("/lots", async (CreateLotRequest? request, ProductionService production, CancellationToken ct) =>
        {
            var bins = await production.CreateBatchAsync(request?.Quantity, request?.RodDiameterMm, ct);
            return Results.Created("/api/lots", bins.Select(ToDto).ToList());
        });

        // 示範用：清掉所有批次與搬運資料（含刀具配送）並把刀具計數歸零（設定、站點、車輛保留）。車上若還有進行中的任務，之後的回報會因找不到派工而被忽略。
        api.MapPost("/demo/reset", async (OpenFleetDbContext db, ILoggerFactory loggers, CancellationToken ct) =>
        {
            await db.Database.ExecuteSqlRawAsync("""TRUNCATE "Lots", "TransportTasks", "OutboxMessages", "InboxMessages" CASCADE""", ct);
            // 刀具計數與換刀紀錄也歸零，機台回到剛換好刀的狀態。
            await db.Database.ExecuteSqlRawAsync(
                """TRUNCATE "ToolChanges", "BatchRuns"; UPDATE "StationTools" SET "PiecesSinceChange" = 0, "ChangeStartedAt" = NULL, "ChangeDueAt" = NULL, "SpareRequestedAt" = NULL, "SpareDeliveredAt" = NULL, "WaitingSince" = NULL""", ct);
            loggers.CreateLogger(nameof(ProductionEndpoints)).LogWarning("Demo data reset: lots, tasks, movements, outbox and inbox cleared.");
            return Results.NoContent();
        });

        return app;
    }

    private static LotDto ToDto(MaterialLot lot) => new()
    {
        Id = lot.Id,
        LotNumber = lot.LotNumber,
        BatchNumber = lot.BatchNumber,
        BinIndex = lot.BinIndex,
        BinCount = lot.BinCount,
        RodDiameterMm = lot.RodDiameterMm,
        PartCode = lot.PartCode,
        Quantity = lot.Quantity,
        Status = lot.Status,
        StationCode = lot.StationCode,
        VehicleId = lot.VehicleId,
        CurrentStepSequence = lot.CurrentStepSequence,
        CreatedAt = lot.CreatedAt,
        UpdatedAt = lot.UpdatedAt,
    };

    /// <summary>把業務規則的驗證錯誤轉成 HTTP 400，端點本身不需要各自 try/catch。</summary>
    private sealed class ValidationFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            try
            {
                return await next(context);
            }
            catch (RequestValidationException ex)
            {
                return Results.ValidationProblem(ex.Errors);
            }
        }
    }
}
