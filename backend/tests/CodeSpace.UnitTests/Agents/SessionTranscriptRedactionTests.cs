using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The masking a CLI session transcript gets before it is stored (the run's result, its mid-run checkpoints, the resume
/// payload a retry is handed). The transcript is newline-delimited JSON that a harness restores VERBATIM for
/// <c>--resume</c>, so the mask has two jobs and both are pinned here: no secret survives, and every line that parsed
/// before still parses — a transcript the CLI cannot read costs the continuation it exists for.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SessionTranscriptRedactionTests
{
    private const string Key = "CANARY-G2-sk-ant-transcript-7f3e9a1c";

    [Fact]
    public void A_masked_transcript_is_still_valid_jsonl_with_no_secret_left()
    {
        var transcript = string.Join('\n',
            Line(new { type = "system", subtype = "init", session_id = "s-1" }),
            Line(new { type = "user", message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "t-1", content = $"ANTHROPIC_API_KEY={Key}\n" } } } }),
            Line(new { type = "assistant", message = new { content = new object[] { new { type = "text", text = $"The key is {Key}, and again {Key}." } } } }),
            "");

        var masked = SessionTranscriptRedaction.Redact(transcript, new SecretRedactor([Key]));

        masked.ShouldNotContain(Key);
        masked.Split('\n').Length.ShouldBe(transcript.Split('\n').Length, "a line is masked in place; the CLI's line structure is the session's structure");
        AssertValidJsonLines(masked);
        masked.ShouldContain(SecretRedactor.Placeholder);
    }

    [Fact]
    public void A_line_without_a_secret_keeps_its_exact_bytes()
    {
        // The CLI wrote these lines; anything the mask did not need to touch must reach --resume byte for byte.
        var untouched = "{\"type\":\"system\",  \"session_id\":\"s-1\",\"cwd\":\"/tmp/w\\/x\"}";
        var transcript = untouched + "\n" + Line(new { text = Key }) + "\n";

        var masked = SessionTranscriptRedaction.Redact(transcript, new SecretRedactor([Key]));

        masked.Split('\n')[0].ShouldBe(untouched);
    }

    [Theory]
    [InlineData("p4ss-w0rd-backslash-tail\\")]          // the file holds `…tail\\`: a raw match leaves a lone `\` that escapes what follows
    [InlineData("pa\"ss-quoted-secret-value")]          // the file holds `pa\"ss…`, so the raw value is never there to match
    [InlineData("tab\tseparated-secret-value")]         // a control character the CLI's encoder escapes
    public void A_secret_the_cli_escaped_is_masked_and_the_line_still_parses(string secret)
    {
        // MUTATION this pins: replace the value-aware mask with plain text replacement over the line. The first case
        // then produces invalid JSON and the other two leave the secret readable once the line is decoded.
        var transcript = Line(new { type = "user", tool_result = $"value={secret};" }) + "\n";

        var masked = SessionTranscriptRedaction.Redact(transcript, new SecretRedactor([secret]));

        AssertValidJsonLines(masked);
        DecodedStrings(masked).ShouldAllBe(text => !text.Contains(secret), $"the decoded transcript still carries the secret: {masked}");
    }

    [Fact]
    public void A_secret_in_a_property_name_is_masked_too()
    {
        var transcript = "{\"" + Key + "\":true}\n";

        var masked = SessionTranscriptRedaction.Redact(transcript, new SecretRedactor([Key]));

        masked.ShouldNotContain(Key);
        AssertValidJsonLines(masked);
    }

    [Fact]
    public void A_numeric_secret_becomes_a_masked_string_and_the_line_still_parses()
    {
        const string pin = "8031975264";
        var transcript = "{\"account\":" + pin + ",\"n\":42}\n";

        var masked = SessionTranscriptRedaction.Redact(transcript, new SecretRedactor([pin]));

        masked.ShouldNotContain(pin);
        AssertValidJsonLines(masked);
        JsonDocument.Parse(masked.TrimEnd('\n')).RootElement.GetProperty("n").GetInt32().ShouldBe(42, "a number that is no secret keeps its value");
    }

    [Fact]
    public void A_line_that_is_not_json_gets_the_plain_mask()
    {
        // A torn tail, or a log line a CLI interleaved: there is no value to mask inside, and nothing to keep valid.
        var transcript = Line(new { ok = true }) + "\n" + $"{{\"type\":\"assistant\",\"text\":\"{Key}";

        var masked = SessionTranscriptRedaction.Redact(transcript, new SecretRedactor([Key]));

        masked.ShouldNotContain(Key);
        masked.Split('\n')[0].ShouldBe(Line(new { ok = true }));
    }

    [Fact]
    public void A_complete_line_holding_a_lone_surrogate_escape_is_still_masked_inside_its_values()
    {
        // Node's JSON.stringify writes a lone surrogate as an escape when a truncated tool output split an emoji: the line
        // is valid JSON, but the reader's own string decoding refuses it. The secret here is one only the value-aware mask
        // can find — its ESC is escaped in lowercase hex, as Node writes it, which no fixed escaped form matches.
        // MUTATION this pins: decode with Utf8JsonReader.GetString → the line falls back and keeps the secret.
        const string secret = "\u001b[0mCANARY-G2-esc-token-41c7";
        var line = "{\"type\":\"user\",\"tool_result\":\"\\u001b[0mCANARY-G2-esc-token-41c7 ... output truncated \\ud83d\"}";

        Should.NotThrow(() => JsonDocument.Parse(line).Dispose(), "fixture check: the line is valid JSON as the CLI wrote it");

        var masked = SessionTranscriptRedaction.Redact(line + "\n", new SecretRedactor([secret]));

        masked.ShouldNotContain("CANARY-G2-esc-token-41c7");
        AssertValidJsonLines(masked);
        masked.ShouldContain("output truncated", Case.Sensitive, "the rest of the value is kept");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_torn_tail_holding_a_secret_the_writer_escaped_is_masked(bool asciiOnlyWriter)
    {
        // A line the reader cannot take — the tail a killed CLI left half written — gets no value-aware mask, so a secret
        // the writer escaped (a multi-line key, a quote, a backslash) must be matched in its escaped forms: a writer's
        // short escapes (\n, \", \\), and an ASCII-only writer's \uXXXX ones.
        // MUTATION this pins: drop the escaped forms from the fallback → both bodies survive.
        const string pem = "-----BEGIN KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC\n-----END KEY-----";
        const string quoted = "pa\"ss\\word+CANARY-G2-1234";
        var encoder = asciiOnlyWriter ? System.Text.Encodings.Web.JavaScriptEncoder.Default : System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        var torn = "{\"type\":\"user\",\"tool_result\":\"" + JsonEncodedText.Encode($"{pem} then {quoted} and the CLI was kil", encoder);

        var masked = SessionTranscriptRedaction.Redact(Line(new { ok = true }) + "\n" + torn, new SecretRedactor([pem, quoted]));

        var tail = masked.Split('\n')[1];
        tail.ShouldNotContain("MIIEvQIBADANBgkqhkiG9w0BAQEFAASC");
        tail.ShouldNotContain("CANARY-G2-1234");
        tail.ShouldContain("and the CLI was kil", Case.Sensitive, "only the secrets are masked");
    }

    [Fact]
    public void A_redactor_with_no_secrets_returns_the_transcript_itself()
    {
        var transcript = Line(new { text = Key }) + "\n";

        SessionTranscriptRedaction.Redact(transcript, SecretRedactor.None).ShouldBeSameAs(transcript);
    }

    private static string Line(object value) => JsonSerializer.Serialize(value);

    private static void AssertValidJsonLines(string transcript)
    {
        foreach (var line in transcript.Split('\n').Where(l => l.Length > 0))
            Should.NotThrow(() => JsonDocument.Parse(line).Dispose(), $"every line --resume reads must still parse: {line}");
    }

    private static IEnumerable<string> DecodedStrings(string transcript)
    {
        foreach (var line in transcript.Split('\n').Where(l => l.Length > 0))
        {
            using var document = JsonDocument.Parse(line);

            foreach (var text in Strings(document.RootElement)) yield return text;
        }
    }

    private static IEnumerable<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString()!],
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Strings(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Strings),
        _ => [],
    };
}
