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
      {/* paddingBottom because this page has no tabs strip. `.ct-head` is authored with
          `padding: 18px 28px 0` on the assumption that a `.ct-tabs` row closes it out — the tabs
          carry their own bottom padding and their underline IS the divider. Members had that strip
          while it was a Settings tab and lost it on the way out, leaving the title sitting flush on
          the rule. 18 is what Runs, Agents and Workflows use for the same reason. */}
      <div className="ct-head" style={{ paddingBottom: 18 }}>
        <div className="ct-crumbs"><span className="cur">Members</span></div>
        <div className="ct-title-row"><h1 className="ct-title">Members</h1></div>
      </div>
      <div className="ct-body"><MembersSettings /></div>
    </section>
  );
}
