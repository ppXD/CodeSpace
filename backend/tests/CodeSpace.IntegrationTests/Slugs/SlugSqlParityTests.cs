using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Slugs;
using CodeSpace.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Slugs;

/// <summary>
/// 🟢 Integration: the SQL slugify used by the slug-backfill migrations
/// (<c>RTRIM(LEFT(TRIM(BOTH '-' FROM regexp_replace(lower(name), '[^a-z0-9_]+', '-', 'g')), 64), '-')</c>)
/// must produce the SAME slug as the runtime C# <see cref="Slug.Slugify"/> — otherwise a workflow backfilled
/// from its name gets a different slug than a freshly-created one would, silently splitting the identity. The
/// C# side alone is pinned by <c>SlugTests</c>; this runs the SQL expression against the real Postgres and
/// asserts equality, closing the parity that was previously asserted only by a comment.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SlugSqlParityTests
{
    private readonly PostgresFixture _fixture;
    public SlugSqlParityTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData("Acme Backend")]
    [InlineData("Backend Services")]
    [InlineData("My Product 2024!")]
    [InlineData("  Padded  Name  ")]
    [InlineData("Already-Kebab")]
    [InlineData("snake_case_kept")]
    [InlineData("---spaces---")]
    [InlineData("UPPER")]
    [InlineData("café münch")]                                   // non-ASCII → hyphens on both sides
    [InlineData("Nightly Audit")]
    [InlineData("$$$")]                                          // punctuation-only → empty on both sides
    [InlineData("   ")]                                          // whitespace → empty on both sides
    [InlineData("Node Manifests")]                              // a reserved word's raw slugify (dedup is separate)
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")] // 64-cap + trailing-hyphen trim
    public async Task Sql_slugify_matches_the_csharp_Slug_Slugify(string name)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        // The exact expression from 0099_workflow_slug.sql (and 0022 / 0042 / 0079), with the name parameterised.
        var fromSql = (await db.Database
            .SqlQueryRaw<string>(
                "SELECT RTRIM(LEFT(TRIM(BOTH '-' FROM regexp_replace(lower({0}), '[^a-z0-9_]+', '-', 'g')), 64), '-') AS \"Value\"",
                name)
            .ToListAsync()).Single();

        fromSql.ShouldBe(Slug.Slugify(name),
            customMessage: $"SQL backfill slugify diverged from C# Slug.Slugify for '{name}' — a backfilled row would get a different slug than a fresh create");
    }
}
