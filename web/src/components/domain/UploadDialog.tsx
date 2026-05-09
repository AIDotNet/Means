import { useEffect, useState } from "react"
import { UploadCloudIcon } from "lucide-react"
import { useTranslation } from "@/i18n"

import { Button } from "@/components/ui/button"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Progress } from "@/components/ui/progress"
import { formatBytes } from "@/lib/formatters"

type UploadDialogProps = {
  open: boolean
  prefix: string
  uploading: boolean
  progress: number
  onOpenChange: (open: boolean) => void
  onUpload: (file: File, key: string) => void
}

export function UploadDialog({
  open,
  prefix,
  uploading,
  progress,
  onOpenChange,
  onUpload,
}: UploadDialogProps) {
  const { t } = useTranslation()
  const [file, setFile] = useState<File | null>(null)
  const [key, setKey] = useState(prefix)
  const [dragActive, setDragActive] = useState(false)

  useEffect(() => {
    if (open) {
      setFile(null)
      setKey(prefix)
      setDragActive(false)
    }
  }, [open, prefix])

  const selectFile = (nextFile: File | null) => {
    setFile(nextFile)
    if (nextFile) {
      setKey(prefix + nextFile.name)
    }
  }

  const dropFile = (event: React.DragEvent<HTMLLabelElement>) => {
    event.preventDefault()
    setDragActive(false)
    selectFile(event.dataTransfer.files.item(0))
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <UploadCloudIcon className="size-4 text-primary" />
            {t("uploadDialog.title")}
          </DialogTitle>
          <DialogDescription>{t("uploadDialog.description")}</DialogDescription>
        </DialogHeader>
        <label
          className={[
            "grid cursor-pointer place-items-center rounded-lg border border-dashed p-8 text-center transition-colors",
            dragActive ? "border-primary bg-primary/10" : "hover:bg-muted/40",
          ].join(" ")}
          onDragEnter={(event) => {
            event.preventDefault()
            setDragActive(true)
          }}
          onDragOver={(event) => event.preventDefault()}
          onDragLeave={() => setDragActive(false)}
          onDrop={dropFile}
        >
          <UploadCloudIcon className="mb-3 size-8 text-primary" />
          <span className="font-medium">{t("uploadDialog.dropzone.title")}</span>
          <span className="mt-1 text-sm text-muted-foreground">
            {file
              ? t("uploadDialog.dropzone.fileSelected", { name: file.name, size: formatBytes(file.size) })
              : t("uploadDialog.dropzone.emptyHint")}
          </span>
          <input
            className="sr-only"
            type="file"
            onChange={(event) => selectFile(event.target.files?.[0] ?? null)}
          />
        </label>
        <label className="grid gap-1.5 text-sm">
          {t("uploadDialog.objectKeyLabel")}
          <Input value={key} onChange={(event) => setKey(event.target.value)} />
        </label>
        {uploading ? <Progress value={progress} /> : null}
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)} disabled={uploading}>
            {t("common.actions.cancel")}
          </Button>
          <Button disabled={!file || !key || uploading} onClick={() => file && onUpload(file, key)}>
            {t("common.actions.upload")}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
