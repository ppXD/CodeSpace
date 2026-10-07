using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Tools;

/// <summary>
/// The shared shaping of a <see cref="ToolCallPreview"/>: a tool's arguments as lines, the redacted and bounded form a
/// human may see, its approval-card text, and its stored JSON. Pure. A preview reaches three surfaces — the card, the
/// ledger row and the run's tool-call audit — so the bounds and the redaction are applied once, by
/// <see cref="Finish"/>, before any of them sees it.
/// </summary>
public static partial class ToolCallPreviews
{
    /// <summary>The longest a value the platform read to explain the call (a pull request's title) is kept, after redaction, before it is cut with a count of what was dropped. An argument is never cut.</summary>
    public const int MaxValueCharacters = 240;

    /// <summary>
    /// The most characters a call's arguments may take, all together, after redaction. They are shown whole — a reviewer
    /// approves exactly what runs, its tail included — so a call whose arguments are longer is not put to a reviewer; it is
    /// told to shorten them. Bounds the card under the chat's message limit.
    /// </summary>
    public const int MaxArgumentCharacters = 8_000;

    /// <summary>The longest a label is kept. A node tool's labels are its schema's own keys; a first-party tool's are whatever keys the model sent.</summary>
    public const int MaxLabelCharacters = 48;

    /// <summary>The most lines a preview keeps. Every argument is kept; the platform's own lines fill what is left, and the rest are counted on one closing line.</summary>
    public const int MaxLines = 16;

    /// <summary>What the card says of a value naming something outside the repositories the run is bound to.</summary>
    public const string OutsideRunNote = "outside this run's repositories";

    /// <summary>The preview of a tool that resolves nothing: each argument as given, in the order given, the whole call its target.</summary>
    public static ToolCallPreview FromArguments(JsonElement input) => new()
    {
        Target = input,
        Lines = input.ValueKind == JsonValueKind.Object ? input.EnumerateObject().Select(property => Line(property.Name, property.Value)).ToList() : [],
    };

    /// <summary>One line naming <paramref name="value"/>, one of the call's own arguments, as a reviewer reads it: whole.</summary>
    public static ToolCallPreviewLine Line(string label, JsonElement value, bool outsideRun = false) => new() { Label = label, Value = Text(value), OutsideRun = outsideRun, Whole = true };

    /// <summary>A JSON value as a reviewer reads it: a string as itself, anything else as its JSON text.</summary>
    public static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();

    /// <summary>
    /// The preview a human may see: every label and value redacted first, then put on one line (so a value can never
    /// forge a line of its own). An argument is kept whole; a value the platform read is cut to its bound, after
    /// redaction, so a cut never leaves part of a secret behind. Throws <see cref="ToolCallPreviewException"/> when the
    /// arguments cannot be shown whole. Pins are left as resolved: they are what the approved call runs with.
    /// </summary>
    public static ToolCallPreview Finish(ToolCallPreview preview, SecretRedactor redactor)
    {
        var lines = preview.Lines.Select(line => Finished(line, redactor)).ToList();

        EnsureArgumentsShowWhole(lines);

        return preview with { Lines = Kept(lines) };
    }

    /// <summary>
    /// The card's text for a finished preview, one line per value: <c>- label: value</c>, and a flag on a value outside the
    /// run's repositories. Plain text, as the chat shows a message body. Text a model chose — a commit message, a key —
    /// cannot mention anyone: a <c>&lt;type:id|label&gt;</c> reference token is broken before the chat's reference parser
    /// can see it. Empty when there is nothing to show.
    /// </summary>
    public static string CardText(ToolCallPreview? preview)
    {
        if (preview is null || preview.Lines.Count == 0) return "";

        return "\n\n" + string.Join('\n', preview.Lines.Select(CardLine));
    }

    public static string Serialize(ToolCallPreview preview) => JsonSerializer.Serialize(preview, AgentJson.Options);

    /// <summary>The stored preview, or null when the row stored none.</summary>
    public static ToolCallPreview? Parse(string? json) => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<ToolCallPreview>(json, AgentJson.Options);

    /// <summary><paramref name="arguments"/> with <paramref name="pins"/> written over them — what an approved call executes with. The arguments as given when there is nothing to pin.</summary>
    public static JsonElement Pinned(JsonElement arguments, IReadOnlyDictionary<string, string>? pins)
    {
        if (pins is not { Count: > 0 } || arguments.ValueKind != JsonValueKind.Object) return arguments;

        var merged = arguments.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);

        foreach (var (key, value) in pins) merged[key] = JsonSerializer.SerializeToElement(value);

        return JsonSerializer.SerializeToElement(merged);
    }

    /// <summary>Every run of whitespace — newlines included — as one space, and no whitespace at either end: how the card shows a value, and so how a rejection's target compares one.</summary>
    public static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static ToolCallPreviewLine Finished(ToolCallPreviewLine line, SecretRedactor redactor) => line with
    {
        Label = Bound(OneLine(redactor.Redact(line.Label)), MaxLabelCharacters),
        Value = line.Whole ? OneLine(redactor.Redact(line.Value)) : Bound(OneLine(redactor.Redact(line.Value)), MaxValueCharacters),
    };

    /// <summary>Refuses arguments a card could not show whole: more than <see cref="MaxArgumentCharacters"/> of them, or more lines of them than a preview keeps.</summary>
    private static void EnsureArgumentsShowWhole(IReadOnlyList<ToolCallPreviewLine> lines)
    {
        var arguments = lines.Where(line => line.Whole).ToList();
        var characters = arguments.Sum(line => line.Label.Length + line.Value.Length);

        if (characters > MaxArgumentCharacters || arguments.Count > MaxLines)
            throw new ToolCallPreviewException($"This call's arguments come to {characters} characters on {arguments.Count} lines, more than the {MaxArgumentCharacters} characters a reviewer is shown whole (on at most {MaxLines} lines), so it was not put to a reviewer and nothing ran. Shorten them — split the work into smaller calls — and ask again.");
    }

    /// <summary>Every argument, and the platform's own lines while there is room, in order; the platform's lines left out are counted on a closing line.</summary>
    private static List<ToolCallPreviewLine> Kept(IReadOnlyList<ToolCallPreviewLine> lines)
    {
        var room = MaxLines - lines.Count(line => line.Whole);
        var kept = new List<ToolCallPreviewLine>();

        foreach (var line in lines)
        {
            if (!line.Whole && room-- <= 0) continue;

            kept.Add(line);
        }

        if (kept.Count < lines.Count) kept.Add(new ToolCallPreviewLine { Label = "…", Value = $"{lines.Count - kept.Count} more not shown" });

        return kept;
    }

    private static string CardLine(ToolCallPreviewLine line) =>
        $"- {Unreferenced(line.Label)}: {(line.Value.Length == 0 ? "(empty)" : Unreferenced(line.Value))}{(line.OutsideRun ? $" — {OutsideRunNote}" : "")}";

    private static string Bound(string text, int max)
    {
        if (text.Length <= max) return text;

        var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;

        return $"{text[..cut]}… (+{text.Length - cut} characters)";
    }

    /// <summary>Breaks the chat's reference-token grammar (<c>&lt;type:id|label&gt;</c>) where it would start, so card text mentions no one.</summary>
    private static string Unreferenced(string text) => ReferenceTokenStart().Replace(text, "‹");

    [GeneratedRegex("<(?=[a-z][a-z0-9_]*:)")]
    private static partial Regex ReferenceTokenStart();
}
