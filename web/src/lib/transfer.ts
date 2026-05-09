import i18n from "@/i18n"

export type UploadProgress = {
  loaded: number
  total: number
  percent: number
}

export type CompletedUploadPart = {
  partNumber: number
  eTag: string
}

export type MultipartUploadHandlers = {
  initiate: () => Promise<{ uploadId: string }>
  presignPart: (uploadId: string, partNumber: number) => Promise<{ url: string }>
  complete: (uploadId: string, parts: CompletedUploadPart[]) => Promise<void>
  abort: (uploadId: string) => Promise<void>
}

export const MULTIPART_UPLOAD_THRESHOLD_BYTES = 5 * 1024 * 1024
export const MULTIPART_PART_SIZE_BYTES = 16 * 1024 * 1024
export const MULTIPART_UPLOAD_CONCURRENCY = 3

// Uploads through the same-origin /s3 presigned URL so fetch credentials and S3
// signing details remain outside the UI components.
export function uploadWithProgress(
  url: string,
  file: File,
  onProgress: (progress: UploadProgress) => void
): Promise<void> {
  return uploadBlobWithProgress(url, file, file.type || "application/octet-stream", onProgress).then(() => undefined)
}

export async function multipartUploadWithProgress(
  file: File,
  handlers: MultipartUploadHandlers,
  onProgress: (progress: UploadProgress) => void,
  partSize = MULTIPART_PART_SIZE_BYTES,
  concurrency = MULTIPART_UPLOAD_CONCURRENCY
): Promise<void> {
  const total = file.size
  const partCount = Math.ceil(total / partSize)
  const loadedByPart = new Array<number>(partCount).fill(0)
  const completedParts = new Array<CompletedUploadPart>(partCount)
  let uploadId: string | null = null
  let nextPartIndex = 0

  const report = () => {
    const loaded = loadedByPart.reduce((sum, value) => sum + value, 0)
    onProgress({
      loaded,
      total,
      percent: total === 0 ? 100 : Math.min(100, Math.round((loaded / total) * 100)),
    })
  }

  try {
    const upload = await handlers.initiate()
    const activeUploadId = upload.uploadId
    uploadId = activeUploadId
    report()

    const worker = async () => {
      while (true) {
        const partIndex = nextPartIndex
        nextPartIndex += 1
        if (partIndex >= partCount) {
          return
        }

        const partNumber = partIndex + 1
        const start = partIndex * partSize
        const end = Math.min(start + partSize, total)
        const blob = file.slice(start, end)
        const transfer = await handlers.presignPart(activeUploadId, partNumber)
        const eTag = await uploadBlobWithProgress(
          transfer.url,
          blob,
          file.type || "application/octet-stream",
          (partProgress) => {
            loadedByPart[partIndex] = partProgress.loaded
            report()
          }
        )

        loadedByPart[partIndex] = blob.size
        completedParts[partIndex] = { partNumber, eTag }
        report()
      }
    }

    await Promise.all(
      Array.from({ length: Math.min(concurrency, partCount) }, () => worker())
    )
    await handlers.complete(activeUploadId, completedParts)
  } catch (error) {
    if (uploadId) {
      await handlers.abort(uploadId).catch(() => undefined)
    }

    throw error
  }
}

function uploadBlobWithProgress(
  url: string,
  blob: Blob,
  contentType: string,
  onProgress: (progress: UploadProgress) => void
): Promise<string> {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest()
    request.open("PUT", url)
    request.setRequestHeader("Content-Type", contentType)
    request.upload.onprogress = (event) => {
      if (!event.lengthComputable) {
        return
      }

      onProgress({
        loaded: event.loaded,
        total: event.total,
        percent: Math.round((event.loaded / event.total) * 100),
      })
    }
    request.onerror = () => reject(new Error(i18n.t("transfer.uploadConnectionFailed")))
    request.onload = () => {
      if (request.status >= 200 && request.status < 300) {
        const eTag = (request.getResponseHeader("ETag") ?? "").replace(/^"+|"+$/g, "")
        if (!eTag) {
          reject(new Error(i18n.t("transfer.uploadHttpFailed", { status: request.status })))
          return
        }

        resolve(eTag)
        return
      }

      reject(new Error(i18n.t("transfer.uploadHttpFailed", { status: request.status })))
    }
    request.send(blob)
  })
}

export function downloadFromUrl(url: string): void {
  const anchor = document.createElement("a")
  anchor.href = url
  anchor.rel = "noopener"
  anchor.click()
}
