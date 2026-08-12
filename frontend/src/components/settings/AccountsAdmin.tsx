import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";

import { accountsApi, type AccountSummary, type PasswordResetLink } from "@/api/accounts";
import { ApiError } from "@/api/request";
import { useMe } from "@/hooks/use-me";

/**
 * Instance administration: every account on the deployment, whether it is switched on, and a way to
 * hand someone back in.
 *
 * <p>Not team-scoped, and deliberately not reachable from a team's settings — deactivating an account
 * is not a fact about one team. The endpoints behind it are global-admin only; a non-admin who
 * navigates here is told so rather than shown an empty table, because an empty table reads as "there
 * is nobody" rather than "this is not yours".</p>
 */
export function AccountsAdmin() {
  const me = useMe();
  const queryClient = useQueryClient();
  const [issued, setIssued] = useState<{ name: string; link: PasswordResetLink } | null>(null);

  const accounts = useQuery({ queryKey: ["admin-accounts"], queryFn: () => accountsApi.list(), retry: false });

  const refresh = () => queryClient.invalidateQueries({ queryKey: ["admin-accounts"] });

  const deactivate = useMutation({ mutationFn: (id: string) => accountsApi.deactivate(id), onSuccess: refresh });
  const reactivate = useMutation({ mutationFn: (id: string) => accountsApi.reactivate(id), onSuccess: refresh });
  const issueReset = useMutation({ mutationFn: (id: string) => accountsApi.issueResetLink(id) });

  if (accounts.error instanceof ApiError && accounts.error.status === 403) {
    return (
      <div className="ct-empty">
        <div className="ct-empty-h">Not yours to see</div>
        <div className="ct-empty-p">Account administration is limited to instance administrators.</div>
      </div>
    );
  }

  const rows = accounts.data ?? [];

  return (
    <>
      <div className="cn-banner" style={{ margin: 16 }}>
        <div className="cn-banner-h">Accounts on this instance</div>
        <div className="cn-banner-p">
          Deactivating an account signs it out of everything immediately and refuses its next request — it does not
          delete anything, and reactivating brings it back. A reset link lets someone in who has lost their password.
        </div>
      </div>

      {accounts.isLoading && <div className="ct-empty"><div className="ct-empty-h">Loading…</div></div>}

      <div className="cn-list" style={{ margin: 16 }}>
        {rows.map((account) => (
          <AccountRow
            key={account.id}
            account={account}
            isMe={account.id === me.data?.id}
            busy={deactivate.isPending || reactivate.isPending || issueReset.isPending}
            onDeactivate={() => deactivate.mutate(account.id)}
            onReactivate={() => reactivate.mutate(account.id)}
            onIssueReset={async () => setIssued({ name: account.name, link: await issueReset.mutateAsync(account.id) })}
          />
        ))}
      </div>

      {issued && <ResetLinkDialog name={issued.name} link={issued.link} onClose={() => setIssued(null)} />}
    </>
  );
}

function AccountRow({ account, isMe, busy, onDeactivate, onReactivate, onIssueReset }: {
  account: AccountSummary;
  isMe: boolean;
  busy: boolean;
  onDeactivate: () => void;
  onReactivate: () => void;
  onIssueReset: () => void;
}) {
  return (
    <div className="cn-row">
      <div className="cn-row-head">
        <div className="cn-mark">{account.name.slice(0, 2).toUpperCase()}</div>
        <div className="cn-meta" style={{ flex: 1 }}>
          <div className="cn-name">
            {account.name}
            {isMe && <span className="cn-status">you</span>}
            {account.isDeactivated
              ? <span className="cn-status cn-status-warn">deactivated</span>
              : <span className="cn-status cn-status-active"><span className="cn-status-dot" /> active</span>}
            {account.passwordMustChange && <span className="cn-status">must rotate</span>}
          </div>
          <div className="cn-sub">
            {account.email}
            {account.lastLoginDate != null && ` · last signed in ${new Date(account.lastLoginDate).toLocaleDateString()}`}
          </div>
        </div>

        <button className="btn" disabled={busy} onClick={onIssueReset}>Reset link</button>

        {/* Deactivating yourself would take away the account that could undo it. */}
        {account.isDeactivated
          ? <button className="btn" disabled={busy} onClick={onReactivate}>Reactivate</button>
          : <button className="btn btn-danger" disabled={busy || isMe} title={isMe ? "You can't deactivate the account you're using." : undefined} onClick={onDeactivate}>Deactivate</button>}
      </div>
    </div>
  );
}

/**
 * Same shape as the invitation link, for the same reason: the server keeps only a hash, so this is
 * the one moment it is readable and the dialog has to be dismissed on purpose.
 */
function ResetLinkDialog({ name, link, onClose }: { name: string; link: PasswordResetLink; onClose: () => void }) {
  const [copied, setCopied] = useState(false);

  return (
    <>
      <div className="mdl-mask" />
      <div className="mdl mdl-dialog" role="dialog" aria-modal="true" aria-label={`Reset link for ${name}`}>
        <div className="mdl-dialog-head"><div className="mdl-dialog-title">Reset link for {name}</div></div>
        <div className="mdl-dialog-body">
          <div style={{ background: "var(--ink)", color: "#F5F1E8", borderRadius: 6, padding: "11px 13px", fontSize: 11, wordBreak: "break-all", lineHeight: 1.55 }}>{link.resetUrl}</div>

          <div className="cn-banner" style={{ marginTop: 10, background: "#F7ECD6", borderColor: "#E8D4A8" }}>
            <div className="cn-banner-p">
              Copy it now — only a hash is stored, so it can't be shown again. It works once, expires
              on its own, and using it signs the account out of everywhere else.
            </div>
          </div>

          <div className="mdl-dialog-foot" style={{ padding: "14px 0 0" }}>
            <button className="btn" onClick={onClose}>Done</button>
            <button className="btn btn-primary" onClick={async () => { await navigator.clipboard.writeText(link.resetUrl); setCopied(true); }}>
              {copied ? "Copied" : "Copy link"}
            </button>
          </div>
        </div>
      </div>
    </>
  );
}
