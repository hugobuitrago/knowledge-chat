namespace Rag.Domain.Enums;

public enum IngestionJobStatus
{
    Queued,
    Running,
    Retrying,
    Completed,
    Failed,
    DeadLetter,
}

