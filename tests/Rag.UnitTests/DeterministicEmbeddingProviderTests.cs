using Rag.Application.Providers;
using Rag.Infrastructure.Ingestion;

namespace Rag.UnitTests;

public sealed class DeterministicEmbeddingProviderTests
{
    [Fact]
    public async Task Same_input_produces_the_same_normalized_vector()
    {
        var options =
            new EmbeddingMetadataOptions
            {
                Provider = "Deterministic",
                Model = "test-model",
                Dimensions = 1536,
            };
        var provider = new DeterministicEmbeddingProvider(options);

        EmbeddingBatch first = await provider.GenerateAsync(
            ["identical text"],
            CancellationToken.None);
        EmbeddingBatch second = await provider.GenerateAsync(
            ["identical text"],
            CancellationToken.None);

        Assert.Equal("test-model", first.Model);
        Assert.Equal(1536, first.Dimensions);
        Assert.Equal(first.Vectors[0].ToArray(), second.Vectors[0].ToArray());
        double length = Math.Sqrt(
            first.Vectors[0].Span.ToArray().Sum(value => value * value));
        Assert.InRange(length, 0.9999, 1.0001);
    }

    [Fact]
    public async Task Batch_preserves_input_order()
    {
        var provider = new DeterministicEmbeddingProvider(
            new EmbeddingMetadataOptions
            {
                Provider = "Deterministic",
                Model = "test-model",
                Dimensions = 1536,
            });

        EmbeddingBatch batch = await provider.GenerateAsync(
            ["alpha", "beta", "alpha"],
            CancellationToken.None);

        Assert.Equal(3, batch.Vectors.Count);
        Assert.Equal(batch.Vectors[0].ToArray(), batch.Vectors[2].ToArray());
        Assert.NotEqual(batch.Vectors[0].ToArray(), batch.Vectors[1].ToArray());
    }
}
