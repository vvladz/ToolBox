using System.Text;
using System.Text.Json;
using ToolBox.Commands;
using ToolBox.Configuration;
using ToolBox.Installation;

namespace ToolBox.Tests;

public sealed class InstallerTests
{
    private static ToolManifest CreateManifest(params ToolBinary[] binaries) =>
        new("Tool", "Fixture", binaries, JsonSerializer.SerializeToElement(new { }));

    [Fact]
    public async Task UpdateSwitchesVersionWithoutRewritingLaunchers()
    {
        using var directory = new TestDirectory();
        var home = directory.CreateDirectory("home with spaces");
        var payload = directory.CreateDirectory("payload");
        Directory.CreateDirectory(Path.Combine(payload, "app", "data"));
        File.WriteAllText(Path.Combine(payload, "app", "Tool.exe"), "fixture executable");
        var data = Path.Combine(payload, "app", "data", "settings.txt");
        File.WriteAllText(data, "first version");
        var manifest = CreateManifest(new("app/Tool.exe", "tool"), new("app/Tool.exe", "short"));
        var selectedDirectories = new List<string?>();
        Task<PrepareResult> Prepare(CommandContext context)
        {
            selectedDirectories.Add(context.InstalledDirectory);
            return Task.FromResult<PrepareResult>(new Prepared(payload));
        }

        var first = await Installer.InstallAsync(manifest, home, Prepare, false, default);
        var launcher = Path.Combine(home, "bin", "short.cmd");
        var launcherContent = File.ReadAllText(launcher);
        var launcherWriteTime = File.GetLastWriteTimeUtc(launcher);
        File.WriteAllText(data, "second version");
        var second = await Installer.InstallAsync(manifest, home, Prepare, false, default);

        Assert.Null(selectedDirectories[0]);
        Assert.Equal(first.VersionDirectory, selectedDirectories[1]);
        Assert.NotEqual(first.VersionDirectory, second.VersionDirectory);
        Assert.Equal("first version", File.ReadAllText(Path.Combine(first.VersionDirectory, "app", "data", "settings.txt")));
        Assert.Equal("second version", File.ReadAllText(Path.Combine(second.VersionDirectory, "app", "data", "settings.txt")));
        Assert.Equal(launcherContent, File.ReadAllText(launcher));
        Assert.Equal(launcherWriteTime, File.GetLastWriteTimeUtc(launcher));
        Assert.Equal(Encoding.UTF8.GetBytes(Path.GetFileName(second.VersionDirectory) + "\n"),
            File.ReadAllBytes(Path.Combine(home, "tools", "Tool", "installed")));
        Assert.All(new DirectoryInfo(home).EnumerateFileSystemInfos("*", SearchOption.AllDirectories),
            entry => Assert.False(entry.Attributes.HasFlag(FileAttributes.ReparsePoint)));
    }

    [Fact]
    public async Task FailedPreparationPreservesInstalledVersion()
    {
        using var directory = new TestDirectory();
        var home = directory.CreateDirectory("home");
        var payload = directory.CreateDirectory("payload");
        File.WriteAllText(Path.Combine(payload, "Tool.exe"), "first version");
        var manifest = CreateManifest(new ToolBinary("Tool.exe", "tool"));
        Task<PrepareResult> Prepare(CommandContext _) => Task.FromResult<PrepareResult>(new Prepared(payload));
        var first = await Installer.InstallAsync(manifest, home, Prepare, false, default);
        var marker = Path.Combine(home, "tools", "Tool", "installed");
        var selected = File.ReadAllText(marker);
        File.Delete(Path.Combine(payload, "Tool.exe"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Installer.InstallAsync(manifest, home, Prepare, false, default));

        Assert.Equal(selected, File.ReadAllText(marker));
        Assert.Equal("first version", File.ReadAllText(Path.Combine(first.VersionDirectory, "Tool.exe")));
    }

    [Fact]
    public async Task UnchangedRestoresAnAliasAndPreservesForeignFiles()
    {
        using var directory = new TestDirectory();
        var home = directory.CreateDirectory("home");
        var payload = directory.CreateDirectory("payload");
        File.WriteAllText(Path.Combine(payload, "Tool.exe"), "fixture executable");
        var manifest = CreateManifest(new ToolBinary("Tool.exe", "tool"));
        var first = await Installer.InstallAsync(manifest, home,
            _ => Task.FromResult<PrepareResult>(new Prepared(payload)), false, default);
        var launcher = Path.Combine(home, "bin", "tool.cmd");
        File.Delete(launcher);
        Task<PrepareResult> UnchangedCommand(CommandContext _) => Task.FromResult<PrepareResult>(new Unchanged());

        var unchanged = await Installer.InstallAsync(manifest, home, UnchangedCommand, false, default);

        Assert.False(unchanged.Changed);
        Assert.Equal(first.VersionDirectory, unchanged.VersionDirectory);
        Assert.True(File.Exists(launcher));
        File.WriteAllText(launcher, "foreign launcher");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Installer.InstallAsync(manifest, home, UnchangedCommand, false, default));
        Assert.Equal("foreign launcher", File.ReadAllText(launcher));
    }
}
