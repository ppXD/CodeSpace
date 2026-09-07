using System.Text.Json;

namespace CodeSpace.Messages.Agents;

/// <summary>
/// One durable process attempt that a physical execution was admitted for, reduced to what a recovery needs: the exact
/// <see cref="SandboxLaunchIdentity"/> the launch was bound to, the spool slot it was bound in, and the
/// <paramref name="RunnerKind"/> that owns both. Nothing about the process itself rides here — a pid belongs to the
/// runner's own receipt, and re-deriving it from the app's record is how a recovery ends up acting on a number that
/// has since been recycled.
///
/// <para><paramref name="RunnerKind"/> is what stops the address from being handed to the wrong backend: the spool
/// slot is one runner's private layout, so a recovery must resolve the runner the execution NAMES rather than the
/// first registered one that happens to be able to re-discover launches at all.</para>
/// </summary>
public sealed record AdmittedLaunch(SandboxLaunchIdentity Identity, string SpoolKey, string RunnerKind);

/// <summary>
/// Reader for the runner locator a native launch records on its process attempt. The locator column is opaque to
/// shared code and immutable once written, so this only ever READS it, and only the one field an address consists of.
/// A locator written by a different runner backend, or by a producer that recorded a spool directory instead of a key,
/// yields null rather than a guess.
/// </summary>
public static class NativeLaunchLocator
{
    public const string SpoolKeyProperty = "spoolKey";

    public static string? SpoolKeyOf(string? locatorJson)
    {
        if (string.IsNullOrWhiteSpace(locatorJson)) return null;

        try
        {
            using var document = JsonDocument.Parse(locatorJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty(SpoolKeyProperty, out var key) || key.ValueKind != JsonValueKind.String) return null;
            var value = key.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException) { return null; }
    }
}
