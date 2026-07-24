using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Rag.Infrastructure.Ingestion;

public static class ExponentialBackoff
{
    public static TimeSpan Calculate(
        Guid jobId,
        int attempt,
        TimeSpan baseDelay,
        TimeSpan maximumDelay)
    {
        if (jobId == Guid.Empty)
        {
            throw new ArgumentException("Job ID cannot be empty.", nameof(jobId));
        }

        if (attempt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        if (baseDelay <= TimeSpan.Zero || maximumDelay < baseDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(baseDelay));
        }

        double exponent = Math.Pow(2, Math.Min(attempt - 1, 30));
        double uncappedMilliseconds = baseDelay.TotalMilliseconds * exponent;
        double cappedMilliseconds = Math.Min(
            uncappedMilliseconds,
            maximumDelay.TotalMilliseconds);

        Span<byte> input = stackalloc byte[20];
        jobId.TryWriteBytes(input);
        BinaryPrimitives.WriteInt32LittleEndian(input[16..], attempt);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        double unitInterval = BinaryPrimitives.ReadUInt64LittleEndian(hash) /
            (double)ulong.MaxValue;
        double jitterFactor = 0.8 + (unitInterval * 0.4);

        return TimeSpan.FromMilliseconds(
            Math.Min(cappedMilliseconds * jitterFactor, maximumDelay.TotalMilliseconds));
    }
}
