using ToolBox.Configuration;

namespace ToolBox.Tests;

public sealed class ManifestTests
{
    [Fact]
    public void ArrayUsesExecutableNamesAndPreservesArguments()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(Path.Combine(directory.Root, "Tool.json"), """
            { "cmd": "DotNet", "bin": ["app\\Tool.exe"], "args": { "branch": "main" } }
            """);

        var manifest = Assert.Single(ManifestReader.ReadAll(directory.Root, "tool"));

        Assert.Equal(new ToolBinary("app/Tool.exe", "Tool"), Assert.Single(manifest.Bin));
        Assert.Equal("main", manifest.Args.GetProperty("branch").GetString());
    }

    [Fact]
    public void AliasesSupportSharedTargetsAndMatchingExecutableNames()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(Path.Combine(directory.Root, "Tool.json"), """
            { "cmd": "Fixture", "bin": { "one": "left/Tool.exe", "short": "left/Tool.exe", "two": "right/Tool.exe" }, "args": {} }
            """);

        var manifest = Assert.Single(ManifestReader.ReadAll(directory.Root, null));

        Assert.Equal(new[]
        {
            new ToolBinary("left/Tool.exe", "one"),
            new ToolBinary("left/Tool.exe", "short"),
            new ToolBinary("right/Tool.exe", "two")
        }, manifest.Bin);
    }

    [Theory]
    [InlineData("{\"tool\":\"a.exe\",\"TOOL\":\"b.exe\"}")]
    [InlineData("{\"tool\":\"a.exe\",\"tool\":\"b.exe\"}")]
    [InlineData("{\"../outside\":\"a.exe\"}")]
    [InlineData("{\"tool\":\"../outside.exe\"}")]
    [InlineData("{\"tool\":\"a.dll\"}")]
    [InlineData("{\"tool\":42}")]
    [InlineData("[\"left/Tool.exe\",\"right/Tool.exe\"]")]
    public void InvalidAliasesAndPathsAreRejected(string bin)
    {
        using var directory = new TestDirectory();
        File.WriteAllText(Path.Combine(directory.Root, "Tool.json"), "{\"cmd\":\"Fixture\",\"args\":{},\"bin\":" + bin + "}");

        Assert.Throws<InvalidDataException>(() => ManifestReader.ReadAll(directory.Root, null));
    }
}
