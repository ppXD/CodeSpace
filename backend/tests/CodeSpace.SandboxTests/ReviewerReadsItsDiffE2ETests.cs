using System.Diagnostics;
using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Credentials.Broker;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.SandboxTests;

/// <summary>
/// Can a read-only reviewer read the change under review with git, offline, the way production launches it? A
/// PR-review agent that is handed a workspace holding the base and head commits — instead of the whole diff inlined
/// into its prompt — depends on it, and nothing else in the repo answers it: the question is what each CLI's OWN
/// read-only mode permits (Claude's <c>--permission-mode plan</c>, Codex's <c>--sandbox read-only</c>) inside OUR
/// confinement, and only the real binaries on a real kernel can say.
///
/// <para>Fidelity: 🟢 HIGH for everything but the model. The pinned CLI binaries, the production harness argv
/// (<see cref="IAgentHarness.BuildInvocation"/>), the production <see cref="LocalProcessRunner"/> (bubblewrap where the
/// host confines, config home, environment scrub, broker-host resolution) and the production model-credential broker
/// all run for real. The only fake is the model behind the broker (<see cref="ScriptedModelUpstream"/>), which scripts
/// the one tool call a reviewer would make. That is deliberate: the question is whether the CLI's permission mode
/// RUNS the command, not whether a model would choose to.</para>
///
/// <para>One stated deviation. The read arm runs the Confined posture with the network ON: under bubblewrap a
/// Network=Off run is severed from everything, the broker included, so it could not reach even a local model. That is
/// not assumed — the second arm pins it, and the argv the CLI sees is identical either way (asserted), so the read
/// arm answers the permission question the production posture would face. What the network-off arm finds is itself
/// the finding: a Confined reviewer on a confining worker cannot reach its model today.</para>
///
/// <para>What it found on Linux, pinned so that a fix has to flip it deliberately: Claude's plan mode reads the diff
/// under our bubblewrap, over a workspace the kernel mounts read-only; Codex runs no command there at all, in
/// read-only or workspace-write, because its own sandbox is a nested bubblewrap that cannot configure its network
/// namespace's loopback once ours has dropped every capability. Unconfined (a dev host), Codex reads the diff like
/// Claude. A Confined reviewer that tries to write its workspace is refused whichever layer gets there first — the
/// CLI's plan mode or the read-only mount — and the workspace is left exactly as it was.</para>
///
/// <para>Armed by <see cref="RequireEnvVar"/> (the sandbox lane sets it after installing the pins, and then a missing
/// or unpinned binary FAILS) or by a harness's own command override for a local run; otherwise it returns. Each arm
/// that ran prints <see cref="RanMarker"/>, which the lane requires, so a silent return can never pass for coverage.</para>
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class ReviewerReadsItsDiffE2ETests(ITestOutputHelper output) : IDisposable
{
    /// <summary>Set by the sandbox lane once the pinned CLIs are installed: from then on a missing or wrong binary is a failure, never a skip.</summary>
    public const string RequireEnvVar = "CODESPACE_REQUIRE_REVIEW_CLIS";

    /// <summary>Printed by every arm that actually ran; the sandbox lane requires one per arm in the test output.</summary>
    public const string RanMarker = "[review-diff-e2e] ran";

    private readonly List<string> _directories = [];

    [Fact]
    public Task A_read_only_claude_reviewer_reads_the_diff_between_two_commits_with_git() => ReadsTheDiffAsync(ClaudeCodeHarness.HarnessKind);

    [Fact]
    public async Task A_read_only_codex_reviewer_reads_the_diff_where_nothing_else_confines_it()
    {
        // Under our bubblewrap Codex's own sandbox cannot start at all — pinned by the test below — so this arm is the
        // dev-host (unconfined) answer only, and it is the answer a fix for that finding has to restore under confinement.
        if (BubblewrapSandbox.Available is not null) return;

        await ReadsTheDiffAsync(CodexHarness.HarnessKind);
    }

    [Theory]
    [InlineData(AgentAutonomyLevel.Confined, "read-only")]
    [InlineData(AgentAutonomyLevel.Standard, "workspace-write")]
    public async Task Codex_cannot_run_a_command_in_its_own_sandbox_nested_inside_ours(AgentAutonomyLevel tier, string codexSandbox)
    {
        // THE FINDING, pinned as observed on Linux: Codex's sandbox is a bubblewrap of its own, and it cannot set up
        // the loopback of its network namespace inside ours ("bwrap: loopback: Failed RTM_NEWADDR: Operation not
        // permitted" — our confinement drops every capability). So under confinement Codex runs no command at all, in
        // read-only AND workspace-write. When this goes red because the command ran, the finding is fixed: flip it into
        // a read arm like Claude's.
        var harness = HarnessFor(CodexHarness.HarnessKind);

        if (!Armed(CodexHarness.HarnessKind) || OperatingSystem.IsWindows()) return;

        if (BubblewrapSandbox.Available is null)
        {
            BubblewrapSandbox.IsRequired.ShouldBeFalse("Sandbox:RequireConfinement is set but this host cannot sandbox (bwrap/userns) — the E2E cannot prove what confinement does here");
            return;
        }

        await RequirePinnedBinaryAsync(harness, CodexHarness.HarnessKind);

        var repo = NewReviewRepository();
        var upstream = new ScriptedModelUpstream([$"git diff {repo.Base} {repo.Head}"], $"REVIEW-DONE-{repo.Nonce}");
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);
        var brokered = await OpenLeaseAsync(broker);

        var task = ReviewTask(CodexHarness.HarnessKind, repo, AgentAutonomyPolicy.Derive(tier) with { Network = AgentNetworkAccess.On }, Brokered(harness, brokered));

        harness.BuildInvocation(task).Args.ShouldContain(codexSandbox, customMessage: $"fixture check: the {tier} tier must launch Codex with --sandbox {codexSandbox}");

        await RunAsync(harness, task);

        var fedBack = ToolOutputs(upstream.Requests);

        upstream.Requests.ShouldNotBeEmpty("fixture check: the network-on posture must reach the model, or this says nothing about the sandbox");
        fedBack.ShouldNotContain($"MARKER-NEW-{repo.Nonce}", customMessage: "Codex's own sandbox ran the command inside ours — the finding is fixed; flip this test into a read arm");
        fedBack.ShouldContain("bwrap:", customMessage: $"the command did not run, but not for the reason this test pins; what the CLI fed back: {Tail(fedBack, 1500)}");

        output.WriteLine($"{RanMarker} codex-nested-sandbox {codexSandbox} fedBack={Tail(fedBack.ReplaceLineEndings(" "), 160)}");
    }

    private async Task ReadsTheDiffAsync(string harnessKind)
    {
        var harness = HarnessFor(harnessKind);

        if (!Armed(harnessKind) || OperatingSystem.IsWindows()) return;

        await RequirePinnedBinaryAsync(harness, harnessKind);

        var repo = NewReviewRepository();
        var upstream = new ScriptedModelUpstream([$"git diff {repo.Base} {repo.Head}"], $"REVIEW-DONE-{repo.Nonce}");
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);
        var brokered = await OpenLeaseAsync(broker);

        var confined = AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Confined);
        var task = ReviewTask(harnessKind, repo, confined with { Network = AgentNetworkAccess.On }, Brokered(harness, brokered));

        harness.BuildInvocation(task).Args.ShouldBe(harness.BuildInvocation(task with { Permissions = confined }).Args,
            customMessage: "fixture check: the CLI must see the same argv with the network on as the production Confined posture gives it, or this arm answers a different question");

        var run = await RunAsync(harness, task);

        run.Spec.ReadOnlyWorkingDirectory.ShouldBeTrue("fixture check: a Confined reviewer must be launched over a read-only workspace, or this arm does not show git reads surviving the mount");

        run.Result.Status.ShouldBe(SandboxStatus.Success, customMessage: $"the {harnessKind} reviewer did not finish cleanly (exit {run.Result.ExitCode}); stderr: {Tail(run.Result.Stderr)}. Reproduce by hand from the argv: {string.Join(' ', run.Spec.Args)}");

        var fedBack = upstream.Requests.Where(r => r.Body.Contains($"MARKER-NEW-{repo.Nonce}", StringComparison.Ordinal) && r.Body.Contains($"MARKER-OLD-{repo.Nonce}", StringComparison.Ordinal)).ToList();

        fedBack.ShouldNotBeEmpty(customMessage: $"the model never received the diff: the {harnessKind} read-only mode did not run `git diff`, or ran it without output. What the CLI fed back as the tool's result: {Tail(ToolOutputs(upstream.Requests), 1500)}. Requests the model saw: {Describe(upstream.Requests)}. stdout tail: {Tail(string.Join('\n', run.Lines))}. stderr tail: {Tail(run.Result.Stderr)}");

        AssertTheStreamCarriesTheDiff(harness, harnessKind, run.Lines, repo);

        var result = harness.BuildResult(run.Lines.SelectMany(harness.ParseEvents).ToList(), run.Result.ExitCode, run.Result.Stderr);

        result.Status.ShouldBe(AgentRunStatus.Succeeded, customMessage: $"the harness fold must read the run as finished: {result.Error}");
        result.Summary.ShouldNotBeNull().ShouldContain(upstream.FinalText, customMessage: "the agent's final answer must reach the fold");

        (await GitAsync(repo.Directory, "status --porcelain")).ShouldBeEmpty("a read-only reviewer must leave the workspace exactly as it found it");
        (await GitAsync(repo.Directory, "rev-parse HEAD")).Trim().ShouldBe(repo.Head, "and on the head it was given");

        output.WriteLine($"{RanMarker} read-only-diff {harnessKind} confined={BubblewrapSandbox.Available is not null}");
    }

    [Fact]
    public async Task A_confined_reviewer_cannot_write_its_workspace()
    {
        const string harnessKind = ClaudeCodeHarness.HarnessKind;
        var harness = HarnessFor(harnessKind);

        if (!Armed(harnessKind) || OperatingSystem.IsWindows()) return;

        if (BubblewrapSandbox.Available is null)
        {
            // Unconfined, only the CLI's own mode stands between a Confined run and a write — the mount is the claim here.
            BubblewrapSandbox.IsRequired.ShouldBeFalse("Sandbox:RequireConfinement is set but this host cannot sandbox (bwrap/userns) — the E2E cannot prove what confinement does here");
            return;
        }

        await RequirePinnedBinaryAsync(harness, harnessKind);

        var repo = NewReviewRepository();
        var upstream = new ScriptedModelUpstream([$"echo TAMPER-{repo.Nonce} > app.txt"], $"REVIEW-DONE-{repo.Nonce}");
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);
        var brokered = await OpenLeaseAsync(broker);

        var task = ReviewTask(harnessKind, repo, AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Confined) with { Network = AgentNetworkAccess.On }, Brokered(harness, brokered));

        var run = await RunAsync(harness, task);

        var fedBack = ToolOutputs(upstream.Requests);

        fedBack.ShouldNotBe("(none)", $"fixture check: the CLI must have handed the write's outcome back to the model, or this says nothing about who refused it. Requests: {Describe(upstream.Requests)}; stderr: {Tail(run.Result.Stderr)}");
        File.ReadAllText(Path.Combine(repo.Directory, "app.txt")).ShouldNotContain($"TAMPER-{repo.Nonce}", customMessage: $"a Confined reviewer wrote its workspace; what the CLI fed back: {Tail(fedBack, 600)}");
        (await GitAsync(repo.Directory, "status --porcelain")).ShouldBeEmpty("the workspace is exactly as the reviewer found it");

        var refusedBy = fedBack.Contains("Read-only file system", StringComparison.Ordinal) ? "read-only-mount" : "cli-permission-mode";
        output.WriteLine($"{RanMarker} write-refused {harnessKind} by={refusedBy} fedBack={Tail(fedBack.ReplaceLineEndings(" "), 160)}");
    }

    [Theory]
    [InlineData(ClaudeCodeHarness.HarnessKind)]
    [InlineData(CodexHarness.HarnessKind)]
    public async Task A_network_off_reviewer_under_confinement_cannot_reach_its_model(string harnessKind)
    {
        var harness = HarnessFor(harnessKind);

        if (!Armed(harnessKind) || OperatingSystem.IsWindows()) return;

        if (BubblewrapSandbox.Available is null)
        {
            // Only confinement severs the network: an unconfined host would let this run reach the broker and say nothing.
            BubblewrapSandbox.IsRequired.ShouldBeFalse("Sandbox:RequireConfinement is set but this host cannot sandbox (bwrap/userns) — the E2E cannot prove what confinement does here");
            return;
        }

        await RequirePinnedBinaryAsync(harness, harnessKind);

        var repo = NewReviewRepository();
        var upstream = new ScriptedModelUpstream([$"git diff {repo.Base} {repo.Head}"], $"REVIEW-DONE-{repo.Nonce}");
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);
        var brokered = await OpenLeaseAsync(broker);

        var task = ReviewTask(harnessKind, repo, AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Confined), Brokered(harness, brokered)) with { TimeoutSeconds = 120 };

        var run = await RunAsync(harness, task);

        upstream.Requests.ShouldBeEmpty(customMessage: $"a Network=Off run under bubblewrap reached its model — confinement no longer severs it from the broker, so this arm's finding (a Confined reviewer cannot reach its model) is out of date and the read arm's network-on deviation can go. Requests: {Describe(upstream.Requests)}");
        run.Result.Status.ShouldNotBe(SandboxStatus.Success, customMessage: "a reviewer that reached no model cannot have finished its review");

        output.WriteLine($"{RanMarker} network-off {harnessKind} status={run.Result.Status} exit={run.Result.ExitCode}");
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best-effort cleanup of a temp directory */ }
        }
    }

    /// <summary>The environment a brokered run's CLI is handed — the harness's own projection of the broker's address and run token, the way the executor builds it.</summary>
    private static IReadOnlyDictionary<string, string> Brokered(IAgentHarness harness, BrokeredModelCredential brokered) => ((IBrokeredModelCredentialProjector)harness).ProjectBrokered(brokered);

    private static IAgentHarness HarnessFor(string harnessKind) => harnessKind == ClaudeCodeHarness.HarnessKind ? new ClaudeCodeHarness() : new CodexHarness();

    /// <summary>The lane's switch, or a local run that points the harness at a binary of its own.</summary>
    private static bool Armed(string harnessKind) =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RequireEnvVar))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(harnessKind == ClaudeCodeHarness.HarnessKind ? ClaudeCodeHarness.CommandEnvVar : CodexHarness.CommandEnvVar));

    /// <summary>Armed means the binary is there and is the pin production installs — anything else fails, so the lane can never pass on a missing or drifted CLI.</summary>
    private async Task RequirePinnedBinaryAsync(IAgentHarness harness, string harnessKind)
    {
        var command = harness.BuildInvocation(new AgentTask { Goal = "version", Harness = harnessKind }).Command;
        var pinned = harnessKind == ClaudeCodeHarness.HarnessKind ? ClaudeCodeHarness.DefaultVersion : CodexHarness.DefaultVersion;

        string version;

        try
        {
            version = await RunToEndAsync(command, "--version", Directory.GetCurrentDirectory());
        }
        catch (Exception ex)
        {
            throw new ShouldAssertException($"the {harnessKind} binary '{command}' could not be run ({ex.Message}); install the pin with: npm install -g <package>@{pinned}", ex);
        }

        version.ShouldContain(pinned, customMessage: $"'{command} --version' reported '{version.Trim()}', but production pins {pinned} (backend/Dockerfile.worker) — the answer this test gives is only about the pinned binary");
    }

    private static async Task<BrokeredModelCredential> OpenLeaseAsync(LoopbackModelCredentialBroker broker)
    {
        var lease = new ModelCredentialLeaseRequest
        {
            RunId = Guid.NewGuid(), TeamId = Guid.NewGuid(), Epoch = 1, Ttl = TimeSpan.FromMinutes(10),
            Upstream = new ResolvedModelCredential { Provider = "Custom", ApiKey = "sk-review-e2e-upstream", BaseUrl = "https://scripted-model.invalid" },
        };

        return (await broker.OpenAsync(lease, CancellationToken.None)).ShouldNotBeNull("the broker must be able to listen on this host — a brokered run has no other route to its model");
    }

    private AgentTask ReviewTask(string harnessKind, ReviewRepository repo, AgentPermissions permissions, IReadOnlyDictionary<string, string> brokeredEnvironment) => new()
    {
        Goal = $"Review the change from {repo.Base} to {repo.Head} in this repository. Read it with git; do not modify anything.",
        Harness = harnessKind,
        Model = harnessKind == ClaudeCodeHarness.HarnessKind ? "claude-sonnet-4-6" : "gpt-5.4",
        WorkspaceDirectory = repo.Directory,
        Permissions = permissions,
        TimeoutSeconds = 300,
        // The home an unconfined host would otherwise inherit is the operator's; under bubblewrap the runner sets HOME to the run's config home regardless.
        Environment = new Dictionary<string, string>(brokeredEnvironment) { ["HOME"] = NewDirectory("review-home") },
    };

    /// <summary>Launch the task the way the executor does: the harness invocation with the run's write scope applied, so a read-only run's workspace is mounted read-only wherever the host confines.</summary>
    private static async Task<ReviewRun> RunAsync(IAgentHarness harness, AgentTask task)
    {
        var spec = AgentRunExecutor.ApplyWriteScope(harness.BuildInvocation(task), task.Permissions);
        var lines = new List<string>();

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds((task.TimeoutSeconds ?? 300) + 60));

        var result = await new LocalProcessRunner().RunStreamingAsync(spec, (line, _) => { lines.Add(line); return Task.CompletedTask; }, budget.Token);

        return new ReviewRun(spec, lines, result);
    }

    /// <summary>What the harness's own reader sees: the diff has to reach the normalized stream a run's journal is built from, not only the model.</summary>
    private static void AssertTheStreamCarriesTheDiff(IAgentHarness harness, string harnessKind, IReadOnlyList<string> lines, ReviewRepository repo)
    {
        var marker = $"MARKER-NEW-{repo.Nonce}";
        var events = lines.SelectMany(harness.ParseEvents).ToList();

        if (harnessKind == ClaudeCodeHarness.HarnessKind)
        {
            // A denial reads as a CommandExecuted event too (the fold ignores is_error), so the marker text and the
            // result line's permission_denials are the evidence, never the event kind alone.
            events.ShouldContain(e => e.Kind == AgentEventKind.CommandExecuted && e.Text.Contains(marker, StringComparison.Ordinal),
                customMessage: $"no CommandExecuted event carried the diff; events: {string.Join(" | ", events.Select(e => $"{e.Kind}:{Tail(e.Text, 80)}"))}");

            var denials = lines.Select(TryParse).OfType<JsonElement>().Where(line => line.TryGetProperty("type", out var type) && type.GetString() == "result" && line.TryGetProperty("permission_denials", out _)).Select(line => line.GetProperty("permission_denials").GetArrayLength()).ToList();

            denials.ShouldNotBeEmpty("fixture check: the run must end on a result line that reports its permission denials");
            denials.Last().ShouldBe(0, "plan mode must not have denied the read");
            return;
        }

        // Codex puts a command's output only in the item's aggregated_output; the normalized Text is the command.
        events.ShouldContain(e => e.Kind == AgentEventKind.CommandExecuted && e.Data.HasValue && e.Data.Value.GetRawText().Contains(marker, StringComparison.Ordinal),
            customMessage: $"no command_execution item carried the diff; events: {string.Join(" | ", events.Select(e => $"{e.Kind}:{Tail(e.Text, 80)}"))}");
    }

    private ReviewRepository NewReviewRepository()
    {
        var nonce = Guid.NewGuid().ToString("N");
        var directory = NewDirectory("review-repo");

        Git(directory, "init -q -b main");
        File.WriteAllText(Path.Combine(directory, "app.txt"), $"line one\nMARKER-OLD-{nonce}\nline three\n");
        Git(directory, "add -A");
        Git(directory, "commit -q -m base");
        var head0 = GitOut(directory, "rev-parse HEAD");

        File.WriteAllText(Path.Combine(directory, "app.txt"), $"line one\nMARKER-NEW-{nonce}\nline three\n");
        Git(directory, "add -A");
        Git(directory, "commit -q -m head");

        return new ReviewRepository(RealPath(directory), head0, GitOut(directory, "rev-parse HEAD"), nonce);
    }

    private string NewDirectory(string label)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cs-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }

    /// <summary>The path the child process will see — macOS resolves <c>/var</c> to <c>/private/var</c>.</summary>
    private static string RealPath(string directory) => GitOut(directory, "rev-parse --show-toplevel");

    private static void Git(string directory, string arguments) => GitOut(directory, arguments);

    private static string GitOut(string directory, string arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-c", "user.name=review-e2e", "-c", "user.email=review-e2e@codespace.test", "-c", "commit.gpgsign=false" }.Concat(arguments.Split(' '))) info.ArgumentList.Add(arg);

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0) throw new InvalidOperationException($"git {arguments} failed ({process.ExitCode}): {stderr}");

        return stdout.Trim();
    }

    private static Task<string> GitAsync(string directory, string arguments) => Task.FromResult(GitOut(directory, arguments));

    private static async Task<string> RunToEndAsync(string command, string argument, string directory)
    {
        var info = new ProcessStartInfo(command) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return stdout;
    }

    private static JsonElement? TryParse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Every tool result the CLI handed back to the model — a Claude <c>tool_result</c> block or a Codex <c>function_call_output</c> item — which is what a failed command looks like from the model's side.</summary>
    private static string ToolOutputs(IReadOnlyList<RecordedRequest> requests)
    {
        var outputs = new List<string>();

        foreach (var body in requests.Select(r => TryParse(r.Body)).OfType<JsonElement>())
        {
            if (body.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array)
                outputs.AddRange(input.EnumerateArray().Where(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output" && item.TryGetProperty("output", out _)).Select(item => item.GetProperty("output").ToString()));

            if (body.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                outputs.AddRange(messages.EnumerateArray().Where(message => message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array).SelectMany(message => message.GetProperty("content").EnumerateArray()).Where(block => block.TryGetProperty("type", out var type) && type.GetString() == "tool_result").Select(block => block.TryGetProperty("content", out var content) ? content.ToString() : ""));
        }

        return outputs.Count == 0 ? "(none)" : outputs.Distinct().Last();
    }

    private static string Describe(IReadOnlyList<RecordedRequest> requests) => requests.Count == 0 ? "(none)" : string.Join("; ", requests.Select(r => $"{r.Method} {r.Path} ({r.Body.Length} chars)"));

    private static string Tail(string text, int length = 600) => text.Length <= length ? text : "…" + text[^length..];

    private sealed record ReviewRepository(string Directory, string Base, string Head, string Nonce);

    private sealed record ReviewRun(SandboxSpec Spec, IReadOnlyList<string> Lines, SandboxResult Result);
}
