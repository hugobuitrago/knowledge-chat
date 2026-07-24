using Microsoft.Extensions.Options;
using Rag.Application.Providers;

namespace Rag.Infrastructure.Ingestion;

internal sealed class EmbeddingBatchExecutor : IDisposable
{
    private readonly IEmbeddingProvider _provider;
    private readonly EmbeddingMetadataOptions _options;
    private readonly SemaphoreSlim _concurrency;

    public EmbeddingBatchExecutor(
        IEmbeddingProvider provider,
        IOptions<EmbeddingMetadataOptions> options)
    {
        _provider = provider;
        _options = options.Value;
        _concurrency = new SemaphoreSlim(
            _options.MaxConcurrency,
            _options.MaxConcurrency);
    }

    public async ValueTask<EmbeddingBatch> GenerateAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken)
    {
        await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
            try
            {
                return await _provider
                    .GenerateAsync(inputs, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new EmbeddingProviderException(
                    "The embedding request timed out.",
                    isTransient: true,
                    exception);
            }
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public void Dispose()
    {
        _concurrency.Dispose();
    }
}
