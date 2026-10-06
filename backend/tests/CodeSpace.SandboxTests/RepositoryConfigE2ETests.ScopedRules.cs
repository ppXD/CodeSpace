using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.SandboxTests;

/// <summary>
/// Rules scoped by <c>paths:</c>, pointed at. No <c>--add-dir</c> loads a scoped rule, and the settings pin shuts the
/// route by which the unpinned CLI attached one once its Read tool opened a file the rule covers. The harness writes a
/// pointer rule of the user's own into the run's config home instead (<c>ClaudeWorkspaceMemory</c>), whose
/// <c>paths:</c> are the repository rule's rebased onto the cwd and whose body is one sentence of the runner's naming
/// the rule. The pinned CLI attaches it on the read the repository's own would have attached on, and on no other.
///
/// <para>Same fidelity as the class: the pinned binary, the production argv and runner, the production broker; only the
/// model is scripted, and it opens files with the CLI's own Read tool, so what attaches is the CLI's doing. The drift
/// detector (<see cref="Pointer_rules_attach_where_the_unpinned_cli_attached_project_rules"/>) runs one corpus of glob
/// shapes through the unpinned CLI, which attaches the repository's rules natively, and through the production run,
/// read for read: the rebase mirrors the CLI's gitignore anchoring, its brace and comma splitting and its <c>/**</c>
/// strip, so a CLI that changes any of them fails it. A pointer is runner text only: the rule's own text never reaches
/// the model through it, nor what that text imports — which, copied into the config home, the CLI would read before
/// the first request (the positive control).</para>
/// </summary>
public sealed partial class RepositoryConfigE2ETests
{
    /// <summary>
    /// The differential corpus: each rule's file below <c>.claude/rules</c> of the repository or its <c>pkg/</c>, its
    /// <c>paths:</c> as written — with whatever else its frontmatter holds after it — and whether the CLI scopes it; the
    /// one it does not loads up front in both runs and is no read's to attach. <c>c-dir</c> ends in a slash, which anchors
    /// nothing; <c>c-deep</c> still ends in <c>/**</c> after the CLI drops one, so its pointer must write one more; and
    /// <c>c-big</c>'s frontmatter closes past the 4 KiB the runner reads first.
    /// </summary>
    private static readonly (string File, string Paths, bool Scoped)[] PointerCorpus =
    [
        (".claude/rules/c-src.md", "[\"src/**\"]", true),
        (".claude/rules/c-ts.md", "\"*.ts\"", true),
        (".claude/rules/c-brace.md", "\"{lib/a,b}\"", true),
        (".claude/rules/c-comma.md", "\"docs/x, y\"", true),
        (".claude/rules/c-neg.md", "[\"gen/**/*.txt\", \"!gen/keep/*.txt\"]", true),
        (".claude/rules/c-anch.md", "[\"/top/**\"]", true),
        (".claude/rules/c-star.md", "[\"**\"]", false),
        (".claude/rules/c-deep.md", "\"src/**/**\"", true),
        (".claude/rules/c-big.md", $"[\"docs/big/**\"]\ndescription: {new string('x', 4200)}", true),
        ("pkg/.claude/rules/c-nested.md", "[\"*.md\"]", true),
        ("pkg/.claude/rules/c-dir.md", "[\"gen/\"]", true),
    ];

    /// <summary>
    /// The reads the differential makes in the first repository: those no rule covers first — a negation, a glob anchored
    /// elsewhere, a nested rule's directory missed — so one that wrongly attached is seen before a later read could
    /// attach the same rule rightly; then one for each rule. <c>other/src/a.txt</c> is covered by <c>c-src</c> alone: the
    /// CLI drops <c>src/**</c>'s <c>/**</c> and reads <c>src</c>, which matches at any depth, while <c>c-deep</c>'s
    /// <c>src/**</c> is anchored, so only <c>src/a.txt</c> attaches it. <c>pkg/x/gen/f.txt</c> is below a <c>gen</c>
    /// directory two levels into <c>pkg/</c>, which <c>c-dir</c>'s <c>gen/</c> covers at any depth.
    /// </summary>
    private static readonly string[] PointerReads = ["nomatch/z.txt", "gen/keep/b.txt", "other/lib/a/f.txt", "other/top/f.txt", "nopkg/n.md", "other/src/a.txt", "src/a.txt", "x/y.ts", "lib/a/f.txt", "docs/x/f.txt", "docs/big/f.txt", "gen/a.txt", "pkg/d/n.md", "pkg/x/gen/f.txt", "top/t.txt"];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public Task A_scoped_rule_reaches_the_model_only_after_a_read_it_matches(int repositories) => ClaudeAttachesAScopedRulePointerAsync(AgentAutonomyLevel.Confined, repositories, lane: "root");

    /// <summary>
    /// The drift detector (Rule 12.5): the corpus run unpinned — the production spec without <c>--setting-sources user</c>
    /// and without its pointers, so the CLI attaches each repository's rules itself — and run as production builds it.
    /// For every read, the rules attached must be the same; the negatives must attach none in either; the production
    /// run must carry one pointer per scoped rule and none for the rule the CLI reads without globs, and none of them may
    /// be in its first request, where an unconditional pointer would load; and nothing a rule says may reach the
    /// production run's model. Root lane, Confined.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Pointer_rules_attach_where_the_unpinned_cli_attached_project_rules(int repositories)
    {
        const string harnessKind = ClaudeCodeHarness.HarnessKind;
        const AgentAutonomyLevel tier = AgentAutonomyLevel.Confined;
        var harness = ReviewerReadsItsDiffE2ETests.HarnessFor(harnessKind);

        if (!ReviewerReadsItsDiffE2ETests.Armed(harnessKind) || OperatingSystem.IsWindows()) return;

        await ReviewerReadsItsDiffE2ETests.RequirePinnedBinaryAsync(harness, harnessKind);

        using var hostile = new ConnectionCounter();
        var workspace = NewWorkspace(repositories);
        var reads = PointerReads.Select(read => Path.Combine(workspace.Repositories[0].Directory, read)).Concat(workspace.Repositories.Skip(1).Select(repo => Path.Combine(repo.Directory, "x", "y.ts"))).ToList();

        foreach (var repo in workspace.Repositories) PlantPointerCorpus(repo);

        var (unpinnedSpec, unpinnedRun, unpinned) = await RunAsync(harness, workspace, tier, task => task, ReadingUpstream(workspace, reads), reshape: WithoutTheSettingsPinOrPointers);
        var (spec, run, upstream) = await RunAsync(harness, workspace, tier, task => task, ReadingUpstream(workspace, reads));

        var first = upstream.Requests.FirstOrDefault(OffersTools)?.Body ?? "";
        var native = reads.Select((_, step) => Attached(workspace, ToolResult(unpinned, step), NativeRule())).ToList();
        var pointed = reads.Select((_, step) => Attached(workspace, ToolResult(upstream, step), PointedRule())).ToList();
        var described = string.Join("; ", reads.Select((read, step) => $"{Path.GetRelativePath(workspace.Directory, read)}: native [{string.Join(", ", native[step])}] pointed [{string.Join(", ", pointed[step])}]"));

        BrokerViolations(unpinnedRun, unpinned, hostile, workspace).ShouldBeEmpty($"fixture check: the unpinned run must run to its answer. {Diagnosis(harnessKind, unpinnedSpec, unpinnedRun, unpinned)}");
        unpinnedSpec.Args.ShouldNotContain("--setting-sources", "fixture check: the control is the unpinned CLI");
        native.Take(5).ShouldAllBe(attached => attached.Count == 0, $"fixture check: the corpus's negatives attach nothing natively, or they test nothing. {described}");
        native.Count(attached => attached.Count > 0).ShouldBe(PointerCorpus.Count(rule => rule.Scoped) + repositories - 1, $"fixture check: natively, each scoped rule attaches on exactly one read, or the corpus distinguishes less than it claims. {described}");

        BrokerViolations(run, upstream, hostile, workspace).ShouldBeEmpty(Diagnosis(harnessKind, spec, run, upstream));
        PointerFiles(spec).Count.ShouldBe(PointerCorpus.Count(rule => rule.Scoped) * repositories, "one pointer per scoped rule of every repository");
        PointerFiles(spec).ShouldNotContain(file => file.Content.Contains("c-star.md", StringComparison.Ordinal), "the rule the CLI reads without globs loads in place: a pointer to it would be unconditional");
        PointersUpFront(first).ShouldBeEmpty($"no pointer may load before the first request: one the CLI read without globs would carry the user's authority. {Diagnosis(harnessKind, spec, run, upstream)}");
        Enumerable.Range(0, reads.Count).Where(step => !ToolResult(upstream, step).Contains(SurfaceText(workspace.Repositories[step < PointerReads.Length ? 0 : step - PointerReads.Length + 1], "READ"), StringComparison.Ordinal)).ShouldBeEmpty($"fixture check: every scripted read handed its file back. {Diagnosis(harnessKind, spec, run, upstream)}");
        pointed.ShouldBe(native, $"the pointers must attach on exactly the reads the unpinned CLI attached the repository's own rules on. {described}");
        reads.Select((_, step) => Attached(workspace, ToolResult(upstream, step), NativeRule())).ShouldAllBe(attached => attached.Count == 0, $"the pinned run attaches no repository rule itself, only pointers to them. {described}");
        CorpusTexts(workspace, scoped: true).Where(text => upstream.Requests.Any(r => r.Body.Contains(text, StringComparison.Ordinal))).ShouldBeEmpty($"a pointer carries no repository text: no scoped rule's own text may reach the production run's model. {Diagnosis(harnessKind, spec, run, upstream)}");
        CorpusTexts(workspace, scoped: false).Where(text => !first.Contains(text, StringComparison.Ordinal)).ShouldBeEmpty($"fixture check: the rule the CLI reads with no globs loads before the first request, as it did unpinned. {Diagnosis(harnessKind, spec, run, upstream)}");

        output.WriteLine($"{RanMarker} pointer-differential {harnessKind} {(repositories == 1 ? "single-repo" : "multi-repo")} {tier} uid={NonRootWorker.EffectiveUid()} confined={BubblewrapSandbox.Available is not null} reads={reads.Count} attached={native.Count(attached => attached.Count > 0)}");
    }

    /// <summary>
    /// The scoped-rule arm, for either lane: each repository's <c>.claude/rules/ts.md</c> scoped to <c>sub/**/*.ts</c>,
    /// whose text tries what a copied rule would do in the config home — an import climbing to the run's MCP declaration
    /// there, and the same through <c>~</c> (HOME is the config home under bubblewrap). The scripted model reads the
    /// first repository's <c>other/sub/x.ts</c>, which the rule's anchored glob does not cover, then its
    /// <c>sub/x.ts</c>. The pointer must be in no request up front or after the first read, and in the second read's
    /// result; another repository's pointer in none; and neither the rule's text nor the declaration's token in any.
    /// The positive control writes each pointer as the repository's rule itself, which hands the token to the model.
    /// </summary>
    internal async Task ClaudeAttachesAScopedRulePointerAsync(AgentAutonomyLevel tier, int repositories, string lane)
    {
        const string harnessKind = ClaudeCodeHarness.HarnessKind;
        var harness = ReviewerReadsItsDiffE2ETests.HarnessFor(harnessKind);

        if (!ReviewerReadsItsDiffE2ETests.Armed(harnessKind) || OperatingSystem.IsWindows()) return;

        await ReviewerReadsItsDiffE2ETests.RequirePinnedBinaryAsync(harness, harnessKind);

        using var hostile = new ConnectionCounter();
        var workspace = NewWorkspace(repositories);
        var read = workspace.Repositories[0];
        var token = $"REPO-MCP-TOKEN-{workspace.Nonce}";
        var home = NewDirectory("repo-config-home");
        var reads = new[] { Path.Combine(read.Directory, "other", "sub", "x.ts"), Path.Combine(read.Directory, "sub", "x.ts") };

        File.WriteAllText(Path.Combine(home, ".mcp.json"), McpDeclaration(token));

        foreach (var repo in workspace.Repositories) PlantScopedRule(repo);

        Task<(SandboxSpec Spec, Run Run, ScriptedModelUpstream Upstream)> Launch(Func<SandboxSpec, SandboxSpec> reshape) =>
            RunAsync(harness, workspace, tier, task => task with { Environment = new Dictionary<string, string>(task.Environment) { ["HOME"] = home } }, ReadingUpstream(workspace, reads), production => reshape(production with { ConfigHomeFiles = [.. production.ConfigHomeFiles, new ConfigHomeFile { RelativePath = ".mcp.json", Content = McpDeclaration(token) }] }));

        var (controlSpec, controlRun, control) = await Launch(WithPointersCopiedFromTheirRules);
        var (spec, run, upstream) = await Launch(production => production);

        var first = upstream.Requests.FirstOrDefault(OffersTools)?.Body ?? "";
        var pointer = RuleSentence(ScopedRuleOf(read));

        BrokerViolations(controlRun, control, hostile, workspace).ShouldBeEmpty($"fixture check: the control must run to its answer. {Diagnosis(harnessKind, controlSpec, controlRun, control)}");
        control.Requests.ShouldContain(r => r.Body.Contains(token, StringComparison.Ordinal), $"positive control: a repository rule copied into the config home imports the run's MCP declaration, so the token reaching no request below is the pointer's doing. {Diagnosis(harnessKind, controlSpec, controlRun, control)}");

        BrokerViolations(run, upstream, hostile, workspace).ShouldBeEmpty(Diagnosis(harnessKind, spec, run, upstream));
        reads.Select((_, step) => ToolResult(upstream, step)).Zip(new[] { "OTHER-TS", "SUB-TS" }).Where(pair => !pair.First.Contains(SurfaceText(read, pair.Second), StringComparison.Ordinal)).ShouldBeEmpty($"fixture check: both scripted reads handed their file back. {Diagnosis(harnessKind, spec, run, upstream)}");
        first.ShouldContain(SurfaceText(read, "MEMORY"), Case.Sensitive, $"fixture check: the repository's own memory loads, so its directory passed the guard and could be pointed into. {Diagnosis(harnessKind, spec, run, upstream)}");
        spec.ConfigHomeFiles.Where(file => file.RelativePath.StartsWith(ClaudeWorkspaceMemory.PointerRulePrefix, StringComparison.Ordinal)).Select(file => (file.RelativePath, file.Content)).ShouldBe(workspace.Repositories.Select((repo, i) => ($"{ClaudeWorkspaceMemory.PointerRulePrefix}{i:000}.md", ExpectedPointer(workspace, repo))), "one pointer per repository, its glob rebased onto the cwd, its body the runner's sentence alone");
        first.ShouldNotContain(pointer, Case.Sensitive, $"a scoped rule's pointer is no instruction up front. {Diagnosis(harnessKind, spec, run, upstream)}");
        ToolResult(upstream, 0).ShouldNotContain(ClaudeWorkspaceMemory.PointerRulePrefix.Split('/')[1], Case.Sensitive, $"other/sub/x.ts is no file the anchored sub/**/*.ts covers, so no pointer may attach on it. {Diagnosis(harnessKind, spec, run, upstream)}");
        ToolResult(upstream, 1).ShouldContain(pointer, Case.Sensitive, $"sub/x.ts is covered: its read must attach the pointer to the rule. {Diagnosis(harnessKind, spec, run, upstream)}");
        workspace.Repositories.Skip(1).Where(repo => upstream.Requests.Any(r => r.Body.Contains(RuleSentence(ScopedRuleOf(repo)), StringComparison.Ordinal))).Select(repo => repo.Directory).ShouldBeEmpty($"a read in one repository attaches no other repository's pointer. {Diagnosis(harnessKind, spec, run, upstream)}");
        workspace.Repositories.Where(repo => upstream.Requests.Any(r => r.Body.Contains(SurfaceText(repo, "SCOPED-RULE"), StringComparison.Ordinal))).Select(repo => repo.Directory).ShouldBeEmpty($"a pointer carries no repository text: the rule's own text reaches no request. {Diagnosis(harnessKind, spec, run, upstream)}");
        upstream.Requests.ShouldNotContain(r => r.Body.Contains(token, StringComparison.Ordinal), $"nothing the rule imports may load: the run's MCP token reached the model. {Diagnosis(harnessKind, spec, run, upstream)}");

        output.WriteLine($"{RanMarker} {(lane == "root" ? "" : lane + " ")}scoped-rule-pointer {harnessKind} {(repositories == 1 ? "single-repo" : "multi-repo")} {tier} uid={NonRootWorker.EffectiveUid()} confined={BubblewrapSandbox.Available is not null}");
    }

    /// <summary>One repository's memory, its rule scoped to <c>sub/**/*.ts</c> with what a copied rule would import, and the two files the arm reads.</summary>
    private static void PlantScopedRule(Repository repo)
    {
        repo.Commit("CLAUDE.md", $"{Mention(repo, "MEMORY")}\n");
        repo.Commit(".claude/rules/ts.md", $"---\npaths:\n  - \"sub/**/*.ts\"\n---\n{Mention(repo, "SCOPED-RULE")}\n\n@../.mcp.json\n\n@~/.mcp.json\n");
        repo.Commit("sub/x.ts", $"{SurfaceText(repo, "SUB-TS")}\n");
        repo.Commit("other/sub/x.ts", $"{SurfaceText(repo, "OTHER-TS")}\n");
    }

    /// <summary>The differential corpus in one repository (<see cref="PointerCorpus"/>), each rule carrying its own text, and every file the reads open, each carrying the repository's <c>READ</c> text.</summary>
    private static void PlantPointerCorpus(Repository repo)
    {
        foreach (var (file, paths, _) in PointerCorpus) repo.Commit(file, $"---\npaths: {paths}\n---\n{Mention(repo, CorpusSlug(file))}\n");

        foreach (var read in PointerReads.Append("x/y.ts").Distinct()) repo.Commit(read, $"{SurfaceText(repo, "READ")}\n");
    }

    /// <summary>The text slug of one corpus rule, from its file name.</summary>
    private static string CorpusSlug(string file) => Path.GetFileNameWithoutExtension(file).ToUpperInvariant();

    /// <summary>The text every corpus rule of every repository carries that the CLI scopes, or that it does not.</summary>
    private static IEnumerable<string> CorpusTexts(Workspace workspace, bool scoped) =>
        workspace.Repositories.SelectMany(repo => PointerCorpus.Where(rule => rule.Scoped == scoped).Select(rule => SurfaceText(repo, CorpusSlug(rule.File))));

    /// <summary>A scripted model that reads each of <paramref name="reads"/> with the CLI's own Read tool, in order, then answers.</summary>
    private static ScriptedModelUpstream ReadingUpstream(Workspace workspace, IEnumerable<string> reads) =>
        new([], $"DONE-{workspace.Nonce}") { ClaudeCalls = reads.Select(path => new ScriptedToolCall("Read", new JsonObject { ["file_path"] = path })).ToList() };

    /// <summary>The unpinned control: the production spec without its settings pin and without its pointer rules, so the CLI attaches the repository's rules itself.</summary>
    private static SandboxSpec WithoutTheSettingsPinOrPointers(SandboxSpec spec)
    {
        var args = spec.Args.ToList();
        var at = args.IndexOf("--setting-sources");

        args.RemoveRange(at, 2);

        return spec with { Args = args, ConfigHomeFiles = spec.ConfigHomeFiles.Where(file => !file.RelativePath.StartsWith(ClaudeWorkspaceMemory.PointerRulePrefix, StringComparison.Ordinal)).ToList() };
    }

    /// <summary>The positive control: every pointer rule replaced by the text of the repository rule it names, as a harness that copied repository rules into the config home would write it.</summary>
    private static SandboxSpec WithPointersCopiedFromTheirRules(SandboxSpec spec) => spec with
    {
        ConfigHomeFiles = spec.ConfigHomeFiles.Select(file => file.RelativePath.StartsWith(ClaudeWorkspaceMemory.PointerRulePrefix, StringComparison.Ordinal) ? file with { Content = File.ReadAllText(PointedRule().Match(file.Content).Groups[1].Value) } : file).ToList(),
    };

    /// <summary>A repository's scoped rule, as the cwd spells it.</summary>
    private static string ScopedRuleOf(Repository repo) => Path.Combine(repo.Directory, ".claude", "rules", "ts.md");

    /// <summary>The sentence a pointer to <paramref name="rule"/> carries — the production template, spelled out.</summary>
    private static string RuleSentence(string rule) => $"The repository rule `{rule}` applies to the file you just read. Read it now and follow it for files it matches.";

    /// <summary>The whole pointer file a repository's scoped rule gets: its glob as written at a single repository's root, rebased below the workspace root otherwise.</summary>
    private static string ExpectedPointer(Workspace workspace, Repository repo)
    {
        var below = Path.GetRelativePath(workspace.Directory, repo.Directory);

        return $"---\npaths:\n  - \"{(below == "." ? "sub/**/*.ts" : $"/{below}/sub/**/*.ts")}\"\n---\n{RuleSentence(ScopedRuleOf(repo))}\n";
    }

    /// <summary>A declaration carrying <paramref name="token"/> as its bearer, as the run's MCP declaration carries its run token.</summary>
    private static string McpDeclaration(string token) => new JsonObject { ["mcpServers"] = new JsonObject { ["codespace"] = new JsonObject { ["type"] = "http", ["url"] = "http://127.0.0.1:9/mcp", ["headers"] = new JsonObject { ["Authorization"] = $"Bearer {token}" } } } }.ToJsonString();

    /// <summary>The text of the <c>tool_result</c> the CLI sent back for the scripted call at <paramref name="step"/> — the main loop's, or a subagent's by its <paramref name="prefix"/> — empty when none did.</summary>
    private static string ToolResult(ScriptedModelUpstream upstream, int step, string prefix = ScriptedModelUpstream.ToolIdPrefix) =>
        upstream.Requests.Select(r => TryParse(r.Body)).OfType<JsonElement>().SelectMany(body => Items(body, "messages")).SelectMany(message => Items(message, "content"))
            .Where(block => Text(block, "type") == "tool_result" && Text(block, "tool_use_id") == prefix + step)
            .Select(block => block.TryGetProperty("content", out var content) ? content.ToString() : "").FirstOrDefault() ?? "";

    /// <summary>Every way a pointer's sentence starts, a rule's or a directory's, that a run's first request holds: none may.</summary>
    private static IEnumerable<string> PointersUpFront(string first) => new[] { "The repository rule `", "Repository instructions for `" }.Where(start => first.Contains(start, StringComparison.Ordinal));

    /// <summary>The pointer rules a spec's config home carries.</summary>
    private static List<ConfigHomeFile> PointerFiles(SandboxSpec spec) => spec.ConfigHomeFiles.Where(file => file.RelativePath.StartsWith(ClaudeWorkspaceMemory.PointerRulePrefix, StringComparison.Ordinal)).ToList();

    /// <summary>Every rule <paramref name="pattern"/> finds in one result, relative to the workspace and sorted: the same rule however the CLI spells the workspace (the physical path, under macOS's <c>/var</c> link).</summary>
    private static IReadOnlyList<string> Attached(Workspace workspace, string result, Regex pattern)
    {
        var name = Path.GetFileName(workspace.Directory) + "/";

        return pattern.Matches(result).Select(match => match.Groups[1].Value).Where(path => path.Contains(name, StringComparison.Ordinal)).Select(path => path[(path.IndexOf(name, StringComparison.Ordinal) + name.Length)..]).Where(path => Path.GetFileName(path).StartsWith("c-", StringComparison.Ordinal) && Path.GetFileName(path) != "c-star.md").Distinct().Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>A repository rule the CLI attaches itself: its contents, headed by its path.</summary>
    [GeneratedRegex(@"Contents of ([^\s:\\]+/\.claude/rules/[^\s:\\]+\.md)")]
    private static partial Regex NativeRule();

    /// <summary>A repository rule a pointer names.</summary>
    [GeneratedRegex("The repository rule `([^`]+)`")]
    private static partial Regex PointedRule();
}
