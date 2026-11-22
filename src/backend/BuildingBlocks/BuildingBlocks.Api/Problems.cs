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

    public static async Task WriteAsync(HttpContext http, IResult result) => await result.ExecuteAsync(http);
}
