using Rag.Domain.Common;

namespace Rag.Domain.Entities;

public sealed class IdempotencyRecord : CreatedEntity
{
    private IdempotencyRecord(
        Guid id,
        Guid tenantId,
        string key,
        string operation,
        string requestHash,
        DateTimeOffset expiresAt)
        : base(id)
    {
        TenantId = DomainGuard.Required(tenantId, nameof(tenantId));
        Key = DomainGuard.Required(key, 200, nameof(key));
        Operation = DomainGuard.Required(operation, 100, nameof(operation));
        RequestHash = DomainGuard.Sha256(requestHash, nameof(requestHash));
        ExpiresAt = expiresAt.ToUniversalTime();
    }

    private IdempotencyRecord()
    {
        Key = string.Empty;
        Operation = string.Empty;
        RequestHash = string.Empty;
    }

    public Guid TenantId { get; private set; }

    public string Key { get; private set; }

    public string Operation { get; private set; }

    public string RequestHash { get; private set; }

    public int? ResponseStatusCode { get; private set; }

    public string? ResponseBodyJson { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public static IdempotencyRecord Create(
        Guid id,
        Guid tenantId,
        string key,
        string operation,
        string requestHash,
        DateTimeOffset expiresAt) =>
        new(id, tenantId, key, operation, requestHash, expiresAt);

    public void StoreResponse(int statusCode, string responseBodyJson)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        ResponseStatusCode = statusCode;
        ResponseBodyJson = DomainGuard.Required(
            responseBodyJson,
            int.MaxValue,
            nameof(responseBodyJson));
    }
}

