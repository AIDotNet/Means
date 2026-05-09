import { useCallback, useEffect, useState } from "react"
import { RefreshCwIcon } from "lucide-react"
import { toast } from "sonner"

import { AuditTimeline } from "@/components/domain/AuditTimeline"
import { PageHeader } from "@/components/layout/PageHeader"
import { Button } from "@/components/ui/button"
import { api, type AuditEntry } from "@/lib/api-client"
import { useTranslation } from "@/i18n"

export function AuditPage() {
  const { t } = useTranslation()
  const [entries, setEntries] = useState<AuditEntry[]>([])
  const [loading, setLoading] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setEntries(await api.audit())
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("audit.errors.loadFailed"))
    } finally {
      setLoading(false)
    }
  }, [t])

  useEffect(() => {
    void load()
  }, [load])

  return (
    <>
      <PageHeader
        eyebrow={t("audit.page.eyebrow")}
        title={t("audit.page.title")}
        description={t("audit.page.description")}
        actions={
          <Button variant="outline" onClick={load} disabled={loading}>
            <RefreshCwIcon className={loading ? "animate-spin" : ""} />
            {t("common.actions.refresh")}
          </Button>
        }
      />
      <AuditTimeline entries={entries} />
    </>
  )
}
