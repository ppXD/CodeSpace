using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

internal enum LegacyPlacementAdoptionCursorMode
{
    Evidence,
    Minting,
    Cleaning,
}

/// <summary>Opaque cursor to one durable arc revision and position; population membership lives in Postgres.</summary>
internal sealed record LegacyPlacementAdoptionCursor
{
    internal const string ProtectorPurpose = "CodeSpace.Storage.LegacyPlacementAdoptionCursor.v2";
    public const int MaximumEncodedLength = 1024;
    public required Guid ProfileId { get; init; }
    public required int ProfileRevision { get; init; }
    public required Guid ArcId { get; init; }
    public required long ArcRevision { get; init; }
    public required LegacyPlacementAdoptionCursorMode Mode { get; init; }
    public required long Position { get; init; }

    public string Encode(IDataProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        var raw = string.Join('\n', "v2", ProfileId.ToString("N"), ProfileRevision.ToString(CultureInfo.InvariantCulture),
            ArcId.ToString("N"), ArcRevision.ToString(CultureInfo.InvariantCulture), ModeValue(Mode), Position.ToString(CultureInfo.InvariantCulture));
        return protector.Protect(raw);
    }

    public static bool TryDecode(string cursor, Guid expectedProfileId, IDataProtector protector, out LegacyPlacementAdoptionCursor parsed)
    {
        parsed = null!;
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > MaximumEncodedLength || protector == null) return false;
        try
        {
            var parts = protector.Unprotect(cursor).Split('\n', StringSplitOptions.None);
            if (parts.Length == 7 && parts[0] == "v2" && Guid.TryParseExact(parts[1], "N", out var profileId) && profileId == expectedProfileId
                && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var profileRevision) && profileRevision > 0
                && Guid.TryParseExact(parts[3], "N", out var arcId) && arcId != Guid.Empty
                && long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var arcRevision) && arcRevision > 0
                && TryMode(parts[5], out var mode)
                && long.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out var position) && position >= 0)
            {
                parsed = new LegacyPlacementAdoptionCursor
                {
                    ProfileId = profileId, ProfileRevision = profileRevision, ArcId = arcId, ArcRevision = arcRevision,
                    Mode = mode, Position = position,
                };
                return true;
            }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException) { }

        return false;
    }

    private static string ModeValue(LegacyPlacementAdoptionCursorMode mode) => mode switch
    {
        LegacyPlacementAdoptionCursorMode.Evidence => "e",
        LegacyPlacementAdoptionCursorMode.Minting => "m",
        _ => "c",
    };

    private static bool TryMode(string value, out LegacyPlacementAdoptionCursorMode mode)
    {
        mode = value switch
        {
            "m" => LegacyPlacementAdoptionCursorMode.Minting,
            "c" => LegacyPlacementAdoptionCursorMode.Cleaning,
            _ => LegacyPlacementAdoptionCursorMode.Evidence,
        };
        return value is "e" or "m" or "c";
    }
}
