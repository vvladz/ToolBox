using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ToolBox.Infrastructure;

namespace ToolBox.Commands;

internal static class Url
{
    private const string ReceiptName = ".url-info.json";
    private sealed record Receipt(string FileName, string Sha256, PayloadFile[] Files);

    internal static Task<PrepareResult> PrepareAsync(CommandContext context)
    {
        JsonFields.Validate(context.Args, "Url args", "url");
        return PrepareAsync(context, JsonFields.RequiredString(context.Args, "url"));
    }

    internal static async Task<PrepareResult> PrepareAsync(CommandContext context, string url, HttpClient? client = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Url requires an absolute HTTPS download URL without credentials or a fragment.");
        var fileName = Names.Validate(Uri.UnescapeDataString(uri.Segments[^1]), "download filename");
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not ".zip" and not ".exe")
            throw new InvalidDataException("The URL path must end in .zip or .exe.");

        var previous = await DownloadedPayload.ReadReceiptAsync<Receipt>(context, ReceiptName);
        using var ownedClient = client is null ? new HttpClient() : null;
        var http = client ?? ownedClient!;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Toolbox/1.0");
        context.Log.Info($"Downloading {fileName} from {uri.Host}.");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"Download failed with HTTP {(int)response.StatusCode}.");

        // Keep only the active and staged payloads. An unchanged daily check leaves no archive on disk.
        var temp = Path.Combine(context.ToolDirectory, context.AttemptId + ".download");
        await using var download = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await response.Content.CopyToAsync(download, context.CancellationToken);
        if (download.Length == 0 || response.Content.Headers.ContentLength is long size && size != download.Length)
            throw new InvalidDataException("The downloaded file is empty or incomplete.");
        download.Position = 0;
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(download, context.CancellationToken));
        var versionChanged = previous?.Sha256 != digest;
        if (!versionChanged && !context.Force && previous?.FileName == fileName &&
            context.InstalledDirectory is { } installed && DownloadedPayload.InstalledFilesExist(previous.Files, installed))
            return new Unchanged();

        var payload = Path.Combine(context.WorkDirectory, "payload");
        if (Path.Exists(payload)) throw new IOException("The download attempt requires a fresh payload directory.");
        Directory.CreateDirectory(payload);
        download.Position = 0;
        if (extension == ".zip")
            DownloadedPayload.ExtractZip(download, payload, context.CancellationToken);
        else
        {
            if (download.ReadByte() != 'M' || download.ReadByte() != 'Z')
                throw new InvalidDataException("The downloaded file has no Windows executable header.");
            download.Position = 0;
            await using var target = new FileStream(Path.Combine(payload, fileName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await download.CopyToAsync(target, context.CancellationToken);
        }
        var receipt = new Receipt(fileName, digest, DownloadedPayload.ListFiles(payload, ReceiptName));
        await File.WriteAllTextAsync(Path.Combine(payload, ReceiptName),
            JsonSerializer.Serialize(receipt, DownloadedPayload.JsonOptions) + "\n", context.CancellationToken);
        return new Prepared(payload, versionChanged);
    }
}
