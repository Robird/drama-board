namespace DramaBoard.Protocol.Tests;

public sealed class SubjectiveWorkspaceTests {
    [Fact]
    public void Constructor_NullText_Throws() {
        Assert.Throws<ArgumentNullException>(() => new SubjectiveWorkspace(null!));
    }

    [Fact]
    public void Constructor_EmptyText_IsAllowed() {
        SubjectiveWorkspace workspace = new(string.Empty);

        Assert.Equal(string.Empty, workspace.Text);
    }

    [Fact]
    public void Constructor_LongText_IsAllowed() {
        SubjectiveWorkspace workspace = new(new string('x', 100_000));

        Assert.Equal(100_000, workspace.Text.Length);
    }
}
