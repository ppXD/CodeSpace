import { createPortal } from "react-dom";

import type { NodeManifestDto } from "@/api/workflows";

import { nodeIconFor, nodeToneFor } from "./nodeIcon";
import { isBodyStartTypeKey, isContainerKind } from "./workflowContainers";

export interface NodeAddMenuProps {
  /** Screen point to anchor the popover at (the "+" click coords). */
  at: { x: number; y: number };
  manifests: NodeManifestDto[];
  onPick: (manifest: NodeManifestDto) => void;
  onClose: () => void;
}

/**
 * The node picker opened by a node's "+" affordance. Lists the types you can add DOWNSTREAM — Steps
 * (Regular + the loop / try containers) and Endpoints (Terminal) — never a second Trigger (the engine
 * assumes one entry point) and never a container's internal body-start marker (loop_start / try_start,
 * auto-created with the container). A fixed-position popover at the click point with a full-screen mask.
 */
export function NodeAddMenu({ at, manifests, onPick, onClose }: NodeAddMenuProps) {
  const steps = manifests.filter((m) => (m.kind === "Regular" || isContainerKind(m.kind)) && !isBodyStartTypeKey(m.typeKey));
  const endpoints = manifests.filter((m) => m.kind === "Terminal");

  const section = (title: string, items: NodeManifestDto[]) =>
    items.length === 0 ? null : (
      <div className="wf-addmenu-section">
        <div className="wf-addmenu-h">{title}</div>
        {items.map((m) => (
          <button key={m.typeKey} type="button" className="wf-addmenu-item" data-tone={nodeToneFor(m)} onClick={() => onPick(m)}>
            <span className="wf-addmenu-item-icon">{nodeIconFor(m, 13)}</span>
            <span className="wf-addmenu-item-name">{m.displayName}</span>
            <span className="wf-addmenu-item-cat">{m.category}</span>
          </button>
        ))}
      </div>
    );

  return createPortal(
    <>
      <div className="wf-addmenu-mask" onClick={onClose} />
      <div className="wf-addmenu" style={{ left: at.x, top: at.y }} role="menu">
        {section("Steps", steps)}
        {section("Endpoints", endpoints)}
      </div>
    </>,
    document.body,
  );
}
