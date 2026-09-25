using System.Collections.Specialized;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using CodeSpace.IntegrationTests.Webhooks;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;

namespace CodeSpace.UnitTests.Providers;

/// <summary>How the loopback provider treats the attempts of one write.</summary>
public enum WriteScenario
{
    /// <summary>The write lands, then a gateway answers 502 — the backend committed, the caller hears a 5xx.</summary>
    LandsThenGatewayError,

    /// <summary>The write lands, then the connection drops mid-answer — on the wire, a timeout's twin: sent, applied, never acknowledged.</summary>
    LandsThenConnectionDrops,

    /// <summary>The provider answers 503 without applying anything; the retry lands.</summary>
    RefusedBeforeLanding,

    /// <summary>Attempt 1 is refused without effect, attempt 2 lands and its answer is lost to a 502 — the effect first appears on the second attempt, and the third must adopt it.</summary>
    RefusedThenLandsThenGatewayError,

    /// <summary>The connection drops before the provider applies anything; the retry lands. To the caller it is the same dropped connection as <see cref="LandsThenConnectionDrops"/> — only a probe can tell them apart.</summary>
    DroppedBeforeLanding
}

/// <summary>What the loopback provider does with one attempt of a write.</summary>
public enum AttemptOutcome { Lands, LandsThenGatewayError, LandsThenConnectionDrops, Refused, DroppedBeforeLanding }

internal static class WriteScenarios
{
    /// <summary>The outcome of attempt <paramref name="attempt"/> (0-based) under <paramref name="scenario"/>; every attempt past the script lands.</summary>
    public static AttemptOutcome OutcomeOf(this WriteScenario scenario, int attempt) => (scenario, attempt) switch
    {
        (WriteScenario.LandsThenGatewayError, 0) => AttemptOutcome.LandsThenGatewayError,
        (WriteScenario.LandsThenConnectionDrops, 0) => AttemptOutcome.LandsThenConnectionDrops,
        (WriteScenario.RefusedBeforeLanding, 0) => AttemptOutcome.Refused,
        (WriteScenario.RefusedThenLandsThenGatewayError, 0) => AttemptOutcome.Refused,
        (WriteScenario.RefusedThenLandsThenGatewayError, 1) => AttemptOutcome.LandsThenGatewayError,
        (WriteScenario.DroppedBeforeLanding, 0) => AttemptOutcome.DroppedBeforeLanding,
        _ => AttemptOutcome.Lands
    };

    /// <summary>The answer for an attempt that has already applied its effect (or, for <see cref="AttemptOutcome.Refused"/> and <see cref="AttemptOutcome.DroppedBeforeLanding"/>, never will).</summary>
    public static StubReply Answer(this AttemptOutcome outcome, Func<string> landedBody) => outcome switch
    {
        AttemptOutcome.Refused => new StubReply(503, """{"message":"503 Service Unavailable"}"""),
        AttemptOutcome.LandsThenGatewayError => new StubReply(502, """{"message":"502 Bad Gateway"}"""),
        AttemptOutcome.LandsThenConnectionDrops or AttemptOutcome.DroppedBeforeLanding => StubReply.DropConnection,
        _ => new StubReply(201, landedBody())
    };
}

/// <summary>
/// One collection on a loopback forge — comments, issues, pull requests, reviews. Each create follows its scripted
/// <see cref="AttemptOutcome"/>: a create that lands is stored before it is answered, however it is answered. A
/// list answers everything stored, so a probe sees exactly what landed — no more, no less. <c>refuse</c> lets a
/// collection turn a create away the way the real provider would (a second open pull request for one branch).
/// </summary>
internal sealed class ForgeCollection
{
    private readonly Func<int, AttemptOutcome> _script;
    private readonly Func<long, JsonElement, string> _render;
    private readonly Func<JsonElement, IReadOnlyList<StoredItem>, StubReply?>? _refuse;
    private readonly List<StoredItem> _stored = new();
    private int _creates;

    public ForgeCollection(Func<int, AttemptOutcome> script, Func<long, JsonElement, string> render, Func<JsonElement, IReadOnlyList<StoredItem>, StubReply?>? refuse = null)
    {
        _script = script;
        _render = render;
        _refuse = refuse;
    }

    public ForgeCollection(WriteScenario scenario, Func<long, JsonElement, string> render, Func<JsonElement, IReadOnlyList<StoredItem>, StubReply?>? refuse = null) : this(attempt => scenario.OutcomeOf(attempt), render, refuse) { }

    public IReadOnlyList<StoredItem> Stored => _stored;

    /// <summary>Something already on the forge before the test's call — stored as if created earlier, not counted as an attempt.</summary>
    public void Seed(object sent) => _stored.Add(new StoredItem(1000 + _stored.Count + 1, JsonSerializer.SerializeToElement(sent)));

    public StubReply Create(RecordedRequest request)
    {
        var outcome = _script(_creates++);
        var sent = JsonDocument.Parse(request.Body).RootElement.Clone();

        if (outcome is AttemptOutcome.Refused or AttemptOutcome.DroppedBeforeLanding) return outcome.Answer(() => string.Empty);

        if (_refuse?.Invoke(sent, _stored) is { } refusal) return refusal;

        var item = new StoredItem(1000 + _stored.Count + 1, sent);
        _stored.Add(item);

        return outcome.Answer(() => _render(item.Id, sent));
    }

    public StubReply List(RecordedRequest _) => new(200, "[" + string.Join(",", _stored.Select(s => _render(s.Id, s.Sent))) + "]");

    /// <summary>
    /// One page of everything stored, the way GitLab pages a list: <c>per_page</c> items (GitLab's default of 20 when the
    /// request names none) from <c>page</c>, with <c>X-Next-Page</c> naming the next page — empty on the last — and the
    /// same page as a <c>Link</c> header, since GitLab sends both. A caller that reads one page sees only that page.
    /// </summary>
    public StubReply ListGitLabPage(RecordedRequest request, string baseUrl)
    {
        var query = ForgeQuery.Of(request);
        var perPage = int.Parse(query["per_page"] ?? "20");
        var page = int.Parse(query["page"] ?? "1");
        var items = _stored.Skip((page - 1) * perPage).Take(perPage).Select(s => _render(s.Id, s.Sent));
        int? nextPage = _stored.Count > page * perPage ? page + 1 : null;

        return new StubReply(200, "[" + string.Join(",", items) + "]") { Headers = GitLabPageHeaders(request, baseUrl, perPage, nextPage) };
    }

    private static Dictionary<string, string> GitLabPageHeaders(RecordedRequest request, string baseUrl, int perPage, int? nextPage)
    {
        var headers = new Dictionary<string, string> { ["X-Next-Page"] = nextPage?.ToString() ?? string.Empty };

        if (nextPage is { } next) headers["Link"] = $"<{baseUrl}{request.PathAndQuery.Split('?')[0]}?per_page={perPage}&page={next}>; rel=\"next\"";

        return headers;
    }

    internal sealed record StoredItem(long Id, JsonElement Sent)
    {
        public string? Text(string field) => Sent.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}

internal static class ForgeQuery
{
    /// <summary>The request's query string, decoded — for a list endpoint that has to honour the filters a probe sends.</summary>
    public static NameValueCollection Of(RecordedRequest request) => HttpUtility.ParseQueryString(new Uri(new Uri("http://stub"), request.PathAndQuery).Query);

    /// <summary>One page of the matching items, the way a provider pages a list: <c>per_page</c> when the request names one.</summary>
    public static IEnumerable<ForgeCollection.StoredItem> Page(this IEnumerable<ForgeCollection.StoredItem> matching, NameValueCollection query) =>
        query["per_page"] is { } perPage ? matching.Take(int.Parse(perPage)) : matching;
}

internal static class ForgeAssertions
{
    /// <summary>The create went out with the caller's text verbatim and exactly one idempotency marker, at the end.</summary>
    public static void ShouldCarryOneMarker(this string? sentBody, string callerText) =>
        sentBody.ShouldNotBeNull().ShouldMatch("^" + Regex.Escape(callerText) + @"\n\n<!-- codespace:idempotency:[0-9a-f]{32} -->$", "the body must be the caller's text followed by exactly one marker");

    public static int Sent(this StubProviderHost host, string method, string pathFragment) =>
        host.Requests.Count(r => r.Method == method && r.PathAndQuery.Contains(pathFragment, StringComparison.OrdinalIgnoreCase));
}
