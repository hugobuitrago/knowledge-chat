namespace Rag.Domain.Common;

internal static class DomainGuard
{
    public static string Required(string value, int maximumLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        string trimmed = value.Trim();
        if (trimmed.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"Value cannot exceed {maximumLength} characters.");
        }

        return trimmed;
    }

    public static string Sha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException(
                "Value must be a 64-character hexadecimal SHA-256 hash.",
                parameterName);
        }

        return value.ToLowerInvariant();
    }

    public static Guid Required(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Value cannot be an empty GUID.", parameterName);
        }

        return value;
    }
}

