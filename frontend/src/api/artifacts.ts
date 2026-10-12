import { fetchResponse } from "./request";

/**
 * Downloads a stored artifact by id.
 *
 * <p>Fetched rather than linked: the team scope is the X-Team-Id HEADER, which a plain anchor cannot carry, and a
 * browser still on its pre-cookie bearer token authenticates by a header too.</p>
 */
export async function downloadArtifact(artifactId: string, fileName: string): Promise<void> {
  const response = await fetchResponse(`/api/artifacts/${encodeURIComponent(artifactId)}`);
  const url = URL.createObjectURL(await response.blob());
  const anchor = document.createElement("a");

  anchor.href = url;
  anchor.download = fileName;
  anchor.click();
  URL.revokeObjectURL(url);
}
