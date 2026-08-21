using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>Opaque v1 keyset bound to one exact team/session/optional requested anchor and membership head.</summary>
internal readonly record struct SessionRunMetadataCursor(Guid TeamId, Guid SessionId, Guid? RunAnchorId, long MembershipHeadRunNumber, long BeforeRunNumber)
{
    private const string WireVersion = "v1";

    public string Encode()
    {
        var anchor = RunAnchorId?.ToString("N") ?? "-";
        var raw = string.Join('\n', WireVersion, TeamId.ToString("N"), SessionId.ToString("N"), anchor,
            MembershipHeadRunNumber.ToString(CultureInfo.InvariantCulture), BeforeRunNumber.ToString(CultureInfo.InvariantCulture));
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    public static SessionRunMetadataCursor Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) throw new InvalidOperationException("Invalid Session run metadata cursor.");
        if (cursor.Length > SessionRunMetadataPageRequest.MaximumCursorLength) throw new InvalidOperationException("Session run metadata cursor is too long.");

        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split('\n');
            if (parts.Length == 6 && parts[0] == WireVersion
                && Guid.TryParseExact(parts[1], "N", out var teamId) && teamId != Guid.Empty
                && Guid.TryParseExact(parts[2], "N", out var sessionId) && sessionId != Guid.Empty
                && TryParseAnchor(parts[3], out var anchorId)
                && long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var head) && head > 0
                && long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var before) && before > 0 && before <= head)
                return new SessionRunMetadataCursor(teamId, sessionId, anchorId, head, before);
        }
        catch (FormatException) { }

        throw new InvalidOperationException("Invalid Session run metadata cursor.");
    }

    private static bool TryParseAnchor(string wire, out Guid? anchorId)
    {
        if (wire == "-")
        {
            anchorId = null;
            return true;
        }

        if (Guid.TryParseExact(wire, "N", out var parsed) && parsed != Guid.Empty)
        {
            anchorId = parsed;
            return true;
        }

        anchorId = null;
        return false;
    }
}
