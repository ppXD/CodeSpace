using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers.Diagnostics;
using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.IntegrationTests.Binding;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Webhooks;

/// <summary>
/// Proves the answer to "what actually happened" survives to the database.
///
/// <para>Before the attempt timeline existed, a dead-lettered webhook left one line of
/// <c>last_error</c> and an attempt count. That cannot distinguish the two failures an operator
/// most needs to tell apart: a token that may never create hooks, and an instance that was
/// unreachable and then answered with a refusal. The last test here stages exactly that shape —
/// nine timeouts, then a 403 — and reads the difference back out.</para>
///
/// <para>Secrets are the other half: the webhook secret staged here is decrypted by the registrar
/// and handed to the provider for real, so asserting it is absent from the persisted request is an
/// end-to-end claim, not a claim about a test fixture.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RepositoryWebhookAttemptDiagnosticsTests
{
    private const string StagedWebhookSecret = "whsec-integration-4f2a9c";

    private readonly PostgresFixture _fixture;

    public RepositoryWebhookAttemptDiagnosticsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        using var scope = _fixture.BeginScope();
        scope.Resolve<TestRemoteHookStore>().Reset();
    }

    [Fact]
    public async Task A_failed_registration_records_what_the_provider_said_and_what_we_sent()
    {
        var webhookId = await StageEnqueuedWebhookAsync().ConfigureAwait(false);
        SetRefusal(new TestHookRefusal { StatusCode = 403, ResponseBody = """{"message":"403 Forbidden - Insufficient permissions"}""", Message = "Git returned HTTP 403 for RegisterWebhookAsync" });

        await RunRegistrarAsync(webhookId).ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var attempt = await db.RepositoryWebhookAttempt.AsNoTracking().SingleAsync(a => a.RepositoryWebhookId == webhookId).ConfigureAwait(false);

        attempt.AttemptNumber.ShouldBe(1);
        attempt.StatusCode.ShouldBe(403,
            customMessage: "The status the provider answered with is the first thing an operator needs, and last_error only carries prose.");
        attempt.ResponseBody.ShouldContain("Insufficient permissions",
            customMessage: "The provider's own words are the diagnosis; without them a 403 could be a scope gap, a role gap, or a protected-branch rule.");
        attempt.RequestMethod.ShouldBe("POST");
        attempt.RequestUrl.ShouldContain("/hooks");
        attempt.RequestBody.ShouldContain("push_events",
            customMessage: "The request we sent is half the evidence — 'we asked for the wrong events' is invisible without it.");
        attempt.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_recorded_request_carries_neither_the_webhook_secret_nor_the_credential()
    {
        var webhookId = await StageEnqueuedWebhookAsync().ConfigureAwait(false);
        SetRefusal(new TestHookRefusal { StatusCode = 403, ResponseBody = "denied", Message = "denied", CredentialToken = "pat-integration-should-never-persist" });

        await RunRegistrarAsync(webhookId).ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var attempt = await db.RepositoryWebhookAttempt.AsNoTracking().SingleAsync(a => a.RepositoryWebhookId == webhookId).ConfigureAwait(false);

        // The secret here is the one the registrar decrypted out of the row and handed to the
        // provider — the real one, not a stand-in.
        var persisted = string.Join("\n", attempt.RequestUrl, attempt.RequestBody, attempt.RequestHeadersJson, attempt.ResponseBody, attempt.Error);

        persisted.ShouldNotContain(StagedWebhookSecret,
            customMessage: "The webhook secret was written to the attempt row. Anyone with a database dump can now forge signed deliveries for this repository.");
        persisted.ShouldNotContain("pat-integration-should-never-persist",
            customMessage: "The provider credential was written to the attempt row. Anyone with a database dump now holds a working token.");
        attempt.RequestHeadersJson.ShouldContain(ProviderCallCapture.Mask,
            customMessage: "The auth header should be present but masked — the operator needs to see WHICH scheme was used, never the token.");
    }

    [Fact]
    public async Task Nine_timeouts_then_a_403_reads_differently_from_ten_403s()
    {
        var webhookId = await StageEnqueuedWebhookAsync().ConfigureAwait(false);

        // The whole reason this table exists. Both shapes dead-letter with attempts = 10 and a
        // last_error naming a 403; only the timeline says the instance was also unreachable.
        for (var attempt = 1; attempt < RepositoryWebhookRegistrar.MaxAttempts; attempt++)
        {
            SetRefusal(new TestHookRefusal { Message = "The operation was canceled." });
            await ReEnqueueAsync(webhookId).ConfigureAwait(false);
            await RunRegistrarAsync(webhookId).ConfigureAwait(false);
        }

        SetRefusal(new TestHookRefusal { StatusCode = 403, ResponseBody = """{"message":"403 Forbidden"}""", Message = "Git returned HTTP 403 for RegisterWebhookAsync" });
        await ReEnqueueAsync(webhookId).ConfigureAwait(false);
        await RunRegistrarAsync(webhookId).ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var hook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.Id == webhookId).ConfigureAwait(false);
        hook.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.DeadLettered);
        hook.Attempts.ShouldBe(RepositoryWebhookRegistrar.MaxAttempts);
        hook.LastError.ShouldContain("403",
            customMessage: "last_error must keep working exactly as it did — something already reads it.");

        var attempts = await db.RepositoryWebhookAttempt.AsNoTracking()
            .Where(a => a.RepositoryWebhookId == webhookId)
            .OrderBy(a => a.AttemptNumber)
            .ToListAsync().ConfigureAwait(false);

        attempts.Count.ShouldBe(RepositoryWebhookRegistrar.MaxAttempts,
            customMessage: "Every attempt has to survive, not just the last — a timeline with holes cannot be read.");
        attempts.Count(a => a.StatusCode == null).ShouldBe(RepositoryWebhookRegistrar.MaxAttempts - 1,
            customMessage: "A call that never reached HTTP must record no status; that absence is what names it a timeout rather than a refusal.");
        attempts.Last().StatusCode.ShouldBe(403);
        attempts.Select(a => a.AttemptNumber).ShouldBe(Enumerable.Range(1, RepositoryWebhookRegistrar.MaxAttempts).ToList(),
            customMessage: "Attempt numbers must line up with repository_webhook.attempts, or the timeline cannot be read against the state machine.");
    }

    private void SetRefusal(TestHookRefusal refusal)
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<TestRemoteHookStore>().Refusal = refusal;
    }

    private async Task RunRegistrarAsync(Guid webhookId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IRepositoryWebhookRegistrar>().RunAsync(webhookId, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Put the row back where the reconciler would put it, so the next attempt runs the real worker path.</summary>
    private async Task ReEnqueueAsync(Guid webhookId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().RepositoryWebhook
            .Where(w => w.Id == webhookId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Enqueued))
            .ConfigureAwait(false);
    }

    /// <summary>Seed a repository + an Enqueued webhook directly, so the registrar's CAS runs against a state we chose.</summary>
    private async Task<Guid> StageEnqueuedWebhookAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = "Owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"team-{suffix}", Name = "Team" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.Git, DisplayName = "Test", BaseUrl = "https://test.local" };
        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            AuthType = AuthType.Pat,
            DisplayName = "PAT",
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "pat-xxx" }))
        };

        var repositoryId = Guid.NewGuid();
        var webhookId = Guid.NewGuid();

        db.User.Add(owner);
        db.Team.Add(team);
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = owner.Id, Role = TeamRole.Owner });
        db.Project.Add(TestProjectSeed.BuildDefaultProject(team.Id, owner.Id));
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(new Repository
        {
            Id = repositoryId,
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            CredentialId = credential.Id,
            ExternalId = $"id-diag-{suffix}",
            NamespacePath = "acme",
            Name = $"diag-{suffix}",
            FullPath = $"acme/diag-{suffix}",
            DefaultBranch = "main",
            Visibility = RepositoryVisibility.Private,
            WebUrl = "https://test.local",
            Status = RepositoryStatus.Active
        });
        db.RepositoryWebhook.Add(new RepositoryWebhook
        {
            Id = webhookId,
            RepositoryId = repositoryId,
            ExternalId = null,
            CallbackUrl = $"https://test.local/api/webhooks/{webhookId}",
            SecretEnc = encryptor.Encrypt(StagedWebhookSecret),
            SubscribedEvents = new List<string> { "push" },
            Active = true,
            RegistrationStatus = RepositoryWebhookRegistrationStatus.Enqueued,
            EnqueuedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return webhookId;
    }
}
