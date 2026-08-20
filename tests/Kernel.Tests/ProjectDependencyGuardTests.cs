using System.Xml.Linq;

namespace DramaBoard.Kernel.Tests;

public sealed class ProjectDependencyGuardTests
{
    public static TheoryData<string, string[]> AllowedReferences =>
        new()
        {
            { "Kernel", [] },
            { "Protocol", [] },
            { "Decision.Validation", ["Protocol"] },
            { "Player", ["Kernel", "Protocol"] },
            { "Host", ["Decision.Validation", "Kernel", "Player", "Protocol"] },
            { "Player.Llm", ["Player", "Protocol"] },
        };

    [Theory]
    [MemberData(nameof(AllowedReferences))]
    public void CoreProject_HasOnlyAllowedDirectProjectReferences(
        string projectName,
        string[] expectedReferences)
    {
        string repositoryRoot = FindRepositoryRoot();
        string projectPath = Path.Combine(
            repositoryRoot,
            "src",
            projectName,
            $"{projectName}.csproj");
        XDocument project = XDocument.Load(projectPath);
        string[] actualReferences =
        [
            .. project
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => value is not null)
                .Select(value => Path.GetFileNameWithoutExtension(value!))
                .OrderBy(value => value, StringComparer.Ordinal),
        ];

        Assert.Equal(
            expectedReferences.OrderBy(value => value, StringComparer.Ordinal),
            actualReferences);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
