using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Rag.Application.Ingestion;

namespace Rag.Infrastructure.Ingestion;

public sealed partial class ParagraphTextChunker : ITextChunker
{
    private const string AlgorithmVersion = "paragraph-sentence-v1";
    private readonly int _maxTokens;
    private readonly int _overlapTokens;

    public ParagraphTextChunker(IOptions<ChunkingOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    public ParagraphTextChunker(ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maxTokens = options.MaxTokens;
        _overlapTokens = options.OverlapTokens;
        if (_overlapTokens >= _maxTokens)
        {
            throw new ArgumentException(
                "Chunk overlap must be smaller than the maximum chunk size.",
                nameof(options));
        }

        ConfigurationHash = Hash(
            $"{AlgorithmVersion}|max={_maxTokens}|overlap={_overlapTokens}");
    }

    public string ConfigurationHash { get; }

    public IReadOnlyList<TextChunk> Chunk(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return [];
        }

        MatchCollection matches = TokenRegex().Matches(normalized);
        if (matches.Count == 0)
        {
            return [];
        }

        var chunks = new List<TextChunk>();
        int startToken = 0;
        while (startToken < matches.Count)
        {
            int endToken = FindEndToken(normalized, matches, startToken);
            Match first = matches[startToken];
            Match last = matches[endToken - 1];
            int startOffset = first.Index;
            int endOffset = last.Index + last.Length;
            string content = normalized[startOffset..endOffset];
            chunks.Add(
                new TextChunk(
                    chunks.Count,
                    content,
                    Hash(content),
                    endToken - startToken,
                    startOffset,
                    endOffset));

            if (endToken == matches.Count)
            {
                break;
            }

            int nextStart = endToken - _overlapTokens;
            startToken = nextStart > startToken ? nextStart : startToken + 1;
        }

        return chunks;
    }

    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string canonical = text
            .TrimStart('\uFEFF')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC);
        var normalized = new StringBuilder(canonical.Length);
        bool pendingSpace = false;
        int consecutiveNewlines = 0;

        foreach (char character in canonical)
        {
            if (character == '\n')
            {
                pendingSpace = false;
                if (normalized.Length > 0 &&
                    normalized[^1] != '\n' &&
                    normalized[^1] == ' ')
                {
                    normalized.Length--;
                }

                consecutiveNewlines++;
                if (normalized.Length > 0 && consecutiveNewlines <= 2)
                {
                    normalized.Append('\n');
                }

                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                pendingSpace = normalized.Length > 0 && normalized[^1] != '\n';
                continue;
            }

            if (pendingSpace)
            {
                normalized.Append(' ');
                pendingSpace = false;
            }

            consecutiveNewlines = 0;
            normalized.Append(character);
        }

        return normalized.ToString().Trim();
    }

    private int FindEndToken(
        string text,
        MatchCollection tokens,
        int startToken)
    {
        int limit = Math.Min(startToken + _maxTokens, tokens.Count);
        if (limit == tokens.Count)
        {
            return limit;
        }

        int minimumPreferredSize = startToken + (_maxTokens / 2);
        int paragraphBoundary = -1;
        int sentenceBoundary = -1;
        for (int candidate = minimumPreferredSize; candidate <= limit; candidate++)
        {
            if (candidate >= tokens.Count)
            {
                break;
            }

            Match previous = tokens[candidate - 1];
            Match next = tokens[candidate];
            ReadOnlySpan<char> gap = text.AsSpan(
                previous.Index + previous.Length,
                next.Index - previous.Index - previous.Length);
            if (gap.Contains("\n\n", StringComparison.Ordinal))
            {
                paragraphBoundary = candidate;
            }
            else if (IsSentenceTerminator(previous.Value) &&
                     gap.Length > 0)
            {
                sentenceBoundary = candidate;
            }
        }

        return paragraphBoundary >= 0
            ? paragraphBoundary
            : sentenceBoundary >= 0
                ? sentenceBoundary
                : limit;
    }

    private static bool IsSentenceTerminator(string token) =>
        token is "." or "!" or "?";

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    [GeneratedRegex(
        @"[\p{L}\p{M}\p{N}_]+(?:['’\-][\p{L}\p{M}\p{N}_]+)*|[^\s]",
        RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
