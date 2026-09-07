using System.Globalization;
using System.Text;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Metadata observation policy, independent of model families and task content. Rejects uncertain identity rather
/// than publishing a redaction marker as a model name. ONE policy shared by every wire reader — the in-process
/// structured clients (<see cref="FromWire(string?, ResolvedModelCredential?)"/>, a fresh per-call redactor built
/// from the request's own credential) and the physical accounting handler (<see cref="FromWire(string?, PersistenceSecretRedactor, PersistenceSecretRedactor?)"/>,
/// reusing the candidate's already-built redactor) — so a bounded, control/format-char-free, non-secret value is the
/// only thing either reader can ever call "observed".
/// </summary>
internal static class ObservedLlmModel
{
    internal const int MaximumLength = 500;

    internal static string? FromWire(string? value, ResolvedModelCredential? credential) =>
        FromWire(value, new PersistenceSecretRedactor(new[] { credential?.ApiKey, credential?.BaseUrl }.OfType<string>()), LlmCallContext.Current?.CaptureRedactor);

    /// <summary>The shared policy, taking already-built redactors — the physical accounting handler's candidate keeps one for its whole lifetime; reusing it here avoids re-building (LINQ + sort) one per POST.</summary>
    internal static string? FromWire(string? value, PersistenceSecretRedactor credentialRedactor, PersistenceSecretRedactor? captureRedactor)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumLength) return null;
        for (var offset = 0; offset < value.Length;)
        {
            if (!Rune.TryGetRuneAt(value, offset, out var rune) || Rune.IsControl(rune) || Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format) return null;
            offset += rune.Utf16SequenceLength;
        }

        return credentialRedactor.Redact(value).Changed || captureRedactor?.Redact(value).Changed == true ? null : value;
    }
}
