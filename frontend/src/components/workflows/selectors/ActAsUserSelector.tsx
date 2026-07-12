import { avatarColor } from "@/lib/avatarColor";
import type { ActAsCandidate } from "@/api/repositories";
import { UUID_RE, useActAsCandidates } from "@/hooks/use-repositories";

import { SearchSelect, type SearchOption } from "./SearchSelect";

/**
 * The "act as" AUTHOR picker for a git write's actAsUserId field (`"x-selector": "actorUser"`). Uses the
 * shared {@link SearchSelect} combobox — the same control the model/repo pickers use — and offers ONLY
 * teammates who have a live linked identity on the SIBLING repository's provider, so a picked author's
 * write can never fail with ActorIdentityRequiredException. Each row shows the person's name, the provider
 * handle the write is attributed to (`@handle`), and a colour-coded initial. The stored value is the bare
 * user-id string (or undefined to drop the key), so the field stays wire-compatible and keeps SchemaForm's
 * Pick ⇄ Expression toggle for binding it to a `{{ref}}`.
 */

const ROLE_HINT =
  "The pull request's commits and authorship are made AS this person, using their own linked GitHub/GitLab " +
  "token. Only teammates who linked an identity on this repository's provider can be picked. Leave empty to " +
  "use the repository's own connection credential.";

function toOption(c: ActAsCandidate): SearchOption {
  const initial = (c.name.trim()[0] ?? "?").toUpperCase();
  return { id: c.userId, label: c.name, meta: `@${c.providerUsername}`, avatar: { text: initial, ...avatarColor(c.userId) } };
}

export function ActAsUserSelector({ repositoryId, value, onChange }: { repositoryId?: string; value: string; onChange: (next: string) => void }) {
  // The candidate query is gated on a literal repository UUID; mirror that gate so the UI can prompt for a
  // repo when it's unset or bound to a {{ref}} (never showing a stale author list for the wrong repo).
  const hasRepo = typeof repositoryId === "string" && UUID_RE.test(repositoryId);
  const candidates = useActAsCandidates(repositoryId);
  const options = hasRepo ? (candidates.data ?? []).map(toOption) : [];

  const hint = !hasRepo
    ? "Choose a repository above to see who can be the author here."
    : (!candidates.isLoading && options.length === 0)
      ? "No teammate has linked a GitHub/GitLab identity on this repository's provider yet — leave empty to use the repository's connection credential."
      : ROLE_HINT;

  return (
    <SearchSelect
      options={options}
      value={value ? [value] : []}
      onChange={(ids) => onChange(ids[0] ?? "")}
      loading={hasRepo && candidates.isLoading}
      placeholder={hasRepo ? "Pick an author…" : "Select a repository first…"}
      hint={hint}
    />
  );
}
