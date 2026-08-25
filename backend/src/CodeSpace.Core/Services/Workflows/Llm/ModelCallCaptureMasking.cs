using CodeSpace.Core.Services.Workflows.Runtime;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Whether ONE model call's capture actually had content replaced — the question migration 0166's masked latch asks,
/// and the one thing the presence of a configured <see cref="PersistenceSecretRedactor"/> cannot answer. The engine
/// builds a redactor for EVERY node scope out of that scope's secret paths, so "a redactor exists" is true for
/// essentially every recorded call while most of them carry no secret at all. Reporting those masked latches 0166 on
/// CONFIGURATION rather than on content, and that latch is monotonic: nothing later can take the claim back.
///
/// <para>This type holds no persistence of its own and reaches no table: it is a per-call observation the decorator
/// folds into the <c>Masked</c> flag of the presence delta it states, which is the only thing that reaches 0166.</para>
///
/// <para>One instance per model CALL, never per scope. A node scope outlives every call made under it, so a latch
/// living there would report a later secret-free call masked because an earlier one carried a secret;
/// <see cref="LlmCallScope.ForOneCall"/> is what mints a fresh one. Within its own call it moves only upward, because
/// the started row's prompt, any delta rows and the terminal row's completion are all parts of the ONE record the
/// presence delta counts as present.</para>
/// </summary>
public sealed class ModelCallCaptureMasking
{
    private bool _observed;

    /// <summary>Whether anything this call persisted was actually replaced by the redactor.</summary>
    public bool Observed => _observed;

    /// <summary>Fold one redaction result in and hand its value straight back, so a caller observes and uses it in one expression.</summary>
    public T Observe<T>(PersistenceRedaction<T> redaction)
    {
        if (redaction.Changed) _observed = true;

        return redaction.Value;
    }
}
