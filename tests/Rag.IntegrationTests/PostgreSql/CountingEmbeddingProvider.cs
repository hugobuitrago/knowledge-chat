using Microsoft.Extensions.Options;
using Rag.Application.Providers;
using Rag.Infrastructure.Ingestion;

namespace Rag.IntegrationTests.PostgreSql;

public sealed class CountingEmbeddingProvider(
    IOptions<EmbeddingMetadataOptions> options) : IEmbeddingProvider
{
    private readonly DeterministicEmbeddingProvider _inner = new(options);
    private int _calls;
    private int _inputs;

    public int Calls => Volatile.Read(ref _calls);

    public int Inputs => Volatile.Read(ref _inputs);

    public int? FailOnCall { get; set; }

    public async ValueTask<EmbeddingBatch> GenerateAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken)
    {
        int call = Interlocked.Increment(ref _calls);
        Interlocked.Add(ref _inputs, inputs.Count);
        if (FailOnCall == call)
        {
            throw new EmbeddingProviderException(
                "Synthetic transient embedding failure.",
                isTransient: true);
        }

        return await _inner.GenerateAsync(inputs, cancellationToken);
    }

    public void Reset()
    {
        Volatile.Write(ref _calls, 0);
        Volatile.Write(ref _inputs, 0);
        FailOnCall = null;
    }
}
