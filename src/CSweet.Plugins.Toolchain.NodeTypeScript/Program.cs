using CSweet.Agent.SDK;
using CSweet.Plugins.Toolchain.NodeTypeScript;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test", StringComparer.Ordinal))
{
    var result = await new AgentTestRuntime().ExecuteCapabilityAsync(
        new ToolchainServiceAgent(),
        ToolchainServiceAgent.DescribeCapability,
        new { });
    Console.WriteLine(result.Value);
    Environment.ExitCode = result.Succeeded ? 0 : 1;
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.AddCSweetAgent<ToolchainServiceAgent>();
await builder.Build().RunAsync();
