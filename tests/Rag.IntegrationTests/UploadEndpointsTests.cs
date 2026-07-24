using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rag.Api.Security;
using Rag.Application.Security;
using Rag.Contracts.Ingestions;
using Rag.Domain.Entities;
using Rag.Domain.Enums;
using Rag.Infrastructure.Persistence;
using Rag.IntegrationTests.PostgreSql;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class UploadEndpointsTests(PostgreSqlFixture database)
{
    private const string Pepper =
        "integration-tests-only-pepper-not-a-production-secret";

    [Fact]
    public async Task Valid_streamed_upload_returns_accepted_and_persists_atomic_records()
    {
        TenantSeed seed = await SeedTenantAsync();
        TestCredential credential = TestCredential.Create(seed.TenantId);
        await using WebApplicationFactory<Program> factory = CreateFactory(credential);
        using HttpClient client = CreateAuthenticatedClient(factory, credential);
        byte[] content = CreateTextContent(512 * 1024);

        using HttpResponseMessage response = await UploadAsync(
            client,
            seed.KnowledgeBaseId,
            $"upload-{Guid.NewGuid():N}",
            "manual.txt",
            "text/plain; charset=utf-8",
            content,
            useChunkedStream: true);
        UploadAcceptedResponse accepted = (await response.Content
            .ReadFromJsonAsync<UploadAcceptedResponse>(CancellationToken.None))!;

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/v1/ingestions/{accepted.JobId:D}", accepted.StatusUrl);

        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext dbContext = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        Document document = await dbContext.Documents
            .SingleAsync(candidate => candidate.Id == accepted.DocumentId);
        KnowledgeBaseVersion version = await dbContext.KnowledgeBaseVersions
            .SingleAsync(candidate => candidate.Id == accepted.VersionId);
        IngestionJob job = await dbContext.IngestionJobs
            .SingleAsync(candidate => candidate.Id == accepted.JobId);
        Assert.Equal(seed.TenantId, document.TenantId);
        Assert.Equal(content.Length, document.SizeBytes);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            document.ContentHash);
        Assert.Equal(DocumentStatus.Uploaded, document.Status);
        Assert.Equal(KnowledgeBaseVersionStatus.Pending, version.Status);
        Assert.Equal(IngestionJobStatus.Queued, job.Status);

        string storedPath = ResolveStoredPath(document.StorageObjectKey);
        Assert.True(File.Exists(storedPath));
        Assert.Equal(content, await File.ReadAllBytesAsync(storedPath));
    }

    [Fact]
    public async Task Same_idempotency_key_and_request_return_the_original_result()
    {
        TenantSeed seed = await SeedTenantAsync();
        TestCredential credential = TestCredential.Create(seed.TenantId);
        await using WebApplicationFactory<Program> factory = CreateFactory(credential);
        using HttpClient client = CreateAuthenticatedClient(factory, credential);
        string idempotencyKey = $"duplicate-{Guid.NewGuid():N}";
        byte[] content = "same upload content"u8.ToArray();

        using HttpResponseMessage firstResponse = await UploadAsync(
            client,
            seed.KnowledgeBaseId,
            idempotencyKey,
            "duplicate.txt",
            "text/plain",
            content);
        using HttpResponseMessage secondResponse = await UploadAsync(
            client,
            seed.KnowledgeBaseId,
            idempotencyKey,
            "duplicate.txt",
            "text/plain",
            content);
        UploadAcceptedResponse first = (await firstResponse.Content
            .ReadFromJsonAsync<UploadAcceptedResponse>())!;
        UploadAcceptedResponse second = (await secondResponse.Content
            .ReadFromJsonAsync<UploadAcceptedResponse>())!;

        Assert.Equal(HttpStatusCode.Accepted, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, secondResponse.StatusCode);
        Assert.Equal(first, second);
        Assert.Equal(
            "true",
            secondResponse.Headers.GetValues("Idempotency-Replayed").Single());

        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext dbContext = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        Assert.Equal(
            1,
            await dbContext.Documents.CountAsync(
                document => document.KnowledgeBaseId == seed.KnowledgeBaseId));
        Assert.Equal(
            1,
            await dbContext.IngestionJobs.CountAsync(
                job => job.KnowledgeBaseId == seed.KnowledgeBaseId));
        Assert.Equal(
            1,
            await dbContext.KnowledgeBaseVersions.CountAsync(
                version => version.KnowledgeBaseId == seed.KnowledgeBaseId));
    }

    [Fact]
    public async Task Concurrent_requests_with_same_idempotency_key_create_one_job()
    {
        TenantSeed seed = await SeedTenantAsync();
        TestCredential credential = TestCredential.Create(seed.TenantId);
        await using WebApplicationFactory<Program> factory = CreateFactory(credential);
        using HttpClient client = CreateAuthenticatedClient(factory, credential);
        string idempotencyKey = $"concurrent-{Guid.NewGuid():N}";
        byte[] content = CreateTextContent(64 * 1024);

        Task<HttpResponseMessage> firstUpload = UploadAsync(
            client,
            seed.KnowledgeBaseId,
            idempotencyKey,
            "concurrent.txt",
            "text/plain",
            content);
        Task<HttpResponseMessage> secondUpload = UploadAsync(
            client,
            seed.KnowledgeBaseId,
            idempotencyKey,
            "concurrent.txt",
            "text/plain",
            content);
        HttpResponseMessage[] responses = await Task.WhenAll(firstUpload, secondUpload);
        using HttpResponseMessage firstResponse = responses[0];
        using HttpResponseMessage secondResponse = responses[1];
        UploadAcceptedResponse first = (await firstResponse.Content
            .ReadFromJsonAsync<UploadAcceptedResponse>())!;
        UploadAcceptedResponse second = (await secondResponse.Content
            .ReadFromJsonAsync<UploadAcceptedResponse>())!;

        Assert.All(
            responses,
            response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        Assert.Equal(first, second);
        Assert.Contains(
            responses,
            response => response.Headers.Contains("Idempotency-Replayed"));

        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext dbContext = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        Assert.Equal(
            1,
            await dbContext.IngestionJobs.CountAsync(
                job => job.KnowledgeBaseId == seed.KnowledgeBaseId));
    }

    [Fact]
    public async Task Reusing_idempotency_key_for_different_content_returns_conflict()
    {
        TenantSeed seed = await SeedTenantAsync();
        TestCredential credential = TestCredential.Create(seed.TenantId);
        await using WebApplicationFactory<Program> factory = CreateFactory(credential);
        using HttpClient client = CreateAuthenticatedClient(factory, credential);
        string idempotencyKey = $"conflict-{Guid.NewGuid():N}";

        using HttpResponseMessage first = await UploadAsync(
            client,
            seed.KnowledgeBaseId,
            idempotencyKey,
            "conflict.txt",
            "text/plain",
            "first content"u8.ToArray());
        using HttpResponseMessage second = await UploadAsync(
            client,
            seed.KnowledgeBaseId,
            idempotencyKey,
            "conflict.txt",
            "text/plain",
            "different content"u8.ToArray());

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(
            "application/problem+json",
            second.Content.Headers.ContentType?.MediaType);

        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext dbContext = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        Assert.Equal(
            1,
            await dbContext.Documents.CountAsync(
                document => document.KnowledgeBaseId == seed.KnowledgeBaseId));
    }

    [Fact]
    public async Task Invalid_files_do_not_create_a_version_document_or_job()
    {
        TenantSeed seed = await SeedTenantAsync();
        TestCredential credential = TestCredential.Create(seed.TenantId);
        await using WebApplicationFactory<Program> factory = CreateFactory(
            credential,
            maximumFileSizeBytes: 1024);
        using HttpClient client = CreateAuthenticatedClient(factory, credential);
        var cases = new[]
        {
            new UploadCase("document.pdf", "text/plain", "valid text"u8.ToArray()),
            new UploadCase("document.txt", "application/octet-stream", "valid text"u8.ToArray()),
            new UploadCase("document.txt", "text/plain", [0xC3, 0x28]),
            new UploadCase("document.txt", "text/plain", " \r\n\t "u8.ToArray()),
            new UploadCase("document.txt", "text/plain", new byte[1025]),
        };

        foreach (UploadCase invalidCase in cases)
        {
            using HttpResponseMessage response = await UploadAsync(
                client,
                seed.KnowledgeBaseId,
                $"invalid-{Guid.NewGuid():N}",
                invalidCase.FileName,
                invalidCase.ContentType,
                invalidCase.Content);

            Assert.True(
                response.StatusCode is HttpStatusCode.BadRequest or
                    (HttpStatusCode)413,
                $"Unexpected status for {invalidCase.FileName}: {response.StatusCode}");
        }

        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext dbContext = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        Assert.False(await dbContext.KnowledgeBaseVersions.AnyAsync(
            version => version.KnowledgeBaseId == seed.KnowledgeBaseId));
        Assert.False(await dbContext.Documents.AnyAsync(
            document => document.KnowledgeBaseId == seed.KnowledgeBaseId));
        Assert.False(await dbContext.IngestionJobs.AnyAsync(
            job => job.KnowledgeBaseId == seed.KnowledgeBaseId));
    }

    [Fact]
    public async Task More_than_one_file_is_rejected_without_persisting_the_staged_upload()
    {
        TenantSeed seed = await SeedTenantAsync();
        TestCredential credential = TestCredential.Create(seed.TenantId);
        await using WebApplicationFactory<Program> factory = CreateFactory(credential);
        using HttpClient client = CreateAuthenticatedClient(factory, credential);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/knowledge-bases/{seed.KnowledgeBaseId:D}/documents");
        request.Headers.Add("Idempotency-Key", $"multi-{Guid.NewGuid():N}");
        var multipart = new MultipartFormDataContent();
        multipart.Add(CreateFileContent("first"u8.ToArray(), "text/plain"), "file", "first.txt");
        multipart.Add(CreateFileContent("second"u8.ToArray(), "text/plain"), "file", "second.txt");
        request.Content = multipart;

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext dbContext = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        Assert.False(await dbContext.KnowledgeBaseVersions.AnyAsync(
            version => version.KnowledgeBaseId == seed.KnowledgeBaseId));
    }

    [Fact]
    public async Task Ingestion_status_is_tenant_scoped()
    {
        TenantSeed tenantA = await SeedTenantAsync();
        TenantSeed tenantB = await SeedTenantAsync();
        TestCredential credential = TestCredential.Create(tenantA.TenantId);
        await using WebApplicationFactory<Program> factory = CreateFactory(credential);
        using HttpClient client = CreateAuthenticatedClient(factory, credential);
        using HttpResponseMessage upload = await UploadAsync(
            client,
            tenantA.KnowledgeBaseId,
            $"status-{Guid.NewGuid():N}",
            "status.txt",
            "text/plain",
            "status content"u8.ToArray());
        UploadAcceptedResponse accepted = (await upload.Content
            .ReadFromJsonAsync<UploadAcceptedResponse>())!;

        using HttpResponseMessage own = await client.GetAsync(accepted.StatusUrl);
        using HttpResponseMessage foreign = await client.GetAsync(
            $"/v1/ingestions/{await SeedJobAsync(tenantB):D}");

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    private async Task<TenantSeed> SeedTenantAsync()
    {
        Guid tenantId = Guid.NewGuid();
        Guid knowledgeBaseId = Guid.NewGuid();
        var tenant = Tenant.Create(tenantId, $"tenant-{tenantId:N}");
        KnowledgeBase knowledgeBase = KnowledgeBase.Create(
            knowledgeBaseId,
            tenantId,
            $"kb-{knowledgeBaseId:N}");
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext dbContext = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        dbContext.AddRange(tenant, knowledgeBase);
        await dbContext.SaveChangesAsync();
        return new TenantSeed(tenantId, knowledgeBaseId);
    }

    private async Task<Guid> SeedJobAsync(TenantSeed seed)
    {
        Guid versionId = Guid.NewGuid();
        Guid documentId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        KnowledgeBaseVersion version = KnowledgeBaseVersion.Create(
            versionId,
            seed.TenantId,
            seed.KnowledgeBaseId,
            "integration-test-model",
            1536);
        Document document = Document.Create(
            documentId,
            seed.TenantId,
            seed.KnowledgeBaseId,
            versionId,
            "seed.txt",
            $"seed/{documentId:N}.txt",
            "text/plain",
            new string('a', 64),
            1);
        IngestionJob job = IngestionJob.Create(
            jobId,
            seed.TenantId,
            seed.KnowledgeBaseId,
            versionId,
            documentId,
            DateTimeOffset.UtcNow,
            2);
        await using AsyncServiceScope scope = database.Services.CreateAsyncScope();
        RagDbContext dbContext = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        dbContext.AddRange(version, document, job);
        await dbContext.SaveChangesAsync();
        return jobId;
    }

    private WebApplicationFactory<Program> CreateFactory(
        TestCredential credential,
        long maximumFileSizeBytes = 1_048_576)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:ConnectionString"] = database.ConnectionString,
            ["Authentication:ApiKey:Pepper"] = Pepper,
            ["Authentication:ApiKey:Clients:0:KeyId"] = credential.KeyId,
            ["Authentication:ApiKey:Clients:0:TenantId"] =
                credential.TenantId.ToString("D"),
            ["Authentication:ApiKey:Clients:0:SecretHash"] =
                ApiKeyHasher.HashSecret(credential.Secret, Pepper),
            ["Authentication:ApiKey:Clients:0:Scopes:0"] = RagScopes.Ingest,
            ["Embedding:Dimensions"] = "1536",
            ["Embedding:Model"] = "integration-test-model",
            ["Jobs:BaseRetryDelaySeconds"] = "1",
            ["Jobs:LeaseDurationSeconds"] = "1",
            ["Jobs:MaxAttempts"] = "2",
            ["Jobs:MaxRetryDelaySeconds"] = "2",
            ["Storage:LocalPath"] = database.StorageRoot,
            ["Storage:Provider"] = "Local",
            ["Uploads:IdempotencyTtlHours"] = "24",
            ["Uploads:MaxFileSizeBytes"] = maximumFileSizeBytes.ToString(),
        };

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

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        Guid knowledgeBaseId,
        string idempotencyKey,
        string fileName,
        string contentType,
        byte[] content,
        bool useChunkedStream = false)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/v1/knowledge-bases/{knowledgeBaseId:D}/documents");
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        var multipart = new MultipartFormDataContent();
        HttpContent fileContent = useChunkedStream
            ? new StreamContent(new NonSeekableChunkedStream(content))
            : new ByteArrayContent(content);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        multipart.Add(fileContent, "file", fileName);
        request.Content = multipart;

        return await client.SendAsync(request, CancellationToken.None);
    }

    private static ByteArrayContent CreateFileContent(byte[] content, string contentType)
    {
        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return fileContent;
    }

    private static byte[] CreateTextContent(int minimumBytes)
    {
        const string line = "Knowledge retrieval streaming test content.\n";
        string text = string.Concat(
            Enumerable.Repeat(line, (minimumBytes / line.Length) + 1));
        return System.Text.Encoding.UTF8.GetBytes(text);
    }

    private string ResolveStoredPath(string objectKey) =>
        Path.GetFullPath(
            Path.Combine(
                database.StorageRoot,
                objectKey.Replace('/', Path.DirectorySeparatorChar)));

    private sealed record TenantSeed(Guid TenantId, Guid KnowledgeBaseId);

    private sealed record TestCredential(string KeyId, string Secret, Guid TenantId)
    {
        public static TestCredential Create(Guid tenantId) =>
            new(
                $"client-{Guid.NewGuid():N}",
                $"secret-{Guid.NewGuid():N}",
                tenantId);
    }

    private sealed record UploadCase(
        string FileName,
        string ContentType,
        byte[] Content);

    private sealed class NonSeekableChunkedStream(byte[] content) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesToCopy = Math.Min(Math.Min(count, 4096), content.Length - _position);
            if (bytesToCopy <= 0)
            {
                return 0;
            }

            content.AsSpan(_position, bytesToCopy).CopyTo(buffer.AsSpan(offset, bytesToCopy));
            _position += bytesToCopy;
            return bytesToCopy;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int bytesToCopy = Math.Min(
                Math.Min(buffer.Length, 4096),
                content.Length - _position);
            if (bytesToCopy <= 0)
            {
                return ValueTask.FromResult(0);
            }

            content.AsMemory(_position, bytesToCopy).CopyTo(buffer);
            _position += bytesToCopy;
            return ValueTask.FromResult(bytesToCopy);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
