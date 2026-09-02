import { useMemo, useState, type ReactNode, useRef } from "react";
import { createPortal } from "react-dom";

import { useDialogKeys } from "./useDialogKeys";
import { useMutation } from "@tanstack/react-query";

import { ApiError } from "@/api/request";
import type { StorageProfileSummary } from "@/api/storage";
import type { RoutedDataClass, StorageRouteSummary } from "@/api/storageRoutes";
import { useAppendStorageRouteRevision, useCreateStorageRoute, useSetStorageRouteState } from "@/hooks/use-storage-routes";
import { useConfirm } from "@/components/dialog";

/**
 * Which kinds of data land at this destination.
 *
 * Every tick is a statement about the NEXT write and about nothing already written — there is no copy, no move, and
 * no risk to stored bytes.
 *
 * An UNTICK is not the mirror image of a tick, and the dialog must not read as though it were.
 * `RoutedDestinationResolver.LocalApplies` admits a local home for exactly two dispositions — no route at all, and a
 * route created and never activated — and a route an operator STOPPED is neither. So unticking does not send writes
 * back to this server's disk: it makes them fail, for every class, and there is no way back to the local home once a
 * class has been routed and activated. That is a real consequence, so it is stated plainly and confirmed.
 *
 * Ticking is done one class at a time on purpose. Each call is atomic by itself, so a failure half way leaves a
 * legible state — some classes moved, the rest did not — rather than anything half-built.
 */
export function WhatLandsHereDialog({ profile, routes, dataClasses, onClose }: {
  profile: StorageProfileSummary;
  routes: StorageRouteSummary[];
  dataClasses: RoutedDataClass[];
  onClose: () => void;
}) {
  const landing = useMemo(
    () => new Set(routes.filter((route) => route.state === "Active" && route.storageProfileId === profile.id).map((route) => route.dataClassTypeKey)),
    [routes, profile.id],
  );
  const [ticked, setTicked] = useState<Set<string>>(() => new Set(landing));

  const confirm = useConfirm();
  const createRoute = useCreateStorageRoute();
  const repointRoute = useAppendStorageRouteRevision();
  const setRouteState = useSetStorageRouteState();

  const apply = useMutation({
    mutationFn: async () => {
      for (const dataClass of dataClasses) {
        const wanted = ticked.has(dataClass.typeKey);
        if (wanted === landing.has(dataClass.typeKey)) continue;

        const existing = routes.find((route) => route.dataClassTypeKey === dataClass.typeKey);
        if (!wanted) {
          // Stopping a class is expressed by state alone: the pointer stays where it is, so turning it back on later
          // needs no decision about where it pointed. What it does NOT do is restore a local home.
          if (existing) await setRouteState.mutateAsync({ routeId: existing.id, input: { expectedXmin: existing.xmin, expectedCurrentRevision: existing.currentRevision, state: "Disabled" } });
          continue;
        }

        // A data class carries exactly one pointer for the life of the team, so this is "create it" only the first
        // time and "repoint it" every time after.
        const head = existing
          ? existing.storageProfileId === profile.id
            ? existing
            // The route's own mode is preserved. Rewriting a Pinned route as CurrentAtWrite would silently change
            // WHICH revision its writes resolve, which is a separate decision an operator made in Advanced.
            : await repointRoute.mutateAsync({ routeId: existing.id, input: { expectedXmin: existing.xmin, expectedCurrentRevision: existing.currentRevision, storageProfileId: profile.id, profileRevisionMode: existing.profileRevisionMode, pinnedProfileRevision: existing.profileRevisionMode === "Pinned" ? existing.pinnedProfileRevision : null } })
          : await createRoute.mutateAsync({ dataClassTypeKey: dataClass.typeKey, storageProfileId: profile.id, profileRevisionMode: "CurrentAtWrite", pinnedProfileRevision: null });

        if (head.state !== "Active") {
          await setRouteState.mutateAsync({ routeId: head.id, input: { expectedXmin: head.xmin, expectedCurrentRevision: head.currentRevision, state: "Active" } });
        }
      }
    },
    onSuccess: onClose,
  });

  const stopping = dataClasses.filter((dataClass) => landing.has(dataClass.typeKey) && !ticked.has(dataClass.typeKey));
  const changed = stopping.length > 0 || dataClasses.some((dataClass) => ticked.has(dataClass.typeKey) && !landing.has(dataClass.typeKey));

  /**
   * Untick is destructive and the operator has to be told what it actually does. Ticking is not: it moves where the
   * next write goes and nothing else, which is what the rows already say.
   */
  async function submit() {
    if (stopping.length > 0) {
      const names = stopping.map((dataClass) => dataClass.displayName).join(" and ");
      const agreed = await confirm({
        title: `Stop storing ${names}?`,
        message: `Writes for ${names} will FAIL until you send them somewhere. Stopping a kind of data does not send it back to this server's own disk — once a kind has been stored at a destination, there is no way back to the local home. What is already stored stays where it is and keeps opening.`,
        confirmLabel: "Stop storing them",
        destructive: true,
      });
      if (!agreed) return;
    }

    apply.mutate();
  }

  const footer = (
    <div className="mdl-foot">
      <span className="wf-form-help" style={{ maxWidth: "46ch" }}>Nothing already stored moves.</span>
      <span style={{ display: "flex", gap: 10 }}>
      <button type="button" className="btn" onClick={onClose}>Cancel</button>
      <button type="button" className="btn btn-primary" disabled={!changed || apply.isPending} onClick={submit}>
        {apply.isPending ? "Applying…" : "Apply"}
      </button>
      </span>
    </div>
  );

  return (
    <Frame title={`What lands in ${profile.stableName}`} onClose={onClose} footer={footer}>
      <p className="wf-form-help">Each choice moves where NEW writes go. Data already stored stays exactly where it is, and keeps opening.</p>

      <div className="wf-form" style={{ marginTop: 14 }}>
        {dataClasses.map((dataClass) => {
          const elsewhere = routes.find((route) => route.dataClassTypeKey === dataClass.typeKey && route.state === "Active" && route.storageProfileId !== profile.id);
          return (
            <label key={dataClass.typeKey} className="wf-form-row" style={{ flexDirection: "row", alignItems: "flex-start", gap: 9 }}>
              <input
                type="checkbox"
                checked={ticked.has(dataClass.typeKey)}
                onChange={(event) => setTicked((current) => {
                  const next = new Set(current);
                  if (event.target.checked) next.add(dataClass.typeKey); else next.delete(dataClass.typeKey);
                  return next;
                })}
              />
              <span>
                <span className="cn-name" style={{ fontSize: 13 }}>{dataClass.displayName}</span>
                <span className="wf-form-help" style={{ display: "block" }}>{consequence(dataClass, ticked.has(dataClass.typeKey), landing.has(dataClass.typeKey), elsewhere)}</span>
              </span>
            </label>
          );
        })}
      </div>

      {apply.error != null && (
        <div className="cn-banner cn-banner-err" role="alert" style={{ marginTop: 14 }}>
          <div className="cn-banner-p">{apply.error instanceof ApiError ? apply.error.message : "Something went wrong applying these choices."}</div>
          <div className="cn-banner-p">Anything already applied stayed applied. Reopen this to see where each kind stands now.</div>
        </div>
      )}

    </Frame>
  );
}

/** What this particular tick, or untick, will do — stated for the state the operator is putting it into. */
function consequence(dataClass: RoutedDataClass, ticked: boolean, landsHereNow: boolean, elsewhere: StorageRouteSummary | undefined): string {
  if (ticked && !landsHereNow) {
    return elsewhere
      ? `The next write moves here from ${elsewhere.storageProfileStableName}. What is already stored there stays there and keeps opening.`
      : dataClass.hasLocalFallback
        ? "The next write lands here instead of on this server's own disk. What is already there stays there and keeps opening."
        : "Not captured at all today. Ticking this is what starts capturing them.";
  }
  if (!ticked && landsHereNow) {
    // NOT the mirror of a tick: a stopped pointer fails closed for every class, local home or not.
    return "Writes for these will FAIL until you send them somewhere — stopping a kind does not send it back to this server's own disk. What is stored here stays here and keeps opening.";
  }
  return ticked ? "Lands here now." : dataClass.hasLocalFallback ? "Written to this server's own disk." : "Not captured at all.";
}

function Frame({ title, onClose, footer, children }: { title: string; onClose: () => void; footer: ReactNode; children: ReactNode }) {
  const surface = useRef<HTMLDivElement>(null);
  useDialogKeys(surface, onClose);

  return createPortal(
    <>
      <div className="mdl-mask" aria-hidden="true" onClick={onClose} />
      <div ref={surface} className="mdl" role="dialog" aria-modal="true" aria-label={title}>
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">{title}</div>
            <div className="mdl-sub">Nothing already stored moves.</div>
          </div>
          <button type="button" className="mdl-x" aria-label="Close" onClick={onClose}>&times;</button>
        </div>
        <div className="mdl-body">{children}</div>
        {footer}
      </div>
    </>,
    document.body,
  );
}
