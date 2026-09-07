using System.Text.Json;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Dtos.Workflows;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

[Trait("Category", "Unit")]
public class TaskLaunchContractTests
{
    private const string DefinitionJson = """
        {
          "schemaVersion": 1,
          "nodes": [
            { "id": "start", "typeKey": "trigger.manual", "config": {}, "inputs": {} },
            { "id": "end", "typeKey": "builtin.terminal", "config": {}, "inputs": {} }
          ],
          "edges": [{ "from": "start", "to": "end" }],
          "launchContract": {
            "version": 1,
            "taskText": "Keep the original obligation",
            "goal": "Keep the original obligation",
            "surfaceKind": "chat",
            "acceptanceChecks": ["sh", "verify.sh"],
            "deliverySpec": { "openPullRequest": false },
            "requestedControls": { "autonomy": "Unleashed", "requirePlanConfirmation": false },
            "resolvedRoute": { "projectionKind": "single-agent", "effectiveAutonomy": "Standard" }
          }
        }
        """;

    [Fact]
    public void Snapshot_round_trip_preserves_original_obligations_and_requested_controls()
    {
        var definition = Read(DefinitionJson);
        var json = JsonSerializer.SerializeToElement(definition, WorkflowJson.Options);

        json.TryGetProperty("launchContract", out var contract).ShouldBeTrue("the run-detail and replay snapshot must retain launch provenance independently of projected nodes");
        contract.GetProperty("goal").GetString().ShouldBe("Keep the original obligation");
        contract.GetProperty("acceptanceChecks").EnumerateArray().Select(x => x.GetString()).ShouldBe(new[] { "sh", "verify.sh" });
        contract.GetProperty("deliverySpec").GetProperty("openPullRequest").GetBoolean().ShouldBeFalse();
        contract.GetProperty("requestedControls").GetProperty("autonomy").GetString().ShouldBe("Unleashed");
        contract.GetProperty("resolvedRoute").GetProperty("effectiveAutonomy").GetString().ShouldBe("Standard");
    }

    [Theory]
    [InlineData("Keep the original obligation", "A weaker obligation")]
    [InlineData("verify.sh", "always-pass.sh")]
    [InlineData("Unleashed", "Standard")]
    [InlineData("\"openPullRequest\": false", "\"openPullRequest\": true")]
    public void Changing_launch_provenance_changes_the_frozen_definition_hash(string original, string replacement)
    {
        DefinitionHash.Compute(Read(DefinitionJson.Replace(original, replacement, StringComparison.Ordinal)))
            .ShouldNotBe(DefinitionHash.Compute(Read(DefinitionJson)), "a modified obligation or control must trip the same snapshot tamper check as a modified graph");
    }

    [Fact]
    public void User_authored_definition_cannot_claim_server_recorded_launch_provenance()
    {
        var result = Validator().Validate(Read(DefinitionJson));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("launchContract", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_definition_keeps_its_existing_hash_and_omits_unknown_launch_provenance()
    {
        var definition = Read("""{"schemaVersion":1,"nodes":[],"edges":[],"inputs":[],"outputs":[]}""");
        var serialized = JsonSerializer.SerializeToElement(definition, WorkflowJson.Options);

        serialized.TryGetProperty("launchContract", out _).ShouldBeFalse("legacy absence is unknown, not a fabricated empty user contract");
        DefinitionHash.Compute(definition).ShouldBe("543eb88e1735b98e7c0477bba9d0b909cea52ffe44fcddb25f043e4558bdd922");
    }

    private static WorkflowDefinition Read(string json) => JsonSerializer.Deserialize<WorkflowDefinition>(json, WorkflowJson.Options)!;

    [Fact]
    public void Snapshot_validation_accepts_a_complete_current_contract()
    {
        Validator().Validate(Read(DefinitionJson), allowLaunchContract: true).Errors.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("\"version\": 1", "\"version\": 2", "Unsupported launchContract version 2")]
    [InlineData("\"version\": 1,", "", "Unsupported launchContract version 0")]
    [InlineData("\"goal\": \"Keep the original obligation\"", "\"goal\": null", "launchContract.goal is required")]
    [InlineData("\"surfaceKind\": \"chat\"", "\"surfaceKind\": \" \"", "launchContract.surfaceKind is required")]
    [InlineData("\"requestedControls\": { \"autonomy\": \"Unleashed\", \"requirePlanConfirmation\": false }", "\"requestedControls\": null", "launchContract.requestedControls is required")]
    [InlineData("\"resolvedRoute\": { \"projectionKind\": \"single-agent\", \"effectiveAutonomy\": \"Standard\" }", "\"resolvedRoute\": null", "launchContract.resolvedRoute.projectionKind is required")]
    public void Snapshot_validation_rejects_unknown_or_incomplete_contracts(string original, string replacement, string error)
    {
        var result = Validator().Validate(Read(DefinitionJson.Replace(original, replacement, StringComparison.Ordinal)), allowLaunchContract: true);

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains(error, StringComparison.Ordinal));
    }

    [Fact]
    public void Contract_hash_survives_workflow_json_enum_and_jsonb_key_reordering()
    {
        var definition = Read(DefinitionJson.Replace("\"autonomy\": \"Unleashed\", \"requirePlanConfirmation\": false", "\"plannerReviewMode\": \"Improve\", \"autonomy\": \"Unleashed\", \"requirePlanConfirmation\": false", StringComparison.Ordinal));
        var stored = JsonSerializer.Serialize(definition, WorkflowJson.Options);
        var reversed = System.Text.Json.Nodes.JsonNode.Parse(stored)!.AsObject().Reverse().ToArray();
        var reordered = new System.Text.Json.Nodes.JsonObject(reversed.Select(p => new KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>(p.Key, p.Value?.DeepClone())));

        DefinitionHash.Compute(Read(reordered.ToJsonString())).ShouldBe(DefinitionHash.Compute(definition));
    }

    private static DefinitionValidator Validator() => new(new NodeRegistry([new TriggerManualNode(), new TerminalNode()]));
}
