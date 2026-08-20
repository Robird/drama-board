namespace DramaBoard.Kernel.Tests;

public sealed class AssemblyDependencyTests
{
    [Fact]
    public void Kernel_DoesNotReferencePlayerHostOrProtocolAssemblies()
    {
        string[] forbidden =
        [
            "DramaBoard.Decision.Validation",
            "DramaBoard.Host",
            "DramaBoard.Player",
            "DramaBoard.Player.Llm",
            "DramaBoard.Protocol",
        ];
        string[] references = typeof(DramaBoard.Kernel.Time.ModelTime).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, forbidden.Contains);
    }
}
