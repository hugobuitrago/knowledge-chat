using Microsoft.Extensions.Primitives;

namespace Rag.Api.Middleware;

public sealed class RequestIdMiddleware(
    RequestDelegate next,
    ILogger<RequestIdMiddleware> logger)
{
    public const string HeaderName = "X-Request-ID";
    private const int MaximumRequestIdLength = 128;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string requestId = GetRequestId(context);
        context.TraceIdentifier = requestId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = requestId;
            return Task.CompletedTask;
        });

        using IDisposable? scope = logger.BeginScope(
            new Dictionary<string, object> { ["RequestId"] = requestId });
        await next(context).ConfigureAwait(false);
    }

    private static string GetRequestId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out StringValues values))
        {
            string? candidate = values.FirstOrDefault();
            if (IsValid(candidate))
            {
                return candidate!;
            }
        }

        return context.TraceIdentifier;
    }

    private static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumRequestIdLength &&
        value.All(static character => character is >= '!' and <= '~');
}

