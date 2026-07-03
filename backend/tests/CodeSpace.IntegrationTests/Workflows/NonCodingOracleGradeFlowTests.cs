using System.Text.Json;
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
/// 🟢 HIGH fidelity (Rule 12): the S7 NON-CODING oracles end to end through the REAL
/// <see cref="ISupervisorAcceptanceGrader"/> — a real bare git remote, a real agent-independent clone, the real
/// grader registry, and (for the rubric) the REAL <c>LlmRubricJudge</c> resolving a pinned pool row through real
/// Postgres. Only the judge's network call is the content-keyed deterministic fake, so a verdict flips ONLY when
/// the committed deliverable actually changes — the acceptance for research/data output is proven as a server-run
/// check on real files, never a self-report.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NonCodingOracleGradeFlowTests
{
    private readonly PostgresFixture _fixture;

    public NonCodingOracleGradeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task The_rubric_judge_grades_the_committed_deliverable_and_the_threshold_math_holds()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();
        var judgeRowId = (await WorkflowsTestSeed.SeedCredentialedModelAsync(_fixture, teamId, "judge-model", provider: DeterministicJudgeLlmClient.ProviderTag)).RowId;

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new()
        {
            ["report.md"] = $"findings… {DeterministicJudgeLlmClient.MeetsMarker("cites")} body text\n",   // meets ONE of two criteria
        });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        var rubric = new AcceptanceRubric
        {
            Criteria = new[]
            {
                new AcceptanceRubricCriterion { Id = "cites", Requirement = "cites sources" },
                new AcceptanceRubricCriterion { Id = "risks", Requirement = "names the risks" },
            },
            JudgeModelId = judgeRowId,
        };

        var spec = new SupervisorAcceptanceSpec { Command = new[] { "report.md" }, Kind = BenchmarkGradingKind.LlmJudge, Rubric = rubric };

        // Default threshold (1.0 — all criteria): 1 of 2 met → FAIL, and the detail names the unmet criterion + evidence.
        var strict = await GradeAsync(repoId, teamId, spec);
        strict.Passed.ShouldBeFalse();
        strict.Detail.ShouldContain("[risks]", customMessage: "the failing detail names the unmet criterion — the revise loop's food");

        // Threshold 0.5: the same verdict passes the weighted floor.
        var lenient = await GradeAsync(repoId, teamId, spec with { Rubric = rubric with { Threshold = 0.5 } });
        lenient.Passed.ShouldBeTrue(lenient.Detail);
        lenient.Detail.ShouldContain("1/2 criteria met");
    }

    [Fact]
    public async Task The_citations_oracle_grades_real_committed_files()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new()
        {
            ["docs/source.md"] = "the primary source\n",
            ["report.md"] = "See [the source](docs/source.md) and [the paper](https://example.com/p).\n",
            ["broken.md"] = "See [gone](docs/missing.md).\n",
        });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        (await GradeAsync(repoId, teamId, CitationsSpec("report.md"))).Passed.ShouldBeTrue("every citation resolves on the real clone");

        var broken = await GradeAsync(repoId, teamId, CitationsSpec("broken.md"));
        broken.Passed.ShouldBeFalse();
        broken.Detail.ShouldContain("docs/missing.md");
    }

    [Fact]
    public async Task The_schema_oracle_grades_real_committed_data()
    {
        if (OperatingSystem.IsWindows()) return;
        if (!await GitAvailableAsync()) return;

        var teamId = await SeedTeamAsync();

        using var remote = new BareRemote();
        await remote.SeedBaseAsync(new()
        {
            ["good.json"] = """{ "name": "widget", "count": 3 }""" + "\n",
            ["bad.json"] = """{ "count": "three" }""" + "\n",
        });
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        var schema = JsonDocument.Parse("""{ "type": "object", "required": ["name"], "properties": { "count": { "type": "integer" } } }""").RootElement.Clone();

        (await GradeAsync(repoId, teamId, SchemaSpec(schema, "good.json"))).Passed.ShouldBeTrue();

        var bad = await GradeAsync(repoId, teamId, SchemaSpec(schema, "bad.json"));
        bad.Passed.ShouldBeFalse();
        bad.Detail.ShouldContain("schema-violations: bad.json");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static SupervisorAcceptanceSpec CitationsSpec(params string[] paths) =>
        new() { Command = paths, Kind = BenchmarkGradingKind.CitationsResolve };

    private static SupervisorAcceptanceSpec SchemaSpec(JsonElement schema, params string[] paths) =>
        new() { Command = paths, Kind = BenchmarkGradingKind.ArtifactSchema, Schema = schema };

    /// <summary>The REAL grader from DI — clone main (the seeded deliverables) and grade the spec against it.</summary>
    private async Task<BenchmarkGrade> GradeAsync(Guid repoId, Guid teamId, SupervisorAcceptanceSpec spec)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISupervisorAcceptanceGrader>().GradeAsync(repoId, teamId, "main", spec, 60, CancellationToken.None);
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"oracle-{userId:N}@test.local", Name = $"oracle-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"oracle-{teamId:N}", Name = "Oracle Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });

        var serializer = scope.Resolve<ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId,
            AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "grade-clone-token" })), Status = CredentialStatus.Active,
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

    /// <summary>A bare local remote seeding arbitrary files (nested paths supported) on main — what the grader clones. GUID-suffixed; best-effort cleanup.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-oracle-grade-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedBaseAsync(Dictionary<string, string> files)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await Git(seed, "clone", _bare, seed);
            await Git(seed, "config", "user.email", "test@codespace.dev");
            await Git(seed, "config", "user.name", "Test");
            await Git(seed, "config", "commit.gpgsign", "false");

            foreach (var (name, content) in files)
            {
                var path = Path.Combine(seed, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content);
            }

            await Git(seed, "add", "-A");
            await Git(seed, "commit", "-m", "seed");
            await Git(seed, "push", "origin", "main");
        }

        private static async Task<string> Git(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");

            return result.Stdout;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
