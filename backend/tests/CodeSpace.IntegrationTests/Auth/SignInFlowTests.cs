using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Auth;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Auth;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Auth;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SignInFlowTests
{
    private readonly PostgresFixture _fixture;

    public SignInFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Seed_admin_can_sign_in_with_known_password()
    {
        // 0006 migration seeds admin@codespace.local / changeme123. This test verifies the
        // seed survives DbUp and the password hash matches the live Pbkdf2PasswordHasher.
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var result = await mediator.Send(new SignInCommand { Name = "admin@codespace.local", Password = "changeme123" }).ConfigureAwait(false);

        result.Token.ShouldNotBeNullOrEmpty();
        result.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        result.User.Email.ShouldBe("admin@codespace.local");
        result.User.Name.ShouldBe("admin");
        result.User.Teams.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Seed_admin_can_sign_in_by_display_name_too()
    {
        // The handler accepts either email or display name. The seed admin's name is
        // "admin" — typing "admin" should resolve to the same user as the full email.
        // A name signs in only while exactly one live account carries it, so no fixture in
        // this shared database may name another account "admin".
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var result = await mediator.Send(new SignInCommand { Name = "admin", Password = "changeme123" }).ConfigureAwait(false);

        result.User.Email.ShouldBe("admin@codespace.local");
        result.User.Name.ShouldBe("admin");
    }

    /// <summary>
    /// Sign-in answers "what may I do" for the account it just authenticated, and the client caches
    /// that answer. The request itself is anonymous — nobody is signed in while it runs — so reading
    /// the grants off the ambient principal returned nothing and every instance capability stayed
    /// invisible until some later request happened to refresh it. Read them for the account instead.
    /// </summary>
    [Fact]
    public async Task Sign_in_carries_the_signed_in_account_own_instance_permissions()
    {
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var result = await mediator.Send(new SignInCommand { Name = "admin@codespace.local", Password = "changeme123" }).ConfigureAwait(false);

        result.User.Permissions.ShouldContain(Permissions.TeamsCreate, "the seed admin holds the Admin role, which grants it");
    }

    [Fact]
    public async Task Sign_in_reports_no_instance_permissions_for_an_account_holding_none()
    {
        var (email, password) = await SeedUnprivilegedUserAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var result = await mediator.Send(new SignInCommand { Name = email, Password = password }).ConfigureAwait(false);

        result.User.Permissions.ShouldBeEmpty("grants are per-account — an ungranted user must not inherit anyone else's");
    }

    [Fact]
    public async Task Wrong_password_throws_InvalidCredentials()
    {
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var act = async () => await mediator.Send(new SignInCommand { Name = "admin@codespace.local", Password = "wrong-password" }).ConfigureAwait(false);

        await act.ShouldThrowAsync<InvalidCredentialsException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task Unknown_email_throws_InvalidCredentials()
    {
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var act = async () => await mediator.Send(new SignInCommand { Name = $"ghost-{Guid.NewGuid():N}@nowhere", Password = "anything" }).ConfigureAwait(false);

        await act.ShouldThrowAsync<InvalidCredentialsException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task User_with_no_password_hash_cannot_sign_in()
    {
        // The seeded SYSTEM user (id 00...01, email system@codespace.local) has no password_hash.
        // Even with the correct row present, sign-in must reject.
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var act = async () => await mediator.Send(new SignInCommand { Name = "system@codespace.local", Password = "anything" }).ConfigureAwait(false);

        await act.ShouldThrowAsync<InvalidCredentialsException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task Email_lookup_is_case_insensitive()
    {
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var result = await mediator.Send(new SignInCommand { Name = "ADMIN@CODESPACE.LOCAL", Password = "changeme123" }).ConfigureAwait(false);

        result.User.Email.ShouldBe("admin@codespace.local");
    }

    [Fact]
    public async Task SignIn_stamps_last_login_date()
    {
        // Snapshot the seed admin's LastLoginDate, sign in, then re-read. The column should
        // have advanced — proves the handler actually persisted the timestamp.
        Guid adminId;
        DateTimeOffset? before;

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var admin = await db.User.AsNoTracking().FirstAsync(u => u.Email == "admin@codespace.local").ConfigureAwait(false);
            adminId = admin.Id;
            before = admin.LastLoginDate;
        }

        using (var signInScope = _fixture.BeginScope())
        {
            await signInScope.Resolve<IMediator>().Send(new SignInCommand { Name = "admin@codespace.local", Password = "changeme123" }).ConfigureAwait(false);
        }

        using (var verifyScope = _fixture.BeginScope())
        {
            var db = verifyScope.Resolve<CodeSpaceDbContext>();
            var admin = await db.User.AsNoTracking().FirstAsync(u => u.Id == adminId).ConfigureAwait(false);
            admin.LastLoginDate.ShouldNotBeNull();
            if (before != null) admin.LastLoginDate.Value.ShouldBeGreaterThan(before.Value);
        }
    }

    [Fact]
    public async Task An_address_resolves_to_its_own_account_before_any_display_name()
    {
        // The lockout: an account NAMED with the victim's address, whose id sorted first, used to be what the address
        // resolved to — the victim's password then failed and the shadow's signed in under the victim's address.
        var victim = await SeedAccountAsync(email: $"victim-{Guid.NewGuid():N}@corp.test", name: null, "victim-password-1").ConfigureAwait(false);
        await SeedAccountAsync(email: $"shadow-{Guid.NewGuid():N}@evil.test", name: victim.Email, "shadow-password-1", id: Guid.Parse("00000000-0000-0000-0000-0000000000b1")).ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var signedIn = await mediator.Send(new SignInCommand { Name = $"  {victim.Email.ToUpperInvariant()} ", Password = "victim-password-1" }).ConfigureAwait(false);
        signedIn.User.Id.ShouldBe(victim.Id);

        var asShadow = async () => await mediator.Send(new SignInCommand { Name = victim.Email, Password = "shadow-password-1" }).ConfigureAwait(false);
        await asShadow.ShouldThrowAsync<InvalidCredentialsException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task A_display_name_one_live_account_carries_signs_it_in()
    {
        var name = $"Solo {Guid.NewGuid():N}";
        var account = await SeedAccountAsync(email: $"solo-{Guid.NewGuid():N}@corp.test", name, "solo-password-1").ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var result = await scope.Resolve<IMediator>().Send(new SignInCommand { Name = $" {name.ToUpperInvariant()}", Password = "solo-password-1" }).ConfigureAwait(false);

        result.User.Id.ShouldBe(account.Id);
    }

    [Fact]
    public async Task A_display_name_two_live_accounts_carry_signs_in_neither()
    {
        // Ambiguity resolves to nobody rather than to whichever id sorts first — so a duplicate can take a name away
        // from its holder, but can never hand the holder's sign-in to someone else.
        var name = $"Shared {Guid.NewGuid():N}";
        var first = await SeedAccountAsync(email: $"first-{Guid.NewGuid():N}@corp.test", name, "first-password-1").ConfigureAwait(false);
        await SeedAccountAsync(email: $"second-{Guid.NewGuid():N}@corp.test", name, "second-password-1").ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var byFirst = async () => await mediator.Send(new SignInCommand { Name = name, Password = "first-password-1" }).ConfigureAwait(false);
        var bySecond = async () => await mediator.Send(new SignInCommand { Name = name, Password = "second-password-1" }).ConfigureAwait(false);

        await byFirst.ShouldThrowAsync<InvalidCredentialsException>().ConfigureAwait(false);
        await bySecond.ShouldThrowAsync<InvalidCredentialsException>().ConfigureAwait(false);
        (await mediator.Send(new SignInCommand { Name = first.Email, Password = "first-password-1" }).ConfigureAwait(false)).User.Id.ShouldBe(first.Id);
    }

    [Fact]
    public async Task Two_live_accounts_cannot_hold_one_address_in_different_case()
    {
        var email = $"Case-{Guid.NewGuid():N}@Corp.test";
        await SeedAccountAsync(email, name: null, "case-password-1").ConfigureAwait(false);

        var act = async () => await SeedAccountAsync(email.ToLowerInvariant(), name: null, "case-password-2").ConfigureAwait(false);

        var thrown = await act.ShouldThrowAsync<DbUpdateException>().ConfigureAwait(false);
        var postgres = thrown.InnerException.ShouldBeOfType<PostgresException>();
        postgres.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        postgres.ConstraintName.ShouldBe("ux_app_user_email_lower");
    }

    [Fact]
    public async Task A_soft_deleted_account_frees_its_address()
    {
        var email = $"freed-{Guid.NewGuid():N}@corp.test";
        var departed = await SeedAccountAsync(email, name: null, "freed-password-1").ConfigureAwait(false);

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            (await db.User.SingleAsync(u => u.Id == departed.Id).ConfigureAwait(false)).DeletedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var successor = await SeedAccountAsync(email.ToUpperInvariant(), name: null, "freed-password-2").ConfigureAwait(false);

        successor.Id.ShouldNotBe(departed.Id);
    }

    [Fact]
    public async Task The_unique_address_migration_runs_over_live_duplicates_warns_and_creates_no_index()
    {
        // A migration that refuses is a deployment that does not start. Which of two accounts keeps an address is whose
        // memberships and history survive — not a choice a migration may make silently — so it changes nothing, says
        // so, and the deployment comes up. Replayed inside a transaction that is rolled back.
        var address = $"dup-{Guid.NewGuid():N}@corp.test";

        await using var replay = await MigrationReplay.OpenAsync(_fixture.ConnectionString).ConfigureAwait(false);

        await replay.ExecuteAsync("DROP INDEX ux_app_user_email_lower").ConfigureAwait(false);
        await InsertRawAccountAsync(replay, address.ToUpperInvariant()).ConfigureAwait(false);
        await InsertRawAccountAsync(replay, address).ConfigureAwait(false);

        await replay.RunScriptAsync("0245_app_user_email_unique_ignoring_case.sql").ConfigureAwait(false);

        var warning = replay.Notices.ShouldHaveSingleItem("the operator must be told why the index is missing");
        warning.Severity.ShouldBe("WARNING");
        warning.MessageText.ShouldContain("0245: 1 address(es) are held by more than one live account");
        warning.Hint.ShouldNotBeNull().ShouldContain("HAVING count(*) > 1");
        warning.Hint.ShouldContain("CREATE UNIQUE INDEX IF NOT EXISTS ux_app_user_email_lower", Case.Sensitive, "the hint carries the statement to run once the duplicates are resolved");
        (await replay.IndexExistsAsync("ux_app_user_email_lower").ConfigureAwait(false)).ShouldBeFalse("over duplicates no index may be created");
        (await replay.CountAccountsAtAsync(address).ConfigureAwait(false)).ShouldBe(2, "no account is merged, renamed or deleted");
    }

    [Fact]
    public async Task The_unique_address_migration_creates_the_index_when_no_live_duplicates_exist()
    {
        var address = $"solo-{Guid.NewGuid():N}@corp.test";

        await using var replay = await MigrationReplay.OpenAsync(_fixture.ConnectionString).ConfigureAwait(false);

        await replay.ExecuteAsync("DROP INDEX ux_app_user_email_lower").ConfigureAwait(false);
        await InsertRawAccountAsync(replay, address).ConfigureAwait(false);

        await replay.RunScriptAsync("0245_app_user_email_unique_ignoring_case.sql").ConfigureAwait(false);

        replay.Notices.ShouldBeEmpty();
        (await replay.IndexExistsAsync("ux_app_user_email_lower").ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public void Issued_token_validates_against_configured_JWT_key()
    {
        // Round-trip the JWT through Microsoft.IdentityModel — same validation parameters as
        // AuthenticationExtension wires up for incoming requests.
        using var scope = _fixture.BeginScope();
        var issuer = scope.Resolve<IJwtTokenIssuer>();

        var user = new User { Id = Guid.NewGuid(), Email = "x@y", Name = "x" };
        var issued = issuer.Issue(user);

        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var key = scope.Resolve<CodeSpace.Core.Settings.Authentication.JwtSymmetricKeySetting>().Value;

        handler.ValidateToken(issued.Token, new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key))
        }, out var validatedToken);

        validatedToken.ShouldNotBeNull();
    }

    private sealed record SeededAccount(Guid Id, string Email);

    private async Task<SeededAccount> SeedAccountAsync(string email, string? name, string password, Guid? id = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var user = new User { Id = id ?? Guid.NewGuid(), Email = email, Name = name ?? $"account-{Guid.NewGuid():N}", PasswordHash = scope.Resolve<IPasswordHasher>().Hash(password) };

        db.User.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new SeededAccount(user.Id, user.Email);
    }

    private static async Task InsertRawAccountAsync(MigrationReplay replay, string email) =>
        await replay.ExecuteAsync($"INSERT INTO app_user (id, email, name, created_date, created_by, last_modified_date, last_modified_by) VALUES (gen_random_uuid(), '{email}', 'duplicate', now(), '{SystemUsers.SeederId}', now(), '{SystemUsers.SeederId}')").ConfigureAwait(false);

    /// <summary>
    /// A migration script replayed on one connection inside a transaction that is always rolled back, with every
    /// server notice it raised kept for the test to read.
    /// </summary>
    private sealed class MigrationReplay : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;

        private MigrationReplay(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
            _connection.Notice += (_, e) => Notices.Add(e.Notice);
        }

        public List<PostgresNotice> Notices { get; } = new();

        public static async Task<MigrationReplay> OpenAsync(string connectionString)
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            return new MigrationReplay(connection, await connection.BeginTransactionAsync().ConfigureAwait(false));
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, _connection, _transaction);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        public async Task RunScriptAsync(string fileName) =>
            await ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", fileName)).ConfigureAwait(false)).ConfigureAwait(false);

        public async Task<bool> IndexExistsAsync(string name) => await ScalarAsync<long>($"SELECT count(*) FROM pg_indexes WHERE indexname = '{name}'").ConfigureAwait(false) == 1;

        public async Task<long> CountAccountsAtAsync(string address) => await ScalarAsync<long>($"SELECT count(*) FROM app_user WHERE lower(email) = lower('{address}') AND deleted_date IS NULL").ConfigureAwait(false);

        public async ValueTask DisposeAsync()
        {
            await _transaction.RollbackAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        private async Task<T> ScalarAsync<T>(string sql)
        {
            await using var command = new NpgsqlCommand(sql, _connection, _transaction);

            return (T)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
        }
    }

    private async Task<(string Email, string Password)> SeedUnprivilegedUserAsync()
    {
        const string password = "plain-user-pw-123";

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var email = $"plain-{Guid.NewGuid():N}@x";
        db.User.Add(new User { Id = Guid.NewGuid(), Email = email, Name = email, PasswordHash = scope.Resolve<IPasswordHasher>().Hash(password) });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return (email, password);
    }
}
