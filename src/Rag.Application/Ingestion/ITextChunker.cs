namespace Rag.Application.Ingestion;

public interface ITextChunker
{
    string ConfigurationHash { get; }

    IReadOnlyList<TextChunk> Chunk(string text);
}

public sealed record TextChunk(
    int Index,
    string Content,
    string ContentHash,
    int TokenCount,
    int StartOffset,
    int EndOffset);
