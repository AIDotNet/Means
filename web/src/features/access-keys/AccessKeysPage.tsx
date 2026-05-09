import { useCallback, useEffect, useState } from "react"
import { KeyRoundIcon, PlusIcon, Trash2Icon } from "lucide-react"
import { toast } from "sonner"

import { AccessKeyDialog } from "@/components/domain/AccessKeyDialog"
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
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { api, type AccessKeyInfo, type AccessKeySecretResult } from "@/lib/api-client"
import { formatDateTime } from "@/lib/formatters"
import { useTranslation } from "@/i18n"

export function AccessKeysPage() {
  const { t } = useTranslation()
  const [keys, setKeys] = useState<AccessKeyInfo[]>([])
  const [accessKey, setAccessKey] = useState("")
  const [created, setCreated] = useState<AccessKeySecretResult | null>(null)
  const [dialogOpen, setDialogOpen] = useState(false)
  const [creating, setCreating] = useState(false)
  const [deleteTarget, setDeleteTarget] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setKeys(await api.accessKeys())
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("accessKeys.errors.loadFailed"))
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
      const result = await api.createAccessKey(accessKey || undefined)
      setCreated(result)
      setDialogOpen(true)
      setAccessKey("")
      await load()
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("accessKeys.errors.createFailed"))
    } finally {
      setCreating(false)
    }
  }

  const remove = async () => {
    if (!deleteTarget) {
      return
    }

    try {
      await api.deleteAccessKey(deleteTarget)
      toast.success(t("accessKeys.toast.deleted"))
      setDeleteTarget(null)
      await load()
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("accessKeys.errors.deleteFailed"))
    }
  }

  return (
    <>
      <PageHeader
        eyebrow={t("accessKeys.page.eyebrow")}
        title={t("accessKeys.page.title")}
        description={t("accessKeys.page.description")}
      />
      <section className="mb-5 rounded-lg border bg-card p-4 text-card-foreground shadow-xs">
        <div className="flex flex-col gap-3 md:flex-row md:items-end">
          <label className="grid flex-1 gap-1.5 text-sm">
            {t("accessKeys.form.customAccessKeyLabel")}
            <Input
              placeholder={t("accessKeys.form.customAccessKeyPlaceholder")}
              value={accessKey}
              onChange={(event) => setAccessKey(event.target.value)}
            />
          </label>
          <Button onClick={create} disabled={creating}>
            <PlusIcon />
            {creating ? t("accessKeys.actions.creating") : t("accessKeys.actions.create")}
          </Button>
        </div>
      </section>
      <div className="rounded-lg border bg-card text-card-foreground shadow-xs">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t("accessKeys.table.columns.accessKey")}</TableHead>
              <TableHead>{t("accessKeys.table.columns.status")}</TableHead>
              <TableHead>{t("accessKeys.table.columns.createdAt")}</TableHead>
              <TableHead className="w-12" />
            </TableRow>
          </TableHeader>
          <TableBody>
            {keys.map((key) => (
              <TableRow key={key.accessKey}>
                <TableCell className="font-mono text-xs">
                  <span className="inline-flex items-center gap-2">
                    <KeyRoundIcon className="size-4 text-primary" />
                    {key.accessKey}
                  </span>
                </TableCell>
                <TableCell>
                  <Badge variant={key.enabled ? "outline" : "destructive"}>
                    {key.enabled ? t("accessKeys.table.status.enabled") : t("accessKeys.table.status.disabled")}
                  </Badge>
                </TableCell>
                <TableCell className="text-muted-foreground">{formatDateTime(key.createdAt)}</TableCell>
                <TableCell>
                  <Button variant="ghost" size="icon-sm" onClick={() => setDeleteTarget(key.accessKey)}>
                    <Trash2Icon />
                    <span className="sr-only">{t("common.actions.delete")}</span>
                  </Button>
                </TableCell>
              </TableRow>
            ))}
            {loading && keys.length === 0 ? (
              <TableRow>
                <TableCell colSpan={4} className="h-32 text-center text-muted-foreground">
                  {t("accessKeys.table.states.loading")}
                </TableCell>
              </TableRow>
            ) : null}
            {!loading && keys.length === 0 ? (
              <TableRow>
                <TableCell colSpan={4} className="h-32 text-center text-muted-foreground">
                  {t("accessKeys.table.states.empty")}
                </TableCell>
              </TableRow>
            ) : null}
          </TableBody>
        </Table>
      </div>
      <AccessKeyDialog created={created} open={dialogOpen} onOpenChange={setDialogOpen} />
      <AlertDialog open={deleteTarget !== null} onOpenChange={(open) => !open && setDeleteTarget(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{t("accessKeys.deleteDialog.title")}</AlertDialogTitle>
            <AlertDialogDescription>{t("accessKeys.deleteDialog.description")}</AlertDialogDescription>
          </AlertDialogHeader>
          <div className="rounded-md border bg-muted/40 px-3 py-2 font-mono text-sm break-all">
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
