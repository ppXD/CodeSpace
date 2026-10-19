using System.Globalization;

namespace CodeSpace.Messages.Failures;

/// <summary>
/// A failure written down as what it is — a <see cref="Code"/> and the things it names — instead of only the sentence it
/// was said in. A run's error is free text, and a person reading "The credential 'GitLab · nacho' was disconnected" is told
/// what happened but not given anything to act on: no screen can link a sentence to a fix. This is the machine-readable
/// half, carried on the run's ledger records and on an agent run's result, so what reads them (the run detail, the Room) can
/// say what is wrong, who can fix it and with which button, and re-read the live state of the thing it names.
///
/// <para>Only failures declared in <see cref="FailureCodes.ProviderConnection"/> are written down — those with a fix a
/// person can make outside the run. Everything here is an identifier, an address or a status the failure's own
/// <see cref="IFailure.Details"/> already puts on an API response; no token, no message text and no provider body is ever
/// carried, so the record is as safe to keep as the response was to send.</para>
/// </summary>
public sealed record PersistedFailure
{
    /// <summary>The stable name from <see cref="FailureCodes"/>.</summary>
    public required string Code { get; init; }

    /// <summary>What the failure's own <see cref="IFailure.Details"/> said, as text — a credential's id and name, the address a provider redirected to, the status it answered. Absent when it said nothing.</summary>
    public IReadOnlyDictionary<string, string>? Details { get; init; }

    /// <summary>The repository the failing step was acting on, when the step names one — the subject a remedy re-links. A failure that carries a credential of its own does not need it, and one that carries none (a provider's 401) cannot be traced to a credential without it.</summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>Where the failure happened when it was not in the run that records it — a sub-workflow's run, carried up to the parent node that ran it (<see cref="FailureOrigin"/>). Absent for a failure that happened in the run itself, and for every record written before sub-workflows carried their failures, which then reads exactly as it did.</summary>
    public FailureOrigin? Origin { get; init; }

    public string? Detail(string key) => Details is not null && Details.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    public Guid? DetailGuid(string key) => Guid.TryParse(Detail(key), out var id) && id != Guid.Empty ? id : null;

    /// <summary>The details as text: a value that was null is left out, anything else is written the way an invariant-culture serializer would, so what is read back is what was thrown.</summary>
    public static IReadOnlyDictionary<string, string>? DetailsAsText(IReadOnlyDictionary<string, object?>? details)
    {
        if (details is null) return null;

        var text = details.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal);

        return text.Count == 0 ? null : text;
    }
}
