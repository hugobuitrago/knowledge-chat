using System.Security.Cryptography;
using System.Text;

namespace Rag.Api.Security;

public static class ApiKeyHasher
{
    public const int HashSizeInBytes = 32;

    public const int MinimumPepperLength = 32;

    public const int MinimumSecretLength = 32;

    public static string HashSecret(string secret, string pepper) =>
        Convert.ToBase64String(ComputeHash(secret, pepper));

    public static bool Verify(
        string secret,
        string pepper,
        string expectedHash)
    {
        byte[] computedHash = ComputeHash(secret, pepper);
        Span<byte> configuredHash = stackalloc byte[HashSizeInBytes];
        bool decoded = Convert.TryFromBase64String(
            expectedHash,
            configuredHash,
            out int bytesWritten);

        return decoded &&
            bytesWritten == HashSizeInBytes &&
            CryptographicOperations.FixedTimeEquals(computedHash, configuredHash);
    }

    private static byte[] ComputeHash(string secret, string pepper)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(pepper);
        if (secret.Length < MinimumSecretLength)
        {
            throw new ArgumentException(
                $"API key secrets must contain at least {MinimumSecretLength} characters.",
                nameof(secret));
        }

        if (pepper.Length < MinimumPepperLength)
        {
            throw new ArgumentException(
                $"The API key pepper must contain at least {MinimumPepperLength} characters.",
                nameof(pepper));
        }

        return HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(pepper),
            Encoding.UTF8.GetBytes(secret));
    }
}
