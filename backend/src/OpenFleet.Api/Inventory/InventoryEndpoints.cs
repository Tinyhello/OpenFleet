using Microsoft.AspNetCore.Mvc;
using OpenFleet.Api.Production;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Inventory;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        var inventory = app.MapGroup("/api/inventory").AddEndpointFilter<ApiErrorFilter>();

        // 每個品項的在庫、已保留、可用、安全庫存與已訂購量，以及低於安全庫存的提醒。
        inventory.MapGet("/", (InventoryService service, CancellationToken ct) => service.GetSummaryAsync(ct));

        inventory.MapGet("/alerts", async (InventoryService service, CancellationToken ct) => (await service.GetSummaryAsync(ct)).Alerts);

        inventory.MapGet("/movements", (string? itemCode, int? take, InventoryService service, CancellationToken ct) =>
            service.GetMovementsAsync(itemCode, take ?? 100, ct));

        // 盤點調整。
        inventory.MapPost("/adjustments", (StockAdjustmentRequest request, InventoryService service, CancellationToken ct) =>
            service.AdjustAsync(request, ct));

        var orders = app.MapGroup("/api/purchase-orders").AddEndpointFilter<ApiErrorFilter>();

        orders.MapGet("/", (string? status, InventoryService service, CancellationToken ct) =>
            service.GetPurchaseOrdersAsync(ParseStatus(status), ct));

        orders.MapGet("/{id:guid}", async (Guid id, InventoryService service, CancellationToken ct) =>
            await service.GetPurchaseOrderAsync(id, ct) is { } order ? Results.Ok(order) : Results.NotFound());

        // 由人建立採購單。重送同一個 Idempotency-Key 不會多一張，回 200 與原本那張單。
        orders.MapPost("/", async (
            CreatePurchaseOrderRequest request,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            InventoryService service,
            CancellationToken ct) =>
        {
            var (order, created) = await service.CreatePurchaseOrderAsync(request, idempotencyKey, ct);
            return created ? Results.Created($"/api/purchase-orders/{order.Id}", order) : Results.Ok(order);
        });

        // 收貨入庫；已經收過或取消的回 409。
        orders.MapPost("/{id:guid}/receive", (Guid id, InventoryService service, CancellationToken ct) =>
            service.ReceivePurchaseOrderAsync(id, ct));

        orders.MapPost("/{id:guid}/cancel", (Guid id, InventoryService service, CancellationToken ct) =>
            service.CancelPurchaseOrderAsync(id, ct));

        return app;
    }

    private static PurchaseOrderStatus? ParseStatus(string? status)
    {
        if (status is null)
        {
            return null;
        }

        if (!Enum.TryParse<PurchaseOrderStatus>(status, ignoreCase: true, out var value) || !Enum.IsDefined(value))
        {
            throw new RequestValidationException("status", $"Status must be one of {string.Join(", ", Enum.GetNames<PurchaseOrderStatus>()).ToLowerInvariant()}.");
        }

        return value;
    }
}
