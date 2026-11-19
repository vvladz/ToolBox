using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ToolBox.Infrastructure;

internal sealed record GitSource(string SourceDirectory, string Commit, bool Changed);

internal static class Git
{
    // The caller holds the installation lock through sync, build and activation.
    internal static async Task<GitSource> SyncAsync(
        string repository,
        string branch,
        string checkoutDirectory,
        string installedCommit,
        RunLog log,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        checkoutDirectory = NormalizePath(checkoutDirectory);
        var parent = Path.GetDirectoryName(checkoutDirectory)
            ?? throw new ArgumentException("The checkout must have a parent directory.");
        Directory.CreateDirectory(parent);

        // Git records local clone sources as absolute paths.
        var localRepository = Directory.Exists(repository) || File.Exists(repository);
        if (localRepository)
            repository = NormalizePath(repository);

        async Task<string> Run(params string[] arguments) =>
            (await ProcessRunner.RunAsync(
                "git", arguments, parent, log, "git", captureOutput: true,
                cancellationToken: cancellationToken)).TrimEnd('\r', '\n');

        await Run("check-ref-format", $"refs/heads/{branch}");

        if (!Directory.Exists(checkoutDirectory) && !File.Exists(checkoutDirectory))
        {
            await Run("clone", "--origin", "origin", "--single-branch", "--no-tags",
                "--branch", branch, "--", repository, checkoutDirectory);
        }

        if (!Directory.Exists(Path.Combine(checkoutDirectory, ".git")))
            throw new InvalidOperationException(
                $"The source directory must be a dedicated Git clone: {checkoutDirectory}");

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var topLevel = await Run("-C", checkoutDirectory, "rev-parse", "--show-toplevel");
        if (!string.Equals(NormalizePath(topLevel), checkoutDirectory, comparison))
            throw new InvalidOperationException("The checkout resolves to a different repository root.");

        var origin = await Run("-C", checkoutDirectory, "config", "--get", "remote.origin.url");
        var originComparison = StringComparison.Ordinal;
        if (localRepository && (Directory.Exists(origin) || File.Exists(origin)))
        {
            origin = NormalizePath(origin);
            originComparison = comparison;
        }

        if (!string.Equals(origin, repository, originComparison))
            throw new InvalidOperationException(
                "The cached origin differs from the requested repository. " +
                "Use a separate source directory for another repository.");

        var dirty = await Run("-C", checkoutDirectory, "status", "--porcelain=v1",
            "--untracked-files=normal");
        if (dirty.Length != 0)
            throw new InvalidOperationException(
                $"The dedicated checkout contains local changes. Inspect it before retrying: {checkoutDirectory}");

        // Never use an old FETCH_HEAD after a failed fetch.
        await Run("-C", checkoutDirectory, "fetch", "--no-tags", "--no-recurse-submodules",
            "--write-fetch-head", "origin", $"refs/heads/{branch}");
        var commit = await Run("-C", checkoutDirectory, "rev-parse", "--verify", "FETCH_HEAD^{commit}");

        var tree = await Run("-C", checkoutDirectory, "ls-tree", "-r", commit);
        if (tree.Split('\n').Any(line => line.StartsWith("160000 ", StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "The Git helper does not initialize submodules. Add a submodule policy to this command.");

        await Run("-C", checkoutDirectory, "-c", "advice.detachedHead=false", "checkout",
            "--detach", "--no-recurse-submodules", commit);

        var changed = !string.Equals(commit, installedCommit, StringComparison.Ordinal);
        log.Info($"Git source {(changed ? "changed" : "unchanged")}: {commit}");
        return new GitSource(checkoutDirectory, commit, changed);
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
