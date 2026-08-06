using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Rag.IntegrationTests.PostgreSql;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class FoundationEndpointsTests(PostgreSqlFixture database)
{
    [Fact]
    public async Task Liveness_does_not_depend_on_readiness_checks()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(InvalidConnectionString);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/health/live",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_returns_service_unavailable_when_critical_dependency_is_not_ready()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(InvalidConnectionString);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/health/ready",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_and_dependencies_are_healthy_with_postgresql()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(database.ConnectionString);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage readinessResponse = await client.GetAsync(
            "/health/ready",
            CancellationToken.None);
        using HttpResponseMessage dependenciesResponse = await client.GetAsync(
            "/health/dependencies",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, readinessResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, dependenciesResponse.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_is_available()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(database.ConnectionString);
        using HttpClient client = factory.CreateClient();

        JsonElement document = await client.GetFromJsonAsync<JsonElement>(
            "/openapi/v1.json",
            CancellationToken.None);

        Assert.StartsWith("3.", document.GetProperty("openapi").GetString());
        Assert.Equal("Rag.Api | v1", document.GetProperty("info").GetProperty("title").GetString());
        Assert.Equal(
            "apiKey",
            document
                .GetProperty("components")
                .GetProperty("securitySchemes")
                .GetProperty("ApiKey")
                .GetProperty("type")
                .GetString());
        Assert.Equal(
            "ApiKey",
            document
                .GetProperty("paths")
                .GetProperty("/v1/knowledge-bases/{knowledgeBaseId}")
                .GetProperty("get")
                .GetProperty("security")[0]
                .EnumerateObject()
                .Single()
                .Name);
        JsonElement uploadOperation = document
            .GetProperty("paths")
            .GetProperty("/v1/knowledge-bases/{knowledgeBaseId}/documents")
            .GetProperty("post");
        Assert.True(uploadOperation.GetProperty("responses").TryGetProperty("202", out _));
        Assert.True(
            uploadOperation
                .GetProperty("requestBody")
                .GetProperty("content")
                .TryGetProperty("multipart/form-data", out _));
        JsonElement retrievalOperation = document
            .GetProperty("paths")
            .GetProperty("/v1/retrieve")
            .GetProperty("post");
        Assert.True(retrievalOperation.GetProperty("responses").TryGetProperty("200", out _));
        Assert.Equal(
            "ApiKey",
            retrievalOperation
                .GetProperty("security")[0]
                .EnumerateObject()
                .Single()
                .Name);
    }

    [Fact]
    public async Task Problem_details_and_response_include_the_request_id()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(database.ConnectionString);
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/route-that-does-not-exist");
        request.Headers.Add("X-Request-ID", "integration-test-request");

        using HttpResponseMessage response = await client.SendAsync(
            request,
            CancellationToken.None);
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "integration-test-request",
            response.Headers.GetValues("X-Request-ID").Single());
        Assert.Equal("integration-test-request", problem.GetProperty("requestId").GetString());
    }

    private const string InvalidConnectionString =
        "Host=127.0.0.1;Port=1;Database=unavailable;Username=none;Password=none;Timeout=1;Command Timeout=1";

    private static WebApplicationFactory<Program> CreateFactory(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting(
                "Database:ConnectionString",
                connectionString));
}
