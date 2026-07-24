using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Rag.Api.Endpoints;
using Rag.Api.Errors;
using Rag.Api.Middleware;
using Rag.Api.OpenApi;
using Rag.Api.RateLimiting;
using Rag.Api.Security;
using Rag.Infrastructure.Ingestion;
using Rag.Infrastructure.Observability;
using Rag.Infrastructure.Persistence;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddRagObservability("Rag.Api");
builder.Services.AddRagPersistence(builder.Configuration);
builder.Services.AddRagIngestion(builder.Configuration, builder.Environment);
builder.Services.AddRagSecurity(builder.Configuration);
builder.Services.AddRagRateLimiting(builder.Configuration);
builder.Services.AddExceptionHandler<InvalidRequestExceptionHandler>();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["requestId"] = context.HttpContext.TraceIdentifier;
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<ApiKeyDocumentTransformer>();
    options.AddOperationTransformer<ApiKeyDocumentTransformer>();
});

WebApplication app = builder.Build();

app.UseMiddleware<RequestIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapOpenApi();
app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions
    {
        Predicate = static registration => registration.Tags.Contains("ready"),
    });
app.MapHealthChecks(
    "/health/dependencies",
    new HealthCheckOptions
    {
        Predicate = static registration => registration.Tags.Contains("dependencies"),
    });
app.MapKnowledgeBaseEndpoints();
app.MapIngestionEndpoints();

app.Run();

public partial class Program;
