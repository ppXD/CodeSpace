import { createFileRoute } from "@tanstack/react-router";

import { MembersSettings } from "@/components/settings/MembersSettings";

/**
 * Members — who is in the team, what they may do, and who has been offered a seat.
 *
 * <p>Top-level rather than a Settings tab: membership is not configuration, it is one of the things a
 * team IS, and it is looked at far more often than a provider list.</p>
 */
export const Route = createFileRoute("/_app/teams/$teamSlug/members")({
  component: MembersPage,
});

function MembersPage() {
  return (
    <section className="ct">
      <div className="ct-head">
        <div className="ct-crumbs"><span className="cur">Members</span></div>
        <div className="ct-title-row"><h1 className="ct-title">Members</h1></div>
      </div>
      <div className="ct-body"><MembersSettings /></div>
    </section>
  );
}
