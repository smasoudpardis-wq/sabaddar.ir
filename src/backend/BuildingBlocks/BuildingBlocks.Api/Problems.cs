using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace RetailCloud.BuildingBlocks.Api;

/// <summary>RFC 9457 responses with the stable codes of D07 x-error-catalog.</summary>
public static class Problems
{
    public static IResult Create(HttpContext http, int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, type: $"https://errors.retailcloud.example/{code}",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["traceId"] = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
            });

    /// <summary>404 for "does not exist" and "belongs to another tenant" alike (BR-003, D-48).</summary>
    public static IResult NotFound(HttpContext http) => Create(http, StatusCodes.Status404NotFound, "not-found", "Not found");

    /// <summary>400 validation with per-field error codes (context.errors).</summary>
    public static IResult Validation(HttpContext http, IDictionary<string, string[]> errors) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Validation failed",
            type: "https://errors.retailcloud.example/validation",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "validation",
                ["errors"] = errors,
                ["traceId"] = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
            });

    /// <summary>403 approval-required (IAM-007): the client shows the request to a manager and retries with approvalId.</summary>
    public static IResult ApprovalRequired(HttpContext http, Guid approvalRequestId, string condition, string? value, string? limit) =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "A manager's approval is required",
            type: "https://errors.retailcloud.example/approval-required",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "approval-required",
                ["approvalRequestId"] = approvalRequestId,
                ["context"] = new { condition, value, limit },
                ["traceId"] = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
            });

    public static async Task WriteAsync(HttpContext http, IResult result) => await result.ExecuteAsync(http);

    /// <summary>403 entitlement-exceeded (TEN-003) with the resource and the plan's limit, for the "upgrade" prompt (D09).</summary>
    public static IResult EntitlementExceeded(HttpContext http, string resource, int? limit) =>
        Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Plan limit reached",
            type: "https://errors.retailcloud.example/entitlement-exceeded",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "entitlement-exceeded",
                ["resource"] = resource,
                ["limit"] = limit,
                ["traceId"] = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
            });
}

/// <summary>Turns <see cref="RetailCloud.BuildingBlocks.Application.EntitlementExceededException"/> from any endpoint into 403.</summary>
public sealed class EntitlementExceptionHandler : Microsoft.AspNetCore.Diagnostics.IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not RetailCloud.BuildingBlocks.Application.EntitlementExceededException e)
        {
            return false;
        }

        await Problems.WriteAsync(httpContext, Problems.EntitlementExceeded(httpContext, e.Resource, e.Limit));
        return true;
    }
}
