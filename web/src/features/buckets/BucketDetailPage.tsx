import { useCallback, useEffect, useState, type ComponentType } from "react"
import {
  ActivityIcon,
  CalendarClockIcon,
  DatabaseIcon,
  FileJsonIcon,
  FolderTreeIcon,
  HistoryIcon,
  ListChecksIcon,
  RefreshCwIcon,
  SaveIcon,
  Settings2Icon,
  ShieldIcon,
  Trash2Icon,
} from "lucide-react"
import { toast } from "sonner"

import { BucketSettingsPanel } from "@/components/domain/BucketSettingsPanel"
import { ObjectBrowser } from "@/components/domain/ObjectBrowser"
import { PolicyEditor } from "@/components/domain/PolicyEditor"
import { PageHeader } from "@/components/layout/PageHeader"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Separator } from "@/components/ui/separator"
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { Textarea } from "@/components/ui/textarea"
import {
  api,
  type BucketConsoleSummary,
  type BucketLifecycle,
  type BucketSettings,
  type BucketVersioning,
  type BucketVersioningStatus,
  type LifecycleRule,
  type ListObjectVersionsResult,
  type ObjectVersion,
} from "@/lib/api-client"
import { formatBytes, formatDateTime, formatNumber } from "@/lib/formatters"
import { downloadFromUrl } from "@/lib/transfer"
import { cn } from "@/lib/utils"
import { useTranslation } from "@/i18n"

type BucketDetailPageProps = {
  bucketName: string
}

type BucketTab = "overview" | "objects" | "versions" | "lifecycle" | "policy" | "settings"

export function BucketDetailPage({ bucketName }: BucketDetailPageProps) {
  const { t } = useTranslation()
  const [activeTab, setActiveTab] = useState<BucketTab>("overview")
  const [summary, setSummary] = useState<BucketConsoleSummary | null>(null)
  const [settings, setSettings] = useState<BucketSettings | null>(null)
  const [policy, setPolicy] = useState("")
  const [loading, setLoading] = useState(false)
  const [savingSettings, setSavingSettings] = useState(false)

  const tabs: Array<{
    id: BucketTab
    label: string
    icon: ComponentType<{ className?: string }>
  }> = [
    { id: "overview", label: t("bucketDetail.tabs.overview"), icon: ActivityIcon },
    { id: "objects", label: t("bucketDetail.tabs.objects"), icon: FolderTreeIcon },
    { id: "versions", label: t("bucketDetail.tabs.versions"), icon: HistoryIcon },
    { id: "lifecycle", label: t("bucketDetail.tabs.lifecycle"), icon: ListChecksIcon },
    { id: "policy", label: t("bucketDetail.tabs.policy"), icon: FileJsonIcon },
    { id: "settings", label: t("bucketDetail.tabs.settings"), icon: Settings2Icon },
  ]

  const loadSummary = useCallback(async () => {
    const next = await api.bucketSummary(bucketName, 24)
    setSummary(next.summary)
  }, [bucketName])

  const loadPolicy = useCallback(async () => {
    const next = await api.policy(bucketName)
    setPolicy(next.policy)
  }, [bucketName])

  const loadSettings = useCallback(async () => {
    setSettings(await api.bucketSettings(bucketName))
  }, [bucketName])

  const refreshAll = useCallback(async () => {
    setLoading(true)
    try {
      await Promise.all([loadSummary(), loadPolicy(), loadSettings()])
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("bucketDetail.errors.refreshBucketInfoFailed"))
    } finally {
      setLoading(false)
    }
  }, [loadPolicy, loadSettings, loadSummary, t])

  useEffect(() => {
    void refreshAll()
  }, [refreshAll])

  const savePolicy = async () => {
    try {
      await api.putPolicy(bucketName, policy)
      toast.success(t("bucketDetail.toast.policySaved"))
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("bucketDetail.errors.savePolicyFailed"))
    }
  }

  const deletePolicy = async () => {
    try {
      await api.deletePolicy(bucketName)
      setPolicy("")
      toast.success(t("bucketDetail.toast.policyDeleted"))
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("bucketDetail.errors.deletePolicyFailed"))
    }
  }

  const saveSettings = async (
    defaultResponseHeaders: Record<string, string>,
    defaultMetadata: Record<string, string>
  ) => {
    setSavingSettings(true)
    try {
      const next = await api.updateBucketSettings(
        bucketName,
        defaultResponseHeaders,
        defaultMetadata
      )
      setSettings(next)
      toast.success(t("bucketDetail.toast.settingsSaved"))
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("bucketDetail.errors.saveSettingsFailed"))
    } finally {
      setSavingSettings(false)
    }
  }

  const refreshActive = async () => {
    setLoading(true)
    try {
      if (activeTab === "policy") {
        await loadPolicy()
      } else if (activeTab === "settings") {
        await loadSettings()
      } else if (activeTab === "versions" || activeTab === "lifecycle") {
        // Child panels own their data lifecycle; remounting through the tab key is not needed.
        await Promise.resolve()
      } else {
        await loadSummary()
      }
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("bucketDetail.errors.refreshFailed"))
    } finally {
      setLoading(false)
    }
  }

  return (
    <>
      <PageHeader
        eyebrow={t("bucketDetail.page.eyebrow")}
        title={bucketName}
        description={t("bucketDetail.page.description")}
        actions={
          <Button variant="outline" onClick={refreshActive} disabled={loading}>
            <RefreshCwIcon className={loading ? "animate-spin" : ""} />
            {t("common.actions.refresh")}
          </Button>
        }
      />

      <div className="space-y-4">
        <BucketMetricStrip summary={summary} settings={settings} />
        <BucketTabs activeTab={activeTab} onChange={setActiveTab} tabs={tabs} />

        {activeTab === "overview" ? (
          <BucketOverview summary={summary} settings={settings} />
        ) : null}
        {activeTab === "objects" ? <ObjectBrowser bucketName={bucketName} /> : null}
        {activeTab === "versions" ? <VersionBrowser bucketName={bucketName} /> : null}
        {activeTab === "lifecycle" ? <LifecycleEditor bucketName={bucketName} /> : null}
        {activeTab === "policy" ? (
          <PolicyEditor
            bucketName={bucketName}
            value={policy}
            onChange={setPolicy}
            onSave={savePolicy}
            onDelete={deletePolicy}
          />
        ) : null}
        {activeTab === "settings" ? (
          <BucketSettingsPanel
            settings={settings}
            saving={savingSettings}
            onSave={saveSettings}
            onReload={async () => {
              try {
                await loadSettings()
              } catch (error) {
                toast.error(error instanceof Error ? error.message : t("bucketDetail.errors.reloadSettingsFailed"))
              }
            }}
          />
        ) : null}
      </div>
    </>
  )
}

function VersionBrowser({ bucketName }: { bucketName: string }) {
  const { t } = useTranslation()
  const [prefix, setPrefix] = useState("")
  const [result, setResult] = useState<ListObjectVersionsResult | null>(null)
  const [loading, setLoading] = useState(false)
  const [keyMarker, setKeyMarker] = useState<string | null>(null)
  const [versionIdMarker, setVersionIdMarker] = useState<string | null>(null)

  const load = useCallback(
    async (nextKeyMarker: string | null = null, nextVersionIdMarker: string | null = null) => {
      setLoading(true)
      try {
        const params = new URLSearchParams()
        params.set("prefix", prefix)
        params.set("maxKeys", "1000")
        if (nextKeyMarker) {
          params.set("keyMarker", nextKeyMarker)
        }
        if (nextVersionIdMarker) {
          params.set("versionIdMarker", nextVersionIdMarker)
        }

        const next = await api.objectVersions(bucketName, params)
        setResult(next)
        setKeyMarker(nextKeyMarker)
        setVersionIdMarker(nextVersionIdMarker)
      } catch (error) {
        toast.error(error instanceof Error ? error.message : t("bucketDetail.versions.errors.loadFailed"))
      } finally {
        setLoading(false)
      }
    },
    [bucketName, prefix, t]
  )

  useEffect(() => {
    setKeyMarker(null)
    setVersionIdMarker(null)
    void load(null, null)
  }, [bucketName, prefix])

  const downloadVersion = async (version: ObjectVersion) => {
    try {
      const transfer = await api.presignDownload(bucketName, version.key, version.versionId)
      downloadFromUrl(transfer.url)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("bucketDetail.versions.errors.downloadFailed"))
    }
  }

  const deleteVersion = async (version: ObjectVersion) => {
    try {
      await api.deleteObject(bucketName, version.key, version.versionId)
      toast.success(t("bucketDetail.versions.toast.deleted"))
      await load(keyMarker, versionIdMarker)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("bucketDetail.versions.errors.deleteFailed"))
    }
  }

  const versions = result?.versions ?? []

  return (
    <section className="overflow-hidden rounded-lg border bg-card text-card-foreground shadow-xs">
      <div className="flex flex-col gap-3 border-b p-4 xl:flex-row xl:items-end">
        <label className="grid flex-1 gap-1.5 text-sm">
          {t("bucketDetail.versions.prefix")}
          <Input value={prefix} onChange={(event) => setPrefix(event.target.value)} placeholder="logs/2026/" />
        </label>
        <div className="flex gap-2">
          <Button variant="outline" onClick={() => load(null, null)} disabled={loading}>
            <RefreshCwIcon className={loading ? "animate-spin" : ""} />
            {t("common.actions.refresh")}
          </Button>
          {result?.isTruncated ? (
            <Button
              variant="secondary"
              onClick={() => load(result.nextKeyMarker, result.nextVersionIdMarker)}
              disabled={loading}
            >
              {t("bucketDetail.versions.nextPage")}
            </Button>
          ) : null}
        </div>
      </div>

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("bucketDetail.versions.columns.key")}</TableHead>
            <TableHead>{t("bucketDetail.versions.columns.version")}</TableHead>
            <TableHead>{t("bucketDetail.versions.columns.state")}</TableHead>
            <TableHead>{t("bucketDetail.versions.columns.size")}</TableHead>
            <TableHead>{t("bucketDetail.versions.columns.updatedAt")}</TableHead>
            <TableHead className="w-40" />
          </TableRow>
        </TableHeader>
        <TableBody>
          {versions.map((version) => (
            <TableRow key={`${version.key}:${version.versionId}`}>
              <TableCell>
                <div className="font-medium break-all">{version.key}</div>
                <div className="mt-1 font-mono text-[11px] text-muted-foreground">{version.eTag || "-"}</div>
              </TableCell>
              <TableCell className="font-mono text-xs break-all">{version.versionId}</TableCell>
              <TableCell>
                <div className="flex flex-wrap gap-1">
                  {version.isLatest ? <Badge>{t("bucketDetail.versions.badges.latest")}</Badge> : null}
                  <Badge variant={version.isDeleteMarker ? "destructive" : "outline"}>
                    {version.isDeleteMarker
                      ? t("bucketDetail.versions.badges.deleteMarker")
                      : t("bucketDetail.versions.badges.object")}
                  </Badge>
                </div>
              </TableCell>
              <TableCell>{version.isDeleteMarker ? "-" : formatBytes(version.size)}</TableCell>
              <TableCell className="text-muted-foreground">{formatDateTime(version.lastModified)}</TableCell>
              <TableCell>
                <div className="flex justify-end gap-2">
                  <Button
                    variant="outline"
                    size="sm"
                    disabled={version.isDeleteMarker}
                    onClick={() => downloadVersion(version)}
                  >
                    {t("bucketDetail.versions.actions.download")}
                  </Button>
                  <Button variant="destructive" size="sm" onClick={() => deleteVersion(version)}>
                    {t("common.actions.delete")}
                  </Button>
                </div>
              </TableCell>
            </TableRow>
          ))}
          {loading && versions.length === 0 ? (
            <TableRow>
              <TableCell colSpan={6} className="h-32 text-center text-muted-foreground">
                {t("bucketDetail.versions.loading")}
              </TableCell>
            </TableRow>
          ) : null}
          {!loading && versions.length === 0 ? (
            <TableRow>
              <TableCell colSpan={6} className="h-32 text-center text-muted-foreground">
                {t("bucketDetail.versions.empty")}
              </TableCell>
            </TableRow>
          ) : null}
        </TableBody>
      </Table>
    </section>
  )
}

function LifecycleEditor({ bucketName }: { bucketName: string }) {
  const { t } = useTranslation()
  const [versioning, setVersioning] = useState<BucketVersioning | null>(null)
  const [lifecycle, setLifecycle] = useState<BucketLifecycle | null>(null)
  const [rulesText, setRulesText] = useState("[]")
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const [nextVersioning, nextLifecycle] = await Promise.all([
        api.bucketVersioning(bucketName),
        api.bucketLifecycle(bucketName),
      ])
      setVersioning(nextVersioning)
      setLifecycle(nextLifecycle)
      setRulesText(JSON.stringify(nextLifecycle.rules, null, 2))
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("bucketDetail.lifecycle.errors.loadFailed"))
    } finally {
      setLoading(false)
    }
  }, [bucketName, t])

  useEffect(() => {
    void load()
  }, [load])

  const save = async () => {
    setSaving(true)
    try {
      const rules = normalizeLifecycleRules(JSON.parse(rulesText))
      const nextStatus = versioning?.status ?? "Off"
      const [nextVersioning, nextLifecycle] = await Promise.all([
        api.putBucketVersioning(bucketName, nextStatus),
        rules.length > 0
          ? api.putBucketLifecycle(bucketName, rules)
          : api.deleteBucketLifecycle(bucketName).then(() => ({ rules: [] })),
      ])
      setVersioning(nextVersioning)
      setLifecycle(nextLifecycle)
      setRulesText(JSON.stringify(nextLifecycle.rules, null, 2))
      toast.success(t("bucketDetail.lifecycle.toast.saved"))
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("bucketDetail.lifecycle.errors.saveFailed"))
    } finally {
      setSaving(false)
    }
  }

  const clearLifecycle = async () => {
    setSaving(true)
    try {
      await api.deleteBucketLifecycle(bucketName)
      const nextLifecycle = { rules: [] }
      setLifecycle(nextLifecycle)
      setRulesText("[]")
      toast.success(t("bucketDetail.lifecycle.toast.deleted"))
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("bucketDetail.lifecycle.errors.deleteFailed"))
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className="rounded-lg border bg-card text-card-foreground shadow-xs">
      <div className="grid gap-0 xl:grid-cols-[0.85fr_1.15fr]">
        <div className="border-b p-5 xl:border-r xl:border-b-0">
          <div className="flex items-center gap-2">
            <CalendarClockIcon className="size-4 text-primary" />
            <h2 className="text-base font-semibold">{t("bucketDetail.lifecycle.versioningTitle")}</h2>
          </div>
          <p className="mt-1 text-sm text-muted-foreground">
            {t("bucketDetail.lifecycle.versioningDescription")}
          </p>
          <Separator className="my-5" />
          <label className="grid gap-1.5 text-sm">
            {t("bucketDetail.lifecycle.versioningStatus")}
            <Select
              value={versioning?.status ?? "Off"}
              onValueChange={(status) =>
                setVersioning({ bucketName, status: status as BucketVersioningStatus })
              }
            >
              <SelectTrigger className="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="Off">Off</SelectItem>
                <SelectItem value="Enabled">Enabled</SelectItem>
                <SelectItem value="Suspended">Suspended</SelectItem>
              </SelectContent>
            </Select>
          </label>
          <div className="mt-5 rounded-md border bg-muted/40 p-3 text-sm text-muted-foreground">
            {t("bucketDetail.lifecycle.versioningHint")}
          </div>
        </div>

        <div className="p-5">
          <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
            <div>
              <div className="flex items-center gap-2">
                <ListChecksIcon className="size-4 text-primary" />
                <h2 className="text-base font-semibold">{t("bucketDetail.lifecycle.rulesTitle")}</h2>
              </div>
              <p className="mt-1 text-sm text-muted-foreground">
                {t("bucketDetail.lifecycle.rulesDescription")}
              </p>
            </div>
            <Badge variant="outline">
              {t("bucketDetail.lifecycle.ruleCount", { count: lifecycle?.rules.length ?? 0 })}
            </Badge>
          </div>
          <Separator className="my-5" />
          <Textarea
            value={rulesText}
            onChange={(event) => setRulesText(event.target.value)}
            className="min-h-72 font-mono text-xs"
            spellCheck={false}
            aria-label={t("bucketDetail.lifecycle.rulesAria")}
          />
          <div className="mt-3 rounded-md border bg-muted/40 p-3 text-xs text-muted-foreground">
            {t("bucketDetail.lifecycle.rulesHint")}
          </div>
          <div className="mt-4 flex flex-wrap justify-end gap-2">
            <Button variant="outline" onClick={load} disabled={loading || saving}>
              <RefreshCwIcon className={loading ? "animate-spin" : ""} />
              {t("common.actions.refresh")}
            </Button>
            <Button variant="destructive" onClick={clearLifecycle} disabled={saving}>
              <Trash2Icon />
              {t("bucketDetail.lifecycle.deleteRules")}
            </Button>
            <Button onClick={save} disabled={saving}>
              <SaveIcon />
              {t("common.actions.save")}
            </Button>
          </div>
        </div>
      </div>
    </section>
  )
}

function BucketMetricStrip({
  summary,
  settings,
}: {
  summary: BucketConsoleSummary | null
  settings: BucketSettings | null
}) {
  const { t } = useTranslation()
  const headerCount = Object.keys(settings?.defaultResponseHeaders ?? {}).length
  const metadataCount = Object.keys(settings?.defaultMetadata ?? {}).length

  return (
    <section className="grid gap-3 md:grid-cols-2 xl:grid-cols-5">
      <MiniMetric label={t("bucketDetail.metrics.objects")} value={formatNumber(summary?.objectCount ?? 0)} />
      <MiniMetric label={t("bucketDetail.metrics.stored")} value={formatBytes(summary?.totalBytes ?? 0)} />
      <MiniMetric label={t("bucketDetail.metrics.requests24h")} value={formatNumber(summary?.requestCount ?? 0)} />
      <MiniMetric
        label={t("bucketDetail.metrics.errors24h")}
        value={formatNumber(summary?.errorCount ?? 0)}
        tone="warning"
      />
      <MiniMetric label={t("bucketDetail.metrics.defaultHeaders")} value={`${headerCount + metadataCount}`} />
    </section>
  )
}

function normalizeLifecycleRules(value: unknown): LifecycleRule[] {
  if (!Array.isArray(value)) {
    throw new Error("Lifecycle rules must be a JSON array.")
  }

  return value.map((item, index) => {
    if (!item || typeof item !== "object") {
      throw new Error(`Lifecycle rule at index ${index} must be an object.`)
    }

    const raw = item as Record<string, unknown>
    const id = String(raw.id ?? "").trim()
    const status = raw.status === "Disabled" ? "Disabled" : "Enabled"
    if (!id) {
      throw new Error(`Lifecycle rule at index ${index} requires id.`)
    }

    return {
      id,
      status,
      prefix: String(raw.prefix ?? ""),
      expirationDays: positiveIntegerOrNull(raw.expirationDays, "expirationDays", index),
      noncurrentVersionExpirationDays: positiveIntegerOrNull(
        raw.noncurrentVersionExpirationDays,
        "noncurrentVersionExpirationDays",
        index
      ),
      abortIncompleteMultipartUploadDays: positiveIntegerOrNull(
        raw.abortIncompleteMultipartUploadDays,
        "abortIncompleteMultipartUploadDays",
        index
      ),
    }
  })
}

function positiveIntegerOrNull(value: unknown, field: string, index: number): number | null {
  if (value === null || value === undefined || value === "") {
    return null
  }

  const parsed = Number(value)
  if (!Number.isInteger(parsed) || parsed <= 0) {
    throw new Error(`Lifecycle rule at index ${index} has invalid ${field}.`)
  }

  return parsed
}

function BucketTabs({
  activeTab,
  onChange,
  tabs,
}: {
  activeTab: BucketTab
  onChange: (tab: BucketTab) => void
  tabs: Array<{
    id: BucketTab
    label: string
    icon: ComponentType<{ className?: string }>
  }>
}) {
  return (
    <div className="flex flex-wrap gap-1 rounded-lg border bg-card p-1 shadow-xs">
      {tabs.map((tab) => {
        const Icon = tab.icon
        const active = activeTab === tab.id
        return (
          <Button
            key={tab.id}
            variant={active ? "secondary" : "ghost"}
            size="sm"
            className={cn("justify-start", active && "shadow-xs")}
            onClick={() => onChange(tab.id)}
          >
            <Icon />
            {tab.label}
          </Button>
        )
      })}
    </div>
  )
}

function BucketOverview({
  summary,
  settings,
}: {
  summary: BucketConsoleSummary | null
  settings: BucketSettings | null
}) {
  const { t } = useTranslation()

  return (
    <section className="rounded-lg border bg-card text-card-foreground shadow-xs">
      <div className="grid gap-0 xl:grid-cols-[1.2fr_0.8fr]">
        <div className="p-5">
          <div className="flex items-center justify-between gap-3">
            <div>
              <h2 className="text-base font-semibold">{t("bucketDetail.overview.dataStatsTitle")}</h2>
              <p className="mt-1 text-sm text-muted-foreground">
                {t("bucketDetail.overview.dataStatsDescription")}
              </p>
            </div>
            <Badge variant="outline">
              {summary?.lastActivityAt
                ? formatDateTime(summary.lastActivityAt)
                : t("bucketDetail.overview.noRequests")}
            </Badge>
          </div>
          <Separator className="my-5" />
          <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
            <StatBlock label="PUT" value={summary?.putCount ?? 0} />
            <StatBlock label="GET" value={summary?.getCount ?? 0} />
            <StatBlock label="HEAD" value={summary?.headCount ?? 0} />
            <StatBlock label="DELETE" value={summary?.deleteCount ?? 0} />
          </div>
          <div className="mt-5 grid gap-3 sm:grid-cols-2">
            <TrafficBlock label="Ingress" value={summary?.ingressBytes ?? 0} />
            <TrafficBlock label="Egress" value={summary?.egressBytes ?? 0} />
          </div>
        </div>

        <div className="border-t p-5 xl:border-t-0 xl:border-l">
          <div className="flex items-center gap-2">
            <ShieldIcon className="size-4 text-primary" />
            <h2 className="text-base font-semibold">{t("bucketDetail.overview.defaultResponseConfigTitle")}</h2>
          </div>
          <p className="mt-1 text-sm text-muted-foreground">
            {t("bucketDetail.overview.defaultResponseConfigDescription")}
          </p>
          <Separator className="my-5" />
          <div className="space-y-4">
            <HeaderPreview
              title={t("bucketDetail.overview.responseHeaders")}
              values={settings?.defaultResponseHeaders ?? {}}
            />
            <HeaderPreview
              title={t("bucketDetail.overview.metadata")}
              values={settings?.defaultMetadata ?? {}}
              prefix="x-amz-meta-"
            />
          </div>
        </div>
      </div>
    </section>
  )
}

function MiniMetric({
  label,
  value,
  tone,
}: {
  label: string
  value: string
  tone?: "warning"
}) {
  return (
    <div className="rounded-lg border bg-card px-4 py-3 shadow-xs">
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className={cn("mt-1 text-xl font-semibold tracking-tight", tone === "warning" && "text-destructive")}>
        {value}
      </div>
    </div>
  )
}

function StatBlock({ label, value }: { label: string; value: number }) {
  return (
    <div className="rounded-md bg-muted/45 px-3 py-3">
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className="mt-1 text-lg font-semibold">{formatNumber(value)}</div>
    </div>
  )
}

function TrafficBlock({ label, value }: { label: string; value: number }) {
  return (
    <div className="flex items-center gap-3 rounded-md border bg-background/55 px-3 py-3">
      <DatabaseIcon className="size-4 text-primary" />
      <div>
        <div className="text-xs text-muted-foreground">{label}</div>
        <div className="font-semibold">{formatBytes(value)}</div>
      </div>
    </div>
  )
}

function HeaderPreview({
  title,
  values,
  prefix = "",
}: {
  title: string
  values: Record<string, string>
  prefix?: string
}) {
  const { t } = useTranslation()
  const entries = Object.entries(values)
  return (
    <div>
      <div className="mb-2 text-xs font-semibold text-muted-foreground uppercase">{title}</div>
      <div className="rounded-md border bg-background/55">
        {entries.length === 0 ? (
          <div className="px-3 py-3 text-sm text-muted-foreground">{t("common.states.notConfigured")}</div>
        ) : (
          entries.map(([name, value]) => (
            <div key={name} className="grid gap-1 border-b px-3 py-2 last:border-b-0">
              <code className="text-xs text-muted-foreground">
                {prefix}
                {name}
              </code>
              <div className="break-all text-sm">{value}</div>
            </div>
          ))
        )}
      </div>
    </div>
  )
}
