import { useCallback, useEffect, useState } from "react"
import {
  CopyIcon,
  DownloadIcon,
  FileIcon,
  FolderIcon,
  MoreHorizontalIcon,
  RefreshCwIcon,
  Trash2Icon,
  UploadCloudIcon,
} from "lucide-react"
import { toast } from "sonner"

import { UploadDialog } from "@/components/domain/UploadDialog"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Checkbox } from "@/components/ui/checkbox"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import { Input } from "@/components/ui/input"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { useTranslation } from "@/i18n"
import { api, type BatchDeleteObjectItem, type ListedObject, type ListObjectsResult, type ObjectInfo } from "@/lib/api-client"
import { fileNameFromKey, formatBytes, formatDateTime } from "@/lib/formatters"
import {
  MULTIPART_UPLOAD_THRESHOLD_BYTES,
  downloadFromUrl,
  multipartUploadWithProgress,
  uploadWithProgress,
} from "@/lib/transfer"

type ObjectBrowserProps = {
  bucketName: string
}

export function ObjectBrowser({ bucketName }: ObjectBrowserProps) {
  const { t } = useTranslation()
  const [prefix, setPrefix] = useState("")
  const [query, setQuery] = useState("")
  const [result, setResult] = useState<ListObjectsResult | null>(null)
  const [selected, setSelected] = useState<ObjectInfo | null>(null)
  const [loading, setLoading] = useState(false)
  const [uploadOpen, setUploadOpen] = useState(false)
  const [uploading, setUploading] = useState(false)
  const [progress, setProgress] = useState(0)
  const [copySourceKey, setCopySourceKey] = useState<string | null>(null)
  const [copyDestinationKey, setCopyDestinationKey] = useState("")
  const [deleteTargetKey, setDeleteTargetKey] = useState<string | null>(null)
  const [selectedKeys, setSelectedKeys] = useState<Set<string>>(new Set())
  const [batchDeleteOpen, setBatchDeleteOpen] = useState(false)
  const [batchDeleting, setBatchDeleting] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const params = new URLSearchParams()
      params.set("prefix", prefix)
      params.set("delimiter", "/")
      params.set("maxKeys", "1000")
      setResult(await api.objects(bucketName, params))
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("objectBrowser.errors.loadFailed"))
    } finally {
      setLoading(false)
    }
  }, [bucketName, prefix, t])

  useEffect(() => {
    void load()
  }, [load])

  const objects = (result?.objects ?? []).filter((object) =>
    object.key.toLowerCase().includes(query.toLowerCase())
  )
  const prefixes = (result?.commonPrefixes ?? []).filter((nextPrefix) =>
    nextPrefix.toLowerCase().includes(query.toLowerCase())
  )

  const openObject = async (object: ListedObject) => {
    try {
      setSelected(await api.objectDetail(bucketName, object.key))
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("objectBrowser.errors.loadDetailFailed"))
    }
  }

  const uploadObject = async (file: File, key: string) => {
    setUploading(true)
    setProgress(0)
    try {
      if (file.size >= MULTIPART_UPLOAD_THRESHOLD_BYTES) {
        await multipartUploadWithProgress(
          file,
          {
            initiate: () => api.initiateMultipartUpload(bucketName, key, file.type || "application/octet-stream"),
            presignPart: (uploadId, partNumber) =>
              api.presignMultipartPart(bucketName, key, uploadId, partNumber),
            complete: (uploadId, parts) =>
              api.completeMultipartUpload(bucketName, key, uploadId, parts).then(() => undefined),
            abort: (uploadId) => api.abortMultipartUpload(bucketName, key, uploadId),
          },
          (nextProgress) => setProgress(nextProgress.percent)
        )
      } else {
        const transfer = await api.presignUpload(bucketName, key)
        await uploadWithProgress(transfer.url, file, (nextProgress) => setProgress(nextProgress.percent))
      }
      toast.success(t("objectBrowser.toast.uploaded"))
      setUploadOpen(false)
      await load()
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("objectBrowser.errors.uploadFailed"))
    } finally {
      setUploading(false)
    }
  }

  const downloadObject = async (key: string) => {
    try {
      const transfer = await api.presignDownload(bucketName, key)
      downloadFromUrl(transfer.url)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("objectBrowser.errors.presignDownloadFailed"))
    }
  }

  const copyObject = async () => {
    if (!copySourceKey || !copyDestinationKey) {
      return
    }

    try {
      await api.copyObject(bucketName, bucketName, copySourceKey, copyDestinationKey)
      toast.success(t("objectBrowser.toast.copied"))
      setCopySourceKey(null)
      setCopyDestinationKey("")
      await load()
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("objectBrowser.errors.copyFailed"))
    }
  }

  const deleteObject = async () => {
    if (!deleteTargetKey) {
      return
    }

    try {
      await api.deleteObject(bucketName, deleteTargetKey)
      toast.success(t("objectBrowser.toast.deleted"))
      setDeleteTargetKey(null)
      setSelected(null)
      await load()
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("objectBrowser.errors.deleteFailed"))
    }
  }

  const toggleKey = (key: string) => {
    setSelectedKeys((prev) => {
      const next = new Set(prev)
      if (next.has(key)) {
        next.delete(key)
      } else {
        next.add(key)
      }
      return next
    })
  }

  const toggleAll = () => {
    if (objects.length > 0 && selectedKeys.size === objects.length) {
      setSelectedKeys(new Set())
    } else {
      setSelectedKeys(new Set(objects.map((object) => object.key)))
    }
  }

  const confirmBatchDelete = async () => {
    if (selectedKeys.size === 0) {
      return
    }

    setBatchDeleting(true)
    try {
      const items: BatchDeleteObjectItem[] = Array.from(selectedKeys).map((key) => ({ key }))
      const result = await api.batchDeleteObjects(bucketName, items)
      if (result.errors.length > 0) {
        toast.warning(
          t("objectBrowser.toast.batchDeletedWithErrors", {
            deleted: result.deleted.length,
            errors: result.errors.length,
          })
        )
      } else {
        toast.success(t("objectBrowser.toast.batchDeleted", { count: result.deleted.length }))
      }
      setSelectedKeys(new Set())
      setBatchDeleteOpen(false)
      await load()
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("objectBrowser.errors.batchDeleteFailed"))
    } finally {
      setBatchDeleting(false)
    }
  }

  return (
    <section className="overflow-hidden rounded-lg border bg-card text-card-foreground shadow-xs">
      <div className="flex flex-col gap-3 border-b p-4 xl:flex-row xl:items-end">
        <label className="grid flex-1 gap-1.5 text-sm">
          {t("objectBrowser.filters.prefix")}
          <Input value={prefix} onChange={(event) => setPrefix(event.target.value)} placeholder="photos/2026/" />
        </label>
        <label className="grid flex-1 gap-1.5 text-sm">
          {t("objectBrowser.filters.search")}
          <Input
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder={t("objectBrowser.filters.searchPlaceholder")}
          />
        </label>
        <div className="flex gap-2">
          {prefix ? (
            <Button variant="outline" onClick={() => setPrefix(parentPrefix(prefix))}>
              {t("objectBrowser.actions.parent")}
            </Button>
          ) : null}
          <Button variant="outline" onClick={load} disabled={loading}>
            <RefreshCwIcon className={loading ? "animate-spin" : ""} />
            {t("common.actions.refresh")}
          </Button>
          {selectedKeys.size > 0 ? (
            <Button variant="destructive" onClick={() => setBatchDeleteOpen(true)}>
              <Trash2Icon />
              {t("objectBrowser.actions.batchDelete", { count: selectedKeys.size })}
            </Button>
          ) : null}
          <Button onClick={() => setUploadOpen(true)}>
            <UploadCloudIcon />
            {t("common.actions.upload")}
          </Button>
        </div>
      </div>

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead className="w-10">
              {objects.length > 0 ? (
                <Checkbox
                  checked={objects.length > 0 && selectedKeys.size === objects.length}
                  onCheckedChange={toggleAll}
                  aria-label={t("objectBrowser.actions.selectAll")}
                />
              ) : null}
            </TableHead>
            <TableHead>{t("objectBrowser.table.columns.objectKey")}</TableHead>
            <TableHead>{t("objectBrowser.table.columns.type")}</TableHead>
            <TableHead>{t("objectBrowser.table.columns.size")}</TableHead>
            <TableHead>{t("objectBrowser.table.columns.updatedAt")}</TableHead>
            <TableHead className="w-12" />
          </TableRow>
        </TableHeader>
        <TableBody>
          {prefixes.map((nextPrefix) => (
            <TableRow key={nextPrefix}>
              <TableCell />
              <TableCell>
                <button
                  className="flex min-w-0 items-center gap-2 font-medium text-primary"
                  onClick={() => setPrefix(nextPrefix)}
                >
                  <FolderIcon className="size-4" />
                  <span className="truncate">{folderName(nextPrefix)}</span>
                </button>
              </TableCell>
              <TableCell>
                <Badge variant="outline">{t("objectBrowser.table.folder")}</Badge>
              </TableCell>
              <TableCell className="text-muted-foreground">-</TableCell>
              <TableCell className="text-muted-foreground">-</TableCell>
              <TableCell />
            </TableRow>
          ))}
          {objects.map((object) => (
            <TableRow key={object.key}>
              <TableCell>
                <Checkbox
                  checked={selectedKeys.has(object.key)}
                  onCheckedChange={() => toggleKey(object.key)}
                  aria-label={t("objectBrowser.actions.selectObject", { objectKey: object.key })}
                />
              </TableCell>
              <TableCell>
                <button
                  className="flex min-w-0 items-center gap-2 text-left font-medium hover:text-primary"
                  onClick={() => openObject(object)}
                >
                  <FileIcon className="size-4 text-primary" />
                  <span className="truncate">{fileNameFromKey(object.key)}</span>
                </button>
                <div className="mt-1 truncate font-mono text-[11px] text-muted-foreground">{object.key}</div>
              </TableCell>
              <TableCell>
                <Badge variant="outline">{object.contentType || "application/octet-stream"}</Badge>
              </TableCell>
              <TableCell>{formatBytes(object.size)}</TableCell>
              <TableCell className="text-muted-foreground">{formatDateTime(object.lastModified)}</TableCell>
              <TableCell>
                <ObjectActions
                  objectKey={object.key}
                  onOpen={() => openObject(object)}
                  onDownload={() => downloadObject(object.key)}
                  onCopy={() => {
                    setCopySourceKey(object.key)
                    setCopyDestinationKey(object.key)
                  }}
                  onDelete={() => setDeleteTargetKey(object.key)}
                />
              </TableCell>
            </TableRow>
          ))}
          {loading && prefixes.length + objects.length === 0 ? (
            <TableRow>
              <TableCell colSpan={6} className="h-32 text-center text-muted-foreground">
                {t("objectBrowser.table.states.loading")}
              </TableCell>
            </TableRow>
          ) : null}
          {!loading && prefixes.length + objects.length === 0 ? (
            <TableRow>
              <TableCell colSpan={6} className="h-32 text-center text-muted-foreground">
                {t("objectBrowser.table.states.empty")}
              </TableCell>
            </TableRow>
          ) : null}
        </TableBody>
      </Table>

      <UploadDialog
        open={uploadOpen}
        prefix={prefix}
        uploading={uploading}
        progress={progress}
        onOpenChange={setUploadOpen}
        onUpload={uploadObject}
      />

      <ObjectDetailDialog
        object={selected}
        onOpenChange={(open) => !open && setSelected(null)}
        onDownload={(key) => downloadObject(key)}
        onCopy={(key) => {
          setCopySourceKey(key)
          setCopyDestinationKey(key)
        }}
        onDelete={(key) => setDeleteTargetKey(key)}
      />

      <Dialog open={copySourceKey !== null} onOpenChange={(open) => !open && setCopySourceKey(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t("objectBrowser.copyDialog.title")}</DialogTitle>
            <DialogDescription>{t("objectBrowser.copyDialog.description")}</DialogDescription>
          </DialogHeader>
          <label className="grid gap-1.5 text-sm">
            {t("objectBrowser.copyDialog.destinationKey")}
            <Input value={copyDestinationKey} onChange={(event) => setCopyDestinationKey(event.target.value)} />
          </label>
          <DialogFooter>
            <Button variant="outline" onClick={() => setCopySourceKey(null)}>
              {t("common.actions.cancel")}
            </Button>
            <Button disabled={!copyDestinationKey} onClick={copyObject}>
              {t("common.actions.copy")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <AlertDialog open={deleteTargetKey !== null} onOpenChange={(open) => !open && setDeleteTargetKey(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t("objectBrowser.deleteDialog.title")}</AlertDialogTitle>
            <AlertDialogDescription>{t("objectBrowser.deleteDialog.description")}</AlertDialogDescription>
          </AlertDialogHeader>
          <div className="rounded-md border bg-muted/40 px-3 py-2 font-mono text-sm break-all">
            {deleteTargetKey}
          </div>
          <AlertDialogFooter>
            <AlertDialogCancel>{t("common.actions.cancel")}</AlertDialogCancel>
            <AlertDialogAction variant="destructive" onClick={deleteObject}>
              {t("common.actions.delete")}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      <AlertDialog open={batchDeleteOpen} onOpenChange={(open) => !open && setBatchDeleteOpen(false)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t("objectBrowser.batchDeleteDialog.title")}</AlertDialogTitle>
            <AlertDialogDescription>
              {t("objectBrowser.batchDeleteDialog.description", { count: selectedKeys.size })}
            </AlertDialogDescription>
          </AlertDialogHeader>
          <div className="max-h-48 overflow-y-auto rounded-md border bg-muted/40 p-2">
            {Array.from(selectedKeys).map((key) => (
              <div key={key} className="px-1 py-0.5 font-mono text-xs break-all">
                {key}
              </div>
            ))}
          </div>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={batchDeleting}>{t("common.actions.cancel")}</AlertDialogCancel>
            <AlertDialogAction
              variant="destructive"
              disabled={batchDeleting}
              onClick={confirmBatchDelete}
            >
              {batchDeleting
                ? t("objectBrowser.batchDeleteDialog.deleting")
                : t("common.actions.delete")}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </section>
  )
}

function ObjectActions({
  objectKey,
  onOpen,
  onDownload,
  onCopy,
  onDelete,
}: {
  objectKey: string
  onOpen: () => void
  onDownload: () => void
  onCopy: () => void
  onDelete: () => void
}) {
  const { t } = useTranslation()

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="icon-sm">
          <MoreHorizontalIcon />
          <span className="sr-only">{t("objectBrowser.actions.menuAria", { objectKey })}</span>
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end">
        <DropdownMenuItem onClick={onOpen}>{t("objectBrowser.actions.viewDetails")}</DropdownMenuItem>
        <DropdownMenuItem onClick={onDownload}>
          <DownloadIcon />
          {t("common.actions.download")}
        </DropdownMenuItem>
        <DropdownMenuItem onClick={onCopy}>
          <CopyIcon />
          {t("common.actions.copy")}
        </DropdownMenuItem>
        <DropdownMenuItem variant="destructive" onClick={onDelete}>
          <Trash2Icon />
          {t("common.actions.delete")}
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

function ObjectDetailDialog({
  object,
  onOpenChange,
  onDownload,
  onCopy,
  onDelete,
}: {
  object: ObjectInfo | null
  onOpenChange: (open: boolean) => void
  onDownload: (key: string) => void
  onCopy: (key: string) => void
  onDelete: (key: string) => void
}) {
  const { t } = useTranslation()

  return (
    <Dialog open={object !== null} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{t("objectBrowser.detail.title")}</DialogTitle>
          <DialogDescription className="break-all font-mono">{object?.key}</DialogDescription>
        </DialogHeader>
        {object ? (
          <div className="grid gap-3 text-sm">
            <DetailRow label="ETag" value={object.eTag} mono />
            <DetailRow label="Content-Type" value={object.contentType} />
            <DetailRow label={t("objectBrowser.detail.size")} value={formatBytes(object.contentLength)} />
            <DetailRow label={t("objectBrowser.detail.lastModified")} value={formatDateTime(object.lastModified)} />
            <div className="rounded-md border bg-muted/25">
              <div className="border-b px-3 py-2 text-xs font-semibold text-muted-foreground uppercase">
                {t("objectBrowser.detail.metadata")}
              </div>
              {Object.entries(object.metadata).length > 0 ? (
                Object.entries(object.metadata).map(([name, value]) => (
                  <DetailRow key={name} label={name} value={value} mono />
                ))
              ) : (
                <div className="px-3 py-3 text-sm text-muted-foreground">{t("objectBrowser.detail.emptyMetadata")}</div>
              )}
            </div>
          </div>
        ) : null}
        <DialogFooter>
          <Button variant="outline" onClick={() => object && onDownload(object.key)}>
            <DownloadIcon />
            {t("common.actions.download")}
          </Button>
          <Button variant="outline" onClick={() => object && onCopy(object.key)}>
            <CopyIcon />
            {t("common.actions.copy")}
          </Button>
          <Button variant="destructive" onClick={() => object && onDelete(object.key)}>
            <Trash2Icon />
            {t("common.actions.delete")}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function DetailRow({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="grid gap-1 border-b px-3 py-2 last:border-b-0 sm:grid-cols-[9rem_1fr]">
      <div className="text-xs font-semibold text-muted-foreground uppercase">{label}</div>
      <div className={mono ? "break-all font-mono text-xs" : "break-all"}>{value}</div>
    </div>
  )
}

function folderName(value: string) {
  return value.split("/").filter(Boolean).at(-1) ?? value
}

function parentPrefix(value: string) {
  const parts = value.split("/").filter(Boolean)
  parts.pop()
  return parts.length > 0 ? `${parts.join("/")}/` : ""
}
