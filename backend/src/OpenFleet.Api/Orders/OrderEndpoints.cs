using Microsoft.AspNetCore.Mvc;
using OpenFleet.Contracts;

namespace OpenFleet.Api.Orders;

public static class OrderEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/api/sales-orders").AddEndpointFilter<ApiErrorFilter>();

        // 建立客戶訂單：每一行明細開一張主工單並建立它的框。重送同一個 Idempotency-Key 回 200 與原本那張訂單。
        orders.MapPost("/", async (
            CreateSalesOrderRequest request,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
            OrderService service,
            CancellationToken ct) =>
        {
            var (order, created) = await service.CreateAsync(request, idempotencyKey, ct);
            return created ? Results.Created($"/api/sales-orders/{order.Id}", order) : Results.Ok(order);
        });

        orders.MapGet("/", (int? take, OrderService service, CancellationToken ct) => service.ListAsync(take ?? 50, ct));

        // 一張訂單：每一行的進度（主工單與補做工單合計）、工單與報廢註記。
        orders.MapGet("/{id:guid}", async (Guid id, OrderService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } order ? Results.Ok(order) : Results.NotFound());

        // 負責人同意分批出貨：一張訂單只能同意一次，只出已經包好、湊成完整的組。
        orders.MapPost("/{id:guid}/partial-shipment", (Guid id, PartialShipmentRequest request, OrderService service, CancellationToken ct) =>
            service.ApprovePartialShipmentAsync(id, request, ct));

        // 負責人把訂單設成急單（排在所有訂單前面）；DELETE 取消。
        orders.MapPost("/{id:guid}/rush", (Guid id, RushRequest request, OrderService service, CancellationToken ct) =>
            service.MarkRushAsync(id, request, ct));
        orders.MapDelete("/{id:guid}/rush", (Guid id, OrderService service, CancellationToken ct) => service.CancelRushAsync(id, ct));

        var workOrders = app.MapGroup("/api/production-orders").AddEndpointFilter<ApiErrorFilter>();

        workOrders.MapGet("/", (int? take, OrderService service, CancellationToken ct) => service.ListProductionOrdersAsync(take ?? 50, ct));

        // 一張工單：開立時的規格、進度、報廢註記與它的框。
        workOrders.MapGet("/{id:guid}", async (Guid id, OrderService service, CancellationToken ct) =>
            await service.GetProductionOrderAsync(id, ct) is { } order ? Results.Ok(order) : Results.NotFound());

        return app;
    }
}
