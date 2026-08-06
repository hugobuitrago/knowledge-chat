namespace Rag.Application.Retrieval;

public static class ReciprocalRankFusion
{
    public static IReadOnlyList<RetrievedChunk> Fuse(
        IReadOnlyList<RetrievalCandidate> vectorRanking,
        IReadOnlyList<RetrievalCandidate> lexicalRanking,
        int rankConstant)
    {
        ArgumentNullException.ThrowIfNull(vectorRanking);
        ArgumentNullException.ThrowIfNull(lexicalRanking);
        if (rankConstant <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rankConstant));
        }

        var scores = new Dictionary<Guid, Accumulator>();
        AddRanking(vectorRanking, rankConstant, scores);
        AddRanking(lexicalRanking, rankConstant, scores);
        return scores.Values
            .Select(static value => new RetrievedChunk(
                value.Candidate.ChunkId,
                value.Candidate.DocumentId,
                value.Candidate.FileName,
                value.Candidate.ChunkIndex,
                value.Candidate.StartOffset,
                value.Candidate.EndOffset,
                value.Candidate.Content,
                value.Score))
            .OrderByDescending(static chunk => chunk.Score)
            .ThenBy(static chunk => chunk.ChunkId)
            .ToArray();
    }

    private static void AddRanking(
        IReadOnlyList<RetrievalCandidate> ranking,
        int rankConstant,
        Dictionary<Guid, Accumulator> scores)
    {
        var seen = new HashSet<Guid>();
        for (int index = 0; index < ranking.Count; index++)
        {
            RetrievalCandidate candidate = ranking[index];
            if (!seen.Add(candidate.ChunkId))
            {
                continue;
            }

            double contribution = 1D / (rankConstant + index + 1D);
            if (scores.TryGetValue(candidate.ChunkId, out Accumulator? accumulator))
            {
                accumulator.Score += contribution;
            }
            else
            {
                scores.Add(candidate.ChunkId, new Accumulator(candidate, contribution));
            }
        }
    }

    private sealed class Accumulator(
        RetrievalCandidate candidate,
        double score)
    {
        public RetrievalCandidate Candidate { get; } = candidate;

        public double Score { get; set; } = score;
    }
}
