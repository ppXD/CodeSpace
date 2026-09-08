using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Services.Agents.Context.Exceptions;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>Opaque, scope-bound keyset cursor for descending durable session events.</summary>
internal readonly record struct SessionAgentEventCursor(long Sequence)
{
    private const string Version = "v1";
    private const int MaxEncodedChars = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string Encode(Guid teamId, Guid sessionId, string? query)
    {
        var raw = string.Join('\n', Version, teamId.ToString("N"), sessionId.ToString("N"), QueryFingerprint(query), Sequence.ToString(CultureInfo.InvariantCulture));
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    public static SessionAgentEventCursor? Decode(string? value, Guid teamId, Guid sessionId, string? query)
    {
        if (value == null) return null;
        if (value.Length is 0 or > MaxEncodedChars) throw Invalid();

        try
        {
            var parts = StrictUtf8.GetString(Base64Url.DecodeFromChars(value)).Split('\n', StringSplitOptions.None);
            if (parts.Length == 5
                && parts[0] == Version
                && Guid.TryParseExact(parts[1], "N", out var cursorTeamId) && cursorTeamId == teamId
                && Guid.TryParseExact(parts[2], "N", out var cursorSessionId) && cursorSessionId == sessionId
                && FixedTimeEquals(parts[3], QueryFingerprint(query))
                && long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) && sequence > 0)
                return new SessionAgentEventCursor(sequence);
        }
        catch (FormatException) { }
        catch (DecoderFallbackException) { }

        throw Invalid();
    }

    private static string QueryFingerprint(string? query) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query?.Trim() ?? "")));

    private static bool FixedTimeEquals(string actual, string expected)
    {
        var actualBytes = Encoding.ASCII.GetBytes(actual);
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private static AgentContextCursorException Invalid() => new("Invalid or out-of-scope session.events cursor. Start a new retrieval when the source, query, team, or session changes.");
}
