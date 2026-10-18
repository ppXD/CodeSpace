namespace CodeSpace.Messages.Dtos.Credentials;

/// <summary>
/// How long the credential health sweep may spend. <paramref name="Probe"/> bounds what one credential costs: a provider that
/// takes longer is given up on, and the credential counts as having had no answer. <paramref name="Pass"/> bounds the whole
/// pass, in-flight probe included, and is kept under the recurring job's interval so a pass is over before the next tick
/// starts. One instance is registered for the process (<see cref="Default"/>); a test registers a shorter one.
/// </summary>
public sealed record CredentialHealthDeadlines(TimeSpan Probe, TimeSpan Pass)
{
    public static readonly CredentialHealthDeadlines Default = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
}
