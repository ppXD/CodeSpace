namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Optional sibling capability of <see cref="IAgentHarness"/> (Rule 7 / ISP — never a widening of it): the generation
/// of the durable RECORD CONTRACT this adapter writes rows under. It is the <c>v&lt;major&gt;</c> half of the
/// execution-identity key <c>&lt;kind&gt;/v&lt;major&gt;</c> that
/// <see cref="Capture.AgentNativeRecordPump.HarnessTypeKeyOf"/> builds and every harness-execution row carries.
///
/// <para><b>It is deliberately NOT the native CLI version.</b> That is <see cref="IAgentHarness.Version"/>, a
/// different quantity that moves for reasons this one must not: a routine CLI major bump changes nothing about how
/// the adapter translates native frames into rows, so it has to leave the key alone or one unchanged adapter's
/// history splits across two keys. The two were conflated until this interface existed — the key took the leading
/// digits of the version string, which gave the pinned codex 0.142.x a leading 0 and therefore the SAME key a future
/// codex 1.x would have taken.</para>
///
/// <para><b>The floor is 1.</b> The database refuses anything else: <c>harness_type_key</c> is checked against
/// <c>^[a-z0-9][a-z0-9._-]{0,126}/v[1-9][0-9]*$</c> (migration 0137), so <c>v0</c> is not a representable key. A
/// declaration of zero or below is clamped up rather than allowed to make capture unopenable — see
/// <see cref="Capture.AgentNativeRecordPump.FirstContractGeneration"/>, which is also the generation an adapter that
/// does not implement this interface is keyed under.</para>
///
/// <para><b>When to bump it.</b> Only when this adapter's own translation changes so that a row it wrote earlier can
/// no longer be read the same way — a rewrite of what a frame means, not a version bump of the CLI it drives. A bump
/// is a committed edit to a literal, reviewed as a PR: it must never be derived from anything and never overridable
/// at runtime, because every row a deployment writes has to agree on it, and a row's key is immutable once written
/// (0137's identity trigger refuses an update to it) so a wrong value can never be repaired in place.</para>
/// </summary>
public interface IAgentHarnessContractGeneration
{
    /// <summary>This adapter's record-contract generation, at least 1. A value below the floor is clamped to it by the caller rather than rejected — refusing it would disable an adapter's capture over a number the caller can safely repair.</summary>
    int ContractGeneration { get; }
}
