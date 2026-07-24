using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Rag.Application.Providers;

namespace Rag.Infrastructure.Ingestion;

public sealed class DeterministicEmbeddingProvider : IEmbeddingProvider
{
    private readonly EmbeddingMetadataOptions _options;

    public DeterministicEmbeddingProvider(IOptions<EmbeddingMetadataOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    public DeterministicEmbeddingProvider(EmbeddingMetadataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public ValueTask<EmbeddingBatch> GenerateAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var vectors = new ReadOnlyMemory<float>[inputs.Count];
        for (int index = 0; index < inputs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrWhiteSpace(inputs[index]);
            vectors[index] = CreateVector(
                inputs[index],
                _options.Dimensions,
                cancellationToken);
        }

        return ValueTask.FromResult(
            new EmbeddingBatch(
                _options.Model,
                _options.Dimensions,
                vectors));
    }

    private static float[] CreateVector(
        string input,
        int dimensions,
        CancellationToken cancellationToken)
    {
        byte[] inputBytes = Encoding.UTF8.GetBytes(input);
        var vector = new float[dimensions];
        Span<byte> counterBytes = stackalloc byte[sizeof(int)];
        int dimension = 0;
        int counter = 0;
        while (dimension < dimensions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteInt32BigEndian(counterBytes, counter++);
            byte[] block = SHA256.HashData([.. inputBytes, .. counterBytes]);
            for (int offset = 0;
                 offset <= block.Length - sizeof(uint) && dimension < dimensions;
                 offset += sizeof(uint))
            {
                uint value = BinaryPrimitives.ReadUInt32BigEndian(
                    block.AsSpan(offset, sizeof(uint)));
                vector[dimension++] = (value / (float)uint.MaxValue * 2F) - 1F;
            }
        }

        double squaredLength = 0;
        foreach (float value in vector)
        {
            squaredLength += value * value;
        }

        float length = (float)Math.Sqrt(squaredLength);
        for (int index = 0; index < vector.Length; index++)
        {
            vector[index] /= length;
        }

        return vector;
    }
}
