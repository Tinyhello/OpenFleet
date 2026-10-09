using Microsoft.EntityFrameworkCore;
using OpenFleet.Api.Data;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Transport;

public static class TransportEndpoints
{
    public static IEndpointRouteBuilder MapTransportEndpoints(this IEndpointRouteBuilder app)
    {
        var tasks = app.MapGroup("/api/transport-tasks");

        tasks.MapGet("/", async (OpenFleetDbContext db, CancellationToken ct) =>
            await QueryTasks(db).OrderByDescending(t => t.CreatedAt).Take(100).ToListAsync(ct));

        tasks.MapGet("/{id:guid}", async (Guid id, OpenFleetDbContext db, CancellationToken ct) =>
            await QueryTasks(db).SingleOrDefaultAsync(t => t.Id == id, ct) is { } task ? Results.Ok(task) : Results.NotFound());

        // 任務的狀態歷史：每次轉移的時間、車輛與原因。
        tasks.MapGet("/{id:guid}/events", async (Guid id, OpenFleetDbContext db, CancellationToken ct) =>
            await db.TaskEvents.AsNoTracking().Where(e => e.TaskId == id).OrderBy(e => e.Id)
                .Select(e => new TaskEventDto
                {
                    FromStatus = e.FromStatus,
                    ToStatus = e.ToStatus,
                    VehicleId = e.VehicleId,
                    Reason = e.Reason,
                    OccurredAt = e.OccurredAt,
                })
                .ToListAsync(ct));

        return app;
    }

    /// <summary>Entity → DTO 的投影交給資料庫做（只查需要的欄位），不把整個物件圖載入記憶體。</summary>
    public static IQueryable<TransportTaskDto> QueryTasks(OpenFleetDbContext db) =>
        db.TransportTasks.AsNoTracking().Select(t => new TransportTaskDto
        {
            Id = t.Id,
            LotId = t.LotId,
            LotNumber = t.Lot.LotNumber,
            FromStationCode = t.FromStationCode,
            ToStationCode = t.ToStationCode,
            Quantity = t.Quantity,
            Kind = t.Kind,
            Status = t.Status,
            VehicleId = t.Assignments.OrderByDescending(a => a.Attempt).Select(a => a.VehicleId).FirstOrDefault(),
            AssignmentStatus = t.Assignments.OrderByDescending(a => a.Attempt).Select(a => (AssignmentStatus?)a.Status).FirstOrDefault(),
            Attempts = t.Assignments.Count,
            FailureReason = t.FailureReason,
            CreatedAt = t.CreatedAt,
            CompletedAt = t.CompletedAt,
        });
}
