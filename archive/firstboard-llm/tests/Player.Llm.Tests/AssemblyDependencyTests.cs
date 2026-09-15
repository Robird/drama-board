namespace DramaBoard.Player.Llm.Tests;

public sealed class AssemblyDependencyTests
{
    [Fact]
    public void PlayerLlm_DoesNotReferenceHostOrKernelAssemblies()
    {
        string[] forbidden =
        [
            "DramaBoard.Host",
            "DramaBoard.Kernel",
        ];
        string[] references = typeof(LlmPlayerDriver).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, forbidden.Contains);
    }
}
