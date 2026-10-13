using System.IO;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Queries.Credentials;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace CodeSpace.UnitTests.Architecture;

/// <summary>
/// Requests that present one credential to its provider on the caller's behalf. The credential marker alone proves the
/// credential is the caller's team's and the caller a member, and no more — so on membership alone a Viewer could spend
/// another member's personal token to enumerate everything that person's account can see at the provider, private
/// repositories outside the team included. Listing what a credential reaches is now its owner's (a Personal credential)
/// or a repos.manage holder's, the bar of the bind the listing feeds.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CredentialSpendGateTests
{
    [Fact]
    public void Listing_what_a_credential_reaches_takes_the_spend_gate()
    {
        typeof(IRequireCredentialSpend).IsAssignableFrom(typeof(ListAccessibleRepositoriesQuery)).ShouldBeTrue("listing what a credential reaches presents it to the provider — gate it on whose it is, not on membership alone");
    }

    [Fact]
    public void The_spend_gate_runs_on_a_credential_already_proven_to_be_the_callers_teams()
    {
        typeof(IRequireCredentialAccess).IsAssignableFrom(typeof(IRequireCredentialSpend)).ShouldBeTrue("the spend tier decides only whether membership is enough; the team match must already hold");
    }

    /// <summary>
    /// DbUp owns the schema; the EF model only describes it. 0249 relaxed each payload check so a revoked credential's
    /// revision, and a deployment-default envelope no template names, can hold an empty payload, and the model's copy of
    /// each check must say the same thing.
    /// </summary>
    [Theory]
    [InlineData(typeof(StorageCredentialRevision), "ck_storage_credential_revision_encrypted_payload")]
    [InlineData(typeof(StorageDefaultCredential), "ck_storage_default_credential_encrypted_payload")]
    public void The_models_payload_check_matches_the_one_0249_installed(Type entity, string constraint)
    {
        using var db = new CodeSpaceDbContext(new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused").UseSnakeCaseNamingConvention().Options);
        var check = db.GetService<IDesignTimeModel>().Model.FindEntityType(entity)!.GetCheckConstraints().Single(c => c.Name == constraint);
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", "0249_shred_retired_secrets.sql"));

        migration.ShouldContain($"ADD CONSTRAINT {constraint} CHECK ({check.Sql})", Case.Sensitive);
    }
}
