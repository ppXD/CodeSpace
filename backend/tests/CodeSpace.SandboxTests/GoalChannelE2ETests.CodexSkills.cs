using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Credentials.Broker;
using CodeSpace.Core.Services.Agents.Harnesses;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.SandboxTests;

/// <summary>
/// Does a goal reach the Codex CLI as text for the model, and never as a skill the CLI loads into the turn first? The
/// pinned 0.142.2 installs skills of its own under <c>CODEX_HOME/skills/.system</c> on every start (a skill installer, a
/// skill creator, a plugin creator, OpenAI docs and image generation) and lists them to the model. A <c>$name</c>
/// anywhere in the goal makes the CLI read that skill's <c>SKILL.md</c> into the request as a user message before the
/// model acts: at the start of a line, mid-sentence, glued to a word, after a backslash, or as
/// <c>[$name](path)</c>. Only an ASCII name character may follow the <c>$</c>, so no escaping short of changing the text
/// the model reads would stop it, and <c>exec</c> hands its prompt over as one text input with no structured channel.
/// No setting turns the scan off; <c>skills.bundled.enabled=false</c> keeps the bundled skills out of the run, which
/// leaves a goal nothing of the CLI's own to name (<see cref="CodexHarness.NoBundledSkillsOverride"/>).
///
/// <para>Fidelity: 🟢 HIGH for everything but the model, as for the Claude arms: the pinned binary, the production argv
/// and stdin (<see cref="IAgentHarness.BuildInvocation"/>), the production durable launch and the production broker.
/// The model is <see cref="ScriptedModelUpstream"/>, which never calls a tool, so a skill body in a request was put
/// there by the CLI.</para>
///
/// <para>Each arm runs its goal twice: as built, and as a POSITIVE CONTROL with the bundled-skill override removed, which
/// must install the bundled skills, list them and inject every one the goal names, one spelling per skill. The control
/// is what keeps this from passing on a goal the CLI never parsed or on a CLI that bundles other skills: it fails when
/// the CLI installs a skill the goal does not name. The persona's own skill, which the platform projects, is named in
/// the same goal and must reach the model in both runs. The goal must reach the model byte for byte, so nothing is
/// escaped. Measured against <see cref="MeasuredCodexVersion"/> (and 0.147.0, which behaves the same); a pin bump fails
/// here until the arms have been re-read against the new binary and the constant moved with it.</para>
/// </summary>
public sealed partial class GoalChannelE2ETests
{
    /// <summary>The Codex release these arms were measured against. A bump of <see cref="CodexHarness.DefaultVersion"/> fails the arms until this moves with it.</summary>
    internal const string MeasuredCodexVersion = "0.142.2";

    private const string CodexModel = "gpt-5.4";

    private static readonly CodexHarness Codex = new();

    [Fact]
    public Task A_codex_goal_naming_the_clis_bundled_skills_reaches_the_model_without_their_bodies() => BundledSkillMentionsLoadNothingAsync(resume: false);

    [Fact]
    public Task A_continued_codex_session_resolves_none_of_the_clis_bundled_skills_either() => BundledSkillMentionsLoadNothingAsync(resume: true);

    /// <summary>
    /// The arm, fresh or continued: the goal names every bundled skill in a spelling of its own and the persona's skill
    /// plainly. A continuation first runs a goal that names nothing, then restores its rollout the way a continue does.
    /// </summary>
    private async Task BundledSkillMentionsLoadNothingAsync(bool resume)
    {
        if (!ReviewerReadsItsDiffE2ETests.Armed(CodexHarness.HarnessKind) || OperatingSystem.IsWindows()) return;

        await ReviewerReadsItsDiffE2ETests.RequirePinnedBinaryAsync(Codex, CodexHarness.HarnessKind);

        var fixture = PlantCodexFixture();
        var session = resume ? CodexSessionOf(await RunCodexAsync(fixture, _ => $"Say hello. GOAL-first-{fixture.Nonce}", AsBuilt), fixture) : null;

        var run = await RunCodexAsync(fixture, fixture.SkillGoal, AsBuilt, session);
        var control = await RunCodexAsync(fixture, fixture.SkillGoal, WithBundledSkills, session);
        var bundled = BundledSkills(control);

        AssertTheControlInjectedEveryBundledSkill(control, bundled, fixture);
        AssertNoBundledSkillReachedTheModel(run, bundled, fixture);
        AssertTheGoalReachedCodexVerbatim(run, fixture);

        if (session is not null) AssertTheContinuationCarriedTheRestoredTurn(run, fixture);

        CodexHarness.DefaultVersion.ShouldBe(MeasuredCodexVersion, $"these arms passed against the pinned {CodexHarness.DefaultVersion}, but were measured against {MeasuredCodexVersion}: re-read what the control injected on the new binary, then move {nameof(MeasuredCodexVersion)} with the pin");

        output.WriteLine($"{RanMarker} skill-mentions codex-cli {(resume ? "resume" : "fresh")} {CodexTier} uid={NonRootWorker.EffectiveUid()} confined={Confined} codex={CodexHarness.DefaultVersion} controlInjected={bundled.Count}/{bundled.Count}");
    }

    /// <summary>The posture a shipped Codex run has.</summary>
    private const AgentAutonomyLevel CodexTier = AgentAutonomyLevel.Standard;

    /// <summary>The production spec as built, carrying <see cref="CodexHarness.NoBundledSkillsOverride"/>.</summary>
    private static SandboxSpec AsBuilt(SandboxSpec spec) => spec;

    /// <summary>The control: the same production spec with the bundled-skill override removed, so the CLI loads what it bundles.</summary>
    private static SandboxSpec WithBundledSkills(SandboxSpec spec)
    {
        var args = spec.Args.ToList();
        var at = args.IndexOf(CodexHarness.NoBundledSkillsOverride);

        at.ShouldBeGreaterThan(0, "fixture check: the production argv must carry the bundled-skill override, or the control removes nothing");
        args[at - 1].ShouldBe("-c", "fixture check: the override rides as a -c pair");
        args.RemoveRange(at - 1, 2);

        return spec with { Args = args };
    }

    /// <summary>
    /// The control installed its bundled skills, listed each one, and injected each one the goal named, whatever spelling
    /// named it. A skill the goal does not name fails here: the run's silence about it would prove nothing.
    /// </summary>
    private static void AssertTheControlInjectedEveryBundledSkill(CodexRun control, IReadOnlyList<BundledSkill> bundled, CodexFixture fixture)
    {
        control.Result.Status.ShouldBe(SandboxStatus.Success, $"POSITIVE CONTROL: the control must run to its answer. {CodexDiagnosis(control)}");
        bundled.ShouldNotBeEmpty($"POSITIVE CONTROL: with the override removed the CLI must install its bundled skills under {Path.Combine(control.ConfigHome, BundledSkillsDirectory)}, or the run's silence proves nothing. {CodexDiagnosis(control)}");

        foreach (var skill in bundled)
        {
            fixture.Spellings.ShouldContainKey(skill.Name, $"POSITIVE CONTROL: the CLI bundles {skill.Name}, which the goal does not name — add a spelling for it, or the run's silence about it proves nothing");
            OutsideTheGoal(control).ShouldContain(text => text.Contains(skill.ListedPath, StringComparison.Ordinal), $"POSITIVE CONTROL: the control must list {skill.Name} to the model. {CodexDiagnosis(control)}");
            Injected(control, skill.Content).ShouldBeTrue($"POSITIVE CONTROL: the control must inject {skill.Name}, named as `{fixture.Spellings[skill.Name]}`. {CodexDiagnosis(control)}");
        }

        Injected(control, fixture.PersonaSkill(control.ConfigHome)).ShouldBeTrue($"POSITIVE CONTROL: the persona's skill, named plainly, must be injected too. {CodexDiagnosis(control)}");
    }

    /// <summary>The run injected no bundled skill the goal named, listed none, and left none installed in its config home.</summary>
    private static void AssertNoBundledSkillReachedTheModel(CodexRun run, IReadOnlyList<BundledSkill> bundled, CodexFixture fixture)
    {
        run.Result.Status.ShouldBe(SandboxStatus.Success, CodexDiagnosis(run));

        foreach (var skill in bundled)
        {
            Injected(run, skill.Content).ShouldBeFalse($"{skill.Name}, named as `{fixture.Spellings[skill.Name]}`, must not be injected into any request. {CodexDiagnosis(run)}");
            OutsideTheGoal(run).ShouldNotContain(text => text.Contains(skill.ListedPath, StringComparison.Ordinal), $"{skill.Name} must not be listed to the model. {CodexDiagnosis(run)}");
        }

        Directory.Exists(Path.Combine(run.ConfigHome, BundledSkillsDirectory)).ShouldBeFalse($"the CLI must install no bundled skill into the run's config home. {CodexDiagnosis(run)}");
    }

    /// <summary>The goal reached the model as one user message, byte for byte, the persona's skill it names was injected beside it — the scan ran on this text — and the harness's fold reads the model's answer.</summary>
    private static void AssertTheGoalReachedCodexVerbatim(CodexRun run, CodexFixture fixture)
    {
        var requests = ResponsesRequests(run);

        requests.ShouldNotBeEmpty($"the model must have been asked. {CodexDiagnosis(run)}");
        UserTexts(requests[^1]).ShouldContain(run.Goal, $"the goal must reach the model unchanged — nothing escaped, nothing dropped. {CodexDiagnosis(run)}");
        Injected(run, fixture.PersonaSkill(run.ConfigHome)).ShouldBeTrue($"the persona's skill, named in the same goal, must still be injected: the platform put it there. {CodexDiagnosis(run)}");

        var folded = Codex.BuildResult(run.Lines.SelectMany(Codex.ParseEvents).ToList(), run.Result.ExitCode, run.Result.Stderr);

        folded.Status.ShouldBe(AgentRunStatus.Succeeded, CodexDiagnosis(run));
        folded.Summary.ShouldBe(fixture.FinalText, $"the run's summary must be the model's answer to the goal. {CodexDiagnosis(run)}");
    }

    /// <summary>The continuation rode <c>exec resume</c> and carried the restored conversation, or it started cold and proves nothing about a continue.</summary>
    private static void AssertTheContinuationCarriedTheRestoredTurn(CodexRun run, CodexFixture fixture)
    {
        run.Spec.Args.Take(2).ShouldBe(["exec", "resume"], "fixture check: the continuation must ride exec resume");
        ResponsesRequests(run).Last().GetRawText().ShouldContain($"GOAL-first-{fixture.Nonce}", customMessage: $"the continuation must carry the restored conversation. {CodexDiagnosis(run)}");
    }

    /// <summary>The thread a finished run left: its id off <c>thread.started</c>, and its rollout read where the executor captures it (<see cref="CodexHarness.SessionTranscriptRelativePath"/>).</summary>
    private static Session CodexSessionOf(CodexRun run, CodexFixture fixture)
    {
        run.Result.Status.ShouldBe(SandboxStatus.Success, CodexDiagnosis(run));

        var threadId = run.Lines.Select(TryParse).OfType<JsonElement>().Where(line => Text(line, "type") == "thread.started").Select(line => Text(line, "thread_id")).FirstOrDefault() ?? "";

        threadId.ShouldNotBeEmpty($"the first run must report its thread. {CodexDiagnosis(run)}");

        var relative = Codex.SessionTranscriptRelativePath(run.ConfigHome, fixture.Workspace, threadId).ShouldNotBeNull($"the first run's rollout must be where a continue captures it. {CodexDiagnosis(run)}");

        return new Session(threadId, File.ReadAllText(Path.Combine(run.ConfigHome, relative)));
    }

    /// <summary>
    /// The run launched the way the executor launches it — durable, through the broker, carrying the persona's skill,
    /// continuing <paramref name="resume"/> when given — against a model that answers at once. A shipped Codex run at
    /// Standard, unless <paramref name="role"/> recasts it (as the output reviewer, whose permissions the lease follows).
    /// </summary>
    private async Task<CodexRun> RunCodexAsync(CodexFixture fixture, Func<string, string> goalFor, Func<SandboxSpec, SandboxSpec> shape, Session? resume = null, Func<AgentTask, AgentTask>? role = null)
    {
        var key = Guid.NewGuid().ToString("N");
        var configHome = LocalProcessRunner.ConfigHomePath(LocalProcessRunner.SpoolDirectoryFor(key));

        var shipped = new AgentTask
        {
            Goal = goalFor(PhysicalConfigHome(configHome)), Harness = CodexHarness.HarnessKind, Model = CodexModel, WorkspaceDirectory = fixture.Workspace, Permissions = AgentAutonomyPolicy.Derive(CodexTier), TimeoutSeconds = 300, Skills = [fixture.Skill],
            ResumeFromSessionId = resume?.Id, RestoredTranscript = resume?.Transcript,
        };
        var task = role?.Invoke(shipped) ?? shipped;

        var upstream = new ScriptedModelUpstream([], fixture.FinalText);
        using var broker = LoopbackModelCredentialBroker.ForTest(upstream);
        var brokered = await OpenLeaseAsync(broker, task.Permissions);

        task = task with { Environment = new Dictionary<string, string>(ReviewerReadsItsDiffE2ETests.Brokered(Codex, brokered)) { ["HOME"] = fixture.Home } };

        var spec = shape(ReviewerReadsItsDiffE2ETests.ProductionSpec(Codex, task, brokered));
        var runner = new LocalProcessRunner();
        var handle = await runner.LaunchAsync(spec, key, CancellationToken.None);

        _directories.Add(handle.SpoolDirectory);
        LocalProcessRunner.ConfigHomePath(handle.SpoolDirectory).ShouldBe(configHome, "fixture check: the config home the goal names must be the one this launch was given");

        var lines = new List<string>();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(task.TimeoutSeconds!.Value + 60));
        var result = await runner.AttachAsync(handle, (frame, _) => { lines.Add(frame.Text); return Task.CompletedTask; }, budget.Token);

        return new CodexRun(task.Goal, spec, configHome, lines, result, upstream.Requests);
    }

    /// <summary>
    /// The run's config home as the CLI spells a skill's path: it resolves every skill root to its physical directory,
    /// and a link names a skill only by that exact path. The spool root exists before the launch creates the run's own
    /// directory below it, so it is the part resolved (macOS keeps temp paths under the <c>/var</c> symlink).
    /// </summary>
    private static string PhysicalConfigHome(string configHome)
    {
        var spoolRoot = Directory.CreateDirectory(LocalProcessRunner.SpoolRoot()).FullName;

        return Path.Combine(PhysicalPath.Directory(spoolRoot), Path.GetRelativePath(spoolRoot, configHome));
    }

    /// <summary>Where the CLI installs its bundled skills, relative to its config home.</summary>
    private static readonly string BundledSkillsDirectory = Path.Combine(CodexHarness.SkillsRoot, ".system");

    /// <summary>Every skill the control CLI installed under <see cref="BundledSkillsDirectory"/>: its name, the path it is listed under, and the file it would inject.</summary>
    private static List<BundledSkill> BundledSkills(CodexRun control)
    {
        var root = Path.Combine(control.ConfigHome, BundledSkillsDirectory);

        if (!Directory.Exists(root)) return [];

        return Directory.EnumerateFiles(root, "SKILL.md", SearchOption.AllDirectories).Select(path => new BundledSkill(FrontmatterName(path), $"/{BundledSkillsDirectory}/{Path.GetFileName(Path.GetDirectoryName(path))}/SKILL.md", File.ReadAllText(path))).ToList();
    }

    /// <summary>The <c>name</c> a skill's frontmatter gives it — the one a mention resolves — bare or quoted, as the bundled files spell it both ways.</summary>
    private static string FrontmatterName(string path) => Regex.Match(File.ReadAllText(path), @"^name:\s*[""']?([^""'\s]+)[""']?\s*$", RegexOptions.Multiline).Groups[1].Value;

    /// <summary>Whether a skill's whole <c>SKILL.md</c> reached any request: the CLI injects the file entire, while a listing carries only its name, description and path.</summary>
    private static bool Injected(CodexRun run, string content) => ResponsesRequests(run).SelectMany(Texts).Any(text => text.Contains(content, StringComparison.Ordinal));

    /// <summary>Every text the requests carried except the goal itself, which names the bundled skills' paths on purpose.</summary>
    private static List<string> OutsideTheGoal(CodexRun run) => ResponsesRequests(run).SelectMany(Texts).Where(text => text != run.Goal).ToList();

    private static List<JsonElement> ResponsesRequests(CodexRun run) => run.Requests.Where(r => r.Path.EndsWith("/responses", StringComparison.Ordinal)).Select(r => TryParse(r.Body)).OfType<JsonElement>().ToList();

    /// <summary>The request's instructions and every text its input items carry, decoded.</summary>
    private static IEnumerable<string> Texts(JsonElement request)
    {
        if (request.TryGetProperty("instructions", out var instructions) && instructions.ValueKind == JsonValueKind.String) yield return instructions.GetString()!;

        if (!request.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Array) yield break;

        foreach (var text in input.EnumerateArray().SelectMany(ContentTexts)) yield return text;
    }

    /// <summary>The texts of the request's user messages.</summary>
    private static List<string> UserTexts(JsonElement request) => request.GetProperty("input").EnumerateArray().Where(item => Text(item, "role") == "user").SelectMany(ContentTexts).ToList();

    private static IEnumerable<string> ContentTexts(JsonElement item) =>
        item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array ? content.EnumerateArray().Select(block => Text(block, "text")).Where(text => text.Length > 0) : [];

    private static string CodexDiagnosis(CodexRun run) =>
        $"argv: {string.Join(' ', run.Spec.Args)}; requests: {ReviewerReadsItsDiffE2ETests.Describe(run.Requests)}; stdout tail: {Tail(string.Join('\n', run.Lines))}; stderr tail: {Tail(run.Result.Stderr)}; status {run.Result.Status} exit {run.Result.ExitCode}";

    /// <summary>A git workspace and a HOME, each empty of skills, and the persona's own skill, whose body is a marker no goal spells.</summary>
    private CodexFixture PlantCodexFixture()
    {
        var nonce = Guid.NewGuid().ToString("N");
        var workspace = NewDirectory("codex-skills-ws");
        var home = NewDirectory("codex-skills-home");

        File.WriteAllText(Path.Combine(workspace, "README.md"), "a repository\n");

        ReviewerReadsItsDiffE2ETests.GitOut(workspace, "init -q -b main");
        ReviewerReadsItsDiffE2ETests.GitOut(workspace, "add -A");
        ReviewerReadsItsDiffE2ETests.GitOut(workspace, "commit -q -m base");

        return new CodexFixture(nonce, workspace, home);
    }

    private sealed record CodexRun(string Goal, SandboxSpec Spec, string ConfigHome, IReadOnlyList<string> Lines, SandboxResult Result, IReadOnlyList<RecordedRequest> Requests);

    /// <summary>A skill the CLI bundles: the name it goes by, the config-home-relative path it is listed under, and its <c>SKILL.md</c>.</summary>
    private sealed record BundledSkill(string Name, string ListedPath, string Content);

    /// <summary>The workspace, HOME and persona skill of one arm, and the goal that names every bundled skill once.</summary>
    private sealed record CodexFixture(string Nonce, string Workspace, string Home)
    {
        public string FinalText => $"DONE-{Nonce}";

        /// <summary>The persona's own skill, projected the way every Codex run projects its persona's skills.</summary>
        public AgentSkill Skill => new() { Slug = $"persona-{Nonce[..8]}", Description = "The persona's own review checklist.", Body = $"PERSONA-SKILL-{Nonce}" };

        /// <summary>The persona skill's <c>SKILL.md</c> as the runner wrote it into <paramref name="configHome"/>, which is what the CLI injects.</summary>
        public string PersonaSkill(string configHome) => File.ReadAllText(Path.Combine(configHome, CodexHarness.SkillsRoot, Skill.Slug, "SKILL.md"));

        /// <summary>
        /// How the goal names each bundled skill of the measured CLI, one spelling per skill, each of which the pinned
        /// CLI resolves: at the start of a line, mid-sentence, glued to a word, after a backslash, and as a link to the
        /// file where the control installs it (<c>{0}</c> is the run's config home).
        /// </summary>
        public IReadOnlyDictionary<string, string> Spellings { get; } = new Dictionary<string, string>
        {
            ["skill-installer"] = "$skill-installer",
            ["plugin-creator"] = "then use $plugin-creator here",
            ["imagegen"] = "x$imagegen",
            ["openai-docs"] = "\\$openai-docs",
            ["skill-creator"] = "[$skill-creator]({0}/skills/.system/skill-creator/SKILL.md)",
        };

        /// <summary>A review goal naming every bundled skill on a line of its own, then the persona's skill plainly.</summary>
        public string SkillGoal(string configHome) => string.Join('\n',
        [
            $"Review this change. GOAL-{Nonce}",
            .. Spellings.Values.Select(spelling => string.Format(spelling, configHome)),
            $"Use ${Skill.Slug} for the checklist.",
        ]);
    }
}
