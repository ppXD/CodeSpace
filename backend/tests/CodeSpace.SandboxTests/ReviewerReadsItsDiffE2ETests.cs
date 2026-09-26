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
/// <para>Every arm runs its tier's production posture, network off included. Under bubblewrap a network-off run whose
/// model is brokered runs in a namespace sealed to that broker (<c>AgentRunExecutor.ApplySealedEgress</c>), so it
/// reaches its model and nothing else; before that seal it was severed from the broker too and reached no model at
/// all, which is why these arms once had to turn the network on to ask anything.</para>
///
/// <para>What it found on Linux, pinned so that a fix has to flip it deliberately: both CLIs read the diff under our
/// bubblewrap, over a workspace the kernel mounts read-only for a Confined run. Codex does so only because the runner
/// stands its own sandbox down there (<see cref="CodexHarness.ConfinedSandboxMode"/>): that sandbox is a nested
/// bubblewrap that cannot configure its network namespace's loopback once ours has dropped every capability, so left in
/// place it ran no command at all. A Confined reviewer that tries to write its workspace is refused — for Codex, whose
/// CLI now allows everything, by the read-only mount itself — and the workspace is left exactly as it was; a Standard
/// Codex writes its workspace and nothing outside it.</para>
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
    public Task A_read_only_claude_reviewer_reads_the_diff_between_two_commits_with_git() => ReadsTheDiffAsync(ClaudeCodeHarness.HarnessKind, AgentAutonomyLevel.Confined);

    [Theory]
    [InlineData(AgentAutonomyLevel.Confined)]
    [InlineData(AgentAutonomyLevel.Standard)]
    public Task A_codex_reviewer_reads_the_diff_with_git_wherever_it_runs(AgentAutonomyLevel tier) => ReadsTheDiffAsync(CodexHarness.HarnessKind, tier);

    [Fact]
    public async Task The_pinned_codex_accepts_the_resume_spelling_of_its_stand_down()
    {
        // `exec resume` rejects --sandbox, so a resumed Codex is stood down through `-c sandbox_mode=…` instead. The real
        // runner applies the swap, and the pinned binary must get past its argument and config parsing to the rollout
        // lookup ("no rollout found" for a thread that does not exist) — the control with a mode Codex does not know
        // proves the lookup is not reached regardless.
        var harness = HarnessFor(CodexHarness.HarnessKind);

        if (!Armed(CodexHarness.HarnessKind) || OperatingSystem.IsWindows()) return;

        await RequirePinnedBinaryAsync(harness, CodexHarness.HarnessKind);

        // A real repository, as every production workspace is: Codex refuses an untrusted non-git directory before it gets anywhere near the rollout.
        var task = new AgentTask { Goal = "resume", Harness = CodexHarness.HarnessKind, WorkspaceDirectory = NewReviewRepository().Directory, ResumeFromSessionId = Guid.NewGuid().ToString(), TimeoutSeconds = 60, Environment = new Dictionary<string, string> { [CodexHarness.ApiKeyEnvVar] = "sk-review-e2e-resume", ["HOME"] = NewDirectory("resume-home") } };
        var spec = AgentRunExecutor.ApplyWriteScope(harness.BuildInvocation(task), task.Permissions);
        var bogus = spec with { Args = spec.Args.Select(arg => arg.StartsWith("sandbox_mode=", StringComparison.Ordinal) ? "sandbox_mode=not-a-mode" : arg).ToList(), WhenRunnerConfines = null };

        var accepted = await new LocalProcessRunner().RunAsync(spec, CancellationToken.None);
        var control = await new LocalProcessRunner().RunAsync(bogus, CancellationToken.None);

        (accepted.Stdout + accepted.Stderr).ShouldContain("no rollout found", Case.Insensitive, $"the pinned codex refused the resume argv the runner handed it (confined={BubblewrapSandbox.Available is not null}): {Tail(accepted.Stderr)}");
        (control.Stdout + control.Stderr).ShouldNotContain("no rollout found", Case.Insensitive, $"control: an unknown sandbox mode must stop Codex before the rollout lookup, or reaching it proves nothing: {Tail(control.Stderr)}");

        output.WriteLine($"{RanMarker} codex-resume-stand-down confined={BubblewrapSandbox.Available is not null}");
    }

    private async Task ReadsTheDiffAsync(string harnessKind, AgentAutonomyLevel tier)
    {
        var harness = HarnessFor(harnessKind);

        if (!Armed(harnessKind) || OperatingSystem.IsWindows()) return;

        await RequirePinnedBinaryAsync(harness, harnessKind);

        var repo = NewReviewRepository();
        var upstream = new ScriptedModelUpstream([$"git diff {repo.Base} {repo.Head}"], $"REVIEW-DONE-{repo.Nonce}");
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);
        var brokered = await OpenLeaseAsync(broker);

        var production = AgentAutonomyPolicy.Derive(tier);
        var task = ReviewTask(harnessKind, repo, production, Brokered(harness, brokered));

        var run = await RunAsync(harness, task, brokered.RebindPort);

        run.Spec.ReadOnlyWorkingDirectory.ShouldBe(production.WriteScope == AgentWriteScope.ReadOnly, $"fixture check: a {tier} reviewer must be launched over the workspace mount its write scope gives it, or this arm does not show git reads surviving it");

        run.Result.Status.ShouldBe(SandboxStatus.Success, customMessage: $"the {harnessKind} reviewer did not finish cleanly (exit {run.Result.ExitCode}); stderr: {Tail(run.Result.Stderr)}. Reproduce by hand from the argv: {string.Join(' ', run.Spec.Args)}");

        var fedBack = upstream.Requests.Where(r => r.Body.Contains($"MARKER-NEW-{repo.Nonce}", StringComparison.Ordinal) && r.Body.Contains($"MARKER-OLD-{repo.Nonce}", StringComparison.Ordinal)).ToList();

        fedBack.ShouldNotBeEmpty(customMessage: $"the model never received the diff: the {harnessKind} read-only mode did not run `git diff`, or ran it without output. What the CLI fed back as the tool's result: {Tail(ToolOutputs(upstream.Requests), 1500)}. Requests the model saw: {Describe(upstream.Requests)}. stdout tail: {Tail(string.Join('\n', run.Lines))}. stderr tail: {Tail(run.Result.Stderr)}");

        AssertTheStreamCarriesTheDiff(harness, harnessKind, run.Lines, repo);

        var result = harness.BuildResult(run.Lines.SelectMany(harness.ParseEvents).ToList(), run.Result.ExitCode, run.Result.Stderr);

        result.Status.ShouldBe(AgentRunStatus.Succeeded, customMessage: $"the harness fold must read the run as finished: {result.Error}");
        result.Summary.ShouldNotBeNull().ShouldContain(upstream.FinalText, customMessage: "the agent's final answer must reach the fold");

        (await GitAsync(repo.Directory, "status --porcelain")).ShouldBeEmpty("a read-only reviewer must leave the workspace exactly as it found it");
        (await GitAsync(repo.Directory, "rev-parse HEAD")).Trim().ShouldBe(repo.Head, "and on the head it was given");

        output.WriteLine($"{RanMarker} read-diff {harnessKind} {tier} confined={BubblewrapSandbox.Available is not null} toldFullAccess={upstream.Requests.Any(r => r.Body.Contains(CodexHarness.ConfinedSandboxMode, StringComparison.Ordinal))}");
    }

    [Theory]
    [InlineData(ClaudeCodeHarness.HarnessKind)]
    [InlineData(CodexHarness.HarnessKind)]
    public async Task A_confined_reviewer_cannot_write_its_workspace(string harnessKind)
    {
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

        var task = ReviewTask(harnessKind, repo, AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Confined), Brokered(harness, brokered));

        var run = await RunAsync(harness, task, brokered.RebindPort);

        var fedBack = ToolOutputs(upstream.Requests);

        fedBack.ShouldNotBe("(none)", $"fixture check: the CLI must have handed the write's outcome back to the model, or this says nothing about who refused it. Requests: {Describe(upstream.Requests)}; stderr: {Tail(run.Result.Stderr)}");
        File.ReadAllText(Path.Combine(repo.Directory, "app.txt")).ShouldNotContain($"TAMPER-{repo.Nonce}", customMessage: $"a Confined reviewer wrote its workspace; what the CLI fed back: {Tail(fedBack, 600)}");
        (await GitAsync(repo.Directory, "status --porcelain")).ShouldBeEmpty("the workspace is exactly as the reviewer found it");

        var refusedBy = fedBack.Contains("Read-only file system", StringComparison.Ordinal) ? "read-only-mount" : "cli-permission-mode";

        // Under our confinement Codex's own sandbox is stood down to full access, so the only thing left to refuse the
        // write is the mount — which is the claim. Claude's plan mode may get there first; either layer is a refusal.
        if (harnessKind == CodexHarness.HarnessKind) refusedBy.ShouldBe("read-only-mount", $"with its own sandbox stood down, the kernel's read-only mount must be what refused Codex's write; what the CLI fed back: {Tail(fedBack, 600)}");

        output.WriteLine($"{RanMarker} write-refused {harnessKind} by={refusedBy} fedBack={Tail(fedBack.ReplaceLineEndings(" "), 160)}");
    }

    [Fact]
    public async Task A_standard_codex_writes_its_workspace_under_our_confinement_and_nothing_outside_it()
    {
        // The other half of standing Codex's sandbox down: a Standard run must still get its work done under ours —
        // the workspace writable, the system root not. Whether it can plant a git hook is pinned as observed: the
        // worker's own commit and push run in this workspace, so protecting .git is a deliberate later flip.
        const string harnessKind = CodexHarness.HarnessKind;
        var harness = HarnessFor(harnessKind);

        if (!Armed(harnessKind) || OperatingSystem.IsWindows()) return;

        if (BubblewrapSandbox.Available is null)
        {
            BubblewrapSandbox.IsRequired.ShouldBeFalse("Sandbox:RequireConfinement is set but this host cannot sandbox (bwrap/userns) — the E2E cannot prove what confinement does here");
            return;
        }

        await RequirePinnedBinaryAsync(harness, harnessKind);

        var repo = NewReviewRepository();
        var systemProbe = $"/usr/cs-tamper-{repo.Nonce}";
        var hookProbe = Path.Combine(repo.Directory, ".git", "hooks", $"cs-probe-{repo.Nonce}");
        var upstream = new ScriptedModelUpstream([$"printf CHANGED-{repo.Nonce} > app.txt", $"printf x > {systemProbe}", $"touch {hookProbe}"], $"WORK-DONE-{repo.Nonce}");
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);
        var brokered = await OpenLeaseAsync(broker);

        var task = ReviewTask(harnessKind, repo, AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Standard), Brokered(harness, brokered)) with { Goal = "Change app.txt." };

        ReviewRun run;

        try { run = await RunAsync(harness, task, brokered.RebindPort); }
        finally { if (File.Exists(systemProbe)) File.Delete(systemProbe); }   // a failed refusal must not leave the probe behind for the next run

        run.Result.Status.ShouldBe(SandboxStatus.Success, customMessage: $"the Standard Codex run did not finish cleanly (exit {run.Result.ExitCode}); stderr: {Tail(run.Result.Stderr)}; last tool output: {Tail(ToolOutputs(upstream.Requests), 600)}");
        File.ReadAllText(Path.Combine(repo.Directory, "app.txt")).ShouldBe($"CHANGED-{repo.Nonce}", $"a Standard Codex must be able to write its workspace under our confinement; requests: {Describe(upstream.Requests)}");
        ToolOutputs(upstream.Requests).ShouldNotBe("(none)", "fixture check: Codex must have run the commands and handed their outcome back to the model");
        upstream.Requests.Any(r => r.Body.Contains("Read-only file system", StringComparison.Ordinal)).ShouldBeTrue($"the write under /usr must have been refused by the read-only root; requests: {Describe(upstream.Requests)}");
        File.Exists(hookProbe).ShouldBeTrue("OBSERVED TODAY: a Standard agent can plant a file under .git/hooks, and the worker's own commit and push run in this workspace. When .git is protected, flip this deliberately.");

        output.WriteLine($"{RanMarker} standard-codex-writes hooksWritable={File.Exists(hookProbe)}");
    }

    [Theory]
    [InlineData(ClaudeCodeHarness.HarnessKind)]
    [InlineData(CodexHarness.HarnessKind)]
    public async Task A_network_off_reviewer_reaches_its_model_through_the_sealed_namespace(string harnessKind)
    {
        // The durable launch every agent run takes, network off, under confinement: the run must be launched inside a
        // namespace sealed to its broker (recorded on its handle), reach its model through it, read the diff, and leave
        // no namespace behind. Before the seal this arm pinned the opposite — a Confined reviewer reached no model.
        var harness = HarnessFor(harnessKind);

        if (!Armed(harnessKind) || OperatingSystem.IsWindows()) return;

        if (BubblewrapSandbox.Available is null)
        {
            // Only confinement seals the network: an unconfined host would let this run reach the broker and say nothing.
            BubblewrapSandbox.IsRequired.ShouldBeFalse("Sandbox:RequireConfinement is set but this host cannot sandbox (bwrap/userns) — the E2E cannot prove what confinement does here");
            return;
        }

        FilteredEgressNetns.CanSeal.ShouldBeTrue("this confining host could not build a throwaway namespace, so every network-off brokered run on it is severed from its model");

        await RequirePinnedBinaryAsync(harness, harnessKind);

        var repo = NewReviewRepository();
        var upstream = new ScriptedModelUpstream([$"git diff {repo.Base} {repo.Head}"], $"REVIEW-DONE-{repo.Nonce}");
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);
        var brokered = await OpenLeaseAsync(broker);

        var task = ReviewTask(harnessKind, repo, AgentAutonomyPolicy.Derive(AgentAutonomyLevel.Confined), Brokered(harness, brokered));
        var spec = ProductionSpec(harness, task, brokered.RebindPort);
        var key = Guid.NewGuid().ToString("N");
        var runner = new LocalProcessRunner();
        var lines = new List<string>();
        var clock = Stopwatch.StartNew();

        var handle = await runner.LaunchAsync(spec, key, CancellationToken.None);
        _directories.Add(handle.SpoolDirectory);

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds((task.TimeoutSeconds ?? 300) + 60));
        var result = await runner.AttachAsync(handle, (frame, _) => { lines.Add(frame.Text); return Task.CompletedTask; }, budget.Token);

        handle.EgressNetnsKey.ShouldBe(key, "a network-off brokered run must be launched inside a sealed namespace keyed by the run");
        handle.Confinement.ShouldNotBeNull().EgressSealedToBroker.ShouldBeTrue("the launch must record that the run was sealed to its broker");
        result.Status.ShouldBe(SandboxStatus.Success, customMessage: $"the {harnessKind} reviewer did not finish cleanly through the sealed namespace (exit {result.ExitCode}); stderr: {Tail(result.Stderr)}; requests the model saw: {Describe(upstream.Requests)}");
        upstream.Requests.ShouldContain(r => r.Body.Contains($"MARKER-NEW-{repo.Nonce}", StringComparison.Ordinal), $"the diff must reach the model through the sealed namespace; last tool output: {Tail(ToolOutputs(upstream.Requests), 600)}");
        (await GitAsync(repo.Directory, "status --porcelain")).ShouldBeEmpty("a Confined reviewer leaves the workspace exactly as it found it");

        output.WriteLine($"{RanMarker} network-off-sealed {harnessKind} seconds={clock.Elapsed.TotalSeconds:F1}");
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

    /// <summary>The spec the executor would hand the runner: the harness invocation sealed to the run's broker lease when its network is off, and with its write scope applied, so a read-only run's workspace is mounted read-only wherever the host confines.</summary>
    private static SandboxSpec ProductionSpec(IAgentHarness harness, AgentTask task, int? brokerPort) =>
        AgentRunExecutor.ApplyWriteScope(AgentRunExecutor.ApplySealedEgress(harness.BuildInvocation(task), task.Permissions, brokerPort), task.Permissions);

    private static async Task<ReviewRun> RunAsync(IAgentHarness harness, AgentTask task, int? brokerPort)
    {
        var spec = ProductionSpec(harness, task, brokerPort);
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
