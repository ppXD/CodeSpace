import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import type { CreateInvitationResult, TeamInvitationSummary, TeamMemberSummary } from "@/api/teams";
import type { TeamRole } from "@/api/types";
import { useMe } from "@/hooks/use-me";
import { TeamPermissions, useTeamInvitations, useTeamManagement, useTeamPermissions } from "@/hooks/use-team-management";
import { useTeamMembers } from "@/hooks/use-team-members";

/**
 * Settings → Members. Who is in the team, what they may do, and who has been offered a seat.
 *
 * <p>Controls are ABSENT rather than present-and-refusing. A 403 is the floor that catches a forged
 * request; it is not how a product tells someone what their role is, so a Viewer sees a roster with
 * no menus at all rather than menus that reject them.</p>
 *
 * <p>Every gate here reads `me.permissions`, which the server expands from the same matrix it refuses
 * on. Nothing on this page maps a role name to an ability.</p>
 */

const ROLE_CONSEQUENCE: Record<TeamRole, string> = {
  Viewer: "Reads everything. Changes nothing.",
  Member: "Launches runs, edits workflows, answers decisions.",
  Admin: "Also manages repositories, credentials and people.",
  Owner: "Full control, including handing the team to someone else.",
};

const ROLE_RANK: Record<TeamRole, number> = { Viewer: 10, Member: 20, Admin: 30, Owner: 40 };

const ASSIGNABLE: TeamRole[] = ["Viewer", "Member", "Admin", "Owner"];

export function MembersSettings() {
  const me = useMe();
  const members = useTeamMembers();
  const { can, role: myRole } = useTeamPermissions();
  const mayManage = can(TeamPermissions.MembersManage);
  const invitations = useTeamInvitations(mayManage);
  const [inviteOpen, setInviteOpen] = useState(false);
  const [issued, setIssued] = useState<{ email: string; result: CreateInvitationResult } | null>(null);

  const rows = members.data ?? [];
  const owners = rows.filter((m) => m.role === "Owner").length;

  return (
    <>
      <div style={{ display: "flex", justifyContent: "flex-end", padding: "16px 16px 0" }}>
        {mayManage && (
          <button className="btn btn-primary" onClick={() => setInviteOpen(true)}>
            <Ic.Plus size={13} /> Invite
          </button>
        )}
      </div>

      <Section title="People" count={rows.length}>
        {members.isLoading && <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>}

        {rows.map((member) => (
          <MemberRow
            key={member.userId}
            member={member}
            isMe={member.userId === me.data?.id}
            myRole={myRole}
            mayManage={mayManage}
            mayTransfer={can(TeamPermissions.TeamManage)}
            isLastOwner={member.role === "Owner" && owners <= 1}
          />
        ))}
      </Section>

      {mayManage && (
        <Section title="Pending invitations" count={invitations.data?.length ?? 0}>
          {(invitations.data ?? []).length === 0 && (
            <div className="ct-empty">
              <div className="ct-empty-h">Nobody is waiting</div>
              <div className="ct-empty-p">There is no public sign-up — an invite link is the only way in.</div>
            </div>
          )}
          {(invitations.data ?? []).map((invitation) => <InvitationRow key={invitation.id} invitation={invitation} onIssued={setIssued} />)}
        </Section>
      )}

      {inviteOpen && (
        <InviteDialog
          myRole={myRole}
          onClose={() => setInviteOpen(false)}
          onIssued={(email, result) => { setInviteOpen(false); setIssued({ email, result }); }}
        />
      )}

      {issued && <IssuedLinkDialog email={issued.email} result={issued.result} onClose={() => setIssued(null)} />}
    </>
  );
}

function Section({ title, count, children }: { title: string; count: number; children: React.ReactNode }) {
  return (
    <div style={{ margin: 16 }}>
      <div style={{ display: "flex", alignItems: "center", gap: 8, marginBottom: 8, fontSize: 13 }}>
        {title} <span style={{ color: "var(--muted-2)" }}>· {count}</span>
      </div>
      <div className="cn-list">{children}</div>
    </div>
  );
}

function MemberRow({ member, isMe, myRole, mayManage, mayTransfer, isLastOwner }: {
  member: TeamMemberSummary;
  isMe: boolean;
  myRole: TeamRole | null;
  mayManage: boolean;
  mayTransfer: boolean;
  isLastOwner: boolean;
}) {
  const { changeRole, removeMember, transferOwnership } = useTeamManagement();
  const [menuOpen, setMenuOpen] = useState(false);

  // Mirrors the server's rule so the UI offers only what would be accepted: you may act on anyone
  // below you and on yourself, never above, never across. The server refuses regardless — this is
  // about not offering a control that would be rejected.
  const outranked = member.role != null && myRole != null && (ROLE_RANK[member.role] < ROLE_RANK[myRole] || isMe);
  const editable = mayManage && member.role != null && outranked && !isLastOwner;

  const error = [changeRole.error, removeMember.error, transferOwnership.error].find(Boolean);

  return (
    <div className="cn-row">
      <div className="cn-row-head">
        <div className="cn-mark">{member.isBot ? "CS" : initials(member.name)}</div>
        <div className="cn-meta" style={{ flex: 1 }}>
          <div className="cn-name">
            {member.name}
            {isMe && <span className="cn-status">you</span>}
            {member.isBot && <span className="cn-status">bot</span>}
          </div>
          <div className="cn-sub">{member.isBot ? "posts run results into chat" : member.email}</div>
        </div>

        {member.role == null
          ? <span className="cn-sub" style={{ padding: "0 8px" }}>—</span>
          : editable
            ? (
              <select
                className="cn-field-i"
                style={{ width: 120, height: 30, fontSize: 11.5, padding: "0 8px" }}
                value={member.role}
                disabled={changeRole.isPending}
                onChange={(e) => changeRole.mutate({ userId: member.userId, role: e.target.value as TeamRole })}
              >
                {ASSIGNABLE.filter((r) => myRole != null && ROLE_RANK[r] <= ROLE_RANK[myRole]).map((r) => <option key={r} value={r}>{r}</option>)}
              </select>
            )
            : (
              <span className="cn-sub" style={{ padding: "0 8px" }} title={isLastOwner ? "A team must always have an owner. Transfer ownership first." : undefined}>
                {member.role}{isLastOwner ? " · locked" : ""}
              </span>
            )}

        {mayManage && member.role != null && !member.isBot && (
          <div style={{ position: "relative" }}>
            <button className="btn btn-icon" aria-label={`Actions for ${member.name}`} onClick={() => setMenuOpen((v) => !v)}>⋯</button>
            {menuOpen && (
              <div className="sb-pop sb-pop-menu" style={{ position: "absolute", right: 0, top: "calc(100% + 4px)", zIndex: 40, minWidth: 230 }} onMouseLeave={() => setMenuOpen(false)}>
                {mayTransfer && !isMe && (
                  <button
                    className="sb-pop-item"
                    onClick={() => { setMenuOpen(false); transferOwnership.mutate(member.userId); }}
                  >
                    Transfer ownership
                    <span className="lt3-opt-d" style={{ display: "block" }}>Makes them Owner and you Admin. One step, both sides.</span>
                  </button>
                )}
                <button
                  className="sb-pop-item sb-pop-menu-danger"
                  disabled={!outranked || isLastOwner}
                  onClick={() => { setMenuOpen(false); removeMember.mutate(member.userId); }}
                >
                  {isMe ? "Leave team" : "Remove from team"}
                  {isLastOwner && <span className="lt3-opt-d" style={{ display: "block" }}>The last owner can't leave. Transfer ownership first.</span>}
                </button>
              </div>
            )}
          </div>
        )}
      </div>

      {error instanceof ApiError && <div className="cn-sub" style={{ color: "var(--danger)", paddingLeft: 42 }}>{error.message}</div>}
    </div>
  );
}

function InvitationRow({ invitation, onIssued }: { invitation: TeamInvitationSummary; onIssued: (v: { email: string; result: CreateInvitationResult }) => void }) {
  const { revokeInvitation, regenerateInvitation } = useTeamManagement();
  const [menuOpen, setMenuOpen] = useState(false);

  return (
    <div className="cn-row">
      <div className="cn-row-head">
        <div className="cn-mark" style={{ background: "transparent", border: "1px dashed var(--line-2)" }}><Ic.Users size={12} /></div>
        <div className="cn-meta" style={{ flex: 1 }}>
          <div className="cn-name">{invitation.email}</div>
          <div className="cn-sub">invited by {invitation.invitedByName}</div>
        </div>

        <span className="cn-sub" style={{ padding: "0 8px" }}>{invitation.role}</span>

        {invitation.isExpired
          ? <span className="cn-status cn-status-warn">expired</span>
          : <span className="cn-sub">expires {relativeDays(invitation.expiresAt)}</span>}

        <div style={{ position: "relative" }}>
          <button className="btn btn-icon" aria-label={`Actions for ${invitation.email}`} onClick={() => setMenuOpen((v) => !v)}>⋯</button>
          {menuOpen && (
            <div className="sb-pop sb-pop-menu" style={{ position: "absolute", right: 0, top: "calc(100% + 4px)", zIndex: 40, minWidth: 230 }} onMouseLeave={() => setMenuOpen(false)}>
              {/* No "copy link": after creation there is nothing to copy — the server kept only a hash.
                  Saying so is what stops someone hunting for a link they think they mislaid. */}
              <button
                className="sb-pop-item"
                disabled={regenerateInvitation.isPending}
                onClick={async () => {
                  setMenuOpen(false);
                  const result = await regenerateInvitation.mutateAsync(invitation.id);
                  onIssued({ email: invitation.email, result });
                }}
              >
                Regenerate link
                <span className="lt3-opt-d" style={{ display: "block" }}>Issues a new link and kills the old one.</span>
              </button>
              <button className="sb-pop-item sb-pop-menu-danger" onClick={() => { setMenuOpen(false); revokeInvitation.mutate(invitation.id); }}>
                Revoke invitation
              </button>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function InviteDialog({ myRole, onClose, onIssued }: { myRole: TeamRole | null; onClose: () => void; onIssued: (email: string, result: CreateInvitationResult) => void }) {
  const { invite } = useTeamManagement();
  const [email, setEmail] = useState("");
  const [role, setRole] = useState<TeamRole>("Member");

  const submit = async () => {
    const result = await invite.mutateAsync({ email: email.trim(), role });
    onIssued(email.trim(), result);
  };

  return (
    <Modal title="Invite to this team">
      <label className="cn-field">
        <span className="cn-field-l">Email</span>
        <input className="cn-field-i" type="email" autoFocus value={email} onChange={(e) => setEmail(e.target.value)} placeholder="them@team.dev" />
      </label>

      <span className="cn-field-l" style={{ marginTop: 12, display: "block" }}>Role</span>
      {ASSIGNABLE.map((option) => {
        // Above your own rank is a promotion you can't make. Saying so here turns a 403 into a
        // sentence read before the click.
        const tooHigh = myRole != null && ROLE_RANK[option] > ROLE_RANK[myRole];
        return (
          <button
            key={option}
            className="lt3-opt"
            data-on={option === role}
            disabled={tooHigh}
            style={tooHigh ? { opacity: .45, cursor: "not-allowed" } : undefined}
            onClick={() => setRole(option)}
          >
            <span className="lt3-opt-m">
              <span className="lt3-opt-t">{option}</span>
              <span className="lt3-opt-d">{tooHigh ? "You can't invite above your own role." : ROLE_CONSEQUENCE[option]}</span>
            </span>
          </button>
        );
      })}

      {invite.error instanceof ApiError && <div className="cn-banner cn-banner-err" style={{ marginTop: 12 }}><div className="cn-banner-p">{invite.error.message}</div></div>}

      <div className="mdl-dialog-foot" style={{ padding: "14px 0 0" }}>
        <button className="btn" onClick={onClose}>Cancel</button>
        <button className="btn btn-primary" disabled={!email.trim() || invite.isPending} onClick={submit}>
          {invite.isPending ? "Creating…" : "Create link"}
        </button>
      </div>
    </Modal>
  );
}

/**
 * The one moment the link is readable. Not a toast: a message that clears itself on a timer is the
 * wrong shape for something genuinely unrecoverable, so this has to be dismissed on purpose.
 */
function IssuedLinkDialog({ email, result, onClose }: { email: string; result: CreateInvitationResult; onClose: () => void }) {
  const [copied, setCopied] = useState(false);

  return (
    <Modal title={`Invite link for ${email}`}>
      <div style={{ background: "var(--ink)", color: "#F5F1E8", borderRadius: 6, padding: "11px 13px", fontSize: 11, wordBreak: "break-all", lineHeight: 1.55 }}>{result.inviteUrl}</div>

      <div className="cn-banner" style={{ marginTop: 10, background: "#F7ECD6", borderColor: "#E8D4A8" }}>
        <div className="cn-banner-p">
          Copy it now. The server stores only a hash, so this link can't be shown again — if you lose it,
          regenerate to issue a new one and kill this one.
        </div>
      </div>

      <div className="mdl-dialog-foot" style={{ padding: "14px 0 0" }}>
        <button className="btn" onClick={onClose}>Done</button>
        <button
          className="btn btn-primary"
          onClick={async () => { await navigator.clipboard.writeText(result.inviteUrl); setCopied(true); }}
        >
          {copied ? "Copied" : "Copy link"}
        </button>
      </div>
    </Modal>
  );
}

function Modal({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <>
      {/* Non-interactive mask, matching the rest of the modal family — a stray click must not
          discard a link that cannot be retrieved. */}
      <div className="mdl-mask" />
      <div className="mdl mdl-dialog" role="dialog" aria-modal="true" aria-label={title}>
        <div className="mdl-dialog-head"><div className="mdl-dialog-title">{title}</div></div>
        <div className="mdl-dialog-body">{children}</div>
      </div>
    </>
  );
}

function initials(name: string): string {
  return name.split(/\s+/).filter(Boolean).slice(0, 2).map((p) => p[0]!.toUpperCase()).join("") || "?";
}

function relativeDays(iso: string): string {
  const days = Math.round((new Date(iso).getTime() - Date.now()) / 86_400_000);

  if (days <= 0) return "today";

  return days === 1 ? "tomorrow" : `in ${days} days`;
}
