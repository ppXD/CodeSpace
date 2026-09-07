using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// Development fixture transport through the real installed CLI and model. This is not a sealed holdout or a
/// Launch-mode comparison.
///
/// <para><b>Report-only until it has a passing history</b> — this repo's rule for a NEW arm. Its first and only live
/// run graded <c>tests-failed-exit-1</c>: the real CLI reached the fixture and ran, and the model's implementation did
/// not satisfy the oracle. Whether THIS never-before-solved fixture is inside the model's ability is unknown, and the
/// strict whole-loop gate would have red the blessed wire on that unknown. So the three-way
/// <c>RealModelGate.AssessLiveAsync</c> carries it instead: the outcome is ALWAYS reported (a capability miss prints as
/// itself, never as a silent green) and only a <c>CodeFault</c> gates — and this arm can raise none, since a fixture
/// that never reaches the model is caught by the hard assertions below instead.</para>
///
/// <para>Those assertions keep their hard character on purpose: an empty <c>Errored</c> list, a persisted
/// <c>claude-code</c> agent run, non-zero token usage and a matching suite version are TRANSPORT facts, and a break in
/// any of them is a wiring regression rather than the model finding the task hard. Only the grade is soft.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelBenchmarkHiddenFixtureE2ETests
{
    private const string Provider = "Anthropic";
    private readonly PostgresFixture _fixture;

    public RealModelBenchmarkHiddenFixtureE2ETests(PostgresFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task The_real_cli_solves_an_external_fixture_unknown_to_the_seed_registry()
    {
        var baseUrl = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var key = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = Environment.GetEnvironmentVariable(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);
        var present = new[] { baseUrl, key, model }.Count(value => value is not null);
        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent; real CLI fixture transport was not measured");
        present.ShouldBe(3, "partial live-model configuration must fail");
        if (OperatingSystem.IsWindows()) throw RealModelGate.ReportSkipped(Provider, "this fixture uses the Linux qualification toolchain");
        Environment.GetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar).ShouldBeNullOrEmpty("the live gate must use the installed CLI, with no fake command override");
        var cli = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "claude", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None);
        cli.Status.ShouldBe(SandboxStatus.Success, "the pinned real Claude CLI must be installed by this qualification job");

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture, inProcessPool: false);
        using var scope = _fixture.BeginScopeAs(userId, teamId);
        var db = scope.Resolve<CodeSpaceDbContext>();
        var credentialId = Guid.NewGuid();
        db.ModelCredential.Add(new ModelCredential
        {
            Id = credentialId, TeamId = teamId, Provider = Provider, DisplayName = "external fixture live CLI",
            EncryptedApiKey = scope.Resolve<IPayloadEncryptor>().Encrypt(key!), BaseUrl = baseUrl!.TrimEnd('/'), Status = CredentialStatus.Active,
            CreatedBy = userId, LastModifiedBy = userId,
        });
        await db.SaveChangesAsync();

        var directory = Path.Combine(Path.GetTempPath(), "cs-live-private-fixture-" + Guid.NewGuid().ToString("N"));
        var reference = "development/" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(directory, "fixtures", reference));
        try
        {
            var oracle = "from inventory import summarize\n" +
                "assert summarize([]) == {}\n" +
                "assert summarize([{'sku':' A ', 'quantity':2},{'sku':'a','quantity':-1},{'sku':'B','quantity':3},{'sku':' ','quantity':9}]) == {'a':1,'b':3}\n" +
                "assert summarize([{'sku':'MiXeD','quantity':0},{'sku':'mixed','quantity':4},{'sku':'other','quantity':-4}]) == {'mixed':4,'other':-4}\n" +
                "rows=[{'sku':' A ','quantity':2}]; summarize(rows); assert rows == [{'sku':' A ','quantity':2}]\nprint('external-fixture-oracle-passed')\n";
            var task = new BenchmarkTask
            {
                Id = "external-inventory", Description = "generic external fixture execution", FixtureRef = reference,
                Goal = "Implement inventory.py summarize(rows): return a dict mapping trimmed, lowercase SKU strings to summed integer quantities. Ignore blank SKUs, retain zero and negative totals, return an empty dict for empty input, and do not mutate input rows. Add and run useful local tests.",
                Harness = "claude-code", Grading = BenchmarkGradingKind.TestsPass, TestCommand = new[] { "python3", "-c", oracle }, Modes = new[] { BenchmarkMode.HarnessCli }, TimeoutSeconds = 240,
            };
            File.WriteAllText(Path.Combine(directory, "fixtures", reference, "inventory.py"), "def summarize(rows):\n    return {}\n");
            File.WriteAllText(Path.Combine(directory, "tasks.json"), JsonSerializer.Serialize(new[] { task }, AgentJson.Options));
            var suite = HiddenSuiteLoader.Load(directory);
            // The three-way report-only gate, NOT AssessLiveWholeLoopAsync: that one reds the blessed wire on a
            // CapabilityMiss, which is the only outcome this arm has ever produced (see the class doc).
            await RealModelGate.AssessLiveAsync(Provider, async () =>
            {
                var request = new CorpusBenchmarkRequest
                {
                    Tasks = suite.Tasks, TeamId = teamId, FixtureStager = suite.FixtureStager, SuiteContentHash = suite.SuiteContentHash,
                    Selection = new BenchmarkAgentSelection { Harness = "claude-code", Model = model, ModelCredentialId = credentialId, Autonomy = AgentAutonomyLevel.Trusted },
                };
                var run = await BenchmarkEvidenceExport.RunAsync(scope, request, BenchmarkEvidenceExport.LiveOptions("external-development-fixture", new[] { baseUrl!, key!, model! }), CancellationToken.None);
                run.Errored.ShouldBeEmpty("a custom fixture must reach the model, never fall back to a seed");
                var result = run.Results.ShouldHaveSingleItem();
                result.AgentRunId.ShouldNotBeNull();
                (await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == result.AgentRunId)).Harness.ShouldBe("claude-code");
                result.TokenUsage.ShouldNotBeNull("a fake no-op CLI is not live-model evidence");
                (result.TokenUsage!.InputTokens + result.TokenUsage.OutputTokens).ShouldBeGreaterThan(0);
                run.SuiteVersion.ShouldBe(EvalSuite.ManifestFor(suite.Tasks, suite.SuiteContentHash).Version);
                return (result.Grade.Passed ? RealModelOutcome.Drove : RealModelOutcome.CapabilityMiss, $"external fixture via real CLI; suite={run.SuiteVersion}; grade={result.Grade.Detail}; usage={result.TokenUsage.InputTokens + result.TokenUsage.OutputTokens}; formatFaultRespawns={result.FormatFaultRespawns}");
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
