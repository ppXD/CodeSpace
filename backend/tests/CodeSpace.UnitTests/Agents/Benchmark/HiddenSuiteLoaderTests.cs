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
    public void The_default_suite_location_is_pinned()
    {
        // Default-on, no env toggle (owner ruling): ONE conventional path — moving it is an explicit decision.
        HiddenSuiteLoader.DefaultSuiteDirectory.ShouldBe(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codespace", "hidden-suite"));
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

    [Fact]
    public void Loaded_suite_stages_its_own_nested_binary_and_empty_directory_content()
    {
        WriteFixture();
        var binary = new byte[] { 0, 255, 1, 128, 10 };
        Directory.CreateDirectory(Path.Combine(_dir, "fixtures/f1/nested/empty"));
        File.WriteAllBytes(Path.Combine(_dir, "fixtures/f1/nested/data.bin"), binary);
        var suite = HiddenSuiteLoader.Load(_dir);
        var target = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(target);

        suite.FixtureStager.Stage("f1", target);

        File.ReadAllText(Path.Combine(target, "check.sh")).ShouldBe("exit 1\n");
        File.ReadAllBytes(Path.Combine(target, "nested/data.bin")).ShouldBe(binary);
        Directory.Exists(Path.Combine(target, "nested/empty")).ShouldBeTrue();
        File.Exists(Path.Combine(target, "tasks.json")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("f1/../../outside")]
    [InlineData("f1\\outside")]
    public void An_unsafe_fixture_reference_is_rejected_when_loading(string reference)
    {
        WriteFixture();
        WriteTasks(Task("t1") with { FixtureRef = reference });
        Should.Throw<InvalidOperationException>(() => HiddenSuiteLoader.Load(_dir));
    }

    [Fact]
    public void A_missing_referenced_fixture_is_not_replaced_with_a_seed()
    {
        WriteTasks(Task("t1"));
        Should.Throw<InvalidOperationException>(() => HiddenSuiteLoader.Load(_dir));
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("add")]
    [InlineData("remove")]
    public void A_fixture_changed_after_freezing_cannot_be_staged(string change)
    {
        WriteFixture();
        var suite = HiddenSuiteLoader.Load(_dir);
        var path = Path.Combine(_dir, "fixtures/f1/check.sh");
        if (change == "edit") File.WriteAllText(path, "exit 0\n");
        if (change == "add") File.WriteAllText(path + ".new", "extra");
        if (change == "remove") File.Delete(path);
        var target = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(target);
        Should.Throw<InvalidOperationException>(() => suite.FixtureStager.Stage("f1", target));
    }

    [Fact]
    public void Source_symlinks_are_rejected_without_following_the_target()
    {
        WriteFixture();
        File.CreateSymbolicLink(Path.Combine(_dir, "fixtures/f1/link"), Path.Combine(_dir, "tasks.json"));
        Should.Throw<InvalidOperationException>(() => HiddenSuiteLoader.Load(_dir));
    }

    [Fact]
    public void Executable_permissions_are_preserved_and_bound_to_the_digest()
    {
        if (OperatingSystem.IsWindows()) return;
        WriteFixture();
        var path = Path.Combine(_dir, "fixtures/f1/check.sh");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var before = HiddenSuiteLoader.Load(_dir);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var after = HiddenSuiteLoader.Load(_dir);
        after.SuiteContentHash.ShouldNotBe(before.SuiteContentHash);
        var target = Path.Combine(_dir, "workspace");
        Directory.CreateDirectory(target);
        after.FixtureStager.Stage("f1", target);
        File.GetUnixFileMode(Path.Combine(target, "check.sh")).ShouldBe(File.GetUnixFileMode(path));
    }

    private void WriteFixture()
    {
        WriteTasks(Task("t1"));
        Directory.CreateDirectory(Path.Combine(_dir, "fixtures/f1"));
        File.WriteAllText(Path.Combine(_dir, "fixtures/f1/check.sh"), "exit 1\n");
    }

    [Fact]
    public void Distinct_suites_with_the_same_reference_never_share_a_stager_or_fallback()
    {
        WriteFixture();
        var first = HiddenSuiteLoader.Load(_dir);
        var secondRoot = Path.Combine(_dir, "second");
        Directory.CreateDirectory(Path.Combine(secondRoot, "fixtures/f1"));
        File.Copy(Path.Combine(_dir, "tasks.json"), Path.Combine(secondRoot, "tasks.json"));
        File.WriteAllText(Path.Combine(secondRoot, "fixtures/f1/check.sh"), "exit 2\n");
        var second = HiddenSuiteLoader.Load(secondRoot);
        first.SuiteContentHash.ShouldNotBe(second.SuiteContentHash);
        foreach (var (suite, expected, name) in new[] { (first, "exit 1\n", "one"), (second, "exit 2\n", "two") })
        {
            var target = Path.Combine(_dir, name);
            Directory.CreateDirectory(target);
            suite.FixtureStager.Stage("f1", target);
            File.ReadAllText(Path.Combine(target, "check.sh")).ShouldBe(expected);
            Should.Throw<InvalidOperationException>(() => suite.FixtureStager.Stage("unknown", target));
        }
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
