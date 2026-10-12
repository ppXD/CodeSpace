using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: the claim board's reader projection. The standings are platform-wide; the receipt behind a row was paid
/// for by one team, whose id and model-credential ids reach only a reader in that team or a global admin. Everything
/// else on the row — the standing, the receipt, the harness, the tier — reads the same for everyone.
/// </summary>
[Trait("Category", "Unit")]
public class QualificationClaimBoardReaderTests
{
    private static readonly Guid PayingTeam = Guid.NewGuid();
    private static readonly Guid CredentialId = Guid.NewGuid();
    private static readonly Guid CredentialModelId = Guid.NewGuid();

    [Fact]
    public async Task A_reader_in_another_team_reads_the_standing_without_the_paying_teams_identifiers()
    {
        var row = await ReadAsAsync(team: Guid.NewGuid(), globalAdmin: false);

        row.Cohort!.TeamId.ShouldBeNull();
        row.Seal!.VerifierBundle!.ModelCredentialId.ShouldBeNull();
        row.Seal.VerifierBundle.ModelCredentialModelId.ShouldBeNull();

        row.Performance.ShouldBe(PerformanceQualification.Sealed);
        row.Cohort.Tier.ShouldBe(LaunchCohortDescriptor.InternalQualificationTier);
        row.Seal.VerifierBundle.Harness.ShouldBe("codex-cli");
        row.Seal.VerifierBundle.Model.ShouldBe("team-private-model");
    }

    [Theory]
    [InlineData(true, false)]   // the paying team's own member
    [InlineData(false, true)]   // a global admin, from any team
    public async Task A_reader_entitled_to_the_paying_team_reads_the_row_whole(bool inPayingTeam, bool globalAdmin)
    {
        var row = await ReadAsAsync(team: inPayingTeam ? PayingTeam : Guid.NewGuid(), globalAdmin);

        row.Cohort!.TeamId.ShouldBe(PayingTeam);
        row.Seal!.VerifierBundle!.ModelCredentialId.ShouldBe(CredentialId);
        row.Seal.VerifierBundle.ModelCredentialModelId.ShouldBe(CredentialModelId);
    }

    [Fact]
    public void A_row_with_no_typed_cohort_still_withholds_its_credential_ids()
    {
        // No cohort names no paying team, so nobody but a global admin can be shown to be entitled.
        var claim = Claim() with { Cohort = null };

        var projected = QualificationClaimBoardReader.WithoutPayingTeam(claim);

        projected.Seal!.VerifierBundle!.ModelCredentialId.ShouldBeNull();
        projected.Cohort.ShouldBeNull();
    }

    [Fact]
    public void An_unmeasured_row_projects_to_itself()
    {
        var unmeasured = new PerformanceClaim { Mode = "supervisor", CapabilityKey = "git-branch", Performance = PerformanceQualification.Unmeasured };

        QualificationClaimBoardReader.WithoutPayingTeam(unmeasured).ShouldBe(unmeasured);
    }

    private static async Task<PerformanceClaim> ReadAsAsync(Guid team, bool globalAdmin)
    {
        var reader = new QualificationClaimBoardReader(new OneRowResolver(Claim()), new StubUser(globalAdmin), new StubTeam(team));

        var board = await reader.ReadAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        return board.Rows.ShouldHaveSingleItem();
    }

    private static PerformanceClaim Claim() => new()
    {
        Mode = "supervisor",
        CapabilityKey = "git-branch",
        Performance = PerformanceQualification.Sealed,
        ReceiptId = Guid.NewGuid(),
        SuiteDigest = "sha256:suite",
        Cohort = new ClaimCohort { TeamId = PayingTeam, Mode = "supervisor", Tier = LaunchCohortDescriptor.InternalQualificationTier, CompletionPolicyVersion = 2 },
        Seal = new ContractSeal { CapabilityKey = "git-branch", SuiteDigest = "sha256:suite", VerifierBundle = new VerifierBundle { Harness = "codex-cli", Model = "team-private-model", ModelCredentialId = CredentialId, ModelCredentialModelId = CredentialModelId } },
    };

    private sealed class OneRowResolver : IQualificationClaimResolver
    {
        private readonly PerformanceClaim _row;

        public OneRowResolver(PerformanceClaim row) { _row = row; }

        public Task<PerformanceClaim> ResolveAsync(string mode, string capabilityKey, DateTimeOffset asOf, CancellationToken cancellationToken) => Task.FromResult(_row);

        public Task<QualificationClaimBoard> ResolveBoardAsync(DateTimeOffset asOf, CancellationToken cancellationToken) => Task.FromResult(new QualificationClaimBoard { AsOf = asOf, Rows = new[] { _row } });
    }

    private sealed class StubUser : ICurrentUser
    {
        private readonly bool _globalAdmin;

        public StubUser(bool globalAdmin) { _globalAdmin = globalAdmin; }

        public Guid? Id { get; } = Guid.NewGuid();
        public string Name => "reader";
        public IReadOnlyList<string> Roles => _globalAdmin ? new[] { CodeSpace.Messages.Constants.Roles.Admin } : Array.Empty<string>();
        public IReadOnlyList<string> Permissions => Array.Empty<string>();
        public bool PasswordMustChange => false;

        public bool HasRole(string role) => Roles.Contains(role);
        public bool HasPermission(string permission) => false;
    }

    private sealed class StubTeam : ICurrentTeam
    {
        public StubTeam(Guid id) { Id = id; }

        public Guid? Id { get; }
        public bool IsSet => true;
    }
}
