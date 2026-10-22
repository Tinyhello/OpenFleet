using OpenFleet.Api.Production;

namespace OpenFleet.Api;

/// <summary>和現在的狀態衝突（代碼重複、單據已經收貨等）；端點轉成 HTTP 409。</summary>
public sealed class ConflictException(string message) : Exception(message);

/// <summary>把業務規則的錯誤轉成 HTTP 狀態：驗證錯誤 → 400、找不到 → 404、狀態衝突 → 409。</summary>
public sealed class ApiErrorFilter : IEndpointFilter
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
        catch (ConflictException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
    }
}
