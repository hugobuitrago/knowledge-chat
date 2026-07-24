using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Rag.Api.OpenApi;

internal sealed class ApiKeyDocumentTransformer :
    IOpenApiDocumentTransformer,
    IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??=
            new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
        document.Components.SecuritySchemes[SecuritySchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = "X-API-Key",
            Description = "Machine credential in the format keyId.secret.",
        };

        return Task.CompletedTask;
    }

    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        bool requiresAuthorization = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<IAuthorizeData>()
            .Any();
        if (!requiresAuthorization)
        {
            return Task.CompletedTask;
        }

        operation.Security ??= [];
        var schemeReference = new OpenApiSecuritySchemeReference(
            SecuritySchemeName,
            context.Document);
        operation.Security.Add(
            new OpenApiSecurityRequirement
            {
                [schemeReference] = [],
            });

        return Task.CompletedTask;
    }

    private const string SecuritySchemeName = "ApiKey";
}
