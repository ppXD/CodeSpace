import { createFileRoute, redirect, useNavigate, useRouter } from "@tanstack/react-router";
import { useEffect, useRef, useState } from "react";

import { isAuthenticated } from "@/api/auth";
import { ApiError } from "@/api/request";
import { useChangePassword } from "@/hooks/use-change-password";

import "@/styles/tui.css";

/**
 * Password rotation page. Reached:
 *   • Automatically after sign-in when the user's password_must_change flag is true
 *     (forced rotation for the bootstrap admin from migration 0006).
 *   • Reactively when any API call returns 403 password_rotation_required.
 *   • Voluntarily from the sidebar menu (future enhancement).
 *
 * Three fields — current, new, confirm — with the same TUI keyboard contract as /signin.
 */

const MIN_PASSWORD_LENGTH = 12;

export const Route = createFileRoute("/change-password")({
  beforeLoad: () => {
    // No JWT → user must sign in first; rotation requires the current password verified
    // by the bearer-token-protected endpoint.
    if (!isAuthenticated()) throw redirect({ to: "/signin" });
  },
  component: ChangePassword,
});

function ChangePassword() {
  const navigate = useNavigate();
  const router = useRouter();
  const change = useChangePassword();

  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [confirm, setConfirm] = useState("");
  const [clientError, setClientError] = useState<string | null>(null);

  const currentRef = useRef<HTMLInputElement>(null);
  useEffect(() => { currentRef.current?.focus(); }, []);

  const submit = (e?: React.FormEvent) => {
    e?.preventDefault();
    setClientError(null);

    if (!current || !next || !confirm) return;

    if (next.length < MIN_PASSWORD_LENGTH) {
      setClientError(`New password must be at least ${MIN_PASSWORD_LENGTH} characters.`);
      return;
    }
    if (next !== confirm) {
      setClientError("New password and confirmation do not match.");
      return;
    }
    if (next === current) {
      setClientError("New password must differ from the current one.");
      return;
    }

    change.mutate(
      { currentPassword: current, newPassword: next },
      {
        onSuccess: async () => {
          await router.invalidate();
          navigate({ to: "/", search: { tab: "all", q: "" } });
        },
      },
    );
  };

  const errorMessage = clientError
    ?? (change.error instanceof ApiError ? change.error.message
      : change.error instanceof Error ? change.error.message
        : null);

  return (
    <div className="tui-root">
      <div className="tui-scanlines" aria-hidden />

      <main className="tui-frame">
        <header className="tui-banner">
          <pre className="tui-ascii" aria-label="rotate password">
{` ┌─┐┌─┐┌┬┐┌─┐┌┬┐┌─┐
 ├┬┘│ │ │ ├─┤ │ ├┤
 ┴└─└─┘ ┴ ┴ ┴ ┴ └─┘`}
          </pre>
        </header>

        <form className="tui-form" onSubmit={submit}>
          <div className="tui-prompt">
            <span className="tui-prompt-host">codespace</span>
            <span className="tui-prompt-sep">:</span>
            <span className="tui-prompt-path">~/auth</span>
            <span className="tui-prompt-sep">$</span>
            <span className="tui-prompt-cmd"> passwd</span>
          </div>

          <label className="tui-field">
            <span className="tui-field-label">current</span>
            <span className="tui-field-arrow">›</span>
            <input
              ref={currentRef}
              type="password"
              className="tui-field-input"
              autoComplete="current-password"
              value={current}
              onChange={(e) => setCurrent(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Escape") setCurrent(""); }}
              disabled={change.isPending}
            />
            <span className="tui-caret" aria-hidden>█</span>
          </label>

          <label className="tui-field">
            <span className="tui-field-label">new</span>
            <span className="tui-field-arrow">›</span>
            <input
              type="password"
              className="tui-field-input"
              autoComplete="new-password"
              value={next}
              onChange={(e) => setNext(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Escape") setNext(""); }}
              disabled={change.isPending}
            />
            <span className="tui-caret" aria-hidden>█</span>
          </label>

          <label className="tui-field">
            <span className="tui-field-label">confirm</span>
            <span className="tui-field-arrow">›</span>
            <input
              type="password"
              className="tui-field-input"
              autoComplete="new-password"
              value={confirm}
              onChange={(e) => setConfirm(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Escape") setConfirm(""); }}
              disabled={change.isPending}
            />
            <span className="tui-caret" aria-hidden>█</span>
          </label>

          <div className="tui-rules">
            <div data-met={next.length >= MIN_PASSWORD_LENGTH}>{next.length >= MIN_PASSWORD_LENGTH ? "✓" : "·"} at least {MIN_PASSWORD_LENGTH} characters</div>
            <div data-met={confirm.length > 0 && next === confirm}>{confirm.length > 0 && next === confirm ? "✓" : "·"} new and confirm match</div>
            <div data-met={next.length > 0 && next !== current}>{next.length > 0 && next !== current ? "✓" : "·"} new differs from current</div>
          </div>

          <div className="tui-actions">
            <button
              type="submit"
              className="tui-submit"
              disabled={!current || !next || !confirm || change.isPending}
            >
              {change.isPending ? "[ ······ ] rotating" : "[ ENTER ] rotate"}
            </button>
          </div>

          {errorMessage && (
            <div className="tui-error" role="alert">
              ! {errorMessage}
            </div>
          )}
        </form>
      </main>
    </div>
  );
}
