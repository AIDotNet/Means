import { useEffect, useState, type ComponentType, type ReactNode } from "react"
import {
  ActivityIcon,
  AlertTriangleIcon,
  ArchiveIcon,
  DatabaseIcon,
  HardDriveIcon,
  RefreshCwIcon,
  ServerIcon,
} from "lucide-react"
import { useTranslation } from "@/i18n"
import { toast } from "sonner"

import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Separator } from "@/components/ui/separator"
import {
  api,
  type DashboardHourlyMetric,
  type DashboardPool,
  type DashboardRecentBucket,
  type DashboardStats,
} from "@/lib/api-client"
import { formatBytes, formatNumber } from "@/lib/formatters"
import { cn } from "@/lib/utils"

const DASHBOARD_HOURS = 24

export function DashboardPage() {
  const { t } = useTranslation()
  const [stats, setStats] = useState<DashboardStats | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    api
      .dashboardStats(DASHBOARD_HOURS)
      .then((nextStats) => {
        if (!cancelled) {
          setStats(nextStats)
        }
      })
      .catch((error: Error) => toast.error(error.message))
      .finally(() => {
        if (!cancelled) {
          setLoading(false)
        }
      })

    return () => {
      cancelled = true
    }
  }, [])

  const refresh = async () => {
    setLoading(true)
    try {
      setStats(await api.dashboardStats(DASHBOARD_HOURS))
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("dashboard.errors.refreshFailed"))
    } finally {
      setLoading(false)
    }
  }

  const hourly = stats?.hourly ?? []
  const totalErrors = sum(hourly, (point) => point.errorCount)
  const totalIngress = sum(hourly, (point) => point.ingressBytes)
  const totalEgress = sum(hourly, (point) => point.egressBytes)

  return (
    <div className="space-y-3">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
        <div>
          <div className="text-xs font-semibold tracking-[0.18em] text-slate-500 uppercase">
            {t("common.brand.consoleName")}
          </div>
          <h1 className="mt-1 text-2xl font-bold tracking-tight text-slate-950 dark:text-foreground">
            {t("common.brand.productName")}
          </h1>
          <p className="text-sm text-muted-foreground">
            {t("dashboard.subtitle", { hours: stats?.range.hours ?? DASHBOARD_HOURS })}
          </p>
        </div>
        <Button variant="outline" size="sm" onClick={refresh} disabled={loading}>
          <RefreshCwIcon className={loading ? "animate-spin" : ""} />
          {t("common.actions.refresh")}
        </Button>
      </div>

      <div className="grid gap-3 xl:grid-cols-[1.05fr_1fr_1.35fr]">
        <CapacityPanel stats={stats} loading={loading} />
        <div className="grid gap-3">
          <StatusPanel
            icon={ServerIcon}
            title={t("dashboard.status.servers")}
            online={stats?.nodes.serversOnline ?? 0}
            offline={stats?.nodes.serversOffline ?? 0}
            footnote={stats?.nodes.serviceHost ?? "api.means.local"}
          />
          <StatusPanel
            icon={HardDriveIcon}
            title={t("dashboard.status.drives")}
            online={stats?.nodes.drivesOnline ?? 0}
            offline={stats?.nodes.drivesOffline ?? 0}
            footnote={stats?.nodes.objectsPath ?? "-"}
          />
        </div>
        <BucketsPanel stats={stats} />
      </div>

      <div className="grid gap-3 xl:grid-cols-[1fr_1.18fr]">
        <ChartPanel
          icon={AlertTriangleIcon}
          title={t("dashboard.charts.errors.title")}
          value={t("dashboard.charts.totalValue", { value: formatNumber(totalErrors) })}
        >
          <ErrorBarChart points={hourly} />
        </ChartPanel>
        <ChartPanel
          icon={ActivityIcon}
          title={t("dashboard.charts.data.title")}
          value={t("dashboard.charts.totalValue", { value: formatBytes(totalIngress + totalEgress) })}
        >
          <DataBarChart points={hourly} />
        </ChartPanel>
      </div>

      <div className="grid gap-3 xl:grid-cols-2">
        {(stats?.pools ?? [emptyPool]).map((pool) => (
          <PoolPanel key={pool.name} pool={pool} />
        ))}
      </div>
    </div>
  )
}

function CapacityPanel({ stats, loading }: { stats: DashboardStats | null; loading: boolean }) {
  const { t } = useTranslation()
  const capacity = stats?.capacity
  const usedPercent = clampPercent(capacity?.usedPercent ?? 0)
  const totalBytes = capacity?.totalBytes ?? 0
  const freeBytes = capacity?.freeBytes ?? 0
  const objectBytes = capacity?.objectBytes ?? 0

  return (
    <Surface className="min-h-[13rem]">
      <PanelTitle icon={DatabaseIcon} title={t("dashboard.capacity.title")} value={formatBytes(totalBytes)} />
      <div className="mt-5 flex items-center gap-5">
        <CapacityDonut percent={usedPercent} muted={loading && !stats} />
        <div className="min-w-0 flex-1">
          <div className="text-4xl font-bold tracking-[-0.08em] text-slate-950 dark:text-foreground">
            {formatBytes(freeBytes)}
          </div>
          <div className="text-xs font-medium text-slate-500 uppercase">{t("dashboard.capacity.available")}</div>
          <div className="mt-4 space-y-2 text-xs">
            <KeyValue label={t("dashboard.capacity.objectData")} value={formatBytes(objectBytes)} accent="bg-blue-600" />
            <KeyValue label={t("dashboard.capacity.usedCapacity")} value={`${usedPercent.toFixed(1)}%`} accent="bg-slate-900" />
          </div>
        </div>
      </div>
    </Surface>
  )
}

function StatusPanel({
  icon: Icon,
  title,
  online,
  offline,
  footnote,
}: {
  icon: ComponentType<{ className?: string }>
  title: string
  online: number
  offline: number
  footnote: string
}) {
  const { t } = useTranslation()

  return (
    <Surface className="min-h-[6.25rem]">
      <PanelTitle icon={Icon} title={title} value={`${online + offline}`} />
      <div className="mt-4">
        <ProgressStrip online={online} offline={offline} />
        <div className="mt-2 flex items-center justify-between gap-2 text-[11px]">
          <StatusPill label={t("dashboard.status.online")} value={online} color="bg-emerald-500" />
          <StatusPill label={t("dashboard.status.offline")} value={offline} color="bg-slate-950 dark:bg-slate-200" />
        </div>
        <div className="mt-2 truncate font-mono text-[10px] text-muted-foreground">{footnote}</div>
      </div>
    </Surface>
  )
}

function BucketsPanel({ stats }: { stats: DashboardStats | null }) {
  const { t } = useTranslation()

  return (
    <Surface className="min-h-[13rem]">
      <PanelTitle icon={ArchiveIcon} title={t("dashboard.buckets.title")} value={`${stats?.summary.bucketCount ?? 0}`} />
      <div className="mt-4 grid grid-cols-3 gap-2 text-center">
        <MiniMetric label={t("dashboard.buckets.metrics.buckets")} value={formatNumber(stats?.summary.bucketCount ?? 0)} />
        <MiniMetric label={t("dashboard.buckets.metrics.objects")} value={formatNumber(stats?.summary.objectCount ?? 0)} />
        <MiniMetric label={t("dashboard.buckets.metrics.size")} value={formatBytes(stats?.summary.totalBytes ?? 0)} />
      </div>
      <Separator className="my-4" />
      <div className="flex items-center justify-between gap-3">
        <div className="text-xs font-semibold uppercase tracking-[0.14em] text-slate-500">
          {t("dashboard.buckets.recentActivity")}
        </div>
        <Badge variant="outline" className="border-blue-200 bg-blue-50 text-blue-700 dark:bg-blue-950/30">
          {t("dashboard.buckets.live")}
        </Badge>
      </div>
      <div className="mt-3 space-y-2">
        {(stats?.recentBuckets ?? []).length > 0 ? (
          stats?.recentBuckets.map((bucket) => (
            <RecentBucketRow key={bucket.bucketName} bucket={bucket} />
          ))
        ) : (
          <div className="rounded-lg border border-dashed p-5 text-sm text-muted-foreground">
            {t("dashboard.buckets.empty")}
          </div>
        )}
      </div>
    </Surface>
  )
}

function ChartPanel({
  icon: Icon,
  title,
  value,
  children,
}: {
  icon: ComponentType<{ className?: string }>
  title: string
  value: string
  children: ReactNode
}) {
  return (
    <Surface>
      <PanelTitle icon={Icon} title={title} value={value} />
      <div className="mt-3">{children}</div>
    </Surface>
  )
}

function PoolPanel({ pool }: { pool: DashboardPool }) {
  const { t } = useTranslation()
  const usedPercent = pool.totalBytes > 0 ? (pool.usedBytes / pool.totalBytes) * 100 : 0

  return (
    <Surface>
      <PanelTitle icon={DatabaseIcon} title={pool.name} value={formatBytes(pool.totalBytes)} />
      <div className="mt-4 grid gap-4 sm:grid-cols-[8rem_1fr] sm:items-center">
        <CapacityDonut percent={clampPercent(usedPercent)} size="sm" />
        <div className="space-y-3">
          <div>
            <div className="text-xs font-semibold text-slate-500 uppercase">{t("dashboard.pool.capacity")}</div>
            <div className="mt-1 flex items-end gap-2">
              <span className="text-2xl font-bold tracking-tight">{formatBytes(pool.objectBytes)}</span>
              <span className="pb-1 text-xs text-muted-foreground">{t("dashboard.pool.objectData")}</span>
            </div>
          </div>
          <ProgressStrip online={pool.onlineDrives} offline={pool.offlineDrives} />
          <div className="flex flex-wrap gap-3 text-[11px]">
            <StatusPill label={t("dashboard.pool.drives")} value={pool.driveCount} color="bg-blue-600" />
            <StatusPill label={t("dashboard.status.online")} value={pool.onlineDrives} color="bg-emerald-500" />
            <StatusPill label={t("dashboard.status.offline")} value={pool.offlineDrives} color="bg-slate-950 dark:bg-slate-200" />
          </div>
        </div>
      </div>
    </Surface>
  )
}

function ErrorBarChart({ points }: { points: DashboardHourlyMetric[] }) {
  const { t } = useTranslation()
  const [activeIndex, setActiveIndex] = useState<number | null>(null)
  const width = 620
  const height = 178
  const padding = 18
  const maxValue = Math.max(1, ...points.map((point) => point.errorCount))
  const step = points.length > 0 ? (width - padding * 2) / points.length : 0
  const barWidth = Math.max(4, step * 0.62)
  const activePoint = activeIndex === null ? null : points[activeIndex] ?? null
  const activeX =
    activeIndex === null || points.length === 0
      ? 50
      : ((padding + activeIndex * step + step / 2) / width) * 100

  return (
    <div className="relative" onMouseLeave={() => setActiveIndex(null)}>
      <svg className="h-44 w-full overflow-hidden" viewBox={`0 0 ${width} ${height}`} role="img">
        <title>{t("dashboard.charts.errors.svgTitle")}</title>
        {[0, 0.25, 0.5, 0.75, 1].map((line) => (
          <line
            key={line}
            x1={padding}
            x2={width - padding}
            y1={padding + line * (height - padding * 2)}
            y2={padding + line * (height - padding * 2)}
            stroke="currentColor"
            className="text-slate-200 dark:text-slate-700"
            strokeDasharray="4 8"
          />
        ))}
        {points.map((point, index) => {
          const value = point.errorCount
          const barHeight = (value / maxValue) * (height - padding * 2)
          const x = padding + index * step + (step - barWidth) / 2
          const y = height - padding - barHeight
          const hot = value > 0
          const active = activeIndex === index

          return (
            <g key={point.hourUtc} onMouseEnter={() => setActiveIndex(index)}>
              <rect
                x={padding + index * step}
                y={padding}
                width={Math.max(1, step)}
                height={height - padding * 2}
                pointerEvents="all"
                className="fill-transparent"
              />
              <rect
                x={x}
                y={y}
                width={barWidth}
                height={Math.max(1, barHeight)}
                rx="2"
                className={cn(
                  hot ? "fill-red-500" : "fill-slate-200 dark:fill-slate-700",
                  active && "opacity-80"
                )}
              >
                <title>
                  {t("dashboard.charts.errors.tooltip", {
                    hour: formatHour(point.hourUtc),
                    count: formatNumber(value),
                  })}
                </title>
              </rect>
              {index % 4 === 0 ? (
                <text x={x} y={height - 2} className="fill-slate-400 text-[10px]">
                  {formatHour(point.hourUtc)}
                </text>
              ) : null}
            </g>
          )
        })}
      </svg>
      {activePoint ? (
        <ChartTooltip
          xPercent={activeX}
          title={`${formatHour(activePoint.hourUtc)} API`}
          rows={[
            { label: t("dashboard.charts.errors.title"), value: formatNumber(activePoint.errorCount), color: "bg-red-500" },
            { label: t("dashboard.charts.requests"), value: formatNumber(activePoint.requestCount), color: "bg-slate-400" },
          ]}
        />
      ) : null}
    </div>
  )
}

function DataBarChart({ points }: { points: DashboardHourlyMetric[] }) {
  const { t } = useTranslation()
  const [activeIndex, setActiveIndex] = useState<number | null>(null)
  const width = 720
  const height = 178
  const paddingX = 22
  const paddingY = 18
  const chartHeight = height - paddingY * 2
  const maxValue = Math.max(1, ...points.flatMap((point) => [point.ingressBytes, point.egressBytes]))
  const step = points.length > 0 ? (width - paddingX * 2) / points.length : 0
  const groupWidth = Math.max(8, step * 0.66)
  const barGap = Math.max(2, groupWidth * 0.14)
  const barWidth = Math.max(3, (groupWidth - barGap) / 2)
  const activePoint = activeIndex === null ? null : points[activeIndex] ?? null
  const activeX =
    activeIndex === null || points.length === 0
      ? 50
      : ((paddingX + activeIndex * step + step / 2) / width) * 100

  return (
    <div className="relative" onMouseLeave={() => setActiveIndex(null)}>
      <svg className="h-44 w-full overflow-hidden" viewBox={`0 0 ${width} ${height}`} role="img">
        <title>{t("dashboard.charts.data.svgTitle")}</title>
        {[0, 0.25, 0.5, 0.75, 1].map((line) => (
          <line
            key={line}
            x1={paddingX}
            x2={width - paddingX}
            y1={paddingY + line * (height - paddingY * 2)}
            y2={paddingY + line * (height - paddingY * 2)}
            stroke="currentColor"
            className="text-slate-200 dark:text-slate-700"
            strokeDasharray="4 8"
          />
        ))}
        {points.map((point, index) => {
          const x = paddingX + index * step + (step - groupWidth) / 2
          const ingressHeight = (point.ingressBytes / maxValue) * chartHeight
          const egressHeight = (point.egressBytes / maxValue) * chartHeight
          const active = activeIndex === index

          return (
            <g key={point.hourUtc} onMouseEnter={() => setActiveIndex(index)}>
              <rect
                x={paddingX + index * step}
                y={paddingY}
                width={Math.max(1, step)}
                height={chartHeight}
                pointerEvents="all"
                className="fill-transparent"
              />
              <rect
                x={x}
                y={height - paddingY - ingressHeight}
                width={barWidth}
                height={Math.max(1, ingressHeight)}
                rx="2"
                className={cn("fill-blue-600", active ? "opacity-90" : "opacity-70")}
              >
                <title>
                  {formatHour(point.hourUtc)} {t("dashboard.charts.data.ingress")}: {formatBytes(point.ingressBytes)}
                </title>
              </rect>
              <rect
                x={x + barWidth + barGap}
                y={height - paddingY - egressHeight}
                width={barWidth}
                height={Math.max(1, egressHeight)}
                rx="2"
                className={cn("fill-violet-600", active ? "opacity-90" : "opacity-70")}
              >
                <title>
                  {formatHour(point.hourUtc)} {t("dashboard.charts.data.egress")}: {formatBytes(point.egressBytes)}
                </title>
              </rect>
              {index % 4 === 0 ? (
                <text x={x - 2} y={height - 2} className="fill-slate-400 text-[10px]">
                  {formatHour(point.hourUtc)}
                </text>
              ) : null}
            </g>
          )
        })}
      </svg>
      {activePoint ? (
        <ChartTooltip
          xPercent={activeX}
          title={`${formatHour(activePoint.hourUtc)} ${t("dashboard.charts.data.title")}`}
          rows={[
            { label: t("dashboard.charts.data.ingress"), value: formatBytes(activePoint.ingressBytes), color: "bg-blue-600" },
            { label: t("dashboard.charts.data.egress"), value: formatBytes(activePoint.egressBytes), color: "bg-violet-600" },
            { label: t("dashboard.charts.requests"), value: formatNumber(activePoint.requestCount), color: "bg-slate-400" },
          ]}
        />
      ) : null}
      <div className="mt-1 flex flex-wrap items-center gap-4 text-xs text-muted-foreground">
        <KeyValue label={t("dashboard.charts.data.ingress")} value={formatBytes(sum(points, (point) => point.ingressBytes))} accent="bg-blue-600" />
        <KeyValue label={t("dashboard.charts.data.egress")} value={formatBytes(sum(points, (point) => point.egressBytes))} accent="bg-violet-600" />
      </div>
    </div>
  )
}

function ChartTooltip({
  xPercent,
  title,
  rows,
}: {
  xPercent: number
  title: string
  rows: { label: string; value: string; color: string }[]
}) {
  return (
    <div
      className="pointer-events-none absolute top-2 z-10 min-w-32 rounded-md border border-slate-200 bg-white/95 px-3 py-2 text-xs shadow-lg shadow-slate-950/10 backdrop-blur dark:border-border dark:bg-card/95"
      style={{ left: `${xPercent}%`, transform: tooltipTransform(xPercent) }}
    >
      <div className="mb-1 font-semibold text-slate-950 dark:text-foreground">{title}</div>
      <div className="space-y-1">
        {rows.map((row) => (
          <div key={row.label} className="flex items-center justify-between gap-4">
            <span className="inline-flex items-center gap-1.5 text-muted-foreground">
              <span className={cn("size-1.5 rounded-full", row.color)} />
              {row.label}
            </span>
            <span className="font-semibold text-foreground">{row.value}</span>
          </div>
        ))}
      </div>
    </div>
  )
}

function RecentBucketRow({ bucket }: { bucket: DashboardRecentBucket }) {
  const { t } = useTranslation()

  return (
    <div className="grid grid-cols-[1fr_auto] items-center gap-3 rounded-lg border bg-slate-50/75 px-3 py-2 dark:bg-muted/30">
      <div className="min-w-0">
        <div className="truncate text-sm font-semibold">{bucket.bucketName}</div>
        <div className="mt-0.5 text-[11px] text-muted-foreground">
          {t("dashboard.buckets.recentLine", {
            requests: formatNumber(bucket.requestCount),
            errors: formatNumber(bucket.errorCount),
            relative: formatRelative(bucket.lastActivityAt, t),
          })}
        </div>
      </div>
      <div className="text-right">
        <div className="text-xs font-semibold">{formatBytes(bucket.totalBytes)}</div>
        <div className="text-[10px] text-muted-foreground">
          {t("dashboard.buckets.objectCount", { count: formatNumber(bucket.objectCount) })}
        </div>
      </div>
    </div>
  )
}

function Surface({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <section
      className={cn(
        "min-w-0 rounded-[1.15rem] border border-slate-200/80 bg-white p-4 text-slate-950 shadow-[0_18px_45px_rgba(15,23,42,0.06)] dark:border-border dark:bg-card dark:text-card-foreground",
        className
      )}
    >
      {children}
    </section>
  )
}

function PanelTitle({
  icon: Icon,
  title,
  value,
}: {
  icon: ComponentType<{ className?: string }>
  title: string
  value?: string
}) {
  return (
    <div className="flex items-center justify-between gap-3">
      <div className="flex min-w-0 items-center gap-2">
        <Icon className="size-3.5 text-slate-700 dark:text-muted-foreground" />
        <span className="truncate text-xs font-bold tracking-tight">{title}</span>
      </div>
      {value ? <div className="truncate text-[10px] font-semibold text-slate-500">{value}</div> : null}
    </div>
  )
}

function MiniMetric({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-xl bg-slate-50 px-3 py-2 dark:bg-muted/30">
      <div className="truncate text-lg font-bold tracking-tight">{value}</div>
      <div className="text-[10px] font-medium text-muted-foreground">{label}</div>
    </div>
  )
}

function CapacityDonut({
  percent,
  muted = false,
  size = "lg",
}: {
  percent: number
  muted?: boolean
  size?: "sm" | "lg"
}) {
  const { t } = useTranslation()
  const radius = 42
  const circumference = 2 * Math.PI * radius
  const offset = circumference - (clampPercent(percent) / 100) * circumference
  const dimension = size === "lg" ? "size-32" : "size-24"

  return (
    <svg className={cn(dimension, muted && "opacity-40")} viewBox="0 0 120 120" role="img">
      <title>{t("dashboard.capacity.donutTitle", { percent: percent.toFixed(1) })}</title>
      <circle
        cx="60"
        cy="60"
        r={radius}
        fill="none"
        stroke="currentColor"
        strokeWidth="20"
        className="text-slate-100 dark:text-slate-800"
      />
      <circle
        cx="60"
        cy="60"
        r={radius}
        fill="none"
        stroke="#2556f4"
        strokeDasharray={circumference}
        strokeDashoffset={offset}
        strokeLinecap="butt"
        strokeWidth="20"
        transform="rotate(-90 60 60)"
      />
    </svg>
  )
}

function ProgressStrip({ online, offline }: { online: number; offline: number }) {
  const total = Math.max(1, online + offline)
  const onlineWidth = (online / total) * 100

  return (
    <div className="flex h-2 overflow-hidden rounded-full bg-slate-950 dark:bg-slate-800">
      <div className="bg-emerald-500" style={{ width: `${onlineWidth}%` }} />
    </div>
  )
}

function StatusPill({ label, value, color }: { label: string; value: number; color: string }) {
  return (
    <span className="inline-flex items-center gap-1 text-muted-foreground">
      <span className={cn("size-1.5 rounded-full", color)} />
      {label} <span className="font-semibold text-foreground">{formatNumber(value)}</span>
    </span>
  )
}

function KeyValue({
  label,
  value,
  accent,
}: {
  label: string
  value: string
  accent: string
}) {
  return (
    <span className="inline-flex items-center gap-1.5">
      <span className={cn("size-2 rounded-full", accent)} />
      <span className="text-muted-foreground">{label}</span>
      <span className="font-semibold text-foreground">{value}</span>
    </span>
  )
}

function sum<T>(items: T[], selector: (item: T) => number) {
  return items.reduce((total, item) => total + selector(item), 0)
}

function clampPercent(value: number) {
  if (!Number.isFinite(value)) {
    return 0
  }

  return Math.min(100, Math.max(0, value))
}

function tooltipTransform(xPercent: number) {
  if (xPercent < 18) {
    return "translateX(0)"
  }

  if (xPercent > 82) {
    return "translateX(-100%)"
  }

  return "translateX(-50%)"
}

function formatHour(value: string) {
  const date = new Date(value)
  return `${date.getHours().toString().padStart(2, "0")}:00`
}

function formatRelative(
  value: string | null,
  t: (key: string, options?: Record<string, unknown>) => string
) {
  if (!value) {
    return t("dashboard.relative.noRecentRequest")
  }

  const elapsedMs = Date.now() - new Date(value).getTime()
  const minutes = Math.max(0, Math.floor(elapsedMs / 60000))
  if (minutes < 1) {
    return t("dashboard.relative.justNow")
  }

  if (minutes < 60) {
    return t("dashboard.relative.minutesAgo", { count: minutes })
  }

  const hours = Math.floor(minutes / 60)
  if (hours < 24) {
    return t("dashboard.relative.hoursAgo", { count: hours })
  }

  return formatHour(value)
}

const emptyPool: DashboardPool = {
  name: "Pool 1",
  totalBytes: 0,
  usedBytes: 0,
  freeBytes: 0,
  objectBytes: 0,
  driveCount: 0,
  onlineDrives: 0,
  offlineDrives: 0,
}
