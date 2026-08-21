using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// A harness adapter's explicit declaration of the model-call observation granularity its stable native protocol
/// exposes. This is truth metadata, not an optional execution feature: every shipped adapter is required by the
/// contract test to implement it, while an older third-party adapter degrades to
/// <see cref="HarnessModelCallObservationCoverage.LegacyUnknown"/> rather than being guessed from its name.
/// </summary>
public interface IAgentHarnessModelCallObservation
{
    HarnessModelCallObservationCoverage ModelCallObservationCoverage { get; }
}
