using System.Xml.Linq;

namespace DramaBoard.Server.Tests;

public sealed class ApplicationBoundaryTests {
    [Fact]
    public void SolutionsContainExactlyTheCoreAndServerWithTheirTests() {
        string root = RepositoryRoot();
        string[] names = ["Kernel", "Spatial", "Protocol", "Player", "Decision.Validation", "Host", "Player.Agency", "Server"];
        string[] expected = names.SelectMany(name => new[] { $"src/{name}/{name}.csproj", $"tests/{name}.Tests/{name}.Tests.csproj" })
            .Order(StringComparer.Ordinal).ToArray();
        foreach (string solution in new[] { "DramaBoard.slnx", "DramaBoard.Local.slnx" }) {
            string[] actual = XDocument.Load(Path.Combine(root, solution)).Descendants("Project")
                .Select(project => project.Attribute("Path")!.Value.Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ServerReferencesOnlyItsSixApplicationDependencies() {
        string[] references = XDocument.Load(Path.Combine(RepositoryRoot(), "src", "Server", "Server.csproj"))
            .Descendants("ProjectReference").Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Decision.Validation", "Kernel", "Player", "Player.Agency", "Protocol", "Spatial" }, references);
    }

    private static string RepositoryRoot() {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DramaBoard.slnx"))) {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
    }
}
