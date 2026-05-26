import { createFileRoute, useNavigate, useRouter } from "@tanstack/react-router";
import { useEffect, useRef, useState } from "react";

import { ApiError } from "@/api/request";
import { useSignIn } from "@/hooks/use-sign-in";

import "@/styles/tui.css";

/**
 * Sign-in page. TUI styling — box-drawing borders, $-prompt, blinking caret. Stripped
 * to the bare minimum: ASCII logo + prompt + two fields + the submit button. No tagline,
 * no version tag, no keyboard hint footer — the keyboard contract is implicit (Tab
 * cycles fields, Enter submits, Esc clears the focused field).
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
    <div className="tui-root">
      <div className="tui-scanlines" aria-hidden />

      <main className="tui-frame">
        <header className="tui-banner">
          <pre className="tui-ascii" aria-label="CodeSpace">
{` ┌─┐┌─┐┌┬┐┌─┐┌─┐┌─┐┌─┐┌─┐┌─┐
 │  │ │ ││├┤ └─┐├─┘├─┤│  ├┤
 └─┘└─┘─┴┘└─┘└─┘┴  ┴ ┴└─┘└─┘`}
          </pre>
        </header>

        <form className="tui-form" onSubmit={submit}>
          <div className="tui-prompt">
            <span className="tui-prompt-host">codespace</span>
            <span className="tui-prompt-sep">:</span>
            <span className="tui-prompt-path">~/auth</span>
            <span className="tui-prompt-sep">$</span>
            <span className="tui-prompt-cmd"> login</span>
          </div>

          <label className="tui-field">
            <span className="tui-field-label">name</span>
            <span className="tui-field-arrow">›</span>
            <input
              ref={nameRef}
              type="text"
              className="tui-field-input"
              autoComplete="username"
              spellCheck={false}
              value={name}
              onChange={(e) => setName(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Escape") setName(""); }}
              disabled={signIn.isPending}
            />
            <span className="tui-caret" aria-hidden>█</span>
          </label>

          <label className="tui-field">
            <span className="tui-field-label">passwd</span>
            <span className="tui-field-arrow">›</span>
            <input
              type="password"
              className="tui-field-input"
              autoComplete="current-password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Escape") setPassword(""); }}
              disabled={signIn.isPending}
            />
            <span className="tui-caret" aria-hidden>█</span>
          </label>

          <div className="tui-actions">
            <button
              type="submit"
              className="tui-submit"
              disabled={!name || !password || signIn.isPending}
            >
              {signIn.isPending ? "[ ······ ] verifying" : "[ ENTER ] sign in"}
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
