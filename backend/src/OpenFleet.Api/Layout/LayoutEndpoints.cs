using OpenFleet.Contracts;
using OpenFleet.Layout;

namespace OpenFleet.Api.Layout;

/// <summary>佈局的 API（P7 第 2 步 5.11）。驗證錯誤與找不到沿用 <see cref="ApiErrorFilter"/> 的格式。</summary>
public static class LayoutEndpoints
{
    public static WebApplication MapLayoutEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/layouts").AddEndpointFilter<ApiErrorFilter>();

        api.MapGet("/", async (LayoutService layouts, CancellationToken ct) => Results.Ok(await layouts.ListAsync(ct)));

        api.MapGet("/active", async (LayoutService layouts, CancellationToken ct) =>
            await layouts.GetActiveAsync(ct) is { } layout ? Results.Ok(layout) : Results.NotFound());

        api.MapGet("/{id:guid}", async (Guid id, LayoutService layouts, CancellationToken ct) =>
            await layouts.GetAsync(id, ct) is { } layout ? Results.Ok(layout) : Results.NotFound());

        api.MapPost("/", async (SaveLayoutRequest request, LayoutService layouts, CancellationToken ct) =>
        {
            var response = await layouts.CreateAsync(request, ct);
            return Results.Created($"/api/layouts/{response.Layout.Id}", response);
        });

        api.MapPut("/{id:guid}", async (Guid id, SaveLayoutRequest request, LayoutService layouts, CancellationToken ct) =>
            Results.Ok(await layouts.UpdateAsync(id, request, ct)));

        api.MapDelete("/{id:guid}", async (Guid id, LayoutService layouts, CancellationToken ct) =>
        {
            await layouts.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        // 只驗證、不存檔：編輯時用來即時顯示問題（和 Unity 的檢查結果相同）。
        // 文件可以是 null 或缺欄位：照樣回 200，問題清單裡有 document-invalid。
        api.MapPost("/validate", (FactoryLayoutDocument? document) => Results.Ok(LayoutValidator.Validate(document)));

        // resetDemo 沒有帶時是 false（查詢參數可以省略）。
        api.MapPost("/{id:guid}/apply", async (Guid id, bool? resetDemo, LayoutService layouts, CancellationToken ct) =>
        {
            var result = await layouts.ApplyAsync(id, resetDemo ?? false, ct);
            return result.Status switch
            {
                ApplyStatus.Applied => Results.Ok(result.Layout),
                ApplyStatus.NotFound => Results.NotFound(),
                ApplyStatus.Invalid => Results.BadRequest(result.Validation),
                _ => Results.Conflict(result.Busy),
            };
        });

        return app;
    }
}
