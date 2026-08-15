using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing;

/// <summary>Opaque descending keyset cursor bound to one durable route identity.</summary>
public readonly record struct StorageRouteRevisionCursor(Guid RouteId, int Revision, Guid Id)
{
    public string Encode()
    {
        var raw = $"{RouteId:N}\n{Revision.ToString(CultureInfo.InvariantCulture)}\n{Id:N}";
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    public static StorageRouteRevisionCursor? Decode(string? cursor, Guid expectedRouteId)
    {
        if (cursor == null) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split('\n', StringSplitOptions.None);
            if (parts.Length == 3 && Guid.TryParseExact(parts[0], "N", out var routeId) && routeId == expectedRouteId
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) && revision > 0
                && Guid.TryParseExact(parts[2], "N", out var id) && id != Guid.Empty)
                return new StorageRouteRevisionCursor(routeId, revision, id);
        }
        catch (FormatException) { }

        throw new InvalidOperationException("Invalid storage route revision cursor.");
    }
}
