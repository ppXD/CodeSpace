import { useEffect } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import { useConfirm } from "@/components/dialog";
import { useDeleteSkill, useSkill } from "@/hooks/use-skills";

/**
 * Read-only skill detail — opened from a Library pack's skill row. Shows the skill's @handle, description,
 * category, and its SKILL.md body, with a Delete action (soft-delete, confirmed). Skills aren't editable in-app
 * yet (a standalone skill editor is a follow-up); this is the inspect-and-remove surface.
 */
export function SkillDetailModal({ skillId, onClose }: { skillId: string; onClose: () => void }) {
  const skill = useSkill(skillId);
  const remove = useDeleteSkill();
  const confirm = useConfirm();

  // Block dismissal while a delete is in flight so its error can't be lost with the modal.
  const dismiss = () => { if (!remove.isPending) onClose(); };

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape" && !remove.isPending) onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose, remove.isPending]);

  async function handleDelete() {
    const data = skill.data;
    if (!data) return;

    const ok = await confirm({
      title: "Delete skill?",
      message: (<><strong>{data.name}</strong> will be removed. Any agent that carries it will drop it.</>),
      confirmLabel: "Delete",
      destructive: true,
    });
    if (!ok) return;

    try {
      await remove.mutateAsync(skillId);
      onClose();
    } catch {
      // The mutation's error renders in the banner below; keep the modal open.
    }
  }

  const removeErr = remove.error instanceof ApiError ? remove.error.message : remove.error ? "Couldn't delete the skill." : null;
  const data = skill.data;

  return createPortal(
    <>
      <div className="mdl-mask" onClick={dismiss} />
      <div className="mdl" role="dialog" aria-modal="true" aria-label={data?.name ?? "Skill"} style={{ width: 600, maxWidth: "94vw" }}>
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">{data?.name ?? "Skill"}</div>
            {data && <div className="mdl-sub" style={{ fontFamily: "var(--font-mono, ui-monospace, monospace)" }}>@{data.slug}{data.category ? ` · ${data.category}` : ""}</div>}
          </div>
          <button type="button" className="mdl-x" onClick={dismiss} disabled={remove.isPending} title="Close" aria-label="Close"><Ic.X size={14} /></button>
        </div>

        <div className="mdl-body">
          {skill.isLoading && <div className="wf-form-empty">Loading…</div>}

          {skill.error && (
            <div className="cn-banner cn-banner-err">
              <div className="cn-banner-h">Couldn't load this skill</div>
              <div className="cn-banner-p">{skill.error instanceof ApiError ? skill.error.message : "The skill may not exist in this team."}</div>
            </div>
          )}

          {data && (
            <>
              {data.description && <p style={{ fontSize: 13, color: "var(--ink-2)", margin: "0 0 12px", lineHeight: 1.55 }}>{data.description}</p>}
              <div className="ed-sec-h"><Ic.Book size={13} /> SKILL.md</div>
              <pre className="mdl-pre">{data.body || "(empty)"}</pre>
            </>
          )}

          {removeErr && (
            <div className="cn-banner cn-banner-err" style={{ marginTop: 14 }}>
              <div className="cn-banner-h">Couldn't delete</div>
              <div className="cn-banner-p">{removeErr}</div>
            </div>
          )}
        </div>

        <div className="mdl-foot">
          <div>
            {data && <button type="button" className="btn btn-danger" onClick={handleDelete} disabled={remove.isPending}><Ic.Trash size={14} /> {remove.isPending ? "Deleting…" : "Delete"}</button>}
          </div>
          <button type="button" className="btn" onClick={dismiss} disabled={remove.isPending}>Close</button>
        </div>
      </div>
    </>,
    document.body,
  );
}
