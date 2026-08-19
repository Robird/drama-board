using System.Xml.Linq;

namespace DramaBoard.Spatial.Tests;

public sealed class DependencyGuardTests
{
    [Fact]
    public void SpatialProject_ReferencesOnlyKernelProject()
    {
        string repositoryRoot = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(repositoryRoot, "src", "Spatial", "Spatial.csproj"));
        string[] projectReferences =
        [
            .. project
                .Descendants("ProjectReference")
                .Select(element => element.Attribute("Include")?.Value)
                .Where(value => value is not null)
                .Select(value => value!),
        ];

        Assert.Equal(["..\\Kernel\\Kernel.csproj"], projectReferences);
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("Reference"));
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
