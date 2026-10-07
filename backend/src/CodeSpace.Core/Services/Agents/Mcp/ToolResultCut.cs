using System.Text.Json;

namespace CodeSpace.Core.Services.Agents.Mcp;

/// <summary>
/// A structured tool result cut to fit a size, keeping its shape: every string longer than a common length keeps its start
/// and end around a count of what was cut, and nothing else changes, so the result still reads as the tool's declared
/// structure. Pure. For a call that already ran, where the answer must still be a success its client accepts.
/// </summary>
public static class ToolResultCut
{
    /// <summary>The shortest a cut string is kept to; a result that does not fit with every long string this short cannot be cut to its shape.</summary>
    private const int ShortestKept = 64;

    /// <summary><paramref name="value"/> with its long strings cut, as little as fits in <paramref name="budget"/> characters of JSON — or null when shortening text cannot make it fit.</summary>
    public static JsonElement? Shrunk(JsonElement value, int budget)
    {
        if (value.GetRawText().Length <= budget) return value;

        if (Cut(value, ShortestKept).GetRawText().Length > budget) return null;

        return Cut(value, LongestKeptThatFits(value, budget));
    }

    /// <summary>The longest each string may keep with the whole still inside <paramref name="budget"/>: a search between <see cref="ShortestKept"/>, which fits, and the longest string.</summary>
    private static int LongestKeptThatFits(JsonElement value, int budget)
    {
        var (fits, tooLong) = (ShortestKept, LongestString(value) + 1);

        while (tooLong - fits > 1)
        {
            var keep = fits + (tooLong - fits) / 2;

            if (Cut(value, keep).GetRawText().Length <= budget) fits = keep;
            else tooLong = keep;
        }

        return fits;
    }

    private static JsonElement Cut(JsonElement value, int keep) => value.ValueKind switch
    {
        JsonValueKind.String => JsonSerializer.SerializeToElement(CutText(value.GetString() ?? "", keep)),
        JsonValueKind.Array => JsonSerializer.SerializeToElement(value.EnumerateArray().Select(item => Cut(item, keep)).ToList()),
        JsonValueKind.Object => JsonSerializer.SerializeToElement(value.EnumerateObject().ToDictionary(property => property.Name, property => Cut(property.Value, keep))),
        _ => value,
    };

    /// <summary><paramref name="text"/> kept to its start and end, about <paramref name="keep"/> characters of it, never splitting a surrogate pair.</summary>
    private static string CutText(string text, int keep)
    {
        if (text.Length <= keep) return text;

        var head = keep * 2 / 3;
        var tail = keep - head;

        if (char.IsHighSurrogate(text[head - 1])) head--;
        if (char.IsLowSurrogate(text[^tail])) tail--;

        return $"{text[..head]}…[{text.Length - head - tail} characters cut]…{text[^tail..]}";
    }

    private static int LongestString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()?.Length ?? 0,
        JsonValueKind.Array => value.EnumerateArray().Select(LongestString).DefaultIfEmpty(0).Max(),
        JsonValueKind.Object => value.EnumerateObject().Select(property => LongestString(property.Value)).DefaultIfEmpty(0).Max(),
        _ => 0,
    };
}
