using System.IO.Compression;
using System.Net;
using System.Text.Json;
using ToolBox.Commands;
using ToolBox.Configuration;
using ToolBox.Installation;

namespace ToolBox.Tests;

public sealed class UrlTests
{
    [Fact]
    public async Task StableZipUrlTracksContentAndPreservesTheActiveVersionAfterFailure()
    {
        using var temp = new TestDirectory();
        const string url = "https://vendor.example/stable.zip";
        var manifest = MakeManifest("app/tool.exe", url);
        var feed = new Download(Archive("version one"));
        using var client = new HttpClient(feed);
        Task<InstallResult> Install(bool force = false) => Installer.InstallAsync(manifest, temp.Root,
            context => Url.PrepareAsync(context, url, client), force, default);

        var first = await Install();
        Assert.True(first.VersionChanged);
        Assert.Equal("version one", File.ReadAllText(Path.Combine(first.VersionDirectory, "app", "tool.exe")));
        Assert.True(File.Exists(Path.Combine(temp.Root, "bin", "tool.cmd")));
        var unchanged = await Install();
        Assert.False(unchanged.Changed);
        Assert.Equal(first.VersionDirectory, unchanged.VersionDirectory);
        var forced = await Install(force: true);
        Assert.True(forced.Changed);
        Assert.False(forced.VersionChanged);
        File.Delete(Path.Combine(forced.VersionDirectory, "app", "config.txt"));
        var repaired = await Install();
        Assert.True(repaired.Changed);
        Assert.False(repaired.VersionChanged);
        Assert.True(File.Exists(Path.Combine(repaired.VersionDirectory, "app", "config.txt")));

        feed.Bytes = Archive("version two");
        var second = await Install();
        Assert.True(second.VersionChanged);
        Assert.Equal("version two", File.ReadAllText(Path.Combine(second.VersionDirectory, "app", "tool.exe")));
        feed.Bytes = "not a ZIP"u8.ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Install());
        feed.Status = HttpStatusCode.NotFound;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Install());
        Assert.Equal(second.VersionDirectory, Installer.GetActiveDirectory(temp.Root, manifest.Id));
        Assert.Empty(Directory.GetFiles(temp.Root, "*.download", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DirectExecutableUsesTheUrlFilenameAndRejectsAnHtmlResponse()
    {
        using var temp = new TestDirectory();
        const string url = "https://vendor.example/my%20app.EXE?channel=stable";
        var manifest = MakeManifest("my app.EXE", url);
        var feed = new Download("MZfixture"u8.ToArray());
        using var client = new HttpClient(feed);
        Task<InstallResult> Install() => Installer.InstallAsync(manifest, temp.Root,
            context => Url.PrepareAsync(context, url, client), false, default);
        var result = await Install();
        Assert.Equal(feed.Bytes, File.ReadAllBytes(Path.Combine(result.VersionDirectory, "my app.EXE")));
        feed.Bytes = "<html>error</html>"u8.ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Install());
        Assert.Equal(result.VersionDirectory, Installer.GetActiveDirectory(temp.Root, manifest.Id));
    }

    [Theory]
    [InlineData("http://vendor.example/tool.zip")]
    [InlineData("https://user:pass@vendor.example/tool.zip")]
    [InlineData("https://vendor.example/download.html")]
    public async Task RegistryRejectsUnsupportedUrls(string url)
    {
        using var temp = new TestDirectory();
        var manifest = MakeManifest("tool.exe", url);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Installer.InstallAsync(manifest, temp.Root, CommandRegistry.Create()["Url"], false, default));
        Assert.IsType<InvalidDataException>(error.InnerException);
    }

    private static ToolManifest MakeManifest(string path, string url) =>
        new("sample", "Url", [new ToolBinary(path, "tool")], JsonSerializer.SerializeToElement(new { url }));

    private static byte[] Archive(string version)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var exe = new StreamWriter(zip.CreateEntry("app/tool.exe").Open())) exe.Write(version);
            using (var config = new StreamWriter(zip.CreateEntry("app/config.txt").Open())) config.Write("config");
        }
        return stream.ToArray();
    }

    private sealed class Download(byte[] bytes) : HttpMessageHandler
    {
        internal byte[] Bytes { get; set; } = bytes;
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new ByteArrayContent(Bytes) });
        }
    }
}
