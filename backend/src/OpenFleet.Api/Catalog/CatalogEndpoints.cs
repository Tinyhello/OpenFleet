using OpenFleet.Api.Production;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Catalog;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/catalog").AddEndpointFilter<ApiErrorFilter>();

        api.MapGet("/rod-sizes", (CatalogService catalog, CancellationToken ct) => catalog.GetRodSizesAsync(ct));

        // 沖頭規格與解析後的有效值（哪些是自己覆寫的、哪些沿用桿徑或製程設定）。
        api.MapGet("/specs", (CatalogService catalog, CancellationToken ct) => catalog.GetSpecsAsync(ct));

        api.MapGet("/specs/{code}", async (string code, CatalogService catalog, CancellationToken ct) =>
            await catalog.GetSpecAsync(code, ct) is { } spec ? Results.Ok(spec) : Results.NotFound());

        // 建立規格，連同 1 個整組、3 個零件、3 種備料與第 1 版 BOM。
        api.MapPost("/specs", async (CreatePunchSpecRequest request, CatalogService catalog, CancellationToken ct) =>
        {
            var spec = await catalog.CreateSpecAsync(request, ct);
            return Results.Created($"/api/catalog/specs/{spec.Code}", spec);
        });

        // kind 和 JSON 一樣用小寫（set、part、blank、bar），大小寫都接受。
        api.MapGet("/items", (string? kind, CatalogService catalog, CancellationToken ct) =>
        {
            ItemKind? parsed = null;
            if (kind is not null)
            {
                if (!Enum.TryParse<ItemKind>(kind, ignoreCase: true, out var value) || !Enum.IsDefined(value))
                {
                    throw new RequestValidationException("kind", $"Kind must be one of {string.Join(", ", Enum.GetNames<ItemKind>()).ToLowerInvariant()}.");
                }

                parsed = value;
            }

            return catalog.GetItemsAsync(parsed, ct);
        });

        api.MapGet("/items/{code}/bom", async (string code, int? version, CatalogService catalog, CancellationToken ct) =>
            await catalog.GetBomAsync(code, version, ct) is { } bom ? Results.Ok(bom) : Results.NotFound());

        api.MapGet("/items/{code}/explosion", (string code, int quantity, CatalogService catalog, CancellationToken ct) =>
            catalog.ExplodeAsync(code, quantity, ct));

        return app;
    }
}
