using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Rag.Api.Security;
using Rag.Application.Security;
using Rag.Contracts.Retrieval;
using Rag.Domain.Entities;
using Rag.Infrastructure.Ingestion;
using Rag.Infrastructure.Persistence;
using Rag.IntegrationTests.PostgreSql;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class RetrievalEndpointsTests(PostgreSqlFixture database)
{
    private const string Pepper =
        "retrieval-tests-only-pepper-not-a-production-secret";
    private const string Model = "api-retrieval-test-model";

    [Fact]
    public async Task Retrieve_returns_ranked_content_with_structured_source()
    {
        RetrievalApiSeed seed = await SeedAsync(includeChatbot: true);
        TestCredential credential = TestCredential.Create(
            seed.TenantId,
            seed.ChatbotId,
            [RagScopes.Retrieve]);
        await using WebApplicationFactory<Program> factory = CreateFactory(credential);
        using HttpClient client = CreateAuthenticatedClient(factory, credential);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/retrieve",
            new RetrieveRequest(seed.KnowledgeBaseId, "API-CODE-77"),
            CancellationToken.None);
        RetrieveResponse result = (await response.Content
            .ReadFromJsonAsync<RetrieveResponse>(CancellationToken.None))!;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(seed.KnowledgeBaseId, result.KnowledgeBaseId);
        Assert.Equal(seed.VersionId, result.VersionId);
        Assert.False(result.Degraded);
        RetrievedChunkResponse chunk = Assert.Single(result.Results);
        Assert.Equal("api-source.txt", chunk.Source.FileName);
        Assert.Equal(0, chunk.Source.ChunkIndex);
        Assert.Equal("API-CODE-77 is the exact support identifier.", chunk.Content);
        Assert.True(chunk.Score > 0D);
    }

    [Fact]
    public async Task Retrieve_requires_scope_and_rejects_tenant_in_payload()
    {
        RetrievalApiSeed seed = await SeedAsync(includeChatbot: false);
        TestCredential forbiddenCredential = TestCredential.Create(
            seed.TenantId,
            chatbotId: null,
            [RagScopes.Admin]);
        await using WebApplicationFactory<Program> forbiddenFactory =
            CreateFactory(forbiddenCredential);
        using HttpClient forbiddenClient = CreateAuthenticatedClient(
            forbiddenFactory,
            forbiddenCredential);
        using HttpResponseMessage forbidden = await forbiddenClient.PostAsJsonAsync(
            "/v1/retrieve",
            new RetrieveRequest(seed.KnowledgeBaseId, "API-CODE-77"),
            CancellationToken.None);

        TestCredential allowedCredential = TestCredential.Create(
            seed.TenantId,
            chatbotId: null,
            [RagScopes.Retrieve]);
        await using WebApplicationFactory<Program> allowedFactory =
            CreateFactory(allowedCredential);
        using HttpClient allowedClient = CreateAuthenticatedClient(
            allowedFactory,
            allowedCredential);
        using HttpResponseMessage rejectedPayload = await allowedClient.PostAsJsonAsync(
            "/v1/retrieve",
            new
            {
                seed.KnowledgeBaseId,
                query = "API-CODE-77",
                tenantId = Guid.NewGuid(),
            },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedPayload.StatusCode);
        Assert.Equal(
            "application/problem+json",
            rejectedPayload.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Chatbot_cannot_retrieve_from_another_knowledge_base()
    {
        RetrievalApiSeed allowed = await SeedAsync(includeChatbot: true);
        RetrievalApiSeed another = await SeedAsync(
            includeChatbot: false,
            tenantId: allowed.TenantId);
        TestCredential credential = TestCredential.Create(
            allowed.TenantId,
            allowed.ChatbotId,
            [RagScopes.Retrieve]);
        await using WebApplicationFactory<Program> factory = CreateFactory(credential);
        using HttpClient client = CreateAuthenticatedClient(factory, credential);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/retrieve",
            new RetrieveRequest(another.KnowledgeBaseId, "API-CODE-77"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<RetrievalApiSeed> SeedAsync(
        bool includeChatbot,
        Guid? tenantId = null)
    {
        Guid resolvedTenantId = tenantId ?? Guid.NewGuid();
        Guid knowledgeBaseId = Guid.NewGuid();
        Guid versionId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();
        Guid? chatbotId = includeChatbot ? Guid.NewGuid() : null;
        const string content = "API-CODE-77 is the exact support identifier.";
        var provider = new DeterministicEmbeddingProvider(
            new EmbeddingMetadataOptions
            {
                Provider = "Deterministic",
                Model = Model,
                Dimensions = RagDatabaseConstants.EmbeddingDimensions,
            });
        float[] vector = (await provider.GenerateAsync(
            ["API-CODE-77"],
            CancellationToken.None)).Vectors[0].ToArray();

        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext context = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        if (tenantId is null)
        {
            context.Tenants.Add(
                Tenant.Create(
                    resolvedTenantId,
                    $"retrieval-api-tenant-{resolvedTenantId:N}"));
        }

        context.KnowledgeBases.Add(
            KnowledgeBase.Create(
                knowledgeBaseId,
                resolvedTenantId,
                $"retrieval-api-kb-{knowledgeBaseId:N}"));
        if (chatbotId is Guid value)
        {
            context.Chatbots.Add(
                Chatbot.Create(
                    value,
                    resolvedTenantId,
                    knowledgeBaseId,
                    $"retrieval-api-chatbot-{value:N}"));
        }

        var version = KnowledgeBaseVersion.Create(
            versionId,
            resolvedTenantId,
            knowledgeBaseId,
            Model,
            RagDatabaseConstants.EmbeddingDimensions);
        version.MarkProcessing();
        version.MarkReady();
        var document = Document.Create(
            documentId,
            resolvedTenantId,
            knowledgeBaseId,
            versionId,
            "api-source.txt",
            $"retrieval-api/{documentId:N}.txt",
            "text/plain",
            Hash("api-source.txt"),
            content.Length);
        document.MarkProcessing();
        document.MarkIndexed();
        context.AddRange(
            version,
            document,
            DocumentChunk.Create(
                Guid.NewGuid(),
                resolvedTenantId,
                knowledgeBaseId,
                versionId,
                documentId,
                chunkIndex: 0,
                content,
                Hash(content),
                tokenCount: 6,
                startOffset: 0,
                endOffset: content.Length,
                Hash("api-retrieval-configuration"),
                vector));
        await context.SaveChangesAsync();
        version.Activate();
        await context.SaveChangesAsync();
        return new RetrievalApiSeed(
            resolvedTenantId,
            knowledgeBaseId,
            versionId,
            chatbotId);
    }

    private WebApplicationFactory<Program> CreateFactory(TestCredential credential)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = database.ConnectionString,
            ["Embedding:Provider"] = "Deterministic",
            ["Embedding:Model"] = Model,
            ["Embedding:Dimensions"] =
                RagDatabaseConstants.EmbeddingDimensions.ToString(),
            ["Storage:Provider"] = "Local",
            ["Storage:LocalPath"] = database.StorageRoot,
            ["Authentication:ApiKey:Pepper"] = Pepper,
            ["Authentication:ApiKey:Clients:0:KeyId"] = credential.KeyId,
            ["Authentication:ApiKey:Clients:0:TenantId"] =
                credential.TenantId.ToString("D"),
            ["Authentication:ApiKey:Clients:0:SecretHash"] =
                ApiKeyHasher.HashSecret(credential.Secret, Pepper),
        };
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

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
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

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed record RetrievalApiSeed(
        Guid TenantId,
        Guid KnowledgeBaseId,
        Guid VersionId,
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
            Guid? chatbotId,
            IReadOnlyList<string> scopes) =>
            new(
                $"retrieve-{Guid.NewGuid():N}",
                $"secret-{Guid.NewGuid():N}",
                tenantId,
                chatbotId,
                scopes);
    }
}
