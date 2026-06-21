import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { modelCredentialsApi, type AddCredentialedModelInput, type AddModelCredentialInput, type CredentialedModelSummary, type UpdateModelCredentialInput } from "@/api/modelCredentials";

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
  modelId: string;
  credentialId: string;
  credentialName: string;
  provider: string;
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
          .then(models => models.filter(m => m.enabled).map(m => ({ modelId: m.modelId, credentialId: c.id, credentialName: c.displayName, provider: c.provider })))
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

/** A row in the model editor — `id` present means it already exists on the credential. */
export interface EditableModelRow { id?: string; modelId: string; displayName: string; }

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

        const orig = r.id ? original.find(o => o.id === r.id) : undefined;
        if (!orig) { toAdd.push({ modelId, displayName: r.displayName.trim() || null }); continue; }
        if (orig.modelId !== modelId || (orig.displayName ?? "") !== r.displayName.trim()) {
          toRemove.push(orig);
          toAdd.push({ modelId, displayName: r.displayName.trim() || null });
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
