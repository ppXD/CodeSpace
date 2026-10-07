using CodeSpace.Core.Authorization;
using CodeSpace.Core.Failures;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using Autofac;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// HIGH fidelity (Rule 12): a pack imported from a pasted URL that embeds a token, through the REAL mediator, the real
/// <see cref="PackCloneFetcher"/> and real <c>git</c>, into real Postgres, against a loopback remote that refuses every
/// request without that token (<see cref="PrivatePackSource"/>). The pack must store its URL without the token and keep
/// the token only sealed; no read a team member can make — a Viewer's included — may return it; Sync and the add-after-
/// sync import must still authenticate, and when either fails after decrypting the sealed source, neither the error body
/// nor the mediator's log may name the token; and a URL that carries no credential must be stored exactly as pasted.
///
/// <para>Positive controls: the remote refuses a clone without the token (so every later success proves the sealed
/// token cloned); the same pack with its sealed source removed cannot sync; a row put back in the legacy shape holds the
/// token (so the raw-row scans that find none can see one). The one seam is the allowlist accepting the loopback http
/// remote. Each test owns its remote, scratch HOME and team, and removes the remote on every path.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PackSealedSourceFlowTests
{
    private readonly PostgresFixture _fixture;

    public PackSealedSourceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task The_private_remote_refuses_a_clone_without_the_token()
    {
        if (!await PrivatePackSource.GitAvailableAsync()) return;

        await using var source = await PrivatePackSource.StartAsync();

        await Should.ThrowAsync<PackImportException>(() => source.Fetcher.FetchAsync(source.CleanUrl, null, CancellationToken.None), "fixture check: a clone that presents no token is refused");

        using var checkout = await source.Fetcher.FetchAsync(source.TokenedUrl, null, CancellationToken.None);
        File.Exists(Path.Combine(checkout.Directory, PrivatePackSource.Agent)).ShouldBeTrue("fixture check: the token clones the pack");
    }

    [Fact]
    public async Task A_tokened_import_stores_the_url_without_the_token_and_keeps_it_only_sealed()
    {
        if (!await PrivatePackSource.GitAvailableAsync()) return;

        await using var source = await PrivatePackSource.StartAsync();
        var harness = new PackCredentialHarness(_fixture, source);
        var team = await harness.SeedTeamAsync();

        var result = await harness.SendAsync(team.OwnerId, team.TeamId, new ImportPackFromUrlCommand { Url = source.TokenedUrl, SourcePaths = new[] { PrivatePackSource.Agent, PrivatePackSource.Skill } });

        result.Items.ShouldAllBe(i => i.Outcome == PackImportOutcome.Imported);
        PrivatePackSource.ShouldHoldNoToken(PackCredentialHarness.Serialize(result), "the import result");

        PrivatePackSource.ShouldHoldNoToken(await harness.RawRowAsync(result.PackId), "the stored pack row");

        var columns = await harness.ColumnsAsync(result.PackId);
        columns.Url.ShouldBe(source.CleanUrl, "the pack is identified and shown by the URL without its userinfo");
        columns.EncryptedCloneUrl.ShouldNotBeNull("the token the import cloned with is kept, sealed");

        (await harness.CloneUrlOfAsync(result.PackId)).ShouldBe(source.TokenedUrl, "the sealed source decrypts to exactly the URL the import cloned");

        using var scope = _fixture.BeginScope();
        var pack = await scope.Resolve<CodeSpaceDbContext>().Pack.AsNoTracking().SingleAsync(p => p.Id == result.PackId);
        pack.Name.ShouldBe("remote", "the name is derived from the path, which the token never touched");
        pack.Kind.ShouldBe(PackKind.GitUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // a legacy row the backfill has not sealed yet: the read side is closed before the backfill runs
    public async Task A_viewer_reads_the_pack_without_its_token_and_cannot_spend_it(bool legacyUnsealed)
    {
        if (!await PrivatePackSource.GitAvailableAsync()) return;

        await using var source = await PrivatePackSource.StartAsync();
        var harness = new PackCredentialHarness(_fixture, source);
        var team = await harness.SeedTeamAsync();
        var packId = await harness.ImportAsync(team, source.TokenedUrl, PrivatePackSource.Agent);

        if (legacyUnsealed)
        {
            await harness.MakeLegacyAsync(packId, source.TokenedUrl);
            (await harness.RawRowAsync(packId)).ShouldContain(PrivatePackSource.Token, Case.Sensitive, "positive control: a legacy row holds the token at rest");
        }

        var list = await harness.SendAsync(team.ViewerId, team.TeamId, new ListPacksQuery());
        var detail = await harness.SendAsync(team.ViewerId, team.TeamId, new GetPackQuery { PackId = packId });

        list.Single(p => p.Id == packId).Url.ShouldBe(source.CleanUrl);
        detail.ShouldNotBeNull().Pack.Url.ShouldBe(source.CleanUrl);
        PrivatePackSource.ShouldHoldNoToken(PackCredentialHarness.Serialize(list), "a Viewer's pack list");
        PrivatePackSource.ShouldHoldNoToken(PackCredentialHarness.Serialize(detail), "a Viewer's pack detail");

        await Should.ThrowAsync<TenantAccessDeniedException>(() => harness.SendAsync(team.ViewerId, team.TeamId, new SyncPackCommand { PackId = packId }), "a Viewer cannot make the server clone with the pack's token");
        await Should.ThrowAsync<TenantAccessDeniedException>(() => harness.SendAsync(team.ViewerId, team.TeamId, new ImportPackArtifactsCommand { PackId = packId, SourcePaths = new[] { PrivatePackSource.Skill } }), "nor import with it");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // positive control: the same pack with its sealed source removed cannot sync
    public async Task Sync_clones_with_the_sealed_token(bool sealedSourceRemoved)
    {
        if (!await PrivatePackSource.GitAvailableAsync()) return;

        await using var source = await PrivatePackSource.StartAsync();
        var harness = new PackCredentialHarness(_fixture, source);
        var team = await harness.SeedTeamAsync();
        var packId = await harness.ImportAsync(team, source.TokenedUrl, PrivatePackSource.Agent, PrivatePackSource.Skill);
        var sealedBefore = (await harness.ColumnsAsync(packId)).EncryptedCloneUrl;

        await source.PublishUpstreamChangeAsync();

        if (sealedSourceRemoved)
        {
            await harness.ClearSealedSourceAsync(packId);

            var failure = await Should.ThrowAsync<PackImportException>(() => harness.SendAsync(team.OwnerId, team.TeamId, new SyncPackCommand { PackId = packId }));
            PrivatePackSource.ShouldHoldNoToken(failure.Message, "the failed sync's message");
            return;
        }

        var sync = await harness.SendAsync(team.OwnerId, team.TeamId, new SyncPackCommand { PackId = packId });

        sync.Updated.ShouldBe(1, "the agent changed upstream, so the authenticated clone refreshed it");
        sync.UpToDate.ShouldBe(1);
        sync.NewArtifacts.Skills.ShouldContain(s => s.SourcePath == PrivatePackSource.NewSkill);
        PrivatePackSource.ShouldHoldNoToken(PackCredentialHarness.Serialize(sync), "the sync result");

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<CodeSpaceDbContext>().AgentDefinition.AsNoTracking().SingleAsync(a => a.PackId == packId && a.DeletedDate == null)).SystemPrompt.ShouldContain("v2 now");

        PrivatePackSource.ShouldHoldNoToken(await harness.RawRowAsync(packId), "the pack row after a sync");
        (await harness.ColumnsAsync(packId)).EncryptedCloneUrl.ShouldBe(sealedBefore, "a sync spends the sealed source; it never re-seals it");
    }

    [Theory]
    [InlineData(false)]   // Sync
    [InlineData(true)]    // the add-after-sync import from the pack
    public async Task A_failed_clone_from_the_sealed_source_names_no_token_to_the_caller_or_in_the_log(bool importFromPack)
    {
        if (!await PrivatePackSource.GitAvailableAsync()) return;

        await using var source = await PrivatePackSource.StartAsync();
        var harness = new PackCredentialHarness(_fixture, source);
        var team = await harness.SeedTeamAsync();
        var packId = await harness.ImportAsync(team, source.TokenedUrl, PrivatePackSource.Agent);

        // The token stays valid; the saved ref is gone upstream, so the clone authenticates with the decrypted URL and then fails.
        await harness.ExecuteAsync("UPDATE pack SET reference = 'deleted-branch' WHERE id = @id", ("id", packId));

        var log = new CapturedLog();
        var failure = importFromPack
            ? await Should.ThrowAsync<PackImportException>(() => harness.SendAsync(team.OwnerId, team.TeamId, new ImportPackArtifactsCommand { PackId = packId, SourcePaths = new[] { PrivatePackSource.Skill } }, log))
            : await Should.ThrowAsync<PackImportException>(() => harness.SendAsync(team.OwnerId, team.TeamId, new SyncPackCommand { PackId = packId }, log));

        failure.Message.ShouldContain("deleted-branch", Case.Sensitive, "fixture check: the clone failed on the missing ref, so the sealed URL was decrypted and handed to git");
        log.Text.ShouldContain(failure.Message, Case.Sensitive, "fixture check: the pipeline logged the failure, so the scan below reads a log that holds it");

        PrivatePackSource.ShouldHoldNoToken(FailureClassifier.Classify(failure).ClientMessage, "the error body the caller and the Library see");
        PrivatePackSource.ShouldHoldNoToken(log.Text, "the mediator's error log");
    }

    [Fact]
    public async Task Importing_from_the_pack_lands_a_new_artifact_in_that_same_pack()
    {
        if (!await PrivatePackSource.GitAvailableAsync()) return;

        await using var source = await PrivatePackSource.StartAsync();
        var harness = new PackCredentialHarness(_fixture, source);
        var team = await harness.SeedTeamAsync();
        var packId = await harness.ImportAsync(team, source.TokenedUrl, PrivatePackSource.Agent);
        var sealedBefore = (await harness.ColumnsAsync(packId)).EncryptedCloneUrl;

        await source.PublishUpstreamChangeAsync();

        var result = await harness.SendAsync(team.OwnerId, team.TeamId, new ImportPackArtifactsCommand { PackId = packId, SourcePaths = new[] { PrivatePackSource.NewSkill } });

        result.PackId.ShouldBe(packId);
        result.Items.ShouldHaveSingleItem().Outcome.ShouldBe(PackImportOutcome.Imported, "the clone authenticated with the pack's sealed token");

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.SkillDefinition.AsNoTracking().SingleAsync(s => s.Id == result.Items[0].DefinitionId)).PackId.ShouldBe(packId, "the artifact lands in the pack it was discovered in");
        (await db.Pack.AsNoTracking().CountAsync(p => p.TeamId == team.TeamId)).ShouldBe(1, "no second pack is resolved or created");

        (await harness.ColumnsAsync(packId)).EncryptedCloneUrl.ShouldBe(sealedBefore, "the pack's own source cloned, so it is recorded unchanged");
        PrivatePackSource.ShouldHoldNoToken(await harness.RawRowAsync(packId), "the pack row after an import from it");
    }

    [Fact]
    public async Task Importing_from_another_teams_pack_is_not_found()
    {
        if (!await PrivatePackSource.GitAvailableAsync()) return;

        await using var source = await PrivatePackSource.StartAsync();
        var harness = new PackCredentialHarness(_fixture, source);
        var owner = await harness.SeedTeamAsync();
        var other = await harness.SeedTeamAsync();
        var packId = await harness.ImportAsync(owner, source.TokenedUrl, PrivatePackSource.Agent);

        await Should.ThrowAsync<KeyNotFoundException>(() => harness.SendAsync(other.OwnerId, other.TeamId, new ImportPackArtifactsCommand { PackId = packId, SourcePaths = new[] { PrivatePackSource.Skill } }), "another team's pack id resolves nothing — its sealed token is never spent");
    }

    [Theory]
    [InlineData(true, "x-access-token:fake%2Dpublish-token-0123456789")]   // a rotated token (here: a new spelling the remote still accepts)
    [InlineData(false, null)]                                              // the token dropped from a repository that is readable without it
    public async Task Re_importing_with_a_rotated_or_dropped_token_keeps_the_same_pack(bool privateRemote, string? secondUserInfo)
    {
        if (!await PrivatePackSource.GitAvailableAsync()) return;

        await using var source = await PrivatePackSource.StartAsync(authenticateReads: privateRemote);
        var harness = new PackCredentialHarness(_fixture, source);
        var team = await harness.SeedTeamAsync();
        var first = await harness.ImportAsync(team, source.TokenedUrl, PrivatePackSource.Agent);

        var secondUrl = secondUserInfo is null ? source.CleanUrl : source.UrlWith(secondUserInfo);
        var second = await harness.ImportAsync(team, secondUrl, PrivatePackSource.Agent, PrivatePackSource.Skill);

        second.ShouldBe(first, "the credential-free URL is the pack's identity, so a re-paste resolves to the same pack instead of forking one");

        var columns = await harness.ColumnsAsync(first);
        columns.Url.ShouldBe(source.CleanUrl);

        if (secondUserInfo is null) columns.EncryptedCloneUrl.ShouldBeNull("a clean re-import that cloned clears the stored credential");
        else (await harness.CloneUrlOfAsync(first)).ShouldBe(secondUrl, "the sealed source is now the URL the last successful import cloned");

        var sync = await harness.SendAsync(team.OwnerId, team.TeamId, new SyncPackCommand { PackId = first });
        sync.UpToDate.ShouldBe(2, "the pack keeps syncing from the source it now records");
    }

    [Fact]
    public async Task A_url_without_a_credential_is_stored_exactly_as_pasted_and_nothing_is_sealed()
    {
        if (!await PrivatePackSource.GitAvailableAsync()) return;

        await using var source = await PrivatePackSource.StartAsync(authenticateReads: false);
        var harness = new PackCredentialHarness(_fixture, source);
        var team = await harness.SeedTeamAsync();

        var packId = await harness.ImportAsync(team, source.CleanUrl, PrivatePackSource.Agent);

        var columns = await harness.ColumnsAsync(packId);
        columns.Url.ShouldBe(source.CleanUrl, "a public URL is byte-identical to the paste");
        columns.EncryptedCloneUrl.ShouldBeNull();
        (await harness.SendAsync(team.ViewerId, team.TeamId, new GetPackQuery { PackId = packId })).ShouldNotBeNull().Pack.Url.ShouldBe(source.CleanUrl);
    }
}
