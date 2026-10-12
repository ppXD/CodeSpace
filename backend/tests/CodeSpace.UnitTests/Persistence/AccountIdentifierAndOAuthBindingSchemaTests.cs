using Shouldly;

namespace CodeSpace.UnitTests.Persistence;

/// <summary>
/// 🟢 Unit: the two migrations behind sign-in resolution and the OAuth browser binding, read as shipped text.
/// </summary>
[Trait("Category", "Unit")]
public class AccountIdentifierAndOAuthBindingSchemaTests
{
    [Fact]
    public void The_address_index_compares_ignoring_case_over_live_accounts_and_never_stops_the_deployment()
    {
        // A migration that raises an error is a deployment that does not start. Over existing duplicates this one warns
        // and creates no index; it never chooses which account keeps an address — that is an operator's decision.
        var sql = Read("0245_app_user_email_unique_ignoring_case.sql");

        sql.ShouldContain("CREATE UNIQUE INDEX IF NOT EXISTS ux_app_user_email_lower ON app_user (lower(email)) WHERE deleted_date IS NULL", Case.Insensitive);
        sql.ShouldContain("RAISE WARNING", Case.Sensitive);
        sql.ShouldNotContain("RAISE EXCEPTION", Case.Insensitive, "a refused migration is an outage");
        sql.ShouldNotContain("DELETE FROM app_user", Case.Insensitive);
        sql.ShouldNotContain("UPDATE app_user", Case.Insensitive);
    }

    [Fact]
    public void The_browser_nonce_column_is_nullable_so_a_pod_on_the_previous_version_keeps_working_mid_deploy()
    {
        var sql = Read("0246_oauth_pending_state_browser_nonce.sql");

        sql.ShouldContain("ADD COLUMN IF NOT EXISTS browser_nonce_hash TEXT NULL", Case.Insensitive);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", file));
}
