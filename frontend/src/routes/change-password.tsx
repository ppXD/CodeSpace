import { createFileRoute, redirect, useNavigate, useRouter } from "@tanstack/react-router";
import { useEffect, useRef, useState } from "react";

import { isAuthenticated } from "@/api/auth";
import { ApiError } from "@/api/request";
import { AuthShell } from "@/components/auth/AuthShell";
import { useChangePassword } from "@/hooks/use-change-password";

/**
 * Password rotation page. Reached:
 *   • Automatically after sign-in when the user's password_must_change flag is true
 *     (forced rotation for the bootstrap admin from migration 0006).
 *   • Reactively when any API call returns 403 password_rotation_required.
 *   • Voluntarily from the sidebar menu (future enhancement).
 *
 * Three fields — current, new, confirm — in the same shell as /signin, because it is reached
 * directly from it and a change of visual language mid-flow reads as a different application.
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

  const rules = [
    { met: next.length >= MIN_PASSWORD_LENGTH, label: `At least ${MIN_PASSWORD_LENGTH} characters` },
    { met: confirm.length > 0 && next === confirm, label: "New and confirm match" },
    { met: next.length > 0 && next !== current, label: "New differs from current" },
  ];

  return (
    <AuthShell context={<>Your account needs a new password before you can continue.</>}>
      <h1 className="auth-title">Rotate your password</h1>
      <p className="auth-lede">This account is still on a password that must be changed.</p>

      <form className="auth-form" onSubmit={submit}>
        <label className="auth-field">
          <span className="auth-label">Current password</span>
          <input
            ref={currentRef}
            type="password"
            className="auth-input"
            autoComplete="current-password"
            value={current}
            onChange={(e) => setCurrent(e.target.value)}
            disabled={change.isPending}
          />
        </label>

        <label className="auth-field">
          <span className="auth-label">New password</span>
          <input
            type="password"
            className="auth-input"
            autoComplete="new-password"
            value={next}
            onChange={(e) => setNext(e.target.value)}
            disabled={change.isPending}
          />
        </label>

        <label className="auth-field">
          <span className="auth-label">Confirm new password</span>
          <input
            type="password"
            className="auth-input"
            autoComplete="new-password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
            disabled={change.isPending}
          />
        </label>

        <ul className="auth-rules">
          {rules.map((rule) => (
            <li key={rule.label} data-met={rule.met}>{rule.met ? "\u2713" : "\u00b7"} {rule.label}</li>
          ))}
        </ul>

        <button type="submit" className="auth-submit" disabled={!current || !next || !confirm || change.isPending}>
          {change.isPending ? "Rotating\u2026" : "Rotate password"}
        </button>

        {errorMessage && <div className="auth-error" role="alert">{errorMessage}</div>}
      </form>
    </AuthShell>
  );
}
