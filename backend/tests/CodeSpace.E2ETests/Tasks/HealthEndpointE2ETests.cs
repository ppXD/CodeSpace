using System.Net;
using CodeSpace.E2ETests.Infrastructure;
using Shouldly;

namespace CodeSpace.E2ETests.Tasks;

/// <summary>
/// E2E coverage for the k8s probe surface through the REAL ASP.NET pipeline. The global FallbackPolicy requires an
/// authenticated user, so the load-bearing assertion is that BOTH health endpoints answer 200 WITHOUT any auth
/// header — a kubelet probe is unauthenticated, so a probe that 401'd would mark every pod perpetually unready.
/// /health/ready additionally runs the DbContext check (the test DB is up), proving readiness reflects real state.
///
/// <para>Tier: 🟢 High-fidelity — real app host + real Postgres + the real middleware pipeline (auth, fallback
/// policy, endpoint routing).</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
[Collection(FakeCliHttpE2ECollection.Name)]
public sealed class HealthEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public HealthEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Liveness_answers_200_anonymously()
    {
        // No Authorization header — a kubelet liveness probe is unauthenticated. AllowAnonymous must bypass the
        // global FallbackPolicy, and liveness carries no checks so it's 200 whenever the process is up.
        var response = await _factory.CreateClient().GetAsync("/health/live");

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            customMessage: "/health/live must be reachable WITHOUT auth (the FallbackPolicy must not apply) and 200 when the process is up");
    }

    [Fact]
    public async Task Readiness_answers_200_anonymously_when_the_database_is_reachable()
    {
        var response = await _factory.CreateClient().GetAsync("/health/ready");

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            customMessage: "/health/ready must be anonymous and 200 when the DbContext check passes (the test Postgres is up)");
    }
}
