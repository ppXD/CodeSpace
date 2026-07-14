using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Messages.Agents.Benchmark;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.UnitTests.Agents.Benchmark;

/// <summary>
/// 🟢 Unit: the sealed-suite mechanism (v4.2 Q contract) — loads from OUTSIDE the repo, hashes to BYTES (an edited
/// fixture under an unchanged ref changes the suite hash — the M1a freeze hole closed for this lane), fails loud on
/// a configured-but-broken suite, self-skips only when unset, and the protocol manifest digest moves on ANY
/// component change.
/// </summary>
[Trait("Category", "Unit")]
public class HiddenSuiteLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hidden-suite-{Guid.NewGuid():N}");

    public HiddenSuiteLoaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ } }

    [Fact]
    public void The_env_var_name_is_pinned()
    {
        // Rule 8: renaming strands every sealed-lane deployment that exported the old name.
        HiddenSuiteLoader.SuiteDirEnvVar.ShouldBe("CODESPACE_HIDDEN_SUITE_DIR");
    }

    [Fact]
    public void A_suite_loads_with_a_bytes_level_hash_and_fixture_edits_move_it()
    {
        WriteTasks(Task("t1"));
        Directory.CreateDirectory(Path.Combine(_dir, "fixtures", "f1"));
        File.WriteAllText(Path.Combine(_dir, "fixtures", "f1", "check.sh"), "exit 1");

        var first = HiddenSuiteLoader.Load(_dir);
        first.Tasks.ShouldHaveSingleItem().Id.ShouldBe("t1");
        first.SuiteContentHash.ShouldStartWith("sha256/canonical-json-v1:");

        // Edit a FIXTURE BYTE under the same ref — the reference didn\u2019t change, the suite identity must.
        File.WriteAllText(Path.Combine(_dir, "fixtures", "f1", "check.sh"), "exit 0");

        HiddenSuiteLoader.Load(_dir).SuiteContentHash.ShouldNotBe(first.SuiteContentHash,
            "a fixture edited under an unchanged ref can never impersonate the frozen suite");
    }

    [Fact]
    public void A_configured_but_broken_suite_fails_loud()
    {
        Should.Throw<InvalidOperationException>(() => HiddenSuiteLoader.Load(_dir))
            .Message.ShouldContain("tasks.json");

        WriteTasksRaw("[]");
        Should.Throw<InvalidOperationException>(() => HiddenSuiteLoader.Load(_dir))
            .Message.ShouldContain("zero tasks", customMessage: "an empty qualification suite is a misconfiguration, not a pass");
    }

    [Fact]
    public void The_protocol_manifest_digest_moves_on_any_component_change()
    {
        var manifest = new EvaluationProtocolManifest
        {
            Tier = EvaluationTier.SealedQualification,
            SuiteContentHash = "sha256/canonical-json-v1:abc",
            TaskCount = 25,
            ModelId = "pinned-model-v1",
            EvaluatorVersion = "grader-v1",
            CodeSpaceCommit = "82b45716",
            CompletionPolicyVersion = 1,
        };

        var digest = manifest.Digest();
        digest.ShouldStartWith("sha256/canonical-json-v1:");

        (manifest with { ModelId = "pinned-model-v2" }).Digest().ShouldNotBe(digest);
        (manifest with { SuiteContentHash = "sha256/canonical-json-v1:zzz" }).Digest().ShouldNotBe(digest);
        (manifest with { EvaluatorVersion = "grader-v2" }).Digest().ShouldNotBe(digest);
        (manifest with { Tier = EvaluationTier.ShadowEvaluation }).Digest().ShouldNotBe(digest,
            "a tier change is a different protocol — sealed results never mix with shadow ones");
        manifest.Digest().ShouldBe(digest, "same components, same identity");
    }

    private void WriteTasks(params BenchmarkTask[] tasks) =>
        File.WriteAllText(Path.Combine(_dir, "tasks.json"), JsonSerializer.Serialize(tasks, CodeSpace.Core.Services.Agents.AgentJson.Options));

    private void WriteTasksRaw(string json) => File.WriteAllText(Path.Combine(_dir, "tasks.json"), json);

    private static BenchmarkTask Task(string id) => new()
    {
        Id = id,
        Description = "hidden probe",
        Goal = "fix it",
        FixtureRef = "f1",
        TestCommand = new[] { "sh", "check.sh" },
        Grading = BenchmarkGradingKind.TestsPass,
        Harness = "codex-cli",
        Modes = new[] { BenchmarkMode.HarnessCli },
    };
}
