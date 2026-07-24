using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rag.Api.Security;
using Rag.Application.Security;
using Rag.Contracts.KnowledgeBases;
using Rag.Domain.Entities;
using Rag.Infrastructure.Persistence;
using Rag.IntegrationTests.PostgreSql;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class SecurityEndpointsTests(PostgreSqlFixture database)
{
    private const string Pepper =
        "integration-tests-only-pepper-not-a-production-secret";

    [Fact]
    public async Task Protected_endpoint_without_credentials_returns_unauthorized()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            $"/v1/knowledge-bases/{Guid.NewGuid():D}",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            "ApiKey",
            response.Headers.WwwAuthenticate.Select(value => value.Scheme));
    }

    [Fact]
    public async Task Credential_without_required_scope_returns_forbidden()
    {
        TenantSeed seed = await SeedTenantAsync();
        TestCredential credential = TestCredential.Create(
            seed.TenantId,
            scopes: [RagScopes.Retrieve]);
        await using WebApplicationFactory<Program> factory = CreateFactory(credential);
        using HttpClient client = CreateAuthenticatedClient(factory, credential);

        using HttpResponseMessage response = await client.GetAsync(
            $"/v1/knowledge-bases/{seed.KnowledgeBaseId:D}",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Tenant_cannot_read_another_tenants_knowledge_base()
    {
        TenantSeed tenantA = await SeedTenantAsync();
        TenantSeed tenantB = await SeedTenantAsync();
        TestCredential credential = TestCredential.Create(
            tenantA.TenantId,
            scopes: [RagScopes.Admin]);
        await using WebApplicationFactory<Program> factory = CreateFactory(credential);
        using HttpClient client = CreateAuthenticatedClient(factory, credential);

        using HttpResponseMessage ownResponse = await client.GetAsync(
            $"/v1/knowledge-bases/{tenantA.KnowledgeBaseId:D}",
            CancellationToken.None);
        using HttpResponseMessage foreignResponse = await client.GetAsync(
            $"/v1/knowledge-bases/{tenantB.KnowledgeBaseId:D}",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignResponse.StatusCode);
    }

    [Fact]
    public async Task Tenant_id_in_payload_is_rejected_and_valid_create_uses_authenticated_tenant()
    {
        TenantSeed tenantA = await SeedTenantAsync();
        TenantSeed tenantB = await SeedTenantAsync();
        TestCredential credential = TestCredential.Create(
            tenantA.TenantId,
            scopes: [RagScopes.Admin]);
        await using WebApplicationFactory<Program> factory = CreateFactory(credential);
        using HttpClient client = CreateAuthenticatedClient(factory, credential);
        string rejectedName = $"rejected-{Guid.NewGuid():N}";

        using HttpResponseMessage rejectedResponse = await client.PostAsJsonAsync(
            "/v1/knowledge-bases",
            new
            {
                name = rejectedName,
                tenantId = tenantB.TenantId,
            },
            CancellationToken.None);
        string acceptedName = $"accepted-{Guid.NewGuid():N}";
        using HttpResponseMessage acceptedResponse = await client.PostAsJsonAsync(
            "/v1/knowledge-bases",
            new CreateKnowledgeBaseRequest(acceptedName),
            CancellationToken.None);
        KnowledgeBaseResponse accepted = (await acceptedResponse.Content
            .ReadFromJsonAsync<KnowledgeBaseResponse>(CancellationToken.None))!;

        Assert.Equal(HttpStatusCode.BadRequest, rejectedResponse.StatusCode);
        Assert.Equal(
            "application/problem+json",
            rejectedResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Created, acceptedResponse.StatusCode);

        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext dbContext = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        Assert.False(await dbContext.KnowledgeBases.AnyAsync(
            knowledgeBase => knowledgeBase.Name == rejectedName,
            CancellationToken.None));
        KnowledgeBase persisted = await dbContext.KnowledgeBases.SingleAsync(
            knowledgeBase => knowledgeBase.Id == accepted.Id,
            CancellationToken.None);
        Assert.Equal(tenantA.TenantId, persisted.TenantId);
    }

    [Fact]
    public async Task Tenant_rate_limit_returns_problem_details()
    {
        TenantSeed seed = await SeedTenantAsync();
        TestCredential credential = TestCredential.Create(
            seed.TenantId,
            scopes: [RagScopes.Admin]);
        await using WebApplicationFactory<Program> factory = CreateFactory(
            credential,
            tenantPermitLimit: 1,
            chatbotPermitLimit: 100);
        using HttpClient client = CreateAuthenticatedClient(factory, credential);

        using HttpResponseMessage first = await client.GetAsync(
            $"/v1/knowledge-bases/{seed.KnowledgeBaseId:D}",
            CancellationToken.None);
        using HttpResponseMessage second = await client.GetAsync(
            $"/v1/knowledge-bases/{seed.KnowledgeBaseId:D}",
            CancellationToken.None);
        JsonElement problem = await second.Content.ReadFromJsonAsync<JsonElement>(
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(
            "application/problem+json",
            second.Content.Headers.ContentType?.MediaType);
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("requestId").GetString()));
    }

    [Fact]
    public async Task Chatbot_rate_limit_is_partitioned_from_tenant_limit()
    {
        TenantSeed seed = await SeedTenantAsync(includeChatbot: true);
        TestCredential credential = TestCredential.Create(
            seed.TenantId,
            seed.ChatbotId,
            [RagScopes.Admin]);
        await using WebApplicationFactory<Program> factory = CreateFactory(
            credential,
            tenantPermitLimit: 100,
            chatbotPermitLimit: 1);
        using HttpClient client = CreateAuthenticatedClient(factory, credential);

        using HttpResponseMessage first = await client.GetAsync(
            $"/v1/knowledge-bases/{seed.KnowledgeBaseId:D}",
            CancellationToken.None);
        using HttpResponseMessage second = await client.GetAsync(
            $"/v1/knowledge-bases/{seed.KnowledgeBaseId:D}",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task Credential_for_inactive_tenant_is_rejected()
    {
        Guid tenantId = Guid.NewGuid();
        await using (AsyncServiceScope scope = database.Services.CreateAsyncScope())
        {
            RagDbContext dbContext = scope.ServiceProvider.GetRequiredService<RagDbContext>();
            Tenant tenant = Tenant.Create(tenantId, $"inactive-{tenantId:N}");
            tenant.Deactivate();
            dbContext.Tenants.Add(tenant);
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }

        TestCredential credential = TestCredential.Create(
            tenantId,
            scopes: [RagScopes.Admin]);
        await using WebApplicationFactory<Program> factory = CreateFactory(credential);
        using HttpClient client = CreateAuthenticatedClient(factory, credential);

        using HttpResponseMessage response = await client.GetAsync(
            $"/v1/knowledge-bases/{Guid.NewGuid():D}",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Api_key_hash_verification_is_keyed_and_rejects_changed_values()
    {
        string secret = $"secret-{Guid.NewGuid():N}";
        string hash = ApiKeyHasher.HashSecret(secret, Pepper);

        Assert.True(ApiKeyHasher.Verify(secret, Pepper, hash));
        Assert.False(ApiKeyHasher.Verify($"{secret}-changed", Pepper, hash));
        Assert.False(ApiKeyHasher.Verify(secret, $"{Pepper}-changed", hash));
    }

    [Fact]
    public async Task Logs_do_not_contain_api_key_secret_or_request_payload()
    {
        TenantSeed seed = await SeedTenantAsync();
        TestCredential credential = TestCredential.Create(
            seed.TenantId,
            scopes: [RagScopes.Admin]);
        using var loggerProvider = new CapturingLoggerProvider();
        await using WebApplicationFactory<Program> factory = CreateFactory(
            credential,
            loggerProvider: loggerProvider);
        using HttpClient client = CreateAuthenticatedClient(factory, credential);
        string sensitivePayloadMarker = $"sensitive-payload-{Guid.NewGuid():N}";

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/knowledge-bases",
            new CreateKnowledgeBaseRequest(sensitivePayloadMarker),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.DoesNotContain(
            loggerProvider.Messages,
            message => message.Contains(credential.Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(
            loggerProvider.Messages,
            message => message.Contains(sensitivePayloadMarker, StringComparison.Ordinal));
    }

    private async Task<TenantSeed> SeedTenantAsync(bool includeChatbot = false)
    {
        Guid tenantId = Guid.NewGuid();
        Guid knowledgeBaseId = Guid.NewGuid();
        Guid? chatbotId = includeChatbot ? Guid.NewGuid() : null;
        var tenant = Tenant.Create(tenantId, $"tenant-{tenantId:N}");
        KnowledgeBase knowledgeBase = KnowledgeBase.Create(
            knowledgeBaseId,
            tenantId,
            $"kb-{knowledgeBaseId:N}");

        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext dbContext = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        dbContext.AddRange(tenant, knowledgeBase);
        if (chatbotId is Guid value)
        {
            dbContext.Chatbots.Add(
                Chatbot.Create(value, tenantId, knowledgeBaseId, $"chatbot-{value:N}"));
        }

        await dbContext.SaveChangesAsync(CancellationToken.None);
        return new TenantSeed(tenantId, knowledgeBaseId, chatbotId);
    }

    private WebApplicationFactory<Program> CreateFactory(
        TestCredential? credential = null,
        int tenantPermitLimit = 100,
        int chatbotPermitLimit = 50,
        ILoggerProvider? loggerProvider = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = database.ConnectionString,
            ["Authentication:ApiKey:Pepper"] = Pepper,
            ["RateLimiting:TenantPermitLimit"] = tenantPermitLimit.ToString(),
            ["RateLimiting:ChatbotPermitLimit"] = chatbotPermitLimit.ToString(),
            ["RateLimiting:WindowSeconds"] = "60",
        };
        if (credential is not null)
        {
            settings["Authentication:ApiKey:Clients:0:KeyId"] = credential.KeyId;
            settings["Authentication:ApiKey:Clients:0:TenantId"] =
                credential.TenantId.ToString("D");
            settings["Authentication:ApiKey:Clients:0:SecretHash"] =
                ApiKeyHasher.HashSecret(credential.Secret, Pepper);
            if (credential.ChatbotId is Guid chatbotId)
            {
                settings["Authentication:ApiKey:Clients:0:ChatbotId"] =
                    chatbotId.ToString("D");
            }

            for (int index = 0; index < credential.Scopes.Count; index++)
            {
                settings[$"Authentication:ApiKey:Clients:0:Scopes:{index}"] =
                    credential.Scopes[index];
            }
        }

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (loggerProvider is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
            }

            foreach ((string key, string? value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });
    }

    private static HttpClient CreateAuthenticatedClient(
        WebApplicationFactory<Program> factory,
        TestCredential credential)
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(
            "X-API-Key",
            $"{credential.KeyId}.{credential.Secret}");
        return client;
    }

    private sealed record TenantSeed(
        Guid TenantId,
        Guid KnowledgeBaseId,
        Guid? ChatbotId);

    private sealed record TestCredential(
        string KeyId,
        string Secret,
        Guid TenantId,
        Guid? ChatbotId,
        IReadOnlyList<string> Scopes)
    {
        public static TestCredential Create(
            Guid tenantId,
            IReadOnlyList<string> scopes) =>
            Create(tenantId, chatbotId: null, scopes);

        public static TestCredential Create(
            Guid tenantId,
            Guid? chatbotId,
            IReadOnlyList<string> scopes) =>
            new(
                $"client-{Guid.NewGuid():N}",
                $"secret-{Guid.NewGuid():N}",
                tenantId,
                chatbotId,
                scopes);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull =>
                null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
                if (exception is not null)
                {
                    messages.Enqueue(exception.ToString());
                }
            }
        }
    }
}
