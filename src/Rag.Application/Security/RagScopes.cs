namespace Rag.Application.Security;

public static class RagScopes
{
    public const string Admin = "rag.admin";

    public const string Ingest = "rag.ingest";

    public const string Retrieve = "rag.retrieve";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [Admin, Ingest, Retrieve],
        StringComparer.Ordinal);
}
