namespace ConnectionClue.Presentation.Tests;

public sealed class RenderingPolicyTests
{
    private static readonly string Source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "App.xaml.cs"));

    [Fact]
    public void The_app_renders_in_software_only_so_it_never_uses_the_gpu()
    {
        const string software = "RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;";
        int policy = Source.IndexOf(software, StringComparison.Ordinal);
        Assert.True(policy >= 0, "The app must force software rendering for the whole process.");
        Assert.True(policy < Source.IndexOf("new MainWindow(", StringComparison.Ordinal), "It must be set before any window exists.");
        Assert.DoesNotContain("RenderMode.Default", Source, StringComparison.Ordinal);
    }
}
