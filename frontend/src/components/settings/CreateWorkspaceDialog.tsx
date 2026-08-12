import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";

import { teamsApi } from "@/api/teams";
import { ApiError } from "@/api/request";

/**
 * Opening a workspace.
 *
 * <p>Asks for a name and nothing else. The slug is derived and deduplicated server-side, because a
 * person naming a team is thinking about what to call it, not about what a URL can contain — and two
 * teams called the same thing is an ordinary thing to want.</p>
 */
export function CreateWorkspaceDialog({ onClose }: { onClose: () => void }) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [name, setName] = useState("");

  const create = useMutation({
    mutationFn: () => teamsApi.create(name.trim()),
    onSuccess: async (team) => {
      // The new team has to exist in /me before navigating, or the shell resolves the slug against a
      // team list that has not heard of it yet and bounces.
      await queryClient.invalidateQueries({ queryKey: ["me"] });
      localStorage.setItem("codespace.activeTeamId", team.id);
      onClose();
      navigate({ to: "/teams/$teamSlug/projects", params: { teamSlug: team.slug } });
    },
  });

  const errorMessage = create.error instanceof ApiError ? create.error.message : create.error instanceof Error ? create.error.message : null;

  return (
    <>
      <div className="mdl-mask" />
      <div className="mdl mdl-dialog" role="dialog" aria-modal="true" aria-label="Create workspace">
        <div className="mdl-dialog-head"><div className="mdl-dialog-title">Create workspace</div></div>
        <div className="mdl-dialog-body">
          <label className="cn-field">
            <span className="cn-field-l">Name</span>
            <input
              className="cn-field-i"
              autoFocus
              value={name}
              placeholder="Platform Team"
              onChange={(e) => setName(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Enter" && name.trim() && !create.isPending) create.mutate(); }}
            />
          </label>

          <div className="cn-field-h">You own what you create.</div>

          {errorMessage && <div className="cn-banner cn-banner-err" style={{ marginTop: 12 }}><div className="cn-banner-p">{errorMessage}</div></div>}

          <div className="mdl-dialog-foot" style={{ padding: "14px 0 0" }}>
            <button className="btn" onClick={onClose}>Cancel</button>
            <button className="btn btn-primary" disabled={!name.trim() || create.isPending} onClick={() => create.mutate()}>
              {create.isPending ? "Creating…" : "Create"}
            </button>
          </div>
        </div>
      </div>
    </>
  );
}
