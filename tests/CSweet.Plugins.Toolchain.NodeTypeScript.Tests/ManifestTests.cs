using CSweet.Agent.SDK;
using System.Text.Json;
using System.Xml.Linq;

namespace CSweet.Plugins.Toolchain.NodeTypeScript.Tests;

public sealed class ManifestTests
{
    [Fact]
    public async Task Manifest_IsValidAndMatchesAgent()
    {
        var root = RepositoryRoot();
        var path = Path.Combine(root, "csweet-plugin.json");

        var manifest = await AgentManifestLoader.LoadAsync(path, CancellationToken.None);
        var agent = new ToolchainServiceAgent();

        Assert.Equal(agent.AgentId, manifest.Id);
        Assert.Equal(agent.Version, manifest.Version);
        Assert.Equal(agent.Version, XDocument.Load(Path.Combine(root, manifest.Runtime.ProjectPath!))
            .Descendants("Version").Single().Value);
        Assert.Equal($"# {agent.Version}", File.ReadLines(Path.Combine(root, "releases", $"{agent.Version}.md")).First());
        using var definition = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "toolchains", "node-typescript.v1.json")));
        Assert.Contains(definition.RootElement.GetProperty("recipes").EnumerateArray(), x => x.GetProperty("key").GetString() == "phaser.web-2d.v1");
        Assert.Contains(ToolchainServiceAgent.DescribeCapability, manifest.Capabilities);
        Assert.True(File.Exists(Path.Combine(
            root,
            manifest.Runtime.ProjectPath!.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task ProviderMetadataMatchesPublishedToolchainAndResponseSchema()
    {
        var root = RepositoryRoot();
        var result = await new AgentTestRuntime().ExecuteCapabilityAsync(new ToolchainServiceAgent(),
            ToolchainServiceAgent.DescribeCapability, new { input = "describe" });
        Assert.True(result.Succeeded, result.Error);
        using var definition = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "toolchains", "node-typescript.v1.json")));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "csweet-plugin.json")));
        var metadata = result.Value!.Value;
        var tools = definition.RootElement.GetProperty("requiredExecutableVersions");
        Assert.Equal(tools.GetProperty("node").GetString(), metadata.GetProperty("node").GetString());
        Assert.Equal(tools.GetProperty("npm").GetString(), metadata.GetProperty("npm").GetString());
        Assert.Equal(definition.RootElement.GetProperty("key").GetString(), metadata.GetProperty("adapterKey").GetString());
        var schema = manifest.RootElement.GetProperty("provides")[0].GetProperty("outputSchema");
        Assert.Equal(schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).Order(),
            metadata.EnumerateObject().Select(x => x.Name).Order());
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(
                   directory.FullName, "CSweet.Plugins.Toolchain.NodeTypeScript.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
