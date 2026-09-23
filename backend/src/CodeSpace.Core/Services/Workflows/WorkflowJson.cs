using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Shared System.Text.Json settings for workflow definition I/O. We deliberately mirror
/// ASP.NET Core's "Web" defaults (camelCase, case-insensitive on read, string enums) so
/// the JSON shape on disk (templates) matches what the API serializes/deserializes — a
/// definition loaded from disk, posted through the SPA, and read back via the API is
/// byte-for-byte identical aside from whitespace.
/// </summary>
public static class WorkflowJson
{
    public static JsonSerializerOptions Options { get; } = BuildOptions();

    /// <summary>
    /// How an object or array is written into surrounding TEXT — a prompt, a message body — by a <c>{{...}}</c>
    /// template, and by any projection that must stay byte-identical to one (<c>MapResultsPrompt</c>). Characters are
    /// left as themselves: the HTML-safe default turned every CJK character and every <c>+ &lt; &gt; &amp; '</c> into a
    /// six-character <c>\uXXXX</c>, so a pull-request diff bound into a review prompt reached the model as escape
    /// codes at up to twice its size — while the string branch beside it already inserted all of those raw, so the
    /// escaping protected nothing. What JSON itself requires is still escaped (the quote, the backslash, control
    /// characters), so a value embedded in a JSON body stays parseable. One exception no built-in encoder lifts: a
    /// character outside the Basic Multilingual Plane (an emoji) is still written as its surrogate-pair escape.
    /// </summary>
    public static JsonSerializerOptions InterpolatedText { get; } = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
