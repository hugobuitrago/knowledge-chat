using Rag.Application.Providers;

namespace Rag.UnitTests;

public sealed class ProviderBoundaryTests
{
    [Fact]
    public void Every_asynchronous_provider_operation_accepts_cancellation()
    {
        Type[] providerTypes =
        [
            typeof(IDocumentStorage),
            typeof(IEmbeddingProvider),
            typeof(IIngestionJobQueue),
            typeof(IStreamingLanguageModelProvider),
            typeof(ILanguageModelProvider),
        ];

        foreach (Type providerType in providerTypes)
        {
            foreach (System.Reflection.MethodInfo method in providerType.GetMethods())
            {
                System.Reflection.ParameterInfo[] parameters = method.GetParameters();

                Assert.NotEmpty(parameters);
                Assert.Equal(typeof(CancellationToken), parameters[^1].ParameterType);
            }
        }
    }
}

