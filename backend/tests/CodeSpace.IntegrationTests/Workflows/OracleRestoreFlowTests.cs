using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 High fidelity (real git, real clone, real check execution): the ORACLE-TAMPER floor — a worker cannot buy a
/// pass by editing the judge that grades it.
///
/// <para><b>Why this tier is mandatory.</b> The restore is a sequence of real git commands whose SEMANTICS are the
/// whole feature. The unit suite drives them through a recording runner that asserts the argv strings and then
/// scripts them as successful — so it proves the grader asks git for the right thing, and nothing at all about what
/// git then does. Every integration and E2E acceptance grade leaves <c>ProtectedPaths</c> null, so before this file
/// no test anywhere had executed the restore against a real repository.</para>
///
/// <para>That gap hid a real hole. <c>git checkout &lt;base&gt; -- &lt;paths&gt;</c> restores files that EXIST at
/// base; it does not remove files the worker ADDED under a protected path. A test directory is exactly where an
/// auto-discovered hook lives — pytest loads any <c>conftest.py</c> it finds, and the shell case below is the same
/// shape — so a worker could leave the oracle's own files pristine, add one file beside them, and still bend the
/// verdict while the evidence announced <c>ORACLE TAMPER VOIDED</c>.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OracleRestoreFlowTests
{
    /// <summary>The oracle: sources an optional setup hook, then runs its one case. The hook does not exist at base — modelled on pytest's conftest.py, which is auto-loaded purely by being present.</summary>
    private const string CheckScript = "#!/bin/sh\n[ -f tests/env.sh ] && . tests/env.sh\nsh tests/case.sh\n";

    private static readonly string[] Protected = { "check.sh", "tests/" };

    private readonly PostgresFixture _fixture;

    public OracleRestoreFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Rewriting_the_oracles_own_files_does_not_buy_a_pass()
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = CheckScript, ["tests/case.sh"] = "exit 1\n" });
        var baseSha = await remote.HeadShaAsync();

        // The worker flips its own judge green — the blunt attack the restore exists to void.
        await remote.CommitOnBranchAsync("candidate", new() { ["tests/case.sh"] = "exit 0\n" });

        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        var grade = await GradeAsync(repoId, teamId, "candidate", baseSha);

        grade.Passed.ShouldBeFalse("the candidate rewrote the failing case to exit 0 — the restore must put the oracle's own bytes back, or a worker grades itself");
        grade.EvidenceTail.ShouldContain("TAMPER VOIDED", Case.Insensitive, "the operator must be told the candidate touched its judge, not merely that the check failed");
    }

    [Fact]
    public async Task Adding_a_file_beside_the_oracle_does_not_buy_a_pass_either()
    {
        // The subtle attack, and the one that shipped working. Nothing the oracle owns is edited: `check.sh` and
        // `tests/case.sh` are byte-identical to base. The worker only ADDS a file the oracle auto-loads.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = CheckScript, ["tests/case.sh"] = "exit 1\n" });
        var baseSha = await remote.HeadShaAsync();

        await remote.CommitOnBranchAsync("candidate", new() { ["tests/env.sh"] = "exit 0\n" });

        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        var grade = await GradeAsync(repoId, teamId, "candidate", baseSha);

        grade.Passed.ShouldBeFalse(
            "the candidate added tests/env.sh — a file the oracle sources — and every byte the oracle already owned was left untouched. "
          + "Restoring only the paths that EXIST at base leaves the addition in place, so the check short-circuits to 0 and the run reports a pass it did not earn. "
          + "A protected path must end up byte-identical to base, which means additions are removed, not just edits reverted.");

        // Failing is not enough — it has to fail for the RIGHT reason. Every fail-closed arm of this grader also
        // yields Passed == false, so `Passed` alone cannot tell "the addition was swept and the restored oracle
        // honestly failed the candidate" apart from "grading collapsed before it ever ran". Reverting the shallow-
        // clone fix produces exactly the latter, and this test stayed green through it until the class was pinned.
        grade.Class.ShouldNotBe(GradeFailureClass.Environment,
            $"the grade fell over instead of grading (detail='{grade.Detail}') — an infra collapse reads as a verdict here, which is how the oracle floor was silently off before");
        grade.Detail.ShouldNotContain("oracle-restore-failed", Case.Insensitive, "the restore itself must succeed; only the CHECK is allowed to fail");
    }

    [Fact]
    public async Task An_untouched_candidate_is_graded_on_its_merits_and_says_so()
    {
        // The scope fence: the restore must not become a way to fail honest work, and its evidence line must not cry
        // tamper at a candidate that changed nothing the oracle owns.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new() { ["check.sh"] = CheckScript, ["tests/case.sh"] = "exit 0\n" });
        var baseSha = await remote.HeadShaAsync();

        await remote.CommitOnBranchAsync("candidate", new() { ["src/feature.txt"] = "the actual work\n" });

        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        var grade = await GradeAsync(repoId, teamId, "candidate", baseSha);

        grade.Passed.ShouldBeTrue("the candidate touched nothing the oracle owns and the check passes — restoring protected paths must be a no-op here");
        (grade.EvidenceTail ?? "").ShouldNotContain("TAMPER", Case.Insensitive, "calling honest work tamper would teach the operator to ignore the warning");
    }

    // ── Chassis ──────────────────────────────────────────────────────────────────────

    private async Task<BenchmarkGrade> GradeAsync(Guid repoId, Guid teamId, string branch, string baseSha)
    {
        using var scope = _fixture.BeginScope();
        var spec = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" }, ProtectedPaths = Protected };

        return await scope.Resolve<ISupervisorAcceptanceGrader>().GradeAsync(repoId, teamId, branch, spec, 60, baseSha, CancellationToken.None);
    }

    private async Task<Guid> SeedTeamAsync() => (await WorkflowsTestSeed.SeedTeamAsync(_fixture)).TeamId;

    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = "oracle-restore-token" })),
            Status = CredentialStatus.Active,
        });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = "main", CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-oracle-restore-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;
        private readonly string _seed;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
            _seed = Path.Combine(_root, "seed");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedBaseAsync(Dictionary<string, string> files)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);

            Directory.CreateDirectory(_seed);
            await Git(_seed, "clone", _bare, _seed);
            await Git(_seed, "config", "user.email", "test@codespace.dev");
            await Git(_seed, "config", "user.name", "Test");
            await Git(_seed, "config", "commit.gpgsign", "false");

            await WriteAsync(files);
            await Git(_seed, "add", "-A");
            await Git(_seed, "commit", "-m", "seed");
            await Git(_seed, "push", "origin", "main");
        }

        public async Task<string> HeadShaAsync() => (await Git(_seed, "rev-parse", "HEAD")).Trim();

        /// <summary>The candidate's commit — branched from the CURRENT head so the base sha the grader restores from is a real ancestor.</summary>
        public async Task CommitOnBranchAsync(string branch, Dictionary<string, string> files)
        {
            await Git(_seed, "checkout", "-b", branch);

            await WriteAsync(files);
            await Git(_seed, "add", "-A");
            await Git(_seed, "commit", "-m", "candidate work");
            await Git(_seed, "push", "origin", branch);
        }

        private async Task WriteAsync(Dictionary<string, string> files)
        {
            foreach (var (name, content) in files)
            {
                var path = Path.Combine(_seed, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content);
            }
        }

        private static async Task<string> Git(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success || result.ExitCode != 0)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr}");

            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
