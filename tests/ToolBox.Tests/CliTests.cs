using ToolBox.Commands;

namespace ToolBox.Tests;

[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection;

[Collection("Console")]
public sealed class CliTests
{
    [Fact]
    public async Task BatchContinuesAfterManifestCommandAndInstallationFailures()
    {
        using var directory = new TestDirectory();
        var home = directory.CreateDirectory("home");
        var manifests = Directory.CreateDirectory(Path.Combine(home, "etc")).FullName;
        var payload = directory.CreateDirectory("payload");
        File.WriteAllText(Path.Combine(payload, "fixture.exe"), "fixture");
        foreach (var name in new[] { "A-good", "D-install-error", "Z-good" })
            File.WriteAllText(Path.Combine(manifests, name + ".json"), "{\"cmd\":\"Fixture\",\"args\":{}}");
        File.WriteAllText(Path.Combine(manifests, "B-json-error.json"), "{broken");
        File.WriteAllText(Path.Combine(manifests, "C-command-error.json"), "{\"cmd\":\"Missing\",\"args\":{}}");
        var visited = new List<string>();
        Task<PrepareResult> Prepare(CommandContext context)
        {
            visited.Add(context.ToolId);
            if (context.ToolId == "D-install-error") throw new IOException("Fixture installation failed.");
            return Task.FromResult<PrepareResult>(context.InstalledDirectory is null ? new Prepared(payload) : new Unchanged());
        }
        var result = await Run([], new() { ["Fixture"] = Prepare }, home);
        Assert.Equal(1, result.Code);
        Assert.Equal(new[] { "A-good", "D-install-error", "Z-good" }, visited);
        Assert.Contains("2 installed, 0 unchanged, 3 failed", result.Output);
        foreach (var name in new[] { "B-json-error", "C-command-error", "D-install-error" }) Assert.Contains(name, result.Error);
        Assert.True(File.Exists(Path.Combine(home, "var", "tools", "Z-good", "installed")));

        result = await Run(["Z-good"], new() { ["Fixture"] = Prepare }, home);
        Assert.Equal(0, result.Code);
        Assert.Contains("0 installed, 1 unchanged, 0 failed", result.Output);
    }

    [Fact]
    public async Task CancellationStopsTheBatchInsteadOfBecomingAToolFailure()
    {
        using var directory = new TestDirectory();
        var manifests = directory.CreateDirectory("etc");
        foreach (var name in new[] { "A", "Z" })
            File.WriteAllText(Path.Combine(manifests, name + ".json"), "{\"cmd\":\"Fixture\",\"args\":{}}");
        using var cancellation = new CancellationTokenSource();
        var visited = new List<string>();
        Task<PrepareResult> Prepare(CommandContext context)
        {
            visited.Add(context.ToolId);
            cancellation.Cancel();
            context.CancellationToken.ThrowIfCancellationRequested();
            throw new Exception("Unreachable.");
        }
        var result = await Run([], new() { ["Fixture"] = Prepare }, directory.Root, cancellation.Token);
        Assert.Equal(130, result.Code);
        Assert.Equal(new[] { "A" }, visited);
    }

    [Fact]
    public async Task MissingDirectoryAndOccupiedGlobalLockFailBeforeInstalling()
    {
        using var directory = new TestDirectory();
        Assert.Equal(1, (await Run([], new(), directory.Root)).Code);
        var manifests = directory.CreateDirectory("etc");
        File.WriteAllText(Path.Combine(manifests, "Tool.json"), "{\"cmd\":\"Fixture\",\"args\":{}}");
        var tools = directory.CreateDirectory("var", "tools");
        using var held = new FileStream(Path.Combine(tools, ".install.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var invoked = false;
        Task<PrepareResult> Prepare(CommandContext context) { invoked = true; throw new Exception("Unreachable."); }
        Assert.Equal(1, (await Run([], new() { ["Fixture"] = Prepare }, directory.Root)).Code);
        Assert.False(invoked);
        Assert.Equal(2, (await Run(["--unknown"], new(), directory.Root)).Code);
    }

    private static async Task<(int Code, string Output, string Error)> Run(string[] args,
        Dictionary<string, ToolCommand> commands, string home, CancellationToken cancellation = default)
    {
        var stdout = Console.Out;
        var stderr = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var code = await Cli.RunAsync(args, commands, home, cancellation);
            return (code, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(stdout); Console.SetError(stderr); }
    }
}
