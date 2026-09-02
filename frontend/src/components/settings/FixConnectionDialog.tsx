import { useMemo, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { useMutation } from "@tanstack/react-query";

import { ApiError } from "@/api/request";
import { storageApi, type StorageCredentialMetadata, type StorageProfileProbeFailure, type StorageProfileSummary, type StorageProviderModuleSummary } from "@/api/storage";
import { useAppendStorageCredentialRevision, useAppendStorageProfileRevision, useStorageProfile } from "@/hooks/use-storage";
import { credentialForRef } from "@/lib/storageCredentialRef";
import { SchemaForm } from "@/components/workflows/SchemaForm";

import { probeFailureGuidance, probeFailureReference } from "./storageProbeGuidance";

/**
 * Repairing a destination in place.
 *
 * The thing this replaces is the reason it exists. A wrong key was always repairable — append a new key version, then
 * point the destination at it — but nothing on the old screen said so, and neither step alone does anything: the
 * pointer names an EXACT key version and never falls forward, so rotating the key without repointing changes nothing
 * at runtime. Operators therefore rebuilt the whole destination instead, which is how a team ends up with retired
 * profiles and revoked keys it can never remove.
 *
 * Here it is one form and one button. A new key is tested against the real destination before either write happens.
 * Leaving the key blank keeps the one already stored and tests the destination as it stands, which is the right shape
 * when it was the address that was wrong.
 */
export function FixConnectionDialog({ profile, provider, credentials, onClose }: {
  profile: StorageProfileSummary;
  provider: StorageProviderModuleSummary | undefined;
  credentials: StorageCredentialMetadata[];
  onClose: () => void;
}) {
  const detail = useStorageProfile(profile.id);
  const current = detail.data?.revisions.find((candidate) => candidate.revision === profile.currentRevision);
  // Resolved from the pointer this destination actually names, never from whichever key of the right provider
  // happens to be active - replacing the wrong key is how a working destination gets broken.
  const credential = useMemo(() => credentialForRef(current?.credentialRef, credentials), [current?.credentialRef, credentials]);
  const storedConfig = useMemo(() => (isRecord(current?.nonSecretConfig) ? { ...(current!.nonSecretConfig as Record<string, unknown>) } : {}), [current?.nonSecretConfig]);

  const [config, setConfig] = useState<Record<string, unknown> | null>(null);
  const [secret, setSecret] = useState<Record<string, unknown>>({});
  const [qualified, setQualified] = useState(false);
  const [failure, setFailure] = useState<StorageProfileProbeFailure | null>(null);

  const appendKey = useAppendStorageCredentialRevision();
  const appendDestination = useAppendStorageProfileRevision();

  const edited = config ?? storedConfig;
  const replacingKey = Object.values(secret).some((value) => typeof value === "string" && value.length > 0);
  const configChanged = JSON.stringify(edited) !== JSON.stringify(storedConfig);

  const test = useMutation({
    // A replacement key can be qualified without saving it. Keeping the stored one, there is nothing unsaved to
    // qualify, so the destination as it stands is what gets asked — which is the question when the address was wrong.
    mutationFn: async () => replacingKey
      ? await storageApi.probeConfiguration({ providerTypeKey: profile.providerTypeKey, nonSecretConfig: edited, secret })
      : await storageApi.probeProfile(profile.id, { profileRevision: null, verifyWriteAccess: true }),
    onSuccess: (result) => {
      setQualified(result.status === "Available");
      setFailure(result.status === "Available" ? null : result.failure ?? null);
    },
  });

  const save = useMutation({
    mutationFn: async () => {
      let credentialRef = current?.credentialRef ?? null;

      if (replacingKey) {
        if (!credential) throw new Error("This destination has no key to replace.");
        const rotated = await appendKey.mutateAsync({
          credentialId: credential.id,
          input: {
            expectedXmin: credential.xmin,
            expectedCurrentRevision: credential.currentRevision,
            providerTypeKey: profile.providerTypeKey,
            secret,
            safeHint: credential.safeHint ?? undefined,
          },
        });
        credentialRef = rotated.credentialRef;
      }

      // The second half, and the one nothing else does for you: the destination has to be pointed at the new key
      // version, or the runtime keeps resolving the old one.
      await appendDestination.mutateAsync({
        profileId: profile.id,
        input: {
          expectedXmin: profile.xmin,
          expectedCurrentRevision: profile.currentRevision,
          providerTypeKey: profile.providerTypeKey,
          nonSecretConfig: edited,
          credentialRef: credentialRef ?? undefined,
        },
      });
    },
    onSuccess: onClose,
  });

  const nothingToDo = !replacingKey && !configChanged;

  const footer = (
    <div className="mdl-foot">
      <span className="wf-form-help" style={{ maxWidth: "46ch" }}>Nothing is written until the destination answers.</span>
      <span style={{ display: "flex", gap: 10 }}>
      <button type="button" className="btn" onClick={onClose}>Cancel</button>
      <button type="button" className="btn" disabled={test.isPending || !current} onClick={() => test.mutate()}>
        {test.isPending ? "Testing…" : "Test connection"}
      </button>
      <button type="button" className="btn btn-primary" disabled={!qualified || nothingToDo || save.isPending} onClick={() => save.mutate()}>
        {save.isPending ? "Saving…" : "Save"}
      </button>
      </span>
    </div>
  );

  return (
    <Frame title={`Fix ${profile.stableName}`} subtitle="Test first. Nothing is written until it answers." onClose={onClose} footer={footer}>
      {detail.isLoading && <span className="wf-form-help">Loading&hellip;</span>}

      {current && (
        <div className="wf-form">
          {provider && <SchemaForm schema={provider.configSchema} value={edited} onChange={(next) => { setConfig(next); setQualified(false); }} />}
          {provider && takesSecret(provider) && (
            <>
              <SchemaForm schema={provider.secretSchema} value={secret} onChange={(next) => { setSecret(next); setQualified(false); }} sensitive />
              <span className="wf-form-help">Leave the key fields empty to keep the key already stored. Filling them replaces it &mdash; the old one is kept, because data already stored here still opens through it.</span>
            </>
          )}
        </div>
      )}

      {failure && (
        <div className="cn-banner cn-banner-err" role="alert" style={{ marginTop: 14 }}>
          <div className="cn-banner-h">That didn&rsquo;t work.</div>
          <div className="cn-banner-p">{probeFailureGuidance(failure.code) ?? "The destination did not answer."}</div>
          <div className="cn-banner-p">Reported as {probeFailureReference(failure)}. Nothing was changed.</div>
        </div>
      )}

      {qualified && (
        <div className="cn-banner" role="status" style={{ marginTop: 14 }}>
          <div className="cn-banner-h">It answered.</div>
          <div className="cn-banner-p">{nothingToDo ? "Nothing needs changing — this destination is working as it stands." : "Save to point this destination at what you just tested."}</div>
        </div>
      )}

      {test.error instanceof ApiError && <Refusal>{test.error.message}</Refusal>}
      {save.error instanceof ApiError && <Refusal>{save.error.message}</Refusal>}
      {save.error != null && !(save.error instanceof ApiError) && <Refusal>{String((save.error as { message?: unknown }).message ?? "The change could not be saved.")}</Refusal>}

    </Frame>
  );
}

function Frame({ title, subtitle, onClose, footer, children }: { title: string; subtitle: string; onClose: () => void; footer: ReactNode; children: ReactNode }) {
  return createPortal(
    <>
      <div className="mdl-mask" aria-hidden="true" onClick={onClose} />
      <div className="mdl" role="dialog" aria-modal="true" aria-label={title}>
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">{title}</div>
            <div className="mdl-sub">{subtitle}</div>
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

function Refusal({ children }: { children: ReactNode }) {
  return (
    <div className="cn-banner cn-banner-err" role="alert" style={{ marginTop: 14 }}>
      <div className="cn-banner-p">{children}</div>
    </div>
  );
}

function takesSecret(provider: StorageProviderModuleSummary): boolean {
  return isRecord(provider.secretSchema) && isRecord(provider.secretSchema.properties) && Object.keys(provider.secretSchema.properties).length > 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
