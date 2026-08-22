using System.Xml.Linq;

namespace DramaBoard.Player.Agency.Tests.Architecture;

public sealed class DependencyGuardTests
{
    [Fact]
    public void PlayerAgencyProject_ReferencesOnlySpatialProject()
    {
        string repositoryRoot = FindRepositoryRoot();
        XDocument project = XDocument.Load(
            Path.Combine(repositoryRoot, "src", "Player.Agency", "Player.Agency.csproj"));
        string[] projectReferences =
        [
            .. project
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => value is not null)
                .Select(value => value!),
        ];

        Assert.Equal(["..\\Spatial\\Spatial.csproj"], projectReferences);
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("Reference"));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null &&
            !File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
        {
            current = current.Parent;
        }

        return current?.FullName ??
            throw new InvalidOperationException("Could not find the repository root.");
    }
}
