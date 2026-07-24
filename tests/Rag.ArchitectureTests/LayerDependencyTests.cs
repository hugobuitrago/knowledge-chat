using System.Xml.Linq;

namespace Rag.ArchitectureTests;

public sealed class LayerDependencyTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedReferences =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["Rag.Domain"] = new HashSet<string>(StringComparer.Ordinal),
            ["Rag.Contracts"] = new HashSet<string>(StringComparer.Ordinal),
            ["Rag.Application"] = new HashSet<string>(
                ["Rag.Contracts", "Rag.Domain"],
                StringComparer.Ordinal),
            ["Rag.Infrastructure"] = new HashSet<string>(
                ["Rag.Application", "Rag.Contracts", "Rag.Domain"],
                StringComparer.Ordinal),
            ["Rag.Api"] = new HashSet<string>(
                ["Rag.Application", "Rag.Contracts", "Rag.Infrastructure"],
                StringComparer.Ordinal),
            ["Rag.Worker"] = new HashSet<string>(
                ["Rag.Application", "Rag.Contracts", "Rag.Infrastructure"],
                StringComparer.Ordinal),
        };

    [Fact]
    public void Production_projects_only_reference_allowed_layers()
    {
        string repositoryRoot = FindRepositoryRoot();

        foreach ((string projectName, IReadOnlySet<string> allowed) in AllowedReferences)
        {
            string projectFile = Path.Combine(
                repositoryRoot,
                "src",
                projectName,
                $"{projectName}.csproj");
            Assert.True(File.Exists(projectFile), $"Missing expected project: {projectFile}");

            XDocument project = XDocument.Load(projectFile);
            IEnumerable<string> references = project
                .Descendants("ProjectReference")
                .Select(reference => reference.Attribute("Include")?.Value)
                .Where(static include => !string.IsNullOrWhiteSpace(include))
                .Select(static include => Path.GetFileNameWithoutExtension(include!));

            foreach (string reference in references)
            {
                Assert.True(
                    allowed.Contains(reference),
                    $"{projectName} must not reference {reference}.");
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rag.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}

