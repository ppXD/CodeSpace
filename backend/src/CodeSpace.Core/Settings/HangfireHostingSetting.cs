using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings;

/// <summary>
/// The pod's Hangfire ROLE, read from the <c>HangfireHosting</c> configuration key — so a deployment sets it
/// through appsettings, a k8s ConfigMap, or the standard <c>HangfireHosting</c> environment variable: one typed
/// value resolved by the normal configuration pipeline, rather than a bespoke boolean toggle.
///
/// <para>Defaults to <see cref="HangfireHosting.Worker"/> when the key is absent or unparseable, and that default
/// is load-bearing: an all-in-one process (local <c>dotnet run</c>, a one-pod deployment, every existing test host)
/// keeps processing exactly as it did before this key existed, so adding the key changes nothing for anyone who
/// does not set it. Defaulting to <see cref="HangfireHosting.Api"/> would silently stop draining the queue on an
/// unconfigured deployment — a far worse failure than running a worker where none was wanted.</para>
/// </summary>
public class HangfireHostingSetting : IConfigurationSetting<HangfireHosting>
{
    /// <summary>The configuration key. Pinned by test (Rule 8) — renaming it silently reverts every deployment to the default role.</summary>
    public const string ConfigurationKey = "HangfireHosting";

    public HangfireHostingSetting(IConfiguration configuration)
    {
        Value = Resolve(configuration.GetValue<string?>(ConfigurationKey));
    }

    public HangfireHosting Value { get; set; }

    /// <summary>Pure resolution (no configuration read) so the default + the case-insensitive parse are unit-pinned directly: absent, blank, or unrecognised resolves to <see cref="HangfireHosting.Worker"/>.</summary>
    public static HangfireHosting Resolve(string? raw) =>
        Enum.TryParse<HangfireHosting>(raw?.Trim(), ignoreCase: true, out var hosting) ? hosting : HangfireHosting.Worker;
}
