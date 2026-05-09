import { useEffect, useState } from "react"
import { PlusIcon, RefreshCwIcon, SaveIcon, Trash2Icon } from "lucide-react"

import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { useTranslation } from "@/i18n"
import type { BucketSettings } from "@/lib/api-client"
import { cn } from "@/lib/utils"

type BucketSettingsPanelProps = {
  settings: BucketSettings | null
  saving: boolean
  onSave: (
    defaultResponseHeaders: Record<string, string>,
    defaultMetadata: Record<string, string>
  ) => void | Promise<void>
  onReload: () => void | Promise<void>
}

type RecordRow = {
  id: string
  name: string
  value: string
}

export function BucketSettingsPanel({
  settings,
  saving,
  onSave,
  onReload,
}: BucketSettingsPanelProps) {
  const { t } = useTranslation()
  const [headerRows, setHeaderRows] = useState<RecordRow[]>([])
  const [metadataRows, setMetadataRows] = useState<RecordRow[]>([])

  useEffect(() => {
    setHeaderRows(rowsFromRecord(settings?.defaultResponseHeaders ?? {}))
    setMetadataRows(rowsFromRecord(settings?.defaultMetadata ?? {}))
  }, [settings])

  const save = () => {
    void onSave(rowsToRecord(headerRows), rowsToRecord(metadataRows))
  }

  return (
    <section className="rounded-lg border bg-card text-card-foreground shadow-xs">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b p-4">
        <div>
          <h2 className="text-base font-semibold">{t("bucketSettings.title")}</h2>
          <p className="mt-1 text-sm text-muted-foreground">
            {t("bucketSettings.description")}
          </p>
        </div>
        <Button variant="outline" size="sm" onClick={onReload}>
          <RefreshCwIcon />
          {t("common.actions.reload")}
        </Button>
      </div>

      <div className="grid gap-5 p-4 xl:grid-cols-2">
        <EditableRecord
          title={t("bucketSettings.headers.title")}
          description={t("bucketSettings.headers.description")}
          emptyText={t("bucketSettings.headers.empty")}
          rows={headerRows}
          onRowsChange={setHeaderRows}
        />
        <EditableRecord
          title={t("bucketSettings.metadata.title")}
          description={t("bucketSettings.metadata.description")}
          emptyText={t("bucketSettings.metadata.empty")}
          prefix="x-amz-meta-"
          rows={metadataRows}
          onRowsChange={setMetadataRows}
        />
      </div>

      <div className="flex justify-end gap-2 border-t p-4">
        <Button onClick={save} disabled={saving}>
          <SaveIcon />
          {saving ? t("bucketSettings.actions.saving") : t("bucketSettings.actions.save")}
        </Button>
      </div>
    </section>
  )
}

function EditableRecord({
  title,
  description,
  emptyText,
  prefix,
  rows,
  onRowsChange,
}: {
  title: string
  description: string
  emptyText: string
  prefix?: string
  rows: RecordRow[]
  onRowsChange: (rows: RecordRow[]) => void
}) {
  const { t } = useTranslation()
  const addRow = () => onRowsChange([...rows, createEmptyRow()])
  const removeRow = (id: string) => onRowsChange(rows.filter((row) => row.id !== id))
  const updateRow = (id: string, next: Partial<Pick<RecordRow, "name" | "value">>) =>
    onRowsChange(rows.map((row) => (row.id === id ? { ...row, ...next } : row)))

  return (
    <div className="rounded-lg border bg-background/50">
      <div className="flex items-start justify-between gap-3 border-b p-3">
        <div>
          <div className="text-sm font-semibold">{title}</div>
          <p className="mt-1 text-xs text-muted-foreground">{description}</p>
        </div>
        <Button variant="outline" size="sm" onClick={addRow}>
          <PlusIcon />
          {t("common.actions.add")}
        </Button>
      </div>
      <div className="space-y-2 p-3">
        {rows.length === 0 ? (
          <div className="rounded-md border border-dashed p-4 text-center text-sm text-muted-foreground">
            {emptyText}
          </div>
        ) : (
          rows.map((row) => (
            <div key={row.id} className="grid gap-2 md:grid-cols-[1fr_1fr_auto]">
              <div className="flex min-w-0">
                {prefix ? (
                  <span className="inline-flex items-center rounded-l-md border border-r-0 bg-muted px-2 font-mono text-xs text-muted-foreground">
                    {prefix}
                  </span>
                ) : null}
                <Input
                  className={cn(prefix && "rounded-l-none")}
                  placeholder={t("bucketSettings.fields.namePlaceholder")}
                  value={row.name}
                  onChange={(event) => updateRow(row.id, { name: event.target.value })}
                />
              </div>
              <Input
                placeholder={t("bucketSettings.fields.valuePlaceholder")}
                value={row.value}
                onChange={(event) => updateRow(row.id, { value: event.target.value })}
              />
              <Button variant="ghost" size="icon-sm" onClick={() => removeRow(row.id)}>
                <Trash2Icon />
                <span className="sr-only">{t("common.actions.delete")}</span>
              </Button>
            </div>
          ))
        )}
      </div>
    </div>
  )
}

function rowsFromRecord(record: Record<string, string>) {
  return Object.entries(record).map(([name, value], index) => ({
    id: `${name}-${index}`,
    name,
    value,
  }))
}

function rowsToRecord(rows: RecordRow[]) {
  return rows.reduce<Record<string, string>>((next, row) => {
    const name = row.name.trim()
    if (name) {
      next[name] = row.value
    }

    return next
  }, {})
}

function createEmptyRow(): RecordRow {
  return {
    id: `${Date.now()}-${Math.random().toString(36).slice(2)}`,
    name: "",
    value: "",
  }
}
