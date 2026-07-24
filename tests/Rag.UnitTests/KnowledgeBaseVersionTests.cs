using Rag.Domain.Entities;
using Rag.Domain.Enums;

namespace Rag.UnitTests;

public sealed class KnowledgeBaseVersionTests
{
    [Fact]
    public void Version_can_only_be_activated_after_becoming_ready()
    {
        KnowledgeBaseVersion version = CreateVersion();

        Assert.Throws<InvalidOperationException>(version.Activate);

        version.MarkProcessing();
        version.MarkReady();
        version.Activate();

        Assert.Equal(KnowledgeBaseVersionStatus.Active, version.Status);
    }

    [Fact]
    public void Active_version_cannot_be_failed()
    {
        KnowledgeBaseVersion version = CreateVersion();
        version.MarkProcessing();
        version.MarkReady();
        version.Activate();

        Assert.Throws<InvalidOperationException>(version.MarkFailed);
    }

    private static KnowledgeBaseVersion CreateVersion() =>
        KnowledgeBaseVersion.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test-model",
            1536);
}

