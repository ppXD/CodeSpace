using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// The wire rule of every acceptance slot a SUPERVISOR decision carries — a plan subtask's, a plan phase's, a stop's,
/// an amend proposal's replacement: the spec binds and persists WITHOUT <see cref="SupervisorAcceptanceSpec.SetupCommand"/>
/// and <see cref="SupervisorAcceptanceSpec.TimeoutSeconds"/>. Both are operator knobs. The setup argv runs workspace
/// bytes before the check; the timeout decides how long the grader runs anything at all. The decision schema never
/// offers either, but its <c>additionalProperties:false</c> is advisory — the server's schema check does not read it
/// and a schema-less fallback request carries none — so a reply naming them bound straight into the spec, was frozen
/// into the ledger, and the grader ran it.
///
/// <para>Declared on those PROPERTIES rather than on the type, so it holds wherever the payloads are read — the
/// decider's fresh bind, the projector's canonical bytes (and so the idempotency key), and every later re-read of a
/// stored ledger row, including one written before this rule existed — while an operator's own spec (node config,
/// <c>AgentTask.Acceptance</c>) keeps both. It strips on write as well, so no server path can freeze either knob into
/// a decision. Everything else the model authored binds unchanged: its check still grades the unit.</para>
/// </summary>
public sealed class ModelAuthoredAcceptanceConverter : JsonConverter<SupervisorAcceptanceSpec>
{
    public override SupervisorAcceptanceSpec? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        WithoutOperatorKnobs(JsonSerializer.Deserialize<SupervisorAcceptanceSpec>(ref reader, options));

    public override void Write(Utf8JsonWriter writer, SupervisorAcceptanceSpec value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, WithoutOperatorKnobs(value), options);

    private static SupervisorAcceptanceSpec? WithoutOperatorKnobs(SupervisorAcceptanceSpec? spec) =>
        spec is null or { SetupCommand: null, TimeoutSeconds: null } ? spec : spec with { SetupCommand = null, TimeoutSeconds = null };
}
