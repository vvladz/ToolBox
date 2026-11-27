using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ToolBox.Commands;
using ToolBox.Configuration;

namespace ToolBox.Tests;

[Collection("Console")]
public sealed class EventTests
{
    [Fact]
    public async Task ReleaseEventsRunTheActiveExecutableOnFirstInstallAndNewTagOnly()
    {
        using var directory = new TestDirectory();
        var manifests = directory.CreateDirectory("etc");
        var file = Path.Combine(manifests, "Tool.json");
        File.WriteAllText(file, """
            {
              "cmd": "GitHubRelease", "bin": { "worker": "fixture.exe" }, "args": {},
              "autostart": ["worker", "/d", "/c", "echo signed-in"],
              "update": ["WORKER", "/d", "/c", "echo updated & echo diagnostic 1>&2"]
            }
            """);
        var releases = new FixtureReleases();
        using var http = new HttpClient(releases);
        var commands = new Dictionary<string, ToolCommand>
        {
            ["GitHubRelease"] = context => GitHubRelease.PrepareAsync(context, "example/tool", "fixture.exe", client: http)
        };
        Task<int> Run(params string[] args) => Cli.RunAsync(args, commands, directory.Root);
        string[] Logs(string name) => Directory.GetFiles(Path.Combine(directory.Root, "var", "log", "Tool"), $"*-{name}-*.log");

        Assert.Equal(0, await Run());
        var first = File.ReadAllText(Assert.Single(Logs("update")));
        Assert.Contains("[update:stdout] updated", first);
        Assert.Contains("[update:stderr] diagnostic", first);
        Assert.Contains("Exit code: 0", first);
        Assert.Equal(0, await Run());
        Assert.Equal(0, await Run("--force"));
        Assert.Single(Logs("update"));
        releases.Version++;
        Assert.Equal(0, await Run("--force"));
        Assert.Equal(2, Logs("update").Length);
        var requests = releases.Requests;
        Assert.Equal(0, await Run("--autostart"));
        Assert.Equal(requests, releases.Requests);
        Assert.Contains("[autostart:stdout] signed-in", File.ReadAllText(Assert.Single(Logs("autostart"))));
        Assert.Equal(2, Logs("update").Length);

        // A missing active version fails only that manifest. The later Tool still runs.
        File.Copy(file, Path.Combine(manifests, "A-Missing.json"));
        Assert.Equal(1, await Run("--autostart"));
        Assert.Equal(2, Logs("autostart").Length);
        File.WriteAllText(file, File.ReadAllText(file).Replace("echo signed-in", "exit 7"));
        Assert.Equal(1, await Run("Tool", "--autostart"));
        Assert.Contains(Logs("autostart"), path => File.ReadAllText(path).Contains("Exit code: 7"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"unknown\"]")]
    [InlineData("[\"tool\",42]")]
    [InlineData("null")]
    public void RejectsMalformedOrUnknownEventCommand(string command)
    {
        using var directory = new TestDirectory();
        var file = Path.Combine(directory.Root, "Tool.json");
        File.WriteAllText(file, """{"cmd":"Fixture","args":{},"bin":["tool.exe"],"autostart":""" + command + "}");
        Assert.Throws<InvalidDataException>(() => ManifestReader.Read(file));
    }

    private sealed class FixtureReleases : HttpMessageHandler
    {
        private readonly byte[] binary = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
        internal int Version = 1;
        internal int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            HttpContent content = request.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)
                ? JsonContent.Create(new
                {
                    id = Version, tag_name = $"v1.0.{Version}", draft = false, prerelease = false,
                    assets = new[] { new { id = Version, name = "fixture.exe", updated_at = "2026-01-01T00:00:00Z",
                        state = "uploaded", size = binary.Length } }
                })
                : new ByteArrayContent(binary);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
