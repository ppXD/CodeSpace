using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// When a credential may be taken out of the team's list: it is disconnected, and nothing leans on it. Pure, so the rule and the
/// words a refusal says are pinned without a database — and so the Providers dialog, which folds away the dead rows with nothing on
/// them, and the removal that accepts them read one rule: a credential still in service is a Disconnect's to end, and what leans on
/// a dead one (<see cref="CredentialDependents"/>) is moved or unlinked before the row goes.
/// </summary>
internal static class CredentialRemoval
{
    /// <summary>Why the credential may not be removed, in words a person can act on; null when it may.</summary>
    public static string? Refusal(Credential credential, CredentialDependents dependents)
    {
        if (credential.Status != CredentialStatus.Revoked)
            return $"Credential '{credential.DisplayName}' is still connected. Disconnect it first — only a disconnected credential with nothing on it is removed.";

        var leaning = Leaning(dependents);

        return leaning == null ? null : $"Credential '{credential.DisplayName}' still has {leaning} leaning on it. Move or unlink them first.";
    }

    /// <summary>"2 repositories, 1 group hook and 1 act-as identity" — only what is there; null when nothing is.</summary>
    private static string? Leaning(CredentialDependents dependents)
    {
        var parts = new List<string>();

        AddCount(parts, dependents.Repositories.Count, "repository", "repositories");
        AddCount(parts, dependents.ConnectionWebhooks.Count, "group hook", "group hooks");
        AddCount(parts, dependents.Identities.Count, "act-as identity", "act-as identities");

        return parts.Count == 0 ? null : parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}";
    }

    private static void AddCount(List<string> parts, int count, string one, string many)
    {
        if (count > 0) parts.Add($"{count} {(count == 1 ? one : many)}");
    }
}
