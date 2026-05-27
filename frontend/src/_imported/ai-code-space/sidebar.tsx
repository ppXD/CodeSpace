import { useLocation, useNavigate } from "@tanstack/react-router";
import { useCallback, useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";

import { clearAuthState } from "@/api/auth";
import type { MeTeam } from "@/api/types";
import { teamToUrlSlug, useActiveTeam } from "@/hooks/use-me";

import { Ic } from "./icons";

/**
 * Left sidebar — team switcher (top), nav (middle), user tab (bottom). Both the team
 * switcher and the user tab open popovers via the same createPortal pattern; the user
 * popover groups the few user-scoped actions (change password, sign out) so the bottom
 * row isn't a single hard-coded signout button.
 */

interface Coords { top: number; left: number; width: number; }

const TEAM_COLORS = ["#C97C3F", "#5C7CD6", "#5BA17A", "#A26DC9", "#7A766E", "#D97757"] as const;
const teamColor = (team: MeTeam | undefined) => team ? TEAM_COLORS[hashCode(team.id) % TEAM_COLORS.length] : "#7A766E";

export function Sidebar() {
  const navigate = useNavigate();
  const { pathname } = useLocation();
  const { me, teams, active, setActive } = useActiveTeam();
  // Active-nav highlight is URL-derived so it stays in sync with browser
  // back/forward and deep-linked entry — no local toggle state needed.
  // Phase 3.0 — repositories now live inside Projects, so the sidebar no longer has a
  // dedicated "Repositories" row. Repos are reached via Project detail → Repositories tab.
  // Old /repositories and /teams/{slug}/repositories routes redirect to the team's
  // Projects list so deep links from before the refactor still land somewhere useful.
  const isProjectsActive = pathname === "/"
    || pathname.startsWith("/repositories")
    || /^\/teams\/[^/]+\/(repositories|projects)/.test(pathname);
  const isWorkflowsActive = /^\/teams\/[^/]+\/workflows/.test(pathname);

  // ── Team switcher ────────────────────────────────────────────────────────────
  const [teamOpen, setTeamOpen] = useState(false);
  const [teamCoords, setTeamCoords] = useState<Coords | null>(null);
  const teamTriggerRef = useRef<HTMLDivElement | null>(null);

  // ── User menu ────────────────────────────────────────────────────────────────
  const [userOpen, setUserOpen] = useState(false);
  const [userCoords, setUserCoords] = useState<Coords | null>(null);
  const userTriggerRef = useRef<HTMLDivElement | null>(null);

  const initial = active?.name.charAt(0).toUpperCase() ?? "?";
  const activeIsPersonal = active?.kind === "Personal";
  const userInitial = me.data?.name.charAt(0).toUpperCase() ?? "?";

  const placeTeamPopover = useCallback(() => {
    const el = teamTriggerRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    const collapsed = el.closest(".app")?.getAttribute("data-sidebar") === "collapsed";
    const W = 280;
    setTeamCoords(collapsed
      ? { top: r.top, left: r.right + 8, width: W }
      : { top: r.bottom + 4, left: r.left, width: r.width });
  }, []);

  const placeUserPopover = useCallback(() => {
    const el = userTriggerRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    const collapsed = el.closest(".app")?.getAttribute("data-sidebar") === "collapsed";
    const W = 240;
    setUserCoords(collapsed
      ? { top: r.top, left: r.right + 8, width: W }
      // Popover opens UPWARD from the user row — bottom-anchored so the row stays visible.
      : { top: r.top - 8, left: r.left, width: r.width });
  }, []);

  useEffect(() => {
    if (!teamOpen) return;
    placeTeamPopover();
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setTeamOpen(false); };
    const onResize = () => placeTeamPopover();
    window.addEventListener("keydown", onKey);
    window.addEventListener("resize", onResize);
    return () => {
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("resize", onResize);
    };
  }, [teamOpen, placeTeamPopover]);

  useEffect(() => {
    if (!userOpen) return;
    placeUserPopover();
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setUserOpen(false); };
    const onResize = () => placeUserPopover();
    window.addEventListener("keydown", onKey);
    window.addEventListener("resize", onResize);
    return () => {
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("resize", onResize);
    };
  }, [userOpen, placeUserPopover]);

  const teamPopover = teamOpen && teamCoords ? createPortal(
    <>
      <div className="sb-pop-mask" onClick={() => setTeamOpen(false)} />
      <div
        className="sb-pop"
        style={{ top: teamCoords.top, left: teamCoords.left, width: teamCoords.width, transformOrigin: "top left" }}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="sb-pop-head">
          <span>Switch team</span>
          <span className="sb-pop-head-count">{teams.length}</span>
        </div>
        <div className="sb-pop-list">
          {teams.length === 0 && (
            <div className="sb-pop-empty">You aren't a member of any team yet.</div>
          )}
          {teams.map(t => {
            const selected = t.id === active?.id;
            const isPersonal = t.kind === "Personal";
            // Personal team: a person icon avatar + "personal" label tag + "Just you · N
            // repos" sub. The "1 member" wording was technically correct but read as
            // cold and confusing for someone's own space. Workspace rows keep the
            // existing member+repo count summary.
            return (
              <div
                key={t.id}
                className="sb-pop-row"
                data-selected={selected}
                data-personal={isPersonal}
                onClick={() => {
                  // Pick the team locally (writes localStorage so the X-Team-Id
                  // header injector picks up the change immediately for any
                  // in-flight requests) AND navigate to the new team's URL so
                  // the address bar reflects the active scope.
                  setActive(t.id);
                  setTeamOpen(false);
                  // Phase 3.0 — team switcher lands on the team's project list, matching
                  // the sidebar's primary nav row.
                  navigate({ to: "/teams/$teamSlug/projects", params: { teamSlug: teamToUrlSlug(t) } });
                }}
              >
                <div className="sb-pop-row-avatar" style={{ background: teamColor(t) }}>
                  {isPersonal ? <Ic.Users size={14} /> : t.name.charAt(0).toUpperCase()}
                </div>
                <div className="sb-pop-row-meta">
                  <div className="sb-pop-row-name">
                    {t.name}
                    {isPersonal && <span className="sb-pop-row-kind">personal</span>}
                  </div>
                  <div className="sb-pop-row-sub">
                    {isPersonal
                      ? `Just you · ${t.repositoryCount} ${t.repositoryCount === 1 ? "repo" : "repos"}`
                      : `${t.memberCount} ${t.memberCount === 1 ? "member" : "members"} · ${t.repositoryCount} ${t.repositoryCount === 1 ? "repo" : "repos"}`}
                  </div>
                </div>
                {selected && <span className="sb-pop-row-check"><Ic.Check size={14} /></span>}
              </div>
            );
          })}
        </div>
        <div className="sb-pop-foot">
          {/* "Create workspace" rather than "Create team" — Personal teams aren't
              user-creatable (one per user, auto-provisioned on signup). The action
              label needs to match what the operator can actually do. */}
          <div className="sb-pop-action"><Ic.Plus size={14} /> Create workspace</div>
        </div>
      </div>
    </>,
    document.body,
  ) : null;

  const userPopover = userOpen && userCoords ? createPortal(
    <>
      <div className="sb-pop-mask" onClick={() => setUserOpen(false)} />
      <div
        className="sb-pop sb-pop-user"
        style={{
          top: userCoords.top,
          left: userCoords.left,
          width: userCoords.width,
          transform: "translateY(-100%)",
          transformOrigin: "bottom left",
        }}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="sb-pop-userhead">
          <div className="sb-pop-userhead-avatar">{userInitial}</div>
          <div className="sb-pop-userhead-meta">
            <div className="sb-pop-userhead-name">{me.data?.name ?? "—"}</div>
            <div className="sb-pop-userhead-sub">{me.data?.email ?? ""}</div>
          </div>
        </div>
        <div className="sb-pop-menu">
          <button
            className="sb-pop-item"
            onClick={() => {
              setUserOpen(false);
              navigate({ to: "/change-password" });
            }}
          >
            <Ic.Key size={14} />
            <span>Change password</span>
          </button>
        </div>
        <div className="sb-pop-menu sb-pop-menu-danger">
          <button
            className="sb-pop-item sb-pop-item-danger"
            onClick={() => {
              clearAuthState();
              // Hard navigation — sheds the QueryClient cache and tears down any popups.
              window.location.assign("/signin");
            }}
          >
            <Ic.SignOut size={14} />
            <span>Sign out</span>
          </button>
        </div>
      </div>
    </>,
    document.body,
  ) : null;

  return (
    <aside className="sb">
      <div
        ref={teamTriggerRef}
        className="sb-ws"
        data-open={teamOpen}
        onClick={() => setTeamOpen(o => !o)}
        title={teamOpen ? "" : "Switch team"}
      >
        <div className="sb-ws-avatar" style={{ background: teamColor(active) }}>
          {activeIsPersonal ? <Ic.Users size={14} /> : initial}
        </div>
        <div className="sb-ws-meta">
          <div className="sb-ws-name">{active?.name ?? (me.isLoading ? "Loading…" : "No team")}</div>
          {/* Sub-line summarises the team: "Just you" for Personal (warmer than "1
              member"), "N members" for Workspace. Falls back to a sign-in CTA when
              there's no active team — covers the unauthenticated edge case. */}
          <div className="sb-ws-plan">
            {!active ? "Sign in to continue"
              : activeIsPersonal ? "Just you"
                : `${active.memberCount} ${active.memberCount === 1 ? "member" : "members"}`}
          </div>
        </div>
        <Ic.ChevronUpDown size={14} className="sb-ws-caret" />
      </div>
      {teamPopover}

      <nav className="sb-nav">
        <div
          className="sb-nav-item"
          data-active={isProjectsActive}
          onClick={() => {
            // Phase 3.0 — primary nav row is now "Projects". The project-detail page
            // surfaces both Variables and Repositories tabs; repos no longer have a
            // dedicated sidebar entry. First-paint clicks before /me resolves are
            // no-ops; the legacy /repositories route still redirects deep links to
            // the team's project list so old bookmarks land somewhere useful.
            if (active) {
              navigate({ to: "/teams/$teamSlug/projects", params: { teamSlug: teamToUrlSlug(active) } });
            } else {
              navigate({ to: "/repositories" });
            }
          }}
          title="Projects"
        >
          <span className="sb-nav-ic"><Ic.Folder size={15} /></span>
          <span className="sb-nav-lbl">Projects</span>
          {active && <span className="sb-nav-badge">{active.repositoryCount}</span>}
        </div>
        <div
          className="sb-nav-item"
          data-active={isWorkflowsActive}
          onClick={() => {
            // Workflows are team-scoped — without an active team we have nowhere to land.
            // First-paint clicks (before /me resolves) are no-ops; a moment later the
            // active team arrives and the click works.
            if (active) {
              navigate({ to: "/teams/$teamSlug/workflows", params: { teamSlug: teamToUrlSlug(active) } });
            }
          }}
          title="Workflows"
        >
          <span className="sb-nav-ic"><Ic.Workflow size={15} /></span>
          <span className="sb-nav-lbl">Workflows</span>
        </div>
      </nav>

      <div className="sb-spacer" />

      <div
        ref={userTriggerRef}
        className="sb-foot"
        data-open={userOpen}
        onClick={() => setUserOpen(o => !o)}
        title={userOpen ? "" : "Account menu"}
      >
        <div className="sb-me">{userInitial}</div>
        <div className="sb-me-meta">
          <div className="sb-me-name">{me.data?.name ?? "—"}</div>
          <div className="sb-me-role">{active?.role ?? "—"}</div>
        </div>
        <Ic.ChevronUpDown size={14} className="sb-ws-caret" />
      </div>
      {userPopover}
    </aside>
  );
}

/** Stable string-to-int hash for picking a deterministic team-avatar colour. */
function hashCode(s: string) {
  let h = 0;
  for (let i = 0; i < s.length; i++) h = (h << 5) - h + s.charCodeAt(i) | 0;
  return Math.abs(h);
}
