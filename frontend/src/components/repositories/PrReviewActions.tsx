import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { ApiError } from "@/api/request";
import type { PullRequestReviewVerdict } from "@/api/types";
import { useActorIdentityGate } from "@/components/identities/ActorIdentityGate";
import { useSubmitPullRequestReview } from "@/hooks/use-repositories";

const VERDICTS: { value: PullRequestReviewVerdict; label: string }[] = [
  { value: "Approve", label: "Approve" },
  { value: "Comment", label: "Comment" },
  { value: "RequestChanges", label: "Request changes" },
];

/**
 * "Review" sidebar action on the PR detail page. Submits a verdict + optional comment back to the
 * provider AS the caller's own linked identity (Model B). If they haven't linked one, the backend's
 * 428 actor_identity_required is caught by the global {@link useActorIdentityGate}, which prompts a
 * link and then retries this exact submission — so review-as-me works on the click after linking.
 */
export function PrReviewActions({ repoId, number }: { repoId: string; number: number }) {
  const [verdict, setVerdict] = useState<PullRequestReviewVerdict>("Approve");
  const [body, setBody] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [submitted, setSubmitted] = useState(false);
  const gate = useActorIdentityGate();
  const review = useSubmitPullRequestReview(repoId, number);

  const needsBody = verdict !== "Approve";
  const canSubmit = !review.isPending && (!needsBody || body.trim() !== "");

  const submit = () => {
    if (!canSubmit) return;

    setError(null);
    review.mutate(
      { verdict, body: body.trim() === "" ? null : body.trim() },
      {
        onSuccess: () => { setSubmitted(true); setBody(""); },
        // Hand a 428 actor_identity_required to the gate; on a successful link it retries this exact
        // submission. Any other error surfaces inline.
        onError: (e) => { if (!gate.prompt(e, submit)) setError(e instanceof ApiError ? e.message : "Couldn't submit the review."); },
      },
    );
  };

  return (
    <div className="prd-side-block prd-review">
      <div className="prd-side-h">Review</div>

      <div className="prd-review-opts" role="radiogroup" aria-label="Review verdict">
        {VERDICTS.map(v => (
          <button
            key={v.value}
            type="button"
            role="radio"
            aria-checked={verdict === v.value}
            className={`prd-review-opt${verdict === v.value ? " is-active" : ""}`}
            onClick={() => { setVerdict(v.value); setSubmitted(false); }}
          >
            {v.label}
          </button>
        ))}
      </div>

      <textarea
        className="prd-review-body"
        value={body}
        onChange={(e) => { setBody(e.target.value); setSubmitted(false); }}
        placeholder={needsBody ? "Add a comment (required)…" : "Add an optional comment…"}
        rows={3}
      />

      {error && <div className="prd-review-msg prd-review-msg-err">{error}</div>}
      {submitted && !error && <div className="prd-review-msg prd-review-msg-ok"><Ic.Check size={12} /> Submitted as you</div>}

      <button className="btn btn-primary prd-review-submit" onClick={submit} disabled={!canSubmit}>
        {review.isPending ? "Submitting…" : "Submit review"}
      </button>
    </div>
  );
}
