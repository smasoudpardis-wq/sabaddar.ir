using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RetailCloud.BuildingBlocks.Application;

namespace RetailCloud.BuildingBlocks.Api;

/// <summary>Result of an idempotent command handler: a success to store and return, or a problem that rolls back.</summary>
public sealed class CommandResult
{
    private CommandResult(int status, object? value, IResult? problem)
    {
        Status = status;
        Value = value;
        Problem = problem;
    }

    public int Status { get; }

    public object? Value { get; }

    public IResult? Problem { get; }

    public static CommandResult Ok(object value) => new(StatusCodes.Status200OK, value, null);

    public static CommandResult Created(object value) => new(StatusCodes.Status201Created, value, null);

    /// <summary>Business error: the transaction rolls back and nothing is stored, so the same key may be retried (D-34).</summary>
    public static CommandResult Fail(IResult problem) => new(0, null, problem);
}

/// <summary>
/// US-006 / BR-030 / BR-031. <c>MapIdempotentPost</c> runs the handler in one unit of work whose first statement claims
/// the Idempotency-Key (lock level L1, D03 §6.2). Replays return the stored body and status byte for byte, with
/// <c>Idempotent-Replayed: true</c>.
/// </summary>
public static partial class Idempotency
{
    public const string Header = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";
    private const int MaxBodyBytes = 1 << 20;

    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")]
    private static partial Regex KeyFormat();

    public static RouteHandlerBuilder MapIdempotentPost<TBody>(this IEndpointRouteBuilder routes, string pattern, string operation,
        Func<TBody, IUnitOfWorkScope, HttpContext, CancellationToken, Task<CommandResult>> handler)
        where TBody : class
    {
        ArgumentNullException.ThrowIfNull(routes);
        return routes.MapPost(pattern, (Delegate)((HttpContext http) => ExecuteAsync(http, operation, handler)))
            .AddEndpointFilter<RequireActiveTenantFilter>()
            .Accepts<TBody>("application/json");
    }

    private static async Task<IResult> ExecuteAsync<TBody>(HttpContext http, string operation,
        Func<TBody, IUnitOfWorkScope, HttpContext, CancellationToken, Task<CommandResult>> handler)
        where TBody : class
    {
        string? key = http.Request.Headers[Header];
        if (key is null || !KeyFormat().IsMatch(key))
        {
            return Problems.Create(http, StatusCodes.Status400BadRequest, "idempotency-key-required",
                "Idempotency-Key header (16–64 of A–Z a–z 0–9 _ -) is required for this operation");
        }

        byte[] body = await ReadBodyAsync(http);
        if (body.Length > MaxBodyBytes)
        {
            return Problems.Create(http, StatusCodes.Status413PayloadTooLarge, "validation", "Request body too large");
        }

        JsonSerializerOptions json = http.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        TBody? command;
        try
        {
            command = JsonSerializer.Deserialize<TBody>(body, json);
        }
        catch (JsonException)
        {
            command = null;
        }

        if (command is null)
        {
            return Problems.Create(http, StatusCodes.Status400BadRequest, "validation", "Request body is not valid JSON for this operation");
        }

        // The hash binds the key to operation + path (route values such as saleId) + exact body (BR-031).
        byte[] hash = SHA256.HashData([.. Encoding.UTF8.GetBytes($"{operation}\n{http.Request.Path}\n"), .. body]);
        RequestContext ctx = http.RequestServices.GetRequiredService<RequestContext>();
        IUnitOfWork uow = http.RequestServices.GetRequiredService<IUnitOfWork>();
        IIdempotencyStore store = http.RequestServices.GetRequiredService<IIdempotencyStore>();

        try
        {
            return await uow.ExecuteAsync(ctx, async (scope, ct) =>
            {
                switch (await store.ClaimAsync(scope, key, operation, hash, ct))
                {
                    case IdempotencyClaim.Replay replay:
                        http.Response.Headers[ReplayedHeader] = "true";
                        return Results.Text(replay.ResponseJson, "application/json", Encoding.UTF8, replay.Status);
                    case IdempotencyClaim.Reused:
                        return Problems.Create(http, StatusCodes.Status409Conflict, "idempotency-key-reused",
                            "This Idempotency-Key was used with a different request");
                    case IdempotencyClaim.Busy:
                        http.Response.Headers.RetryAfter = "1";
                        return Problems.Create(http, StatusCodes.Status409Conflict, "in-progress",
                            "A request with this Idempotency-Key is still being processed");
                }

                CommandResult result = await handler(command, scope, http, ct);
                if (result.Problem is not null)
                {
                    throw new CommandFailedException(result.Problem);
                }

                string responseJson = JsonSerializer.Serialize(result.Value, json);
                await store.CompleteAsync(scope, key, result.Status, responseJson, ct);
                return Results.Text(responseJson, "application/json", Encoding.UTF8, result.Status);
            }, http.RequestAborted);
        }
        catch (CommandFailedException failed)
        {
            return failed.Problem; // rolled back: no record, the key stays usable (D-34)
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContext http)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBodyBytes)
            {
                break;
            }
        }

        return buffer.ToArray();
    }

    private sealed class CommandFailedException(IResult problem) : Exception("Command failed; rolling back.")
    {
        public IResult Problem { get; } = problem;
    }
}

/// <summary>
/// SEC-T-016 / TM-018: every error leaves as problem+json with a stable <c>code</c> and <c>traceId</c>; exception
/// messages and stack traces stay in the logs, in every environment.
/// </summary>
internal static class ProblemDefaults
{
    private static readonly Dictionary<int, string> Codes = new()
    {
        [StatusCodes.Status400BadRequest] = "validation",
        [StatusCodes.Status401Unauthorized] = "invalid-token",
        [StatusCodes.Status403Forbidden] = "forbidden",
        [StatusCodes.Status404NotFound] = "not-found",
        [StatusCodes.Status405MethodNotAllowed] = "method-not-allowed",
        [StatusCodes.Status413PayloadTooLarge] = "validation",
        [StatusCodes.Status415UnsupportedMediaType] = "validation",
        [StatusCodes.Status500InternalServerError] = "internal",
    };

    public static void Customize(ProblemDetailsContext c)
    {
        int status = c.ProblemDetails.Status ?? c.HttpContext.Response.StatusCode;
        if (!c.ProblemDetails.Extensions.ContainsKey("code"))
        {
            string code = Codes.GetValueOrDefault(status, status >= 500 ? "internal" : "validation");
            c.ProblemDetails.Extensions["code"] = code;
            c.ProblemDetails.Type = $"https://errors.retailcloud.example/{code}";
        }

        c.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? c.HttpContext.TraceIdentifier;
        if (status >= 500)
        {
            c.ProblemDetails.Title = "Unexpected error";
            c.ProblemDetails.Detail = null;
            c.ProblemDetails.Extensions.Remove("exception");
            c.ProblemDetails.Extensions.Remove("errors");
        }
    }
}
