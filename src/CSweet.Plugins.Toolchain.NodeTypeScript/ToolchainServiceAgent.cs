using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Plugins.Toolchain.NodeTypeScript;

public sealed class ToolchainServiceAgent : CSweetAgentBase, IAgentConnectedService
{
    public const string DescribeCapability = "toolchain.node-typescript.execute.v1";
    public override string AgentId => "com.csweet.toolchain.node-typescript";
    public override string Version => "1.0.2";

    protected override Task<AgentWorkResult> ExecuteCapabilityCoreAsync(
        AgentCapabilityRequest request, AgentRuntimeContext context, CancellationToken cancellationToken) =>
        Task.FromResult(request.Capability == DescribeCapability
            ? AgentWorkResult.Success(new { adapterKey = "node-typescript.v1", recipes = new[] { "phaser.web-2d.v1", "babylon.web-3d.v1" }, node = "24.15.0", npm = "11.12.1" })
            : AgentWorkResult.Failure($"Capability '{request.Capability}' is not supported."));

    public override async Task HandleEventAsync(AgentEventEnvelope message, AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("CSWEET_BUILD_ID") is not null) return;
        if (message.EventType != WorkstreamEventNames.BuildRequestedV1) return;
        var resourceEvent = DeserializePayload<GenericResourceEvent>(message.Data);
        if (resourceEvent is null || resourceEvent.AggregateId == Guid.Empty) return;
        var builds = await context.Platform.ReadBuildsAsync(new ReadBuildV2Request(resourceEvent.AggregateId), cancellationToken);
        var build = builds.SingleOrDefault();
        if (build is null || build.Status != DeliveryBuildStatuses.Queued ||
            build.RecipeKey is not ("phaser.web-2d.v1" or "babylon.web-3d.v1")) return;
        var claimed = await context.Platform.ClaimBuildAsync(new ClaimBuildRequest(build.Id, build.Revision,
            TimeSpan.FromMinutes(10), $"claim:{build.Id:N}:{build.Attempt + 1}"), cancellationToken);
        await ExecuteBuildAsync(claimed, context, cancellationToken);
    }

    public async Task RunConnectedAsync(AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        var value = Environment.GetEnvironmentVariable("CSWEET_BUILD_ID");
        if (string.IsNullOrWhiteSpace(value))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return;
        }
        if (!Guid.TryParse(value, out var buildId) ||
            !long.TryParse(Environment.GetEnvironmentVariable("CSWEET_BUILD_EXPECTED_REVISION"), out var expectedRevision))
            throw new InvalidOperationException("The managed build identity is invalid.");
        var build = (await context.Platform.ReadBuildsAsync(new ReadBuildV2Request(buildId), cancellationToken)).SingleOrDefault()
            ?? throw new InvalidOperationException("The assigned build no longer exists.");
        if (build.Revision != expectedRevision || build.Status != DeliveryBuildStatuses.Queued)
            throw new InvalidOperationException("The assigned build revision is stale or no longer queued.");
        var claimed = await context.Platform.ClaimBuildAsync(new ClaimBuildRequest(build.Id, build.Revision,
            TimeSpan.FromMinutes(10), $"managed-claim:{build.Id:N}:{build.Revision}"), cancellationToken);
        await ExecuteBuildAsync(claimed, context, cancellationToken);
    }

    private static async Task AwaitOutputIngestionAsync(CancellationToken token)
    {
        var root = RequireSandboxRoot("CSWEET_BUILD_OUTPUT_ROOT");
        await File.WriteAllTextAsync(Path.Combine(root, ".csweet-adapter-complete"), "ready", token);
        var acknowledged = Path.Combine(root, ".csweet-output-uploaded");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        while (!File.Exists(acknowledged)) await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
    }

    private static async Task ExecuteBuildAsync(DeliveryBuildV2 build, AgentRuntimeContext context, CancellationToken token)
    {
        var inputRoot = RequireSandboxRoot("CSWEET_BUILD_INPUT_ROOT");
        var outputRoot = RequireSandboxRoot("CSWEET_BUILD_OUTPUT_ROOT");
        var commands = new List<string>(); var logs = new List<string>();
        var observed = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var tool in new[] { "node", "npm" })
            {
                commands.Add(tool + " --version");
                observed[tool] = (await RunAsync(tool, ["--version"], inputRoot, token)).Trim().TrimStart('v');
            }
            if (observed["node"] != "24.15.0" || observed["npm"] != "11.12.1")
                throw new InvalidOperationException("The Office image does not provide the exact declared Node 24.15.0 and npm 11.12.1 runtime.");
            RequireFile(inputRoot, "package-lock.json");
            foreach (var command in new[] { "ci --offline --ignore-scripts --no-audit --fund=false", "run lint", "run test", "run build", "run smoke" })
            {
                var args = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                commands.Add("npm " + command);
                logs.Add(await RunAsync("npm", args, inputRoot, token));
                build = await context.Platform.HeartbeatBuildAsync(new HeartbeatBuildRequest(build.Id,
                    build.ClaimId!.Value, build.Revision, TimeSpan.FromMinutes(10), $"heartbeat:{build.Id:N}:{build.Revision}"), token);
            }
            foreach (var package in new[] { "playwright", "vite", "vitest" })
            {
                using var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(inputRoot, "node_modules", package, "package.json"), token));
                observed[package] = metadata.RootElement.GetProperty("version").GetString()!;
            }
            if (observed["playwright"] != "1.62.1")
                throw new InvalidOperationException("The restored Playwright version does not match the declared toolchain.");
            CopyDirectory(Path.Combine(inputRoot, "dist"), Path.Combine(outputRoot, "dist"));
            var testResults = Path.Combine(inputRoot, "test-results");
            if (Directory.Exists(testResults)) CopyDirectory(testResults, Path.Combine(outputRoot, "captures"));
            CreateDeterministicZip(Path.Combine(outputRoot, "dist"), Path.Combine(outputRoot, "package.zip"));
            var outputs = BuildManifest(outputRoot);
            var lockHash = Sha256(Path.Combine(inputRoot, "package-lock.json"));
            var manifestHash = HashManifest(outputs);
            var provenance = new BuildExecutionProvenance("com.csweet.toolchain.node-typescript", "1.0.2",
                build.DefinitionDigest, Environment.GetEnvironmentVariable("CSWEET_CERTIFIED_IMAGE_DIGEST") ?? "broker-missing",
                JsonSerializer.SerializeToElement(observed), lockHash, commands,
                build.SourceRevision, manifestHash, logs.Select((_, i) => $"log:{i + 1}").ToList());
            await AwaitOutputIngestionAsync(token);
            await context.Platform.ReportBuildAsync(new ReportBuildV2Request(build.Id, build.ClaimId!.Value,
                build.Revision, DeliveryBuildStatuses.Succeeded, outputs, provenance,
                [new BuildValidationReport("toolchain.web-game.v1", "Passed", "Lint, unit tests, production build, and Playwright smoke capture passed.", [], [])],
                null, null, $"report:{build.Id:N}:{build.Attempt}"), token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var provenance = new BuildExecutionProvenance("com.csweet.toolchain.node-typescript", "1.0.2",
                build.DefinitionDigest, Environment.GetEnvironmentVariable("CSWEET_CERTIFIED_IMAGE_DIGEST") ?? "broker-missing",
                JsonSerializer.SerializeToElement(observed), string.Empty, commands,
                build.SourceRevision, string.Empty, logs.Select((_, i) => $"log:{i + 1}").ToList());
            await context.Platform.ReportBuildAsync(new ReportBuildV2Request(build.Id, build.ClaimId!.Value,
                build.Revision, DeliveryBuildStatuses.Failed, [], provenance, [], "node-toolchain-failed",
                exception.Message, $"failure:{build.Id:N}:{build.Attempt}"), token);
        }
    }

    private static string RequireSandboxRoot(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) throw new InvalidOperationException($"Managed sandbox variable {variable} is unavailable.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }
    private static void RequireFile(string root, string relative) { if (!File.Exists(Path.Combine(root, relative))) throw new InvalidOperationException($"Required checked-in file '{relative}' is missing."); }
    private static async Task<string> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken token)
    {
        var start = new ProcessStartInfo(fileName) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token); var output = (await stdout) + (await stderr);
        if (process.ExitCode != 0) throw new InvalidOperationException($"{fileName} exited {process.ExitCode}: {output[^Math.Min(output.Length, 4000)..]}");
        return output.Length <= 4000 ? output : output[^4000..];
    }
    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source)) throw new InvalidOperationException("Expected build output is missing.");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file); if (relative.Split(Path.DirectorySeparatorChar).Contains("..")) throw new InvalidOperationException("Output traversal detected.");
            var target = Path.Combine(destination, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
        }
    }
    private static void CreateDeterministicZip(string source, string target)
    {
        using var archive = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var entry = archive.CreateEntry(Path.GetRelativePath(source, file).Replace('\\', '/'), CompressionLevel.SmallestSize);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using var input = File.OpenRead(file); using var output = entry.Open(); input.CopyTo(output);
        }
    }
    private static IReadOnlyList<BuildOutputManifestEntry> BuildManifest(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(file => new BuildOutputManifestEntry(Path.GetRelativePath(root, file).Replace('\\', '/'), Sha256(file),
            new FileInfo(file).Length, ContentType(file), file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "package" : "evidence")).ToList();
    private static string Sha256(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    private static string HashManifest(IReadOnlyList<BuildOutputManifestEntry> manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var output in manifest.OrderBy(x => x.RelativePath.Replace('\\', '/'), StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("relativePath", output.RelativePath.Replace('\\', '/'));
                writer.WriteString("sha256", output.Sha256.ToLowerInvariant());
                writer.WriteNumber("size", output.Size);
                writer.WriteString("contentType", output.ContentType.ToLowerInvariant());
                writer.WriteString("typeKey", output.TypeKey);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
    private static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch { ".html" => "text/html", ".css" => "text/css", ".js" => "application/javascript", ".json" => "application/json", ".png" => "image/png", ".webm" => "video/webm", ".zip" => "application/zip", _ => "application/octet-stream" };
}
