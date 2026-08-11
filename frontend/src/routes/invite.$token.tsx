import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Link, createFileRoute, useNavigate, useRouter } from "@tanstack/react-router";
import { useState } from "react";

import { storeJwt } from "@/api/auth";
import { invitationsApi, type InvitationPreview } from "@/api/invitations";
import { ApiError } from "@/api/request";
import { AuthShell } from "@/components/auth/AuthShell";

/**
 * Invitation acceptance. Public by design — it lives outside `_app` because the person opening it
 * has no session yet, and requiring one would send every invitee to a sign-in page for an account
 * they don't have.
 *
 * The link is the credential, so the page shows nothing about the team until the server has vouched
 * for the token: an invalid token must not reveal that a team exists, let alone name it.
 */

const MIN_PASSWORD_LENGTH = 12;
const ACTIVE_TEAM_STORAGE_KEY = "codespace.activeTeamId";

export const Route = createFileRoute("/invite/$token")({
  component: AcceptInvitation,
});

function AcceptInvitation() {
  const { token } = Route.useParams();

  const invitation = useQuery({
    queryKey: ["invitation", token],
    queryFn: () => invitationsApi.preview(token),
    retry: false,
  });

  if (invitation.isPending) {
    return (
      <AuthShell>
        <p className="auth-lede">Checking your invitation…</p>
      </AuthShell>
    );
  }

  if (invitation.isError) return <DeadLink error={invitation.error} />;

  return <AcceptForm token={token} invitation={invitation.data} />;
}

function AcceptForm({ token, invitation }: { token: string; invitation: InvitationPreview }) {
  const navigate = useNavigate();
  const router = useRouter();
  const queryClient = useQueryClient();

  const [name, setName] = useState("");
  const [password, setPassword] = useState("");

  const accept = useMutation({
    mutationFn: () => invitationsApi.accept(token, invitation.accountExists ? {} : { name: name.trim(), password }),
    onSuccess: async (response) => {
      storeJwt(response.token);

      // Land the invitee in the team they were invited to rather than whichever team sorts first,
      // so the page they arrive on is the reason they clicked the link.
      const joined = response.user.teams.find((team) => team.name === invitation.teamName) ?? response.user.teams[0];
      if (joined) localStorage.setItem(ACTIVE_TEAM_STORAGE_KEY, joined.id);

      queryClient.setQueryData(["me"], response.user);
      await router.invalidate();
      navigate({ to: "/" });
    },
  });

  const passwordTooShort = password.length > 0 && password.length < MIN_PASSWORD_LENGTH;
  const ready = invitation.accountExists || (name.trim().length > 0 && password.length >= MIN_PASSWORD_LENGTH);

  const errorMessage =
    accept.error instanceof ApiError ? accept.error.message
      : accept.error instanceof Error ? accept.error.message
        : null;

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!ready || accept.isPending) return;
    accept.mutate();
  };

  return (
    <AuthShell
      context={
        <>
          <strong>{invitation.invitedByName}</strong> invited you to<br />
          <strong>{invitation.teamName}</strong> as <em>{invitation.role}</em>.
          <br />
          <br />
          Link expires {formatExpiry(invitation.expiresAt)}.
        </>
      }
    >
      <h1 className="auth-title">{invitation.accountExists ? "Join the team" : "Create your account"}</h1>
      <p className="auth-lede">Joining as {invitation.email}</p>

      <form className="auth-form" onSubmit={submit}>
        {!invitation.accountExists && (
          <>
            <label className="auth-field">
              <span className="auth-label">Your name</span>
              <input
                type="text"
                className="auth-input"
                autoComplete="name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                disabled={accept.isPending}
              />
            </label>

            <label className="auth-field">
              <span className="auth-label">Set a password</span>
              <input
                type="password"
                className="auth-input"
                autoComplete="new-password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                disabled={accept.isPending}
              />
            </label>

            <p className="auth-hint">
              {passwordTooShort ? `At least ${MIN_PASSWORD_LENGTH} characters — ${MIN_PASSWORD_LENGTH - password.length} to go.` : `At least ${MIN_PASSWORD_LENGTH} characters.`}
            </p>
          </>
        )}

        <button type="submit" className="auth-submit" disabled={!ready || accept.isPending}>
          {accept.isPending ? "Joining…" : `Join ${invitation.teamName}`}
        </button>

        {errorMessage && <div className="auth-error" role="alert">{errorMessage}</div>}
      </form>

      <p className="auth-note">
        {invitation.accountExists
          ? <>This address already has an account. <Link to="/signin">Sign in</Link> if you'd rather join from there.</>
          : <>You'll also get a personal workspace of your own, separate from this team.</>}
      </p>
    </AuthShell>
  );
}

function DeadLink({ error }: { error: unknown }) {
  // Every failure mode collapses to one message on purpose. Distinguishing "expired" from "revoked"
  // from "never existed" would let anyone holding a random token learn which guesses were once real.
  const message = error instanceof ApiError && error.status >= 500
    ? "Something went wrong checking this invitation. Try again in a moment."
    : "This invitation link is no longer valid. It may have expired, been revoked, or already been used.";

  return (
    <AuthShell>
      <h1 className="auth-title">Invitation unavailable</h1>
      <p className="auth-lede">{message}</p>
      <p className="auth-note">Ask whoever invited you to send a new link. If you already have an account, <Link to="/signin">sign in</Link>.</p>
    </AuthShell>
  );
}

function formatExpiry(iso: string): string {
  const expires = new Date(iso);

  if (Number.isNaN(expires.getTime())) return "soon";

  const days = Math.round((expires.getTime() - Date.now()) / 86_400_000);

  if (days <= 0) return "today";

  return days === 1 ? "tomorrow" : `in ${days} days`;
}
