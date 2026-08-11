import { createFileRoute, useNavigate, useRouter } from "@tanstack/react-router";
import { useEffect, useRef, useState } from "react";

import { ApiError } from "@/api/request";
import { AuthShell } from "@/components/auth/AuthShell";
import { useSignIn } from "@/hooks/use-sign-in";

/**
 * Sign-in page. The only way into the product: there is no public sign-up, so the note under the
 * form says where an account comes from instead of offering a link that would 404.
 */

export const Route = createFileRoute("/signin")({
  component: SignIn,
});

function SignIn() {
  const navigate = useNavigate();
  const router = useRouter();
  const signIn = useSignIn();

  // The field is "name" rather than "email" because the backend accepts either an email
  // or a display name. The seed admin's name is "admin"; users with email-style logins
  // type their email here.
  const [name, setName] = useState("");
  const [password, setPassword] = useState("");
  const nameRef = useRef<HTMLInputElement>(null);

  useEffect(() => { nameRef.current?.focus(); }, []);

  const submit = (e?: React.FormEvent) => {
    e?.preventDefault();
    if (!name || !password || signIn.isPending) return;

    signIn.mutate(
      { name: name.trim(), password },
      {
        onSuccess: async (response) => {
          await router.invalidate();
          navigate({ to: response.user.passwordMustChange ? "/change-password" : "/" });
        },
      },
    );
  };

  const errorMessage =
    signIn.error instanceof ApiError ? signIn.error.message
      : signIn.error instanceof Error ? signIn.error.message
        : null;

  return (
    <AuthShell>
      <h1 className="auth-title">Sign in</h1>
      <p className="auth-lede">Use the email or name your workspace owner invited.</p>

      <form className="auth-form" onSubmit={submit}>
        <label className="auth-field">
          <span className="auth-label">Email or name</span>
          <input
            ref={nameRef}
            type="text"
            className="auth-input"
            autoComplete="username"
            spellCheck={false}
            value={name}
            onChange={(e) => setName(e.target.value)}
            disabled={signIn.isPending}
          />
        </label>

        <label className="auth-field">
          <span className="auth-label">Password</span>
          <input
            type="password"
            className="auth-input"
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            disabled={signIn.isPending}
          />
        </label>

        <button type="submit" className="auth-submit" disabled={!name || !password || signIn.isPending}>
          {signIn.isPending ? "Signing in…" : "Sign in"}
        </button>

        {errorMessage && <div className="auth-error" role="alert">{errorMessage}</div>}
      </form>

      <p className="auth-note">No public sign-up. Ask a workspace owner for an invite link.</p>
    </AuthShell>
  );
}
