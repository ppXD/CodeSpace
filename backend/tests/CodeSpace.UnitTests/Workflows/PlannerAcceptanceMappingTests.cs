using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using CodeSpace.Messages.Plans;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class PlannerAcceptanceMappingTests
{
    [Fact]
    public void Model_schema_separates_argv_from_paths_and_requires_a_known_format_and_oracle()
    {
        var acceptance = PlannerSchema.ResponseSchema.GetProperty("properties").GetProperty("subtasks").GetProperty("items").GetProperty("properties").GetProperty("acceptance");
        acceptance.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ShouldBe(new[] { "formatVersion", "kind" });
        var properties = acceptance.GetProperty("properties");
        properties.TryGetProperty("command", out _).ShouldBeFalse("a fresh model response must not overload command as either argv or file paths");
        properties.GetProperty("argv").GetProperty("type").GetString().ShouldBe("array");
        properties.GetProperty("artifactPaths").GetProperty("type").GetString().ShouldBe("array");
        properties.GetProperty("formatVersion").GetProperty("enum")[0].GetInt32().ShouldBe(2);
    }

    [Fact]
    public void Exact_argv_survives_mapping_and_the_persisted_plan_without_promoting_or_trimming_arguments()
    {
        var argv = new[] { "sh", "-c", "printf '<%s>' \"$1\"", "--", "", "  ", "a b", "$(not-executed-by-mapping)" };
        var item = WorkPlanItem.From(Parse(JsonSerializer.SerializeToElement(new { formatVersion = 2, kind = "TestsPass", argv })));
        item.Acceptance!.Kind.ShouldBe(BenchmarkGradingKind.TestsPass);
        item.Acceptance.Command.ShouldBe(argv);
        var persisted = JsonSerializer.Serialize(item, AgentJson.Options);
        var restored = JsonSerializer.Deserialize<WorkPlanItem>(persisted, AgentJson.Options)!;
        restored.Acceptance!.Command.ShouldBe(argv);
        ContractHashing.Hash(restored.Acceptance, AgentJson.Options).ShouldBe(ContractHashing.Hash(new SupervisorAcceptanceSpec { Kind = BenchmarkGradingKind.TestsPass, Command = argv }, AgentJson.Options));
        persisted.ShouldNotContain("formatVersion", customMessage: "the model wire version must not silently change the existing runtime contract hash vocabulary");
    }

    [Theory]
    [InlineData("ArtifactPresent", "{}")]
    [InlineData("LlmJudge", "{\"rubric\":{\"criteria\":[{\"id\":\"coverage\",\"requirement\":\"explains the evidence\"}],\"threshold\":1}}")]
    [InlineData("CitationsResolve", "{}")]
    [InlineData("ArtifactSchema", "{\"schema\":{\"type\":\"object\",\"required\":[\"answer\"]}}")]
    public void Every_file_oracle_remains_expressible_and_valid_literal_filenames_are_not_guessed_to_be_commands(string kind, string extras)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(extras)!;
        payload["formatVersion"] = JsonSerializer.SerializeToElement(2);
        payload["kind"] = JsonSerializer.SerializeToElement(kind);
        payload["artifactPaths"] = JsonSerializer.SerializeToElement(new[] { "test", "-f", "notes with spaces.md" });
        var acceptance = Parse(JsonSerializer.SerializeToElement(payload)).Acceptance!;
        acceptance.Kind.ShouldBe(Enum.Parse<BenchmarkGradingKind>(kind));
        acceptance.Command.ShouldBe(new[] { "test", "-f", "notes with spaces.md" }, "file intent is explicitly typed, never inferred from names that resemble executables or flags");
        AgentAcceptanceContract.ValidateAuthored(acceptance).ShouldBeNull();
    }

    [Theory]
    [InlineData("{\"kind\":\"ArtifactPresent\",\"command\":[\"test\",\"-f\",\"report.md\"]}")]
    [InlineData("{\"formatVersion\":1,\"kind\":\"TestsPass\",\"argv\":[\"true\"]}")]
    [InlineData("{\"formatVersion\":99,\"kind\":\"TestsPass\",\"argv\":[\"true\"]}")]
    [InlineData("{\"formatVersion\":2,\"argv\":[\"true\"]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"TestsPass\",\"argv\":[\"true\"],\"artifactPaths\":[\"report.md\"]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"ArtifactPresent\",\"argv\":[\"test\",\"-f\",\"report.md\"]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"TestsPass\",\"argv\":[\"\",\"true\"]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"TestsPass\",\"argv\":[\"true\",null]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"TestsPass\",\"argv\":[\"true\",\"x\\u0000y\"]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"ArtifactPresent\",\"artifactPaths\":[\"\"]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"LlmJudge\",\"artifactPaths\":[\"report.md\"]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"LlmJudge\",\"artifactPaths\":[\"report.md\"],\"rubric\":{\"criteria\":[null]}}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"ArtifactSchema\",\"artifactPaths\":[\"report.json\"]}")]
    [InlineData("{\"formatVersion\":2,\"kind\":\"UnknownOracle\",\"argv\":[\"true\"]}")]
    public void An_unknown_legacy_or_incomplete_fresh_response_is_rejected_instead_of_dropping_or_reinterpreting_the_contract(string payload)
    {
        Should.Throw<InvalidOperationException>(() => Parse(JsonDocument.Parse(payload).RootElement));
    }

    [Fact]
    public void Existing_persisted_v1_plan_remains_readable_with_the_same_bytes_and_hash()
    {
        const string stored = "{\"id\":\"item\",\"title\":\"Keep\",\"instruction\":\"Keep the report\",\"acceptance\":{\"command\":[\"report.md\"],\"kind\":\"ArtifactPresent\"}}";
        var item = JsonSerializer.Deserialize<WorkPlanItem>(stored, AgentJson.Options)!;
        item.Acceptance!.Command.ShouldBe(new[] { "report.md" });
        JsonSerializer.Serialize(item, AgentJson.Options).ShouldBe(stored);
        ContractHashing.Hash(item.Acceptance, AgentJson.Options).ShouldBe(ContractHashing.Hash(JsonDocument.Parse(stored).RootElement.GetProperty("acceptance"), AgentJson.Options));
    }

    [Fact]
    public void Case_insensitive_subtask_binding_cannot_bypass_the_fresh_acceptance_version_boundary()
    {
        var legacy = JsonDocument.Parse("{\"goal\":\"g\",\"subtasks\":[{\"id\":\"s\",\"title\":\"T\",\"instruction\":\"I\",\"Acceptance\":{\"kind\":\"ArtifactPresent\",\"command\":[\"test\",\"-f\",\"report.md\"]}}]}").RootElement;
        Should.Throw<InvalidOperationException>(() => LlmWorkflowPlanner.Deserialize(legacy));
    }

    private static PlannedSubtask Parse(JsonElement acceptance) => LlmWorkflowPlanner.Deserialize(JsonSerializer.SerializeToElement(new { goal = "verify", subtasks = new[] { new { id = "item", title = "Item", instruction = "Do the work", acceptance } } })).Subtasks.Single();
}
