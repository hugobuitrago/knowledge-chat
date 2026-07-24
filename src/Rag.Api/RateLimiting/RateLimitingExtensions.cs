using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Rag.Application.Security;

namespace Rag.Api.RateLimiting;

internal static class RateLimitingExtensions
{
    public static IServiceCollection AddRagRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<TenantRateLimitOptions>()
            .Bind(configuration.GetSection(TenantRateLimitOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddRateLimiter();
        services
            .AddOptions<RateLimiterOptions>()
            .Configure<IOptions<TenantRateLimitOptions>>(
                static (frameworkOptions, configuredOptions) =>
                    ConfigureFrameworkOptions(frameworkOptions, configuredOptions.Value));

        return services;
    }

    private static void ConfigureFrameworkOptions(
        RateLimiterOptions frameworkOptions,
        TenantRateLimitOptions configuredOptions)
    {
        frameworkOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        frameworkOptions.GlobalLimiter = PartitionedRateLimiter.CreateChained(
            CreateTenantLimiter(configuredOptions),
            CreateChatbotLimiter(configuredOptions));
        frameworkOptions.OnRejected = WriteRejectionAsync;
    }

    private static PartitionedRateLimiter<HttpContext> CreateTenantLimiter(
        TenantRateLimitOptions options) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (!ShouldLimit(context) ||
                context.User.FindFirst(RagClaimTypes.TenantId)?.Value is not string tenantId)
            {
                return RateLimitPartition.GetNoLimiter("tenant:not-applicable");
            }

            return RateLimitPartition.GetFixedWindowLimiter(
                $"tenant:{tenantId}",
                _ => CreateLimiterOptions(
                    options.TenantPermitLimit,
                    options.WindowSeconds));
        });

    private static PartitionedRateLimiter<HttpContext> CreateChatbotLimiter(
        TenantRateLimitOptions options) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            if (!ShouldLimit(context) ||
                context.User.FindFirst(RagClaimTypes.TenantId)?.Value is not string tenantId ||
                context.User.FindFirst(RagClaimTypes.ChatbotId)?.Value is not string chatbotId)
            {
                return RateLimitPartition.GetNoLimiter("chatbot:not-applicable");
            }

            return RateLimitPartition.GetFixedWindowLimiter(
                $"chatbot:{tenantId}:{chatbotId}",
                _ => CreateLimiterOptions(
                    options.ChatbotPermitLimit,
                    options.WindowSeconds));
        });

    private static FixedWindowRateLimiterOptions CreateLimiterOptions(
        int permitLimit,
        int windowSeconds) =>
        new()
        {
            AutoReplenishment = true,
            PermitLimit = permitLimit,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            Window = TimeSpan.FromSeconds(windowSeconds),
        };

    private static bool ShouldLimit(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<TenantRateLimitedMetadata>() is not null;

    private static async ValueTask WriteRejectionAsync(
        OnRejectedContext context,
        CancellationToken cancellationToken)
    {
        HttpResponse response = context.HttpContext.Response;
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(
                MetadataName.RetryAfter,
                out TimeSpan retryAfter))
        {
            response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds)
                .ToString(CultureInfo.InvariantCulture);
        }

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too Many Requests",
            Detail = "The request rate limit for this tenant or chatbot was exceeded.",
            Type = "https://www.rfc-editor.org/rfc/rfc6585#section-4",
        };
        problem.Extensions["requestId"] = context.HttpContext.TraceIdentifier;

        IProblemDetailsService problemDetailsService = context.HttpContext.RequestServices
            .GetRequiredService<IProblemDetailsService>();
        bool written = await problemDetailsService.TryWriteAsync(
            new ProblemDetailsContext
            {
                HttpContext = context.HttpContext,
                ProblemDetails = problem,
            }).ConfigureAwait(false);
        if (!written)
        {
            await response.WriteAsJsonAsync(problem, cancellationToken).ConfigureAwait(false);
        }
    }
}
