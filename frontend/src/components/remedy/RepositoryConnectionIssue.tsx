import { Ic } from "@/_imported/ai-code-space/icons";
import type { CredentialSummary } from "@/api/types";
import { useMe } from "@/hooks/use-me";
import { TeamPermissions, useTeamPermissions } from "@/hooks/use-team-management";
import type { ConnectionRepository } from "@/hooks/use-repository-connection-issues";
import { isStaticToken, type ConnectionIssue } from "@/lib/credentialHealth";

import { useProviderFixes } from "./useProviderFixes";

/** The chip a repository wears when its connection does not work: plain words, amber for something to repair, red once the credential is gone. */
export function ConnectionIssueChip({ issue }: { issue: ConnectionIssue }) {
  const gone = issue.kind === "disconnected" || issue.kind === "retired";

  return (
    <span className={gone ? "cn-status cn-status-error" : "cn-status cn-status-warn"} title={issue.title} data-issue={issue.kind}>
      <Ic.Triangle size={10} /> {issue.label}
    </span>
  );
}

/**
 * The way out, on the repository itself: Replace token for a pasted token and Reconnect for a sign-in — the credential's own
 * person's (on membership alone) or anyone's who manages credentials, and for a sign-in only over someone the caller reaches: an Admin
 * cannot sign in as another Admin or an Owner — and Re-link to another credential (when the caller manages repositories).
 * The same fixes the run page offers, done by the same code. The server stays the authority: it says whether the caller may repair this
 * credential (`callerMayRepair`, decided by the rules the two endpoints apply), so a person is never offered a button that could only answer
 * 403. A server that predates the flag leaves the role to say: the credential's own person, or `credentials.manage`.
 *
 * It stops clicks reaching the row it sits in: a repository row opens the repository, and a dialog's portal still bubbles React events.
 */
export function RepositoryConnectionFix({ repository, issue, onConnectNew }: { repository: ConnectionRepository; issue: ConnectionIssue; onConnectNew?: () => void }) {
  const fix = useProviderFixes();
  const me = useMe();
  const { can } = useTeamPermissions();
  const credential: CredentialSummary | undefined = issue.credential;

  const mayRelink = can(TeamPermissions.ReposManage);
  const ownsIt = credential?.ownership === "Personal" && credential.ownerUserId != null && credential.ownerUserId === me.data?.id;
  const mayRepair = credential != null && (credential.callerMayRepair ?? (ownsIt || can(TeamPermissions.CredentialsManage)));
  // A credential that merely looks fine (the repository is in Error for another reason) or whose person left is moved off, not repaired.
  const repairable = credential != null && (issue.kind === "rejected" || issue.kind === "disconnected");

  return (
    <div className="repo-fix" onClick={e => e.stopPropagation()}>
      {mayRepair && repairable && credential && (
        <button
          type="button"
          className="repo-fix-cta"
          disabled={fix.reconnectingId === credential.id}
          onClick={() => isStaticToken(credential) ? fix.replaceToken(credential) : void fix.reconnect(credential)}
        >
          <Ic.Key size={11} />
          {isStaticToken(credential) ? "Replace token" : fix.reconnectingId === credential.id ? "Reconnecting…" : "Reconnect"}
        </button>
      )}
      {mayRelink && (
        <button type="button" className="repo-fix-cta" onClick={() => fix.relink([{ id: repository.id, fullPath: repository.fullPath }], repository.providerInstanceId, credential?.id ?? null)}>
          <Ic.Link size={11} /> Re-link credential
        </button>
      )}
      {!mayRelink && !(mayRepair && repairable) && <span className="rmd-ask">Ask a team Admin or Owner to fix this connection.</span>}
      {onConnectNew && mayRelink && <button type="button" className="repo-fix-cta" onClick={onConnectNew}><Ic.Plus size={11} /> Connect a credential</button>}
      <span onClick={e => e.stopPropagation()}>{fix.dialogs}</span>
    </div>
  );
}
