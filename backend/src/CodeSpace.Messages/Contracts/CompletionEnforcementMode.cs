using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Contracts;

/// <summary>
/// HOW the completion protocol applies to one execution (P2a / Lock Clause 1). Stamped immutably at run creation —
/// a replay/rerun is a NEW execution and explicitly receives the policy current at ITS creation, never inherited
/// inference. <see cref="Legacy"/> = pre-protocol run (null columns read as Legacy); <see cref="Shadow"/> = the
/// assessment is composed and recorded but NEVER mutates the run's terminal status; <see cref="Enforced"/> = the
/// terminal CAS is owned by the completion protocol (P2b, per qualified mode cohort — Lock Clause 1). An
/// unrecognized stored value reads Legacy with fail-close semantics: never enforce on uncertain policy.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompletionEnforcementMode
{
    Legacy,
    Shadow,
    Enforced,
}
