using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rag.Application.Security;
using Rag.Infrastructure.Persistence;

namespace Rag.Api.Security;

internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<ApiKeyAuthenticationOptions> apiKeyOptions,
    RagDbContext dbContext)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        ApiKeyAuthenticationOptions options = apiKeyOptions.CurrentValue;
        if (!Request.Headers.TryGetValue(options.HeaderName, out var values))
        {
            return AuthenticateResult.NoResult();
        }

        if (values.Count != 1 ||
            !TryParseCredential(values[0], out string keyId, out string secret))
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        ApiKeyClientOptions? client = options.Clients.Find(candidate =>
            string.Equals(candidate.KeyId, keyId, StringComparison.Ordinal));
        if (client is null ||
            !ApiKeyHasher.Verify(secret, options.Pepper, client.SecretHash))
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        bool tenantIsActive = await dbContext.Tenants
            .AsNoTracking()
            .AnyAsync(
                tenant => tenant.Id == client.TenantId && tenant.IsActive,
                Context.RequestAborted)
            .ConfigureAwait(false);
        if (!tenantIsActive)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        if (client.ChatbotId is Guid chatbotId)
        {
            bool chatbotBelongsToTenant = await dbContext.Chatbots
                .AsNoTracking()
                .AnyAsync(
                    chatbot => chatbot.Id == chatbotId &&
                        chatbot.TenantId == client.TenantId,
                    Context.RequestAborted)
                .ConfigureAwait(false);
            if (!chatbotBelongsToTenant)
            {
                return AuthenticateResult.Fail("Invalid API key.");
            }
        }

        ClaimsPrincipal principal = CreatePrincipal(client);
        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = SchemeName;
        await base.HandleChallengeAsync(properties).ConfigureAwait(false);
    }

    private static ClaimsPrincipal CreatePrincipal(ApiKeyClientOptions client)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, client.KeyId),
            new(RagClaimTypes.TenantId, client.TenantId.ToString("D")),
        };
        if (client.ChatbotId is Guid chatbotId)
        {
            claims.Add(new Claim(RagClaimTypes.ChatbotId, chatbotId.ToString("D")));
        }

        claims.AddRange(client.Scopes.Select(scope =>
            new Claim(RagClaimTypes.Scope, scope)));

        return new ClaimsPrincipal(
            new ClaimsIdentity(claims, SchemeName, ClaimTypes.NameIdentifier, ClaimTypes.Role));
    }

    private static bool TryParseCredential(
        string? credential,
        out string keyId,
        out string secret)
    {
        keyId = string.Empty;
        secret = string.Empty;
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 512)
        {
            return false;
        }

        int separator = credential.IndexOf('.');
        if (separator is < 3 or > 64 ||
            separator != credential.LastIndexOf('.') ||
            credential.Length - separator - 1 < ApiKeyHasher.MinimumSecretLength)
        {
            return false;
        }

        keyId = credential[..separator];
        secret = credential[(separator + 1)..];
        return true;
    }
}
