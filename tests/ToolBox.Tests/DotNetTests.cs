using System.Text.Json;
using ToolBox.Commands;
using ToolBox.Configuration;
using ToolBox.Infrastructure;
using ToolBox.Installation;

namespace ToolBox.Tests;

public sealed class DotNetTests
{
    [Fact]
    public async Task PublishesRunnableProjectDirectoriesAndSkipsUnchangedBuilds()
    {
        using var directory = new TestDirectory();
        using var log = new RunLog(Path.Combine(directory.Root, "fixture.log"));
        var repository = directory.CreateDirectory("repository");
        var home = directory.CreateDirectory("home with spaces");
        string[] projectNames = ["ToolOne", "ToolTwo"];
        File.WriteAllText(Path.Combine(repository, "global.json"), """
            { "sdk": { "version": "11.0.100", "rollForward": "latestMajor", "allowPrerelease": false } }
            """);
        File.WriteAllText(Path.Combine(repository, "NuGet.Config"),
            "<configuration><packageSources><clear /></packageSources></configuration>");
        File.WriteAllText(Path.Combine(repository, ".gitignore"), "**/bin/\n**/obj/\n");
        foreach (var name in projectNames)
        {
            var project = Path.Combine(repository, name);
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, name + ".csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net11.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                  <ItemGroup>
                    <Content Include="message.txt" CopyToPublishDirectory="Always" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(project, "Program.cs"),
                "Console.WriteLine(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, \"message.txt\")));");
            File.WriteAllText(Path.Combine(project, "message.txt"), name);
        }

        Task<string> Git(params string[] arguments) =>
            ProcessRunner.RunAsync("git", arguments, repository, log, "fixture-git");
        await Git("init", "--initial-branch=main");
        await Git("add", ".");
        await Git("-c", "user.name=Toolbox Tests", "-c", "user.email=toolbox-tests@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "-m", "Create publish fixture");

        var manifest = new ToolManifest("Tools", "DotNet",
            projectNames.Select(name => new ToolBinary($"{name}/{name}.exe", name.ToLowerInvariant())).ToArray(),
            JsonSerializer.SerializeToElement(new
            {
                repository,
                branch = "main",
                projects = projectNames.Select(name => $"{name}/{name}.csproj").ToArray(),
                runtime = "win-x64"
            }));
        var installed = await Installer.InstallAsync(manifest, home, DotNet.PrepareAsync, false, default);

        Assert.True(installed.Changed);
        foreach (var name in projectNames)
        {
            var output = Path.Combine(installed.VersionDirectory, name);
            foreach (var extension in new[] { ".exe", ".dll", ".deps.json", ".runtimeconfig.json" })
                Assert.True(File.Exists(Path.Combine(output, name + extension)), $"Missing {name}{extension}");
            Assert.Equal(name, File.ReadAllText(Path.Combine(output, "message.txt")));
            Assert.Equal(name, await ProcessRunner.RunAsync(Path.Combine(output, name + ".exe"), [],
                directory.Root, log, "published-tool", captureOutput: true));
        }

        var unchanged = await Installer.InstallAsync(manifest, home, DotNet.PrepareAsync, false, default);
        Assert.False(unchanged.Changed);
        Assert.Equal(installed.VersionDirectory, unchanged.VersionDirectory);
    }
}
