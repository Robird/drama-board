using System.Xml.Linq;

namespace DramaBoard.Spatial.Tests;

public sealed class DependencyGuardTests {
    [Fact]
    public void SpatialProject_ReferencesOnlyKernelAndDurableGraphRuntime() {
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
        Assert.Equal(["../../eng/DurableGraph.Source.props", "../../eng/DurableGraph.Source.targets"],
            project.Descendants("Import").Select(element => element.Attribute("Project")!.Value).ToArray());

        // Source integration must not make Persistence or build tools runtime dependencies.
        XDocument sourceReferences = XDocument.Load(Path.Combine(repositoryRoot, "eng", "DurableGraph.Source.props"));
        XElement[] references = sourceReferences.Descendants("ProjectReference").ToArray();
        Assert.Equal(["$(DurableGraphSourceRoot)/src/DurableGraph/DurableGraph.csproj"],
            references.Where(element => element.Attribute("ReferenceOutputAssembly")?.Value != "false")
                .Select(element => element.Attribute("Include")!.Value).ToArray());
        Assert.All(references.Where(element => element.Attribute("ReferenceOutputAssembly")?.Value == "false"),
            element => Assert.Equal("all", element.Attribute("PrivateAssets")?.Value));
        Assert.Empty(sourceReferences.Descendants("PackageReference"));
        Assert.Empty(sourceReferences.Descendants("Reference"));
    }

    private static string FindRepositoryRoot() {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props"))) {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
