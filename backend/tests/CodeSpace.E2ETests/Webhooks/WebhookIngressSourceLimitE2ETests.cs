using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using CodeSpace.Api.Extensions;
using CodeSpace.E2ETests.Infrastructure;
using Shouldly;

namespace CodeSpace.E2ETests.Webhooks;

/// <summary>
/// What one unauthenticated source can make the ingress rate limiter HOLD. The per-target limiter keys on the hook id or
/// callback token in the URL — values the sender chooses — so a sender changing the target on every request used to get
/// a fresh limiter each time: never limited, and each limiter kept alive past its window (about 34 KB apiece for a
/// kilobytes-long token). These are the two bounds on that: a per-address budget that runs before any per-target limiter
/// is created, and routes that refuse a target no hook or minted token can be.
///
/// <para>Tier: 🟢 High-fidelity — real app host (<see cref="WebhookApiFactory"/>), real routing, the production
/// <c>RateLimiterOptions</c> from <c>WebhookIngressRateLimitExtension</c>, the real controllers and their lookups against
/// real Postgres. Its own class, and so its own host: spending one address's whole budget here must not throttle the
/// sibling tests that share <c>WebhookOutsiderHardeningE2ETests</c>' host (TestServer sends every request from the same
/// unknown address).</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class WebhookIngressSourceLimitE2ETests : IClassFixture<WebhookApiFactory>
{
    /// <summary>Well inside the one-minute window, so the budget cannot refill mid-test and pass the final request for the wrong reason.</summary>
    private static readonly TimeSpan BudgetSpendDeadline = TimeSpan.FromSeconds(45);

    private readonly WebhookApiFactory _factory;

    public WebhookIngressSourceLimitE2ETests(WebhookApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task One_source_changing_the_target_on_every_request_is_limited_by_its_address()
    {
        var client = _factory.CreateClient();
        var statuses = new ConcurrentDictionary<HttpStatusCode, int>();
        var clock = Stopwatch.StartNew();

        await Parallel.ForEachAsync(Enumerable.Range(0, WebhookIngressRateLimitExtension.SourcePermitsPerWindow), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (i, ct) =>
        {
            var response = await client.PostAsync(RotatingTarget(i), new StringContent("{}"), ct);
            statuses.AddOrUpdate(response.StatusCode, 1, (_, n) => n + 1);
        });

        clock.Elapsed.ShouldBeLessThan(BudgetSpendDeadline, "the budget must be spent inside one window or the final request below proves nothing; a slow host here is the thing to look at");
        statuses.Keys.ShouldBe(new[] { HttpStatusCode.NotFound }, ignoreOrder: true, customMessage: $"every request inside the address's budget must reach its endpoint and be told the target does not exist; got {string.Join(", ", statuses.Select(s => $"{(int)s.Key}x{s.Value}"))}");

        var over = await client.PostAsync(RotatingTarget(WebhookIngressRateLimitExtension.SourcePermitsPerWindow), new StringContent("{}"));

        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, customMessage: "a sender that changes the target every time must still run out — check the GlobalLimiter in WebhookIngressRateLimitExtension; without it each new target is a new limiter and nothing ever answers 429");
    }

    [Fact]
    public async Task A_callback_token_no_minted_token_can_be_never_reaches_the_limiter_and_leaves_nothing_behind()
    {
        // The measured shape: a fresh 8000-character token per request kept ~34 KB alive per request for about 70 s.
        const int requests = 1000;
        var client = _factory.CreateClient();
        await client.PostAsync($"/api/workflows/callbacks/{new string('w', 8000)}", new StringContent("{}"));   // warm the fallback path

        var before = GC.GetTotalMemory(forceFullCollection: true);

        for (var i = 0; i < requests; i++)
        {
            var response = await client.PostAsync($"/api/workflows/callbacks/{RandomToken(8000)}", new StringContent("{}"));
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, customMessage: "an over-long token matches no callback route, so it is answered by the fallback and never by the limiter or the action");
        }

        var retained = GC.GetTotalMemory(forceFullCollection: true) - before;

        retained.ShouldBeLessThan(10L * 1024 * 1024, $"{requests} requests with distinct 8000-character tokens retained {retained / 1024} KB; before the route bounded the token they retained about 34 MB — check the {{token:length(32)}} constraint on WorkflowCallbacksController.Resume");
    }

    /// <summary>A target nobody has registered, spread across all three ingress routes so the budget is shown to be per address and not per route.</summary>
    private static string RotatingTarget(int i) => (i % 3) switch
    {
        0 => $"/api/webhooks/{Guid.NewGuid()}",
        1 => $"/api/webhooks/connection/{Guid.NewGuid()}",
        _ => $"/api/workflows/callbacks/{Guid.NewGuid():N}"
    };

    private static string RandomToken(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var chars = new char[length];
        for (var i = 0; i < length; i++) chars[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        return new string(chars);
    }
}
