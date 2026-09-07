using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Settings;

/// <summary>A deployment security setting is invalid; starting with a broader fallback is not permitted.</summary>
public sealed class SandboxConfigurationException : InvalidOperationException, IFailure
{
    public SandboxConfigurationException(string configurationKey, string expected) : base($"Refusing to start: '{configurationKey}' must be {expected} when configured. Omit the setting to use its committed default.")
    {
        ConfigurationKey = configurationKey;
    }

    public string ConfigurationKey { get; }

    public FailureKind Kind => FailureKind.Internal;

    public string Code => FailureCodes.Internal;
}
