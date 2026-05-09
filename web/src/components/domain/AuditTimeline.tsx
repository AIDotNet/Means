import { Badge } from "@/components/ui/badge"
import type { AuditEntry } from "@/lib/api-client"
import { formatDateTime } from "@/lib/formatters"
import { useTranslation } from "@/i18n"

type AuditTimelineProps = {
  entries: AuditEntry[]
}

export function AuditTimeline({ entries }: AuditTimelineProps) {
  const { t } = useTranslation()

  if (entries.length === 0) {
    return (
      <div className="rounded-[1.15rem] border border-dashed border-slate-200 bg-white p-8 text-center text-sm text-muted-foreground shadow-[0_18px_45px_rgba(15,23,42,0.04)] dark:border-border dark:bg-card">
        {t("auditTimeline.empty")}
      </div>
    )
  }

  return (
    <div className="divide-y divide-slate-100 overflow-hidden rounded-[1.15rem] border border-slate-200/80 bg-white text-slate-950 shadow-[0_18px_45px_rgba(15,23,42,0.05)] dark:divide-border dark:border-border dark:bg-card dark:text-card-foreground">
      {entries.map((entry) => (
        <div key={entry.id} className="grid gap-3 p-4 md:grid-cols-[180px_1fr_auto] md:items-center">
          <div className="text-xs text-muted-foreground">{formatDateTime(entry.occurredAt)}</div>
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-medium">{entry.action}</span>
              <Badge variant={entry.status === "success" ? "outline" : "destructive"}>
                {entry.status}
              </Badge>
            </div>
            <div className="mt-1 truncate text-sm text-muted-foreground">
              {entry.resource}
              {entry.message ? ` · ${entry.message}` : ""}
            </div>
          </div>
          <div className="text-sm text-muted-foreground">{entry.actor}</div>
        </div>
      ))}
    </div>
  )
}
