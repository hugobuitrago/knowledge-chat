using Rag.Domain.Entities;
using Rag.Domain.Enums;

namespace Rag.UnitTests;

public sealed class KnowledgeBaseVersionArchivalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ready_or_active_version_can_be_archived(bool activate)
    {
        KnowledgeBaseVersion version = KnowledgeBaseVersion.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test-model",
            1536);
        version.MarkProcessing();
        version.MarkReady();
        if (activate)
        {
            version.Activate();
        }

        version.Archive();

        Assert.Equal(KnowledgeBaseVersionStatus.Archived, version.Status);
    }
}
