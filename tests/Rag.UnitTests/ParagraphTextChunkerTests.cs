using System.Security.Cryptography;
using System.Text;
using Rag.Application.Ingestion;
using Rag.Infrastructure.Ingestion;

namespace Rag.UnitTests;

public sealed class ParagraphTextChunkerTests
{
    [Fact]
    public void Normalization_is_canonical_and_deterministic()
    {
        const string input =
            "\uFEFF  Café\t com   espaços.\r\n\r\n\r\nSegundo\u00A0parágrafo.  ";

        string first = ParagraphTextChunker.Normalize(input);
        string second = ParagraphTextChunker.Normalize(input);

        Assert.Equal("Café com espaços.\n\nSegundo parágrafo.", first);
        Assert.Equal(first, second);
        Assert.True(first.IsNormalized(NormalizationForm.FormC));
    }

    [Fact]
    public void Chunking_prefers_paragraph_boundaries_and_respects_the_limit()
    {
        ParagraphTextChunker chunker = CreateChunker(maxTokens: 10, overlapTokens: 2);
        const string input =
            "one two three four five.\n\nsix seven eight nine ten eleven twelve.";

        IReadOnlyList<TextChunk> chunks = chunker.Chunk(input);

        Assert.True(chunks.Count >= 2);
        Assert.EndsWith("five.", chunks[0].Content, StringComparison.Ordinal);
        Assert.All(chunks, chunk => Assert.InRange(chunk.TokenCount, 1, 10));
        Assert.All(chunks, chunk => Assert.False(string.IsNullOrWhiteSpace(chunk.Content)));
    }

    [Fact]
    public void Chunk_positions_and_hashes_reference_normalized_content()
    {
        ParagraphTextChunker chunker = CreateChunker(maxTokens: 8, overlapTokens: 2);
        string normalized = ParagraphTextChunker.Normalize(
            "First sentence.  Second sentence is longer. Third sentence.");

        IReadOnlyList<TextChunk> first = chunker.Chunk(normalized);
        IReadOnlyList<TextChunk> second = chunker.Chunk(normalized);

        Assert.Equal(first, second);
        foreach (TextChunk chunk in first)
        {
            Assert.Equal(
                chunk.Content,
                normalized[chunk.StartOffset..chunk.EndOffset]);
            Assert.Equal(Hash(chunk.Content), chunk.ContentHash);
        }
    }

    private static ParagraphTextChunker CreateChunker(
        int maxTokens,
        int overlapTokens) =>
        new(
            new ChunkingOptions
            {
                MaxTokens = maxTokens,
                OverlapTokens = overlapTokens,
            });

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
