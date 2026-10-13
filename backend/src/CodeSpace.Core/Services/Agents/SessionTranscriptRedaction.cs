using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Masks a run's secrets in a CLI session transcript — the newline-delimited JSON a harness restores verbatim for
/// <c>--resume</c> — without breaking it. A secret is replaced INSIDE the JSON value that holds it (a string, a property
/// name, a number), so every line that parsed before still parses, the line count is unchanged, and a line that holds no
/// secret keeps its exact bytes.
///
/// <para>Plain text replacement over the line is not enough on either count. A value the CLI escaped — a secret with a
/// quote, a backslash or a control character in it — is not in the file as its own characters, so exact matching never
/// sees it; and a match that ends inside an escape sequence leaves a lone backslash that escapes whatever follows, which
/// the CLI then refuses to resume. A line that is not one JSON value (a torn tail, an interleaved log line) has no value
/// to mask inside and nothing to keep valid, so it gets plain replacement — of each secret and of the forms a JSON writer
/// escapes it to, since the tail of a value the CLI was writing is still escaped text.</para>
/// </summary>
public static class SessionTranscriptRedaction
{
    /// <summary>Deep enough for any tool payload a CLI records. A deeper line falls back to the plain mask rather than failing.</summary>
    private const int MaxDepth = 1000;

    private static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = MaxDepth };

    /// <summary>The CLI's own escaping is not reproduced; any valid escaping is. Relaxed, so a rewritten line keeps its non-ASCII text readable rather than <c>\uXXXX</c>-encoded.</summary>
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = MaxDepth };

    /// <summary><paramref name="transcript"/> with every secret of <paramref name="redactor"/> masked as <see cref="SecretRedactor.Placeholder"/>; the same instance when nothing was masked.</summary>
    public static string Redact(string transcript, SecretRedactor redactor)
    {
        if (redactor.IsEmpty || string.IsNullOrEmpty(transcript)) return transcript;

        var unparsed = redactor.WithVariants(JsonEscapedForms);
        var lines = transcript.Split('\n');
        var changed = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var masked = RedactLine(lines[i], redactor, unparsed);

            changed |= !ReferenceEquals(masked, lines[i]);
            lines[i] = masked;
        }

        return changed ? string.Join('\n', lines) : transcript;
    }

    /// <summary>
    /// One line, masked. A line with no secret in its text and no escape in it cannot hold a secret in any value, so it
    /// is returned as is without being parsed — the common case, and the reason a large transcript costs one scan. A
    /// line that is not one JSON value is masked by <paramref name="unparsed"/>: the secrets and their escaped forms.
    /// </summary>
    private static string RedactLine(string line, SecretRedactor redactor, SecretRedactor unparsed)
    {
        if (ReferenceEquals(redactor.Redact(line), line) && !line.Contains('\\')) return line;

        return RedactJsonValues(line, redactor) ?? unparsed.Redact(line);
    }

    /// <summary>
    /// The forms a JSON writer puts a secret in inside a string: with the short escapes (<c>\n</c>, <c>\"</c>, <c>\\</c>) the
    /// CLIs' writers use, and with an ASCII-only writer's <c>\uXXXX</c> ones. Every form that differs from the secret holds
    /// a backslash, which is what lets a line without one skip the parse.
    /// </summary>
    private static IEnumerable<string> JsonEscapedForms(string secret) =>
        [JsonEncodedText.Encode(secret, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString(), JsonEncodedText.Encode(secret, JavaScriptEncoder.Default).ToString()];

    /// <summary>The line re-written token by token with its values masked; the line itself when no value held a secret; null when it is not one JSON value.</summary>
    private static string? RedactJsonValues(string line, SecretRedactor redactor)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line), ReaderOptions);
        var output = new ArrayBufferWriter<byte>();
        var changed = false;

        try
        {
            using var writer = new Utf8JsonWriter(output, WriterOptions);

            while (reader.Read()) changed |= CopyToken(ref reader, writer, redactor);
        }
        catch (Exception notOneValue) when (notOneValue is JsonException or InvalidOperationException)
        {
            return null;
        }

        return changed ? Encoding.UTF8.GetString(output.WrittenSpan) : line;
    }

    /// <summary>Copy the reader's current token to <paramref name="writer"/>, masked; true when masking changed it.</summary>
    private static bool CopyToken(ref Utf8JsonReader reader, Utf8JsonWriter writer, SecretRedactor redactor)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject: writer.WriteStartObject(); return false;
            case JsonTokenType.EndObject: writer.WriteEndObject(); return false;
            case JsonTokenType.StartArray: writer.WriteStartArray(); return false;
            case JsonTokenType.EndArray: writer.WriteEndArray(); return false;
            case JsonTokenType.PropertyName: return WriteMasked(Text(ref reader), redactor, writer.WritePropertyName);
            case JsonTokenType.String: return WriteMasked(Text(ref reader), redactor, writer.WriteStringValue);
            case JsonTokenType.Number: return WriteNumber(reader.ValueSpan, redactor, writer);
            case JsonTokenType.True: writer.WriteBooleanValue(true); return false;
            case JsonTokenType.False: writer.WriteBooleanValue(false); return false;
            case JsonTokenType.Null: writer.WriteNullValue(); return false;
            default: return false;
        }
    }

    /// <summary>
    /// The token's text, unescaped here rather than by <see cref="Utf8JsonReader.GetString"/>, which refuses a lone
    /// surrogate escape (<c>\ud83d</c>) — what Node's <c>JSON.stringify</c> writes when a truncated tool output split an
    /// emoji. The line is valid JSON, and refusing it would leave the whole line to the plain mask. Written back, such a
    /// half character becomes U+FFFD; only a line that held a secret is written back at all.
    /// </summary>
    private static string Text(ref Utf8JsonReader reader)
    {
        var raw = Encoding.UTF8.GetString(reader.ValueSpan);

        return reader.ValueIsEscaped ? Unescape(raw) : raw;
    }

    /// <summary>A JSON string's raw text, unescaped. The reader has already refused any malformed escape, so every backslash here starts a valid one.</summary>
    private static string Unescape(string raw)
    {
        var text = new StringBuilder(raw.Length);

        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\')
            {
                text.Append(raw[i]);
                continue;
            }

            var escape = raw[++i];

            if (escape == 'u')
            {
                text.Append((char)ushort.Parse(raw.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                i += 4;
                continue;
            }

            text.Append(escape switch { 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => escape });
        }

        return text.ToString();
    }

    private static bool WriteMasked(string value, SecretRedactor redactor, Action<string> write)
    {
        var masked = redactor.Redact(value);

        write(masked);

        return !ReferenceEquals(masked, value);
    }

    /// <summary>A number is kept exactly as written unless a secret is in it; then it becomes the masked string, since <c>***</c> is no number.</summary>
    private static bool WriteNumber(ReadOnlySpan<byte> raw, SecretRedactor redactor, Utf8JsonWriter writer)
    {
        var text = Encoding.UTF8.GetString(raw);
        var masked = redactor.Redact(text);

        if (ReferenceEquals(masked, text))
        {
            writer.WriteRawValue(raw, skipInputValidation: true);
            return false;
        }

        writer.WriteStringValue(masked);

        return true;
    }
}
