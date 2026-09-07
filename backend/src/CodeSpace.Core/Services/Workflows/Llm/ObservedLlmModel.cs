using System.Globalization;
using System.Text;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>Metadata observation policy, independent of model families and task content. Rejects uncertain identity rather than publishing a redaction marker as a model name.</summary>
internal static class ObservedLlmModel
{
    internal const int MaximumLength = 500;

    internal static string? FromWire(string? value, ResolvedModelCredential? credential)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumLength) return null;
        for (var offset = 0; offset < value.Length;)
        {
            if (!Rune.TryGetRuneAt(value, offset, out var rune) || Rune.IsControl(rune) || Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format) return null;
            offset += rune.Utf16SequenceLength;
        }

        var secrets = new PersistenceSecretRedactor(new[] { credential?.ApiKey, credential?.BaseUrl }.OfType<string>());
        if (secrets.Redact(value).Changed || LlmCallContext.Current?.CaptureRedactor?.Redact(value).Changed == true) return null;
        return value;
    }
}
