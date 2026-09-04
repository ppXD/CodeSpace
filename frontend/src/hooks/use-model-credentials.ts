import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { MAX_PRICE_PER_MILLION_USD, modelCredentialsApi, type AddCredentialedModelInput, type AddModelCredentialInput, type CredentialedModelSummary, type ModelPriceInput, type UpdateModelCredentialInput } from "@/api/modelCredentials";

const MODEL_CREDENTIALS_KEY = ["model-credentials"] as const;

/** The team's model credentials (secret-free summaries). Optional provider filter; the broader key prefix
 *  means every mutation below refreshes both the filtered and unfiltered views. */
export function useModelCredentials(provider?: string) {
  return useQuery({
    queryKey: provider ? [...MODEL_CREDENTIALS_KEY, provider] : MODEL_CREDENTIALS_KEY,
    queryFn: () => modelCredentialsApi.list(provider),
  });
}

/** One pickable model, resolved from the team's model credentials (not a hardcoded harness list). */
export interface CredentialedModelOption {
  /** The credentialed-model ROW id (a ModelCredentialModel id) — the unambiguous (credential, model) handle the
   *  backend's model pool keys on (two credentials exposing the same model name are distinct rows). */
  rowId: string;
  modelId: string;
  credentialId: string;
  credentialName: string;
  provider: string;
  /** The EFFECTIVE capability tier (probed ?? brain), surfaced as a hint so the operator sees how auto ranks it. Null = un-tiered. */
  tier?: "Unknown" | "Basic" | "Strong" | "Frontier" | null;
  /** Endpoint reachability — false = a self-hosted gateway auto avoids; the picker shows it as offline. */
  available?: boolean | null;
}

/**
 * Every enabled model the team's model credentials expose, flattened across credentials. Drives the
 * launch composer's Model picker — selecting one pins both the model id and the owning credential.
 */
export function useCredentialedModels() {
  return useQuery({
    queryKey: ["credentialed-models"],
    queryFn: async (): Promise<CredentialedModelOption[]> => {
      const creds = await modelCredentialsApi.list();
      const lists = await Promise.all(creds.map(c =>
        modelCredentialsApi.listModels(c.id)
          .then(models => models.filter(m => m.enabled).map(m => ({ rowId: m.id, modelId: m.modelId, credentialId: c.id, credentialName: c.displayName, provider: c.provider, tier: m.probedCapabilityTier ?? m.capabilityTier, available: m.available })))
          .catch(() => [] as CredentialedModelOption[]),
      ));
      return lists.flat();
    },
    staleTime: 60_000,
  });
}

/** One credential's maintained model list (for the per-credential management surface). */
export function useCredentialedModelList(credentialId: string) {
  return useQuery({
    queryKey: ["model-credentials", credentialId, "models"],
    queryFn: () => modelCredentialsApi.listModels(credentialId),
    enabled: !!credentialId,
  });
}

/** Invalidate both a credential's own model list and the flattened pool the launch picker reads. */
function useInvalidateCredentialModels(credentialId: string) {
  const queryClient = useQueryClient();
  return () => {
    queryClient.invalidateQueries({ queryKey: ["model-credentials", credentialId, "models"] });
    queryClient.invalidateQueries({ queryKey: ["credentialed-models"] });
  };
}

export function useAddCredentialedModel(credentialId: string) {
  const invalidate = useInvalidateCredentialModels(credentialId);
  return useMutation({
    mutationFn: (input: AddCredentialedModelInput) => modelCredentialsApi.addModel(credentialId, input),
    onSuccess: invalidate,
  });
}

export function useRemoveCredentialedModel(credentialId: string) {
  const invalidate = useInvalidateCredentialModels(credentialId);
  return useMutation({
    mutationFn: (modelRowId: string) => modelCredentialsApi.removeModel(credentialId, modelRowId),
    onSuccess: invalidate,
  });
}

export function useRefreshCredentialedModels(credentialId: string) {
  const invalidate = useInvalidateCredentialModels(credentialId);
  return useMutation({
    mutationFn: () => modelCredentialsApi.refreshModels(credentialId),
    onSuccess: invalidate,
  });
}

export function useSetDefaultCredentialedModel(credentialId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (modelRowId: string) => modelCredentialsApi.setDefaultModel(credentialId, modelRowId),
    // Invalidate ONLY the flattened launch-picker pool — NOT the per-credential list. Refetching the latter would
    // rebuild the open editor's rows from the server, wiping any unsaved model-id/display-name edits; the modal flips
    // the star OPTIMISTICALLY instead, and the per-credential list re-syncs on the next modal open.
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["credentialed-models"] }),
  });
}

/**
 * Price one model row, or clear its price (both blank). Applied IMMEDIATELY, like the default star — the price is
 * what makes a run's cost cap enforceable for this model, so it should not wait behind an unrelated Save.
 */
export function useSetCredentialedModelPrice(credentialId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ modelRowId, input }: { modelRowId: string; input: ModelPriceInput }) =>
      modelCredentialsApi.setModelPrice(credentialId, modelRowId, input),
    // Same reasoning as useSetDefaultCredentialedModel: invalidate ONLY the flattened pool, never the per-credential
    // list the open editor is bound to — refetching it would rebuild the rows and wipe unsaved model-id edits.
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["credentialed-models"] }),
  });
}

/** A row in the model editor — `id` present means it already exists on the credential. */
export interface EditableModelRow { id?: string; modelId: string; displayName: string; inputUsdPerMillion?: string; outputUsdPerMillion?: string; }

/**
 * Reconcile an edited set of model rows against the credential's current models. There is no update
 * endpoint, so a changed row is a remove-then-add, a dropped row is a delete, and a brand-new row is an add.
 */
export function useSaveCredentialedModels(credentialId: string) {
  const invalidate = useInvalidateCredentialModels(credentialId);
  return useMutation({
    mutationFn: async ({ original, rows }: { original: CredentialedModelSummary[]; rows: EditableModelRow[] }) => {
      const kept = new Set(rows.map(r => r.id).filter((id): id is string => !!id));
      const toRemove = original.filter(o => !kept.has(o.id));
      const toAdd: AddCredentialedModelInput[] = [];

      for (const r of rows) {
        const modelId = r.modelId.trim();
        if (!modelId) continue;

        // The row's price rides along on every add: a rename is a remove-then-add, and dropping the price there
        // would silently un-enforce the cost cap the operator had just made enforceable.
        //
        // A HALF price is dropped rather than sent. The backend rejects one-sided prices (ModelPrice.FromNullable),
        // and remove+add go out in ONE Promise.all — so sending it would delete the row and then 500 on the add,
        // destroying a model the operator only meant to rename. Renaming with a half-filled price now keeps the
        // model and simply carries no price, which is the state it was already in.
        const price = completePrice(r);

        const orig = r.id ? original.find(o => o.id === r.id) : undefined;
        if (!orig) { toAdd.push({ modelId, displayName: r.displayName.trim() || null, ...price }); continue; }
        if (orig.modelId !== modelId || (orig.displayName ?? "") !== r.displayName.trim()) {
          toRemove.push(orig);
          toAdd.push({ modelId, displayName: r.displayName.trim() || null, ...price });
        }
      }

      await Promise.all([
        ...toRemove.map(o => modelCredentialsApi.removeModel(credentialId, o.id)),
        ...toAdd.map(m => modelCredentialsApi.addModel(credentialId, m)),
      ]);
    },
    onSuccess: invalidate,
  });
}

/**
 * The row's price as the API accepts it: BOTH sides set, or NEITHER. Half a price prices nothing and the backend
 * rejects it outright, so a one-sided row carries no price rather than failing the whole save.
 */
export function completePrice(row: { inputUsdPerMillion?: string; outputUsdPerMillion?: string }): ModelPriceInput {
  const input = parsePrice(row.inputUsdPerMillion);
  const output = parsePrice(row.outputUsdPerMillion);

  return input === null || output === null
    ? { inputUsdPerMillion: null, outputUsdPerMillion: null }
    : { inputUsdPerMillion: input, outputUsdPerMillion: output };
}

/**
 * What is wrong with either typed price field, or null when both are acceptable (blank included). Blank is not an
 * error — it means unpriced. Anything typed must be a non-negative number the engine can actually price a call
 * with, so the operator learns at the edit rather than from a run that refuses to start.
 */
export function priceFieldIssue(row: { inputUsdPerMillion?: string; outputUsdPerMillion?: string }): string | null {
  return fieldIssue(row.inputUsdPerMillion, "$/M in") ?? fieldIssue(row.outputUsdPerMillion, "$/M out");
}

function fieldIssue(raw: string | undefined, label: string): string | null {
  const trimmed = (raw ?? "").trim();
  if (!trimmed) return null;

  const value = Number(trimmed);

  if (!Number.isFinite(value)) return `${label} must be a number.`;
  if (value < 0) return `${label} cannot be negative.`;
  if (value > MAX_PRICE_PER_MILLION_USD) return `${label} is too large — the most a model can cost is $${MAX_PRICE_PER_MILLION_USD.toLocaleString()} per million tokens.`;

  return null;
}

/** A blank / unparseable price field is "unpriced" (null), never 0 — a $0 model would read as free and defeat the cap. */
export function parsePrice(raw: string | undefined): number | null {
  const trimmed = (raw ?? "").trim();
  if (!trimmed) return null;

  const value = Number(trimmed);
  return Number.isFinite(value) && value >= 0 && value <= MAX_PRICE_PER_MILLION_USD ? value : null;
}

/** Add input plus an optional set of models to seed onto the new credential in one user action. */
export type AddModelCredentialWithModelsInput = AddModelCredentialInput & { models?: AddCredentialedModelInput[] };

export function useAddModelCredential() {
  const queryClient = useQueryClient();
  return useMutation({
    // Create the credential, then best-effort seed any models the operator typed (each its own row, so one
    // failing model never rolls back the credential — it can be re-added from the manager afterwards).
    mutationFn: async ({ models, ...credential }: AddModelCredentialWithModelsInput) => {
      const created = await modelCredentialsApi.add(credential);
      const valid = (models ?? []).filter(m => m.modelId.trim() !== "");
      if (valid.length) await Promise.allSettled(valid.map(m => modelCredentialsApi.addModel(created.id, m)));
      return created;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: MODEL_CREDENTIALS_KEY });
      queryClient.invalidateQueries({ queryKey: ["credentialed-models"] });
    },
  });
}

export function useUpdateModelCredential() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, input }: { id: string; input: UpdateModelCredentialInput }) => modelCredentialsApi.update(id, input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: MODEL_CREDENTIALS_KEY }),
  });
}

export function useRevokeModelCredential() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => modelCredentialsApi.revoke(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: MODEL_CREDENTIALS_KEY }),
  });
}
