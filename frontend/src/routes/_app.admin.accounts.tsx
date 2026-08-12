import { createFileRoute } from "@tanstack/react-router";

import { AccountsAdmin } from "@/components/settings/AccountsAdmin";

/**
 * Instance administration — accounts across every team. Deliberately outside a team's settings: it
 * is not a fact about one team, and the endpoints behind it are global-admin only.
 */
export const Route = createFileRoute("/_app/admin/accounts")({
  component: AccountsPage,
});

function AccountsPage() {
  return (
    <section className="ct">
      <div className="ct-head">
        <div className="ct-crumbs"><span className="cur">Accounts</span></div>
        <div className="ct-title-row"><h1 className="ct-title">Accounts</h1></div>
      </div>
      <div className="ct-body"><AccountsAdmin /></div>
    </section>
  );
}
