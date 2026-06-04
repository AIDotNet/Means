import { getObjectFromUrl, putObjectToUrl } from "@means/sdk";

type PresignResponse = {
  putUrl: string;
  getUrl: string;
};

export async function uploadWithPresignedUrls(file: File): Promise<Blob> {
  const presignResponse = await fetch("/api/storage/presign", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      key: `browser/${crypto.randomUUID()}-${file.name}`,
      contentType: file.type || "application/octet-stream",
    }),
  });

  if (!presignResponse.ok) {
    throw new Error(`Unable to presign upload: ${presignResponse.status}`);
  }

  const { putUrl, getUrl } = (await presignResponse.json()) as PresignResponse;

  await putObjectToUrl(putUrl, file, {
    contentType: file.type || "application/octet-stream",
  });

  const downloaded = await getObjectFromUrl(getUrl);
  return downloaded.response.blob();
}
