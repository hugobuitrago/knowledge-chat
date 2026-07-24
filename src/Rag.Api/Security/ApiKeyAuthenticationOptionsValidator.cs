using Microsoft.Extensions.Options;
using Rag.Application.Security;

namespace Rag.Api.Security;

internal sealed class ApiKeyAuthenticationOptionsValidator :
    IValidateOptions<ApiKeyAuthenticationOptions>
{
    public ValidateOptionsResult Validate(
        string? name,
        ApiKeyAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (!IsValidHeaderName(options.HeaderName))
        {
            failures.Add(
                $"{ApiKeyAuthenticationOptions.SectionName}:HeaderName is not a valid HTTP header name.");
        }

        if (options.Clients.Count > 0 && options.Pepper.Length < ApiKeyHasher.MinimumPepperLength)
        {
            failures.Add(
                $"{ApiKeyAuthenticationOptions.SectionName}:Pepper must contain at least " +
                $"{ApiKeyHasher.MinimumPepperLength} characters when clients are configured.");
        }

        var keyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ApiKeyClientOptions client in options.Clients)
        {
            ValidateClient(client, keyIds, failures);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateClient(
        ApiKeyClientOptions client,
        HashSet<string> keyIds,
        List<string> failures)
    {
        string prefix =
            $"{ApiKeyAuthenticationOptions.SectionName}:Clients[{client.KeyId}]";

        if (!IsValidKeyId(client.KeyId))
        {
            failures.Add(
                $"{prefix}:KeyId must contain 3 to 64 ASCII letters, digits, '-' or '_'.");
        }
        else if (!keyIds.Add(client.KeyId))
        {
            failures.Add($"{prefix}:KeyId must be unique.");
        }

        if (client.TenantId == Guid.Empty)
        {
            failures.Add($"{prefix}:TenantId is required.");
        }

        if (client.ChatbotId == Guid.Empty)
        {
            failures.Add($"{prefix}:ChatbotId cannot be an empty GUID.");
        }

        if (!IsSha256Hash(client.SecretHash))
        {
            failures.Add($"{prefix}:SecretHash must be a Base64-encoded SHA-256 hash.");
        }

        if (client.Scopes.Count == 0 ||
            client.Scopes.Any(scope => !RagScopes.All.Contains(scope)))
        {
            failures.Add(
                $"{prefix}:Scopes must contain only supported RAG scopes and cannot be empty.");
        }

        if (client.Scopes.Count != client.Scopes.Distinct(StringComparer.Ordinal).Count())
        {
            failures.Add($"{prefix}:Scopes cannot contain duplicates.");
        }
    }

    private static bool IsValidHeaderName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsValidKeyId(string value) =>
        value.Length is >= 3 and <= 64 &&
        value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsSha256Hash(string value)
    {
        Span<byte> hash = stackalloc byte[ApiKeyHasher.HashSizeInBytes];
        return Convert.TryFromBase64String(value, hash, out int bytesWritten) &&
            bytesWritten == ApiKeyHasher.HashSizeInBytes;
    }
}
