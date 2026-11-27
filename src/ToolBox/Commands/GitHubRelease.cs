using System.IO.Enumeration;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ToolBox.Infrastructure;

namespace ToolBox.Commands;

internal static class GitHubRelease
{
    private const string ReceiptName = ".release-info.json";
    private const string Builder = "github-release.v1";

    internal static Task<PrepareResult> PrepareAsync(CommandContext context)
    {
        JsonFields.Validate(context.Args, "GitHubRelease args",
            "repository", "assetPattern", "executableName", "archiveDirectory");
        return PrepareAsync(context,
            JsonFields.RequiredString(context.Args, "repository"),
            JsonFields.RequiredString(context.Args, "assetPattern"),
            JsonFields.OptionalString(context.Args, "executableName"),
            JsonFields.OptionalString(context.Args, "archiveDirectory"));
    }

    internal static async Task<PrepareResult> PrepareAsync(
        CommandContext context,
        string repository,
        string assetPattern,
        string? executableName = null,
        string? archiveDirectory = null,
        HttpClient? client = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(part => part.Length == 0 || part is "." or ".." ||
            part.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.')))
            throw new ArgumentException("The GitHub repository must be 'owner/repo'.", nameof(repository));
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPattern);
        if (assetPattern.IndexOfAny(['/', '\\']) >= 0 || assetPattern.Any(char.IsControl))
            throw new ArgumentException("The asset pattern must be a filename or filename mask, without a path.", nameof(assetPattern));
        if (executableName is not null)
        {
            Names.Validate(executableName, "executable name");
            if (!executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The executable name must end in .exe.", nameof(executableName));
        }
        archiveDirectory = archiveDirectory is null ? null : Names.RelativePath(archiveDirectory, "archive directory");
        repository = string.Join('/', parts).ToLowerInvariant();

        using var ownedClient = client is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : null;
        var http = client ?? ownedClient!;
        if (http.DefaultRequestHeaders.Authorization is not null)
            throw new ArgumentException("Use GITHUB_TOKEN instead of default Authorization headers on the HTTP client.", nameof(client));

        var apiRoot = $"https://api.github.com/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}";
        context.Log.Info($"Checking the latest stable GitHub release of {repository}.");
        using var response = await GetAsync(http, new Uri(apiRoot + "/releases/latest"), false, context.CancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"No accessible latest stable release was found for {repository}.");
        RequireSuccess(response, "release metadata");
        await using var metadataStream = await response.Content.ReadAsStreamAsync(context.CancellationToken);
        using var document = await JsonDocument.ParseAsync(metadataStream, cancellationToken: context.CancellationToken);
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean())
            throw new InvalidDataException("GitHub returned a draft or prerelease for the latest stable release.");

        var matches = release.GetProperty("assets").EnumerateArray()
            .Where(asset => FileSystemName.MatchesSimpleExpression(assetPattern,
                asset.GetProperty("name").GetString() ?? string.Empty, ignoreCase: true))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Asset pattern '{assetPattern}' matched {matches.Length} release assets; exactly one is required.");
        var asset = matches[0];
        var assetName = Names.Validate(asset.GetProperty("name").GetString()!, "release asset name");
        var extension = Path.GetExtension(assetName).ToLowerInvariant();
        if (extension is not ".exe" and not ".zip")
            throw new InvalidOperationException($"Unsupported release asset '{assetName}'; select an EXE or ZIP.");
        if (extension == ".zip" && executableName is not null)
            throw new ArgumentException("executableName applies only to a direct EXE asset.", nameof(executableName));
        if (extension == ".exe" && archiveDirectory is not null)
            throw new ArgumentException("archiveDirectory applies only to a ZIP asset.", nameof(archiveDirectory));
        if (asset.GetProperty("state").GetString() != "uploaded")
            throw new InvalidOperationException($"Release asset '{assetName}' is not ready for download.");

        var expected = new ReleaseInfo
        {
            Builder = Builder,
            Repository = repository,
            ReleaseId = release.GetProperty("id").GetInt64(),
            Tag = release.GetProperty("tag_name").GetString() ?? string.Empty,
            AssetId = asset.GetProperty("id").GetInt64(),
            AssetName = assetName,
            AssetUpdatedAt = asset.GetProperty("updated_at").GetDateTimeOffset(),
            AssetSize = asset.GetProperty("size").GetInt64(),
            AssetPattern = assetPattern,
            ExecutableName = extension == ".exe" ? executableName ?? assetName : null,
            ArchiveDirectory = archiveDirectory
        };
        if (expected.ReleaseId <= 0 || expected.AssetId <= 0 || expected.AssetSize <= 0 || expected.Tag.Length == 0)
            throw new InvalidDataException("GitHub returned incomplete release asset metadata.");
        var previous = await DownloadedPayload.ReadReceiptAsync<ReleaseInfo>(context, ReceiptName);
        if (!context.Force && previous is not null && context.InstalledDirectory is { } installedDirectory &&
            SameAsset(previous, expected) && DownloadedPayload.InstalledFilesExist(previous.Files, installedDirectory))
        {
            context.Log.Info($"Release asset {assetName} matches the active installation; download skipped.");
            return new Unchanged();
        }

        var payloadRoot = Path.Combine(context.WorkDirectory, "payload");
        var download = Path.Combine(context.WorkDirectory, "release-download" + extension);
        if (Path.Exists(payloadRoot) || Path.Exists(download))
            throw new InvalidOperationException("The release attempt must use a fresh work directory.");
        Directory.CreateDirectory(context.WorkDirectory);
        context.Log.Info($"Downloading {assetName} from release {expected.Tag}.");
        using (var assetResponse = await GetAsync(http,
            new Uri(apiRoot + $"/releases/assets/{expected.AssetId}"), true, context.CancellationToken))
        {
            RequireSuccess(assetResponse, "release asset download");
            await using var source = await assetResponse.Content.ReadAsStreamAsync(context.CancellationToken);
            await using var target = new FileStream(download, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous);
            await source.CopyToAsync(target, context.CancellationToken);
        }
        if (new FileInfo(download).Length != expected.AssetSize)
            throw new InvalidDataException("Downloaded asset size does not match GitHub metadata.");

        Directory.CreateDirectory(payloadRoot);
        string payload;
        if (extension == ".exe")
        {
            File.Move(download, Path.Combine(payloadRoot, expected.ExecutableName!));
            payload = payloadRoot;
        }
        else
        {
            using var archive = File.OpenRead(download);
            DownloadedPayload.ExtractZip(archive, payloadRoot, context.CancellationToken);
            payload = archiveDirectory is null ? payloadRoot : Path.Combine(payloadRoot, archiveDirectory);
            if (!Directory.Exists(payload))
                throw new DirectoryNotFoundException($"Archive directory '{archiveDirectory}' was not found.");
        }
        var files = DownloadedPayload.ListFiles(payload, ReceiptName);
        context.CancellationToken.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(Path.Combine(payload, ReceiptName),
            JsonSerializer.Serialize(expected with { Files = files }, DownloadedPayload.JsonOptions) + "\n",
            new UTF8Encoding(false), context.CancellationToken);
        context.Log.Info($"Prepared {files.Length} release file(s).");
        return new Prepared(payload, previous?.Repository != expected.Repository || previous.Tag != expected.Tag);
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, Uri uri, bool binary, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
                throw new InvalidDataException("GitHub download redirects must use HTTPS without user information.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Toolbox/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(binary ? "application/octet-stream" : "application/vnd.github+json"));
            if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) && uri.Port == 443)
            {
                request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
                var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN")?.Trim();
                if (!string.IsNullOrEmpty(token))
                {
                    if (token.Any(char.IsWhiteSpace)) throw new InvalidOperationException("GITHUB_TOKEN contains whitespace.");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
            }
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidDataException("GitHub sent a redirect without a location.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new HttpRequestException("GitHub sent too many redirects.");
    }

    private static void RequireSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub {operation} failed with HTTP {(int)response.StatusCode}.");
    }

    private static bool SameAsset(ReleaseInfo actual, ReleaseInfo expected) =>
        actual.Builder == expected.Builder && actual.Repository == expected.Repository &&
        actual.ReleaseId == expected.ReleaseId && actual.Tag == expected.Tag &&
        actual.AssetId == expected.AssetId && actual.AssetName == expected.AssetName &&
        actual.AssetUpdatedAt == expected.AssetUpdatedAt && actual.AssetSize == expected.AssetSize &&
        actual.AssetPattern == expected.AssetPattern && actual.ExecutableName == expected.ExecutableName &&
        actual.ArchiveDirectory == expected.ArchiveDirectory;

    private sealed record ReleaseInfo
    {
        public string Builder { get; init; } = string.Empty;
        public string Repository { get; init; } = string.Empty;
        public long ReleaseId { get; init; }
        public string Tag { get; init; } = string.Empty;
        public long AssetId { get; init; }
        public string AssetName { get; init; } = string.Empty;
        public DateTimeOffset AssetUpdatedAt { get; init; }
        public long AssetSize { get; init; }
        public string AssetPattern { get; init; } = string.Empty;
        public string? ExecutableName { get; init; }
        public string? ArchiveDirectory { get; init; }
        public PayloadFile[] Files { get; init; } = [];
    }
}
