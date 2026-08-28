import { createFileRoute } from "@tanstack/react-router";

import { StorageDefaultsAdmin } from "@/components/settings/StorageDefaultsAdmin";

/**
 * Instance administration — the storage default offered to every team. Deliberately outside a team's
 * settings: a default describes the whole deployment, and the endpoints behind it are capability-gated
 * rather than team-scoped.
 */
export const Route = createFileRoute("/_app/admin/storage")({
  component: StoragePage,
});

function StoragePage() {
  return (
    <section className="ct">
      {/* paddingBottom for the same reason as every other tab-less page — `.ct-head` leaves its
          bottom padding to a `.ct-tabs` strip that this page does not have. */}
      <div className="ct-head" style={{ paddingBottom: 18 }}>
        <div className="ct-crumbs"><span className="cur">Storage</span></div>
        <div className="ct-title-row"><h1 className="ct-title">Storage defaults</h1></div>
      </div>
      <div className="ct-body"><StorageDefaultsAdmin /></div>
    </section>
  );
}
