import { createFileRoute } from "@tanstack/react-router";

import { ModelCredentialsPage } from "@/components/modelCredentials/ModelCredentialsPage";

/** Team Model Credentials settings — the keys agents authenticate model providers with. */
export const Route = createFileRoute("/_app/teams/$teamSlug/model-credentials")({
  component: ModelCredentialsPage,
});
