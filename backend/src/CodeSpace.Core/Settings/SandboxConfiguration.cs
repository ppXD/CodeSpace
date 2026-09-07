using System.Globalization;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Settings;

/// <summary>Parses the effective deployment security settings before either startup path can admit work.</summary>
internal static class SandboxConfiguration
{
    public static AgentAutonomyLevel? ParseMaxAutonomy(string? raw)
    {
        if (raw is null) return null;

        var value = raw.Trim();
        if (Enum.TryParse<AgentAutonomyLevel>(value, ignoreCase: true, out var level) && string.Equals(Enum.GetName(level), value, StringComparison.OrdinalIgnoreCase)) return level;

        throw new SandboxConfigurationException(RuntimeSettings.MaxAutonomyKey, $"one of {string.Join(", ", Enum.GetNames<AgentAutonomyLevel>())}");
    }

    public static bool ParseRequireConfinement(string? raw)
    {
        if (raw is null) return false;
        if (bool.TryParse(raw, out var value)) return value;

        throw new SandboxConfigurationException(RuntimeSettings.RequireConfinementKey, "true or false");
    }

    public static int? ParseMemoryCeilingMb(string? raw)
    {
        if (raw is null) return null;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0) return value;

        throw new SandboxConfigurationException(RuntimeSettings.AgentMemoryCeilingMbKey, $"a positive integer no greater than {int.MaxValue}");
    }
}
