import { useCallback, useEffect, useState } from "react"
import { PlusIcon, RefreshCwIcon } from "lucide-react"
import { toast } from "sonner"

import { BucketTable } from "@/components/domain/BucketTable"
import { PageHeader } from "@/components/layout/PageHeader"
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
import { api, type BucketUsage } from "@/lib/api-client"
import { useTranslation } from "@/i18n"

type BucketsPageProps = {
  onOpenBucket: (bucketName: string) => void
}

export function BucketsPage({ onOpenBucket }: BucketsPageProps) {
  const { t } = useTranslation()
  const [buckets, setBuckets] = useState<BucketUsage[]>([])
  const [open, setOpen] = useState(false)
  const [bucketName, setBucketName] = useState("")
  const [loading, setLoading] = useState(false)
  const [creating, setCreating] = useState(false)
  const [deleteTarget, setDeleteTarget] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setBuckets(await api.buckets())
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("buckets.errors.loadFailed"))
    } finally {
      setLoading(false)
    }
  }, [t])

  useEffect(() => {
    void load()
  }, [load])

  const create = async () => {
    setCreating(true)
    try {
      await api.createBucket(bucketName)
      toast.success(t("buckets.toast.created"))
      setOpen(false)
      setBucketName("")
      await load()
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("buckets.errors.createFailed"))
    } finally {
      setCreating(false)
    }
  }

  const remove = async () => {
    if (!deleteTarget) {
      return
    }

    try {
      await api.deleteBucket(deleteTarget)
      toast.success(t("buckets.toast.deleted"))
      setDeleteTarget(null)
      await load()
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("buckets.errors.deleteFailed"))
    }
  }

  return (
    <>
      <PageHeader
        eyebrow={t("buckets.page.eyebrow")}
        title={t("buckets.page.title")}
        description={t("buckets.page.description")}
        actions={
          <>
            <Button variant="outline" onClick={load} disabled={loading}>
              <RefreshCwIcon className={loading ? "animate-spin" : ""} />
              {t("common.actions.refresh")}
            </Button>
            <Button onClick={() => setOpen(true)}>
              <PlusIcon />
              {t("buckets.actions.create")}
            </Button>
          </>
        }
      />
      <BucketTable buckets={buckets} loading={loading} onOpen={onOpenBucket} onDelete={setDeleteTarget} />
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t("buckets.createDialog.title")}</DialogTitle>
            <DialogDescription>{t("buckets.createDialog.description")}</DialogDescription>
          </DialogHeader>
          <label className="grid gap-1.5 text-sm">
            {t("buckets.createDialog.nameLabel")}
            <Input
              autoFocus
              placeholder="static-assets"
              value={bucketName}
              onChange={(event) => setBucketName(event.target.value)}
            />
          </label>
          <DialogFooter>
            <Button variant="outline" onClick={() => setOpen(false)}>
              {t("common.actions.cancel")}
            </Button>
            <Button disabled={!bucketName || creating} onClick={create}>
              {creating ? t("buckets.actions.creating") : t("common.actions.create")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
      <AlertDialog open={deleteTarget !== null} onOpenChange={(nextOpen) => !nextOpen && setDeleteTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t("buckets.deleteDialog.title")}</AlertDialogTitle>
            <AlertDialogDescription>{t("buckets.deleteDialog.description")}</AlertDialogDescription>
          </AlertDialogHeader>
          <div className="rounded-md border bg-muted/40 px-3 py-2 font-mono text-sm">
            {deleteTarget}
          </div>
          <AlertDialogFooter>
            <AlertDialogCancel>{t("common.actions.cancel")}</AlertDialogCancel>
            <AlertDialogAction variant="destructive" onClick={remove}>
              {t("common.actions.delete")}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  )
}
