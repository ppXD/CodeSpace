using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Messages.Constants;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The <c>node.completed</c> payload is a durable ledger contract: rows written today are read back by a resume
/// months later. Two properties are pinned here. (1) The un-redacted shapes must stay BYTE-IDENTICAL to what the
/// logger emitted before the redaction claim existed — a stray key or a reordered one would change every row's
/// bytes for no reason. (2) The claim, when present, must land under the exact wire key the engine's reader
/// queries for; a rename on one side alone silently turns "unrecoverable" back into "trustworthy".
/// </summary>
public class NodeCompletedPayloadTests
{
    private static IReadOnlyDictionary<string, JsonElement> Outputs() =>
        new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement("v") };

    [Theory]
    [InlineData(false, null, """{"outputs":{"value":"v"},"duration_ms":7}""")]
    [InlineData(false, "true", """{"outputs":{"value":"v"},"routingHints":["true"],"duration_ms":7}""")]
    [InlineData(true, null, """{"outputs":{"value":"v"},"duration_ms":7,"outputsRedacted":true}""")]
    [InlineData(true, "true", """{"outputs":{"value":"v"},"routingHints":["true"],"duration_ms":7,"outputsRedacted":true}""")]
    public void NodeCompletedPayload_emits_the_exact_ledger_shape(bool outputsRedacted, string? routingHint, string expected)
    {
        var hints = routingHint is null ? null : new List<string> { routingHint };

        RunRecordLogger.NodeCompletedPayload(Outputs(), hints, TimeSpan.FromMilliseconds(7), outputsRedacted).ShouldBe(expected);
    }

    [Fact]
    public void The_redaction_claim_lands_under_the_constant_the_reader_queries_for()
    {
        // The engine's recovery reader asks Postgres for payload_json->>'<this const>'. If the writer's anonymous
        // member name and this const ever drift apart, every unrecoverable row reads as trustworthy again — the
        // exact silent regression this whole guard exists to end.
        WorkflowRunRecordPayloadKeys.OutputsRedacted.ShouldBe("outputsRedacted");

        JsonDocument.Parse(RunRecordLogger.NodeCompletedPayload(Outputs(), null, TimeSpan.Zero, outputsRedacted: true))
            .RootElement.GetProperty(WorkflowRunRecordPayloadKeys.OutputsRedacted).GetBoolean().ShouldBeTrue();

        JsonDocument.Parse(RunRecordLogger.NodeCompletedPayload(Outputs(), null, TimeSpan.Zero, outputsRedacted: false))
            .RootElement.TryGetProperty(WorkflowRunRecordPayloadKeys.OutputsRedacted, out _)
            .ShouldBeFalse("absence is the claim's conservative default — it is what every pre-existing row and every nothing-to-redact row says");
    }
}
