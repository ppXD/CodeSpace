import type { StorageProfileProbeFailure, StorageProfileProbeFailureCode } from "@/api/storage";

/**
 * What an operator should DO about a probe answer, in one sentence.
 *
 * Shared by every screen that shows one, because the same fault must not read as two different problems depending on
 * where it was noticed. The closed code is still shown alongside — it is what makes a support conversation possible —
 * but it is never the whole message: "ProbeSignatureMismatch" tells an operator nothing about which of the four
 * fields they should look at.
 */
export function probeFailureGuidance(code: StorageProfileProbeFailureCode): string | null {
  switch (code) {
    case "ProbeCredentialInvalid": return "The provider does not recognize this AccessKey ID. Check it for a typo, or use a different key.";
    case "ProbeSignatureMismatch": return "The provider rejected the request signature. That is usually the wrong AccessKey secret, or an endpoint and region that don't match each other. Re-enter the secret and check the endpoint.";
    case "ProbeSecurityTokenInvalid": return "The STS security token is not valid.";
    case "ProbeSecurityTokenExpired": return "The STS security token has expired. Temporary keys need a fresh one.";
    case "ProbeSecurityTokenMissing": return "This is a temporary AccessKey, so it also needs an STS security token.";
    case "ProbeClockSkew": return "The provider rejected the signing time. Check this server's clock — the SDK already tried to correct for skew.";
    case "ProbeDestinationMissing": return "The bucket does not exist, or is not reachable at this endpoint. Check both.";
    case "ProbePermissionDenied": return "The key is valid, but its policy does not allow writing here. Check the policy's resource path and its prefix condition.";
    case "ProbeForbidden": return "The key is valid, but it is not allowed to do this here.";
    case "ProbeNetworkUnavailable": return "The endpoint could not be reached at all. Check DNS, TLS, proxy and network routing.";
    case "ProbeThrottled": return "The provider is rate-limiting this key right now. Trying again shortly usually works.";
    case "ConfigurationInvalid": return "These connection details are not a shape this provider accepts. Check every field.";
    case "CredentialSecretInvalid": return "These key fields are not a shape this provider accepts.";
    case "CredentialMissing": return "This provider needs a key, and none was given.";
    case "ProviderModuleMissing": return "This deployment does not have that provider installed.";
    default: return null;
  }
}

/** The closed code as an operator sees it quoted: stage and code, nothing else. Provider text never reaches here. */
export function probeFailureReference(failure: StorageProfileProbeFailure): string {
  return `${failure.stage} / ${failure.code}`;
}
