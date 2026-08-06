using Rag.Application.Retrieval;

namespace Rag.UnitTests;

public sealed class ReciprocalRankFusionTests
{
    [Fact]
    public void Candidate_present_in_both_rankings_is_promoted_and_deduplicated()
    {
        RetrievalCandidate vectorOnly = CreateCandidate(Guid.NewGuid(), Guid.NewGuid());
        RetrievalCandidate shared = CreateCandidate(Guid.NewGuid(), Guid.NewGuid());
        RetrievalCandidate lexicalOnly = CreateCandidate(Guid.NewGuid(), Guid.NewGuid());

        IReadOnlyList<RetrievedChunk> result = ReciprocalRankFusion.Fuse(
            [vectorOnly, shared],
            [shared, lexicalOnly],
            rankConstant: 60);

        Assert.Equal(3, result.Count);
        Assert.Equal(shared.ChunkId, result[0].ChunkId);
        Assert.Equal(3, result.Select(static chunk => chunk.ChunkId).Distinct().Count());
        Assert.True(result[0].Score > result[1].Score);
    }

    [Fact]
    public void Duplicate_within_one_ranking_contributes_only_once()
    {
        RetrievalCandidate duplicate = CreateCandidate(Guid.NewGuid(), Guid.NewGuid());

        IReadOnlyList<RetrievedChunk> result = ReciprocalRankFusion.Fuse(
            [duplicate, duplicate],
            [],
            rankConstant: 10);

        RetrievedChunk chunk = Assert.Single(result);
        Assert.Equal(1D / 11D, chunk.Score, precision: 12);
    }

    [Fact]
    public void Rank_constant_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReciprocalRankFusion.Fuse([], [], rankConstant: 0));
    }

    private static RetrievalCandidate CreateCandidate(Guid chunkId, Guid documentId) =>
        new(
            chunkId,
            documentId,
            "source.txt",
            ChunkIndex: 0,
            StartOffset: 0,
            EndOffset: 4,
            Content: "test",
            StrategyScore: 1D);
}
