using Rag.Application.Abstractions;

namespace Rag.Infrastructure.Persistence;

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

