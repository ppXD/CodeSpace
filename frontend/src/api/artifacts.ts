import { fetchResponse } from "./request";

/**
 * Downloads a stored artifact by id.
 *
 * <p>Fetched rather than linked: the API authenticates on the Authorization and X-Team-Id HEADERS, which a plain
 * anchor cannot carry — an `href` to the same URL returns 401, not the file.</p>
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
