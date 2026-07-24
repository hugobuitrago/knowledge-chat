using System.Security.Claims;
using Rag.Application.Security;

namespace Rag.Api.Security;

internal sealed class HttpCurrentTenant(IHttpContextAccessor httpContextAccessor) : ICurrentTenant
{
    public Guid TenantId => ReadRequiredGuidClaim(RagClaimTypes.TenantId);

    public Guid? ChatbotId
    {
        get
        {
            string? value = Principal.FindFirstValue(RagClaimTypes.ChatbotId);
            return value is null
                ? null
                : Guid.TryParse(value, out Guid chatbotId)
                    ? chatbotId
                    : throw new InvalidOperationException(
                        "The authenticated identity contains an invalid chatbot claim.");
        }
    }

    private ClaimsPrincipal Principal =>
        httpContextAccessor.HttpContext?.User
        ?? throw new InvalidOperationException("No active HTTP request is available.");

    private Guid ReadRequiredGuidClaim(string claimType)
    {
        string? value = Principal.FindFirstValue(claimType);
        return Guid.TryParse(value, out Guid identifier) && identifier != Guid.Empty
            ? identifier
            : throw new InvalidOperationException(
                $"The authenticated identity does not contain a valid '{claimType}' claim.");
    }
}
