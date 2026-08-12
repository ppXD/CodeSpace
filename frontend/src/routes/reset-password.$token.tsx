import { useMutation } from "@tanstack/react-query";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useState } from "react";

import { authApi } from "@/api/auth";
import { ApiError } from "@/api/request";
import { AuthShell } from "@/components/auth/AuthShell";

/**
 * Setting a new password from a reset link.
 *
 * <p>Public, like the invite page and for the same reason: someone who cannot sign in is exactly who
 * needs this, and requiring a session would make the link useless to the only people it is for.</p>
 *
 * <p>It does NOT sign them in. Spending the link ends every session the account had, which is the
 * point of it — handing back a session here would undo that for whoever prompted the reset. They
 * arrive at sign-in and use the password they just chose.</p>
 */

const MIN_PASSWORD_LENGTH = 12;

export const Route = createFileRoute("/reset-password/$token")({
  component: ResetPassword,
});

function ResetPassword() {
  const { token } = Route.useParams();
  const navigate = useNavigate();

  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [done, setDone] = useState(false);

  const reset = useMutation({
    mutationFn: () => authApi.resetPassword(token, password),
    onSuccess: () => setDone(true),
  });

  const tooShort = password.length > 0 && password.length < MIN_PASSWORD_LENGTH;
  const mismatch = confirm.length > 0 && confirm !== password;
  const ready = password.length >= MIN_PASSWORD_LENGTH && confirm === password;

  const errorMessage = reset.error instanceof ApiError ? reset.error.message : reset.error instanceof Error ? reset.error.message : null;

  if (done) {
    return (
      <AuthShell context={<>Your password has been changed. Every session the account had is now signed out.</>}>
        <h1 className="auth-title">Password changed</h1>
        <p className="auth-lede">Sign in with the password you just chose.</p>
        <button className="auth-submit" onClick={() => navigate({ to: "/signin" })}>Go to sign in</button>
      </AuthShell>
    );
  }

  return (
    <AuthShell context={<>Choose a new password.<br />This link works once and then stops.</>}>
      <h1 className="auth-title">Set a new password</h1>
      <p className="auth-lede">You won't need your old one.</p>

      <form className="auth-form" onSubmit={(e) => { e.preventDefault(); if (ready && !reset.isPending) reset.mutate(); }}>
        <label className="auth-field">
          <span className="auth-label">New password</span>
          <input type="password" className="auth-input" autoComplete="new-password" autoFocus value={password} onChange={(e) => setPassword(e.target.value)} disabled={reset.isPending} />
        </label>

        <label className="auth-field">
          <span className="auth-label">Confirm new password</span>
          <input type="password" className="auth-input" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} disabled={reset.isPending} />
        </label>

        <p className="auth-hint">
          {tooShort ? `At least ${MIN_PASSWORD_LENGTH} characters — ${MIN_PASSWORD_LENGTH - password.length} to go.`
            : mismatch ? "The two entries don't match."
              : `At least ${MIN_PASSWORD_LENGTH} characters.`}
        </p>

        <button type="submit" className="auth-submit" disabled={!ready || reset.isPending}>
          {reset.isPending ? "Saving…" : "Set password"}
        </button>

        {errorMessage && <div className="auth-error" role="alert">{errorMessage}</div>}
      </form>

      <p className="auth-note">Didn't ask for this? Ignore the link — it expires on its own and changes nothing until used.</p>
    </AuthShell>
  );
}
