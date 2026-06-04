import { useEffect, useState } from "react"
import { CopyIcon, SaveIcon } from "lucide-react"
import { toast } from "sonner"

import { PageHeader } from "@/components/layout/PageHeader"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Separator } from "@/components/ui/separator"
import { useTranslation } from "@/i18n"
import { api, type Overview, type SystemSettings } from "@/lib/api-client"
import { formatBytes } from "@/lib/formatters"

export function SettingsPage() {
  const { t } = useTranslation()
  const [overview, setOverview] = useState<Overview | null>(null)
  const [settings, setSettings] = useState<SystemSettings | null>(null)
  const [maxUploadMiB, setMaxUploadMiB] = useState("")
  const [publicOrigin, setPublicOrigin] = useState("")
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    Promise.all([api.overview(), api.settings()])
      .then(([nextOverview, nextSettings]) => {
        setOverview(nextOverview)
        setSettings(nextSettings)
        setMaxUploadMiB(bytesToMiB(nextSettings.maxUploadSizeBytes).toString())
        setPublicOrigin(nextSettings.publicOrigin ?? "")
      })
      .catch((error: Error) => toast.error(error.message))
  }, [])

  const copy = async (value: string) => {
    await navigator.clipboard.writeText(value)
    toast.success(t("settings.toast.copied"))
  }

  const saveSettings = async () => {
    if (!settings) {
      return
    }

    const parsed = Number(maxUploadMiB)
    if (!Number.isFinite(parsed) || parsed <= 0) {
      toast.error(t("settings.errors.invalidUploadLimit"))
      return
    }

    const maxUploadSizeBytes = Math.round(parsed * 1024 * 1024)
    if (
      maxUploadSizeBytes < settings.minimumMaxUploadSizeBytes ||
      maxUploadSizeBytes > settings.maximumMaxUploadSizeBytes
    ) {
      toast.error(
        t("settings.errors.rangeLimit", {
          min: formatBytes(settings.minimumMaxUploadSizeBytes),
          max: formatBytes(settings.maximumMaxUploadSizeBytes),
        })
      )
      return
    }

    let normalizedPublicOrigin: string | null
    try {
      normalizedPublicOrigin = normalizePublicOriginInput(publicOrigin)
    } catch {
      toast.error(t("settings.errors.invalidPublicOrigin"))
      return
    }

    setSaving(true)
    try {
      const updated = await api.updateSettings(maxUploadSizeBytes, normalizedPublicOrigin)
      setSettings(updated)
      setMaxUploadMiB(bytesToMiB(updated.maxUploadSizeBytes).toString())
      setPublicOrigin(updated.publicOrigin ?? "")
      toast.success(t("settings.toast.saved"))
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("settings.errors.saveFailed"))
    } finally {
      setSaving(false)
    }
  }

  const pathStyle = `https://${overview?.serviceHost ?? "api.means.local"}/{bucket}/{key}`
  const virtualHosted = `https://{bucket}.${overview?.domainSuffix ?? "means.local"}/{key}`
  const aliasPrefix = overview?.aliasPrefix ?? "/s3"
  const boundAlias = `${settings?.publicOrigin ?? window.location.origin}${aliasPrefix}/{bucket}/{key}`
  const minimumMiB = settings ? bytesToMiB(settings.minimumMaxUploadSizeBytes) : 1
  const maximumMiB = settings ? bytesToMiB(settings.maximumMaxUploadSizeBytes) : 5242880

  return (
    <>
      <PageHeader
        eyebrow={t("settings.page.eyebrow")}
        title={t("settings.page.title")}
        description={t("settings.page.description")}
      />
      <div className="grid gap-5 xl:grid-cols-2">
        <SettingsSection title={t("settings.sections.uploadLimits.title")}>
          <div className="grid gap-3">
            <Label htmlFor="max-upload-size">{t("settings.sections.uploadLimits.inputLabel")}</Label>
            <div className="flex flex-col gap-2 sm:flex-row">
              <Input
                id="max-upload-size"
                type="number"
                min={minimumMiB}
                max={maximumMiB}
                step={1}
                value={maxUploadMiB}
                onChange={(event) => setMaxUploadMiB(event.target.value)}
              />
              <Button className="shrink-0" onClick={saveSettings} disabled={!settings || saving}>
                <SaveIcon />
                {t("common.actions.save")}
              </Button>
            </div>
            <div className="flex flex-wrap gap-2 text-xs text-muted-foreground">
              <Badge variant="secondary">
                {t("settings.sections.uploadLimits.current", {
                  value: settings ? formatBytes(settings.maxUploadSizeBytes) : "-",
                })}
              </Badge>
              <Badge variant="outline">
                {t("settings.sections.uploadLimits.range", {
                  min: settings ? formatBytes(settings.minimumMaxUploadSizeBytes) : "-",
                  max: settings ? formatBytes(settings.maximumMaxUploadSizeBytes) : "-",
                })}
              </Badge>
            </div>
          </div>
        </SettingsSection>
        <SettingsSection title={t("settings.sections.endpoints.title")}>
          <div className="grid gap-3">
            <Label htmlFor="public-origin">{t("settings.sections.endpoints.publicOrigin")}</Label>
            <div className="flex flex-col gap-2 sm:flex-row">
              <Input
                id="public-origin"
                placeholder="https://means.asia"
                value={publicOrigin}
                onChange={(event) => setPublicOrigin(event.target.value)}
              />
              <Button className="shrink-0" onClick={saveSettings} disabled={!settings || saving}>
                <SaveIcon />
                {t("common.actions.save")}
              </Button>
            </div>
          </div>
          <SettingRow label={t("settings.sections.endpoints.boundAlias")} value={boundAlias} onCopy={copy} />
          <SettingRow label="Path-style" value={pathStyle} onCopy={copy} />
          <SettingRow label="Virtual-hosted-style" value={virtualHosted} onCopy={copy} />
          <SettingRow label={t("settings.sections.endpoints.consoleAlias")} value={aliasPrefix} onCopy={copy} />
        </SettingsSection>
        <SettingsSection title={t("settings.sections.localStorage.title")}>
          <SettingRow label={t("settings.sections.localStorage.metadataStore")} value={overview?.metadataPath ?? "-"} onCopy={copy} />
          <SettingRow label={t("settings.sections.localStorage.objectBlobs")} value={overview?.objectsPath ?? "-"} onCopy={copy} />
          <SettingRow label={t("settings.sections.localStorage.version")} value={overview?.version ?? "-"} onCopy={copy} />
        </SettingsSection>
        <SettingsSection title={t("settings.sections.compression.title")}>
          <div className="flex flex-wrap gap-2">
            <Badge variant="outline">br</Badge>
            <Badge variant="outline">gzip</Badge>
            <Badge variant="outline">Vary: Accept-Encoding</Badge>
            <Badge variant="outline">{t("settings.sections.compression.badges.rangeNoCompression")}</Badge>
            <Badge variant="outline">{t("settings.sections.compression.badges.weakEtag")}</Badge>
          </div>
        </SettingsSection>
        <SettingsSection title={t("settings.sections.architecture.title")}>
          <div className="space-y-2 text-sm text-muted-foreground">
            <p>{t("settings.sections.architecture.currentMode")}</p>
            <p>{t("settings.sections.architecture.futureAbstraction")}</p>
          </div>
        </SettingsSection>
      </div>
    </>
  )
}

function bytesToMiB(value: number): number {
  return Math.round(value / 1024 / 1024)
}

function normalizePublicOriginInput(value: string): string | null {
  const trimmed = value.trim()
  if (!trimmed) {
    return null
  }

  const candidate = trimmed.includes("://") ? trimmed : `https://${trimmed}`
  const url = new URL(candidate)
  if (
    (url.protocol !== "http:" && url.protocol !== "https:") ||
    url.username ||
    url.password ||
    url.search ||
    url.hash ||
    url.pathname !== "/"
  ) {
    throw new Error("Invalid public origin")
  }

  return url.origin
}

function SettingsSection({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="rounded-lg border bg-card p-5 text-card-foreground shadow-xs">
      <h2 className="font-medium">{title}</h2>
      <Separator className="my-4" />
      <div className="space-y-3">{children}</div>
    </section>
  )
}

function SettingRow({
  label,
  value,
  onCopy,
}: {
  label: string
  value: string
  onCopy: (value: string) => void
}) {
  const { t } = useTranslation()

  return (
    <div className="grid gap-1.5">
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className="flex items-center gap-2 rounded-md border bg-background/50 px-3 py-2">
        <code className="min-w-0 flex-1 truncate text-xs">{value}</code>
        <Button variant="ghost" size="icon-xs" onClick={() => onCopy(value)}>
          <CopyIcon />
          <span className="sr-only">{t("common.actions.copy")}</span>
        </Button>
      </div>
    </div>
  )
}
