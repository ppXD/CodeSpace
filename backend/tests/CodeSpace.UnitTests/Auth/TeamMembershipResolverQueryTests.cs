using System;
using System.Threading;
using System.Threading.Tasks;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.UnitTests.Auth;

/// <summary>
/// Proves the role lookup is a query the database can run, without needing a database to prove it.
///
/// <para>EF compiles and translates a query BEFORE it opens a connection, so the two failures are
/// distinguishable: an untranslatable expression throws <see cref="InvalidOperationException"/>
/// carrying "could not be translated", while a translatable one gets as far as failing to connect.
/// Pointing the context at a closed port therefore tells us which happened.</para>
///
/// <para>Worth its own test because the projection is the non-obvious part of the resolver — it reads
/// the membership role through the team via a nested collection projection, and an expression EF
/// cannot translate would surface as a 500 on every authorized write rather than as a build failure.
/// This calls the real production method, so it cannot drift from what ships.</para>
/// </summary>
[Trait("Category", "Unit")]
public class TeamMembershipResolverQueryTests
{
    private const string UnreachableDatabase = "Host=127.0.0.1;Port=1;Database=codespace;Username=none;Password=none;Timeout=1;Command Timeout=1";

    [Fact]
    public async Task The_role_lookup_translates_to_SQL()
    {
        using var db = BuildContext();
        var resolver = new TeamMembershipResolver(db, new StubUser(Guid.NewGuid()));

        var thrown = await Record.ExceptionAsync(() => resolver.ResolveRoleAsync(Guid.NewGuid(), CancellationToken.None)).ConfigureAwait(false);

        thrown.ShouldNotBeNull("the lookup must have reached the database layer — if it returned, this test is no longer exercising the query");
        IsTranslationFailure(thrown).ShouldBeFalse($"the role lookup's projection cannot be translated to SQL, so every permission-gated write would fail at runtime:\n{thrown}");
    }

    [Fact]
    public async Task The_membership_check_translates_to_SQL()
    {
        // EnsureMembershipAsync delegates to the same query, and it is the one every team-scoped
        // read already depends on — a translation break here takes the whole API down, not just writes.
        using var db = BuildContext();
        var resolver = new TeamMembershipResolver(db, new StubUser(Guid.NewGuid()));

        var thrown = await Record.ExceptionAsync(() => resolver.EnsureMembershipAsync(Guid.NewGuid(), CancellationToken.None)).ConfigureAwait(false);

        thrown.ShouldNotBeNull();
        IsTranslationFailure(thrown).ShouldBeFalse($"the membership check's projection cannot be translated to SQL:\n{thrown}");
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused_before_any_query()
    {
        // Order matters: the null-user guard has to fire ahead of the database call, or an
        // unauthenticated request turns into a connection error instead of a 403.
        using var db = BuildContext();
        var resolver = new TeamMembershipResolver(db, new StubUser(null));

        await Should.ThrowAsync<TenantAccessDeniedException>(() => resolver.ResolveRoleAsync(Guid.NewGuid(), CancellationToken.None)).ConfigureAwait(false);
    }

    private static bool IsTranslationFailure(Exception thrown) =>
        thrown is InvalidOperationException && thrown.Message.Contains("could not be translated", StringComparison.Ordinal);

    private static CodeSpaceDbContext BuildContext()
    {
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(UnreachableDatabase).UseSnakeCaseNamingConvention().Options;

        return new CodeSpaceDbContext(options);
    }

    private sealed class StubUser : ICurrentUser
    {
        public StubUser(Guid? id) { Id = id; }

        public Guid? Id { get; }
        public string Name => "stub";
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public IReadOnlyList<string> Permissions => Array.Empty<string>();
        public bool PasswordMustChange => false;

        public bool HasRole(string role) => false;
        public bool HasPermission(string permission) => false;
    }
}
