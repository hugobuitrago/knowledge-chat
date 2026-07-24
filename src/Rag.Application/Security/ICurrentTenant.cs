namespace Rag.Application.Security;

public interface ICurrentTenant
{
    Guid TenantId { get; }

    Guid? ChatbotId { get; }
}
