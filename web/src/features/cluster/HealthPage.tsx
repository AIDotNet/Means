import { useEffect, useMemo, useState, type ComponentType, type ReactNode } from "react"
import {
  AlertTriangleIcon,
  DatabaseZapIcon,
  HardDriveIcon,
  RefreshCwIcon,
  RotateCwIcon,
  ShieldCheckIcon,
  StethoscopeIcon,
} from "lucide-react"
import { toast } from "sonner"

import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Separator } from "@/components/ui/separator"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import { useTranslation } from "@/i18n"
import {
  api,
  type BackgroundTaskManagement,
  type BackgroundTaskRunRecord,
  type BackgroundTaskSnapshot,
  type ClusterDiagnostics,
  type ReplicaRepairQueueItemDiagnostics,
  type StorageDiskInfo,
} from "@/lib/api-client"
import { formatBytes, formatDateTime, formatNumber } from "@/lib/formatters"
import { cn } from "@/lib/utils"

type HealthPageState = {
  diagnostics: ClusterDiagnostics
  tasks: BackgroundTaskManagement
}

type DiskRow = StorageDiskInfo & {
  nodeStatus: string
}

export function HealthPage() {
  const { t } = useTranslation()
  const [state, setState] = useState<HealthPageState | null>(null)
  const [loading, setLoading] = useState(true)
  const [runningTaskId, setRunningTaskId] = useState<string | null>(null)

  const load = async (showLoading: boolean) => {
    if (showLoading) {
      setLoading(true)
    }

    try {
      const [diagnostics, tasks] = await Promise.all([api.diagnostics(), api.backgroundTasks()])
      setState({ diagnostics, tasks })
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("health.errors.loadFailed"))
    } finally {
      if (showLoading) {
        setLoading(false)
      }
    }
  }

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    Promise.all([api.diagnostics(), api.backgroundTasks()])
      .then(([diagnostics, tasks]) => {
        if (!cancelled) {
          setState({ diagnostics, tasks })
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

  const runTask = async (taskId: string) => {
    setRunningTaskId(taskId)
    try {
      const snapshot = await api.runBackgroundTask(taskId)
      setState((current) => current ? { ...current, tasks: replaceTask(current.tasks, snapshot) } : current)
      toast.success(t("health.tasks.runSucceeded", { task: snapshot.name }))
      await load(false)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("health.tasks.runFailed"))
    } finally {
      setRunningTaskId(null)
    }
  }

  const diagnostics = state?.diagnostics
  const summary = diagnostics?.summary
  const replica = diagnostics?.objectReplicas
  const repair = diagnostics?.repairQueue
  const erasureCoding = diagnostics?.erasureCoding
  const nodes = diagnostics?.topology.nodes ?? []
  const disks = useMemo<DiskRow[]>(
    () =>
      nodes.flatMap((node) =>
        node.disks.map((disk) => ({
          ...disk,
          nodeStatus: node.status,
        }))
      ),
    [nodes]
  )
  const unhealthyCount =
    (summary?.offlineNodeCount ?? 0)
    + (summary?.offlineDiskCount ?? 0)
    + (replica?.underReplicatedObjectCount ?? 0)
    + (repair?.failedCount ?? 0)
  const repairStatusSummary = repair?.statuses.length
    ? repair.statuses
        .map((status) => `${t(`health.repairStatuses.${status.status.toLowerCase()}`, { defaultValue: status.status })}: ${formatNumber(status.count)}`)
        .join(" / ")
    : t("health.diagnostics.none")

  return (
    <div className="space-y-3">
      <PageHeader
        eyebrow={t("health.page.eyebrow")}
        title={t("health.page.title")}
        description={t("health.page.description")}
        action={
          <Button variant="outline" size="sm" onClick={() => load(true)} disabled={loading}>
            <RefreshCwIcon className={loading ? "animate-spin" : ""} />
            {t("common.actions.refresh")}
          </Button>
        }
      />

      <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
        <HealthTile
          icon={ShieldCheckIcon}
          label={t("health.metrics.overall")}
          value={unhealthyCount === 0 ? t("health.metrics.healthy") : formatNumber(unhealthyCount)}
          detail={t("health.metrics.unhealthyDetail")}
          tone={unhealthyCount === 0 ? "ok" : "warn"}
        />
        <HealthTile
          icon={HardDriveIcon}
          label={t("health.metrics.disks")}
          value={formatNumber(summary?.diskCount ?? disks.length)}
          detail={t("health.metrics.onlineOffline", {
            online: summary?.onlineDiskCount ?? disks.filter((disk) => disk.status === "Online").length,
            offline: summary?.offlineDiskCount ?? disks.filter((disk) => disk.status !== "Online").length,
          })}
          tone={(summary?.offlineDiskCount ?? 0) > 0 ? "warn" : "ok"}
        />
        <HealthTile
          icon={DatabaseZapIcon}
          label={t("health.metrics.replicas")}
          value={formatNumber(replica?.missingReplicaFileCount ?? 0)}
          detail={t("health.metrics.missingReplicaFiles")}
          tone={(replica?.missingReplicaFileCount ?? 0) > 0 ? "warn" : "ok"}
        />
        <HealthTile
          icon={AlertTriangleIcon}
          label={t("health.metrics.repairQueue")}
          value={formatNumber(repair?.pendingCount ?? 0)}
          detail={t("health.metrics.failedRepairs", { count: repair?.failedCount ?? 0 })}
          tone={(repair?.failedCount ?? 0) > 0 ? "warn" : "neutral"}
        />
      </div>

      <div className="grid gap-3 xl:grid-cols-[1.2fr_0.8fr]">
        <Surface>
          <PanelTitle icon={HardDriveIcon} title={t("health.disks.title")} value={t("health.disks.count", { count: disks.length })} />
          <div className="mt-4">
            <DiskTable disks={disks} loading={loading && !state} />
          </div>
        </Surface>
        <Surface>
          <PanelTitle icon={StethoscopeIcon} title={t("health.diagnostics.title")} value={diagnostics?.generatedAt ? formatDateTime(diagnostics.generatedAt) : "-"} />
          <div className="mt-4 grid gap-3">
            <DiagnosticBlock
              title={t("health.diagnostics.replicaTitle")}
              facts={[
                [t("health.diagnostics.desiredReplicas"), formatNumber(replica?.desiredReplicaCount ?? 0)],
                [t("health.diagnostics.replicaRecords"), formatNumber(replica?.replicaRecordCount ?? 0)],
                [t("health.diagnostics.underReplicated"), formatNumber(replica?.underReplicatedObjectCount ?? 0)],
                [t("health.diagnostics.withoutManifest"), formatNumber(replica?.objectsWithoutReplicaManifestCount ?? 0)],
              ]}
            />
            <DiagnosticBlock
              title={t("health.diagnostics.repairTitle")}
              facts={[
                [t("health.diagnostics.pending"), formatNumber(repair?.pendingCount ?? 0)],
                [t("health.diagnostics.completed"), formatNumber(repair?.completedCount ?? 0)],
                [t("health.diagnostics.failed"), formatNumber(repair?.failedCount ?? 0)],
                [t("health.diagnostics.retryable"), formatNumber(repair?.retryableFailedCount ?? 0)],
                [t("health.diagnostics.maxAttempts"), formatNumber(repair?.maxAttemptsReachedCount ?? 0)],
                [t("health.diagnostics.statusMix"), repairStatusSummary],
                [t("health.diagnostics.oldestPending"), repair?.oldestPendingAt ? formatDateTime(repair.oldestPendingAt) : t("health.diagnostics.none")],
                [t("health.diagnostics.lastUpdated"), repair?.lastUpdatedAt ? formatDateTime(repair.lastUpdatedAt) : t("health.diagnostics.none")],
              ]}
            />
            <DiagnosticBlock
              title={t("health.diagnostics.ecTitle")}
              facts={[
                [t("health.diagnostics.ecProfiles"), formatNumber(erasureCoding?.profileCount ?? 0)],
                [t("health.diagnostics.ecEnabled"), formatNumber(erasureCoding?.enabledProfileCount ?? 0)],
                [t("health.diagnostics.ecDisabled"), formatNumber(erasureCoding?.disabledProfileCount ?? 0)],
              ]}
            />
          </div>
        </Surface>
      </div>

      <Surface>
        <PanelTitle
          icon={AlertTriangleIcon}
          title={t("health.repairItems.title")}
          value={t("health.repairItems.count", { count: repair?.items.length ?? 0 })}
        />
        <div className="mt-4">
          <RepairQueueTable items={repair?.items ?? []} loading={loading && !state} />
        </div>
      </Surface>

      <Surface>
        <PanelTitle icon={RotateCwIcon} title={t("health.tasks.title")} value={t("health.tasks.count", { count: state?.tasks.tasks.length ?? 0 })} />
        <div className="mt-4 grid gap-3 xl:grid-cols-2">
          {(state?.tasks.groups ?? []).map((group) => (
            <TaskGroup key={group.category} title={group.name} tasks={group.tasks} runningTaskId={runningTaskId} onRun={runTask} />
          ))}
        </div>
      </Surface>

      <Surface>
        <PanelTitle icon={StethoscopeIcon} title={t("health.tasks.historyTitle")} value={t("health.tasks.historyCount", { count: state?.tasks.history.length ?? 0 })} />
        <div className="mt-4">
          <TaskHistoryTable history={state?.tasks.history ?? []} />
        </div>
      </Surface>
    </div>
  )
}

function RepairQueueTable({
  items,
  loading,
}: {
  items: ReplicaRepairQueueItemDiagnostics[]
  loading: boolean
}) {
  const { t } = useTranslation()

  if (loading) {
    return <EmptyState>{t("health.repairItems.loading")}</EmptyState>
  }

  if (items.length === 0) {
    return <EmptyState>{t("health.repairItems.empty")}</EmptyState>
  }

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>{t("health.repairItems.columns.object")}</TableHead>
          <TableHead>{t("health.repairItems.columns.status")}</TableHead>
          <TableHead>{t("health.repairItems.columns.reason")}</TableHead>
          <TableHead>{t("health.repairItems.columns.attempts")}</TableHead>
          <TableHead>{t("health.repairItems.columns.schedule")}</TableHead>
          <TableHead>{t("health.repairItems.columns.error")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {items.map((item) => (
          <TableRow key={`${item.objectId}:${item.reason}`}>
            <TableCell>
              <div className="max-w-72 truncate font-semibold">{item.key}</div>
              <div className="max-w-72 truncate text-xs text-muted-foreground">{item.bucketName}</div>
              <div className="max-w-72 truncate font-mono text-[11px] text-muted-foreground">{item.objectId}</div>
            </TableCell>
            <TableCell><RepairStatusBadge status={item.status} /></TableCell>
            <TableCell>
              <div className="max-w-48 truncate font-mono text-xs">{item.reason}</div>
            </TableCell>
            <TableCell>{formatNumber(item.attemptCount)}</TableCell>
            <TableCell>
              <div className="text-xs">
                <span className="font-medium text-muted-foreground">{t("health.repairItems.queued")}</span>{" "}
                {formatDateTime(item.queuedAt)}
              </div>
              <div className="mt-1 text-xs">
                <span className="font-medium text-muted-foreground">{t("health.repairItems.next")}</span>{" "}
                {item.nextAttemptAt ? formatDateTime(item.nextAttemptAt) : t("health.diagnostics.none")}
              </div>
            </TableCell>
            <TableCell>
              <div className="max-w-72 truncate font-mono text-xs text-muted-foreground">
                {item.lastError ?? t("health.diagnostics.none")}
              </div>
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  )
}

function DiskTable({ disks, loading }: { disks: DiskRow[]; loading: boolean }) {
  const { t } = useTranslation()

  if (loading) {
    return <EmptyState>{t("health.disks.loading")}</EmptyState>
  }

  if (disks.length === 0) {
    return <EmptyState>{t("health.disks.empty")}</EmptyState>
  }

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>{t("health.disks.columns.disk")}</TableHead>
          <TableHead>{t("health.disks.columns.node")}</TableHead>
          <TableHead>{t("health.disks.columns.capacity")}</TableHead>
          <TableHead>{t("health.disks.columns.available")}</TableHead>
          <TableHead>{t("health.disks.columns.lastSeen")}</TableHead>
          <TableHead>{t("health.disks.columns.status")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {disks.map((disk) => (
          <TableRow key={`${disk.nodeId}:${disk.diskId}`}>
            <TableCell>
              <div className="font-semibold">{disk.diskId}</div>
              <div className="max-w-72 truncate font-mono text-xs text-muted-foreground">{disk.mountPath}</div>
            </TableCell>
            <TableCell>
              <div className="font-medium">{disk.nodeId}</div>
              <div className="text-xs text-muted-foreground">{disk.poolId}</div>
            </TableCell>
            <TableCell>{formatBytes(disk.totalBytes)}</TableCell>
            <TableCell>{formatBytes(disk.availableBytes)}</TableCell>
            <TableCell>{formatDateTime(disk.lastSeenAt)}</TableCell>
            <TableCell>
              <div className="flex flex-wrap gap-1.5">
                <StatusBadge status={disk.status} />
                {disk.nodeStatus !== disk.status ? <StatusBadge status={disk.nodeStatus} /> : null}
              </div>
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  )
}

function DiagnosticBlock({ title, facts }: { title: string; facts: Array<[string, string]> }) {
  return (
    <div className="rounded-xl border border-slate-200 bg-slate-50/80 p-3 dark:border-border dark:bg-muted/30">
      <div className="text-sm font-bold">{title}</div>
      <div className="mt-3 grid gap-2 sm:grid-cols-2">
        {facts.map(([label, value]) => (
          <div key={label}>
            <div className="text-[11px] font-semibold uppercase tracking-[0.08em] text-muted-foreground">{label}</div>
            <div className="mt-0.5 break-words text-sm font-bold sm:text-base">{value}</div>
          </div>
        ))}
      </div>
    </div>
  )
}

function TaskGroup({
  title,
  tasks,
  runningTaskId,
  onRun,
}: {
  title: string
  tasks: BackgroundTaskSnapshot[]
  runningTaskId: string | null
  onRun: (taskId: string) => void
}) {
  const { t } = useTranslation()

  return (
    <div className="rounded-xl border border-slate-200 bg-slate-50/80 p-3 dark:border-border dark:bg-muted/30">
      <div className="mb-3 flex items-center justify-between gap-3">
        <div className="text-sm font-bold">{title}</div>
        <Badge variant="outline">{t("health.tasks.count", { count: tasks.length })}</Badge>
      </div>
      <div className="space-y-2">
        {tasks.map((task) => {
          const running = runningTaskId === task.taskId || task.status === "Running"
          return (
            <div key={task.taskId} className="rounded-lg bg-white p-3 shadow-xs ring-1 ring-slate-200 dark:bg-card dark:ring-border">
              <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                <div className="min-w-0">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="truncate text-sm font-semibold">{task.name}</span>
                    <StatusBadge status={task.status} />
                  </div>
                  <div className="mt-0.5 font-mono text-[11px] text-muted-foreground">{task.taskId}</div>
                </div>
                <Button
                  size="sm"
                  variant="outline"
                  disabled={!task.manualRunSupported || running}
                  onClick={() => onRun(task.taskId)}
                >
                  <RotateCwIcon className={running ? "animate-spin" : ""} />
                  {t("health.tasks.runNow")}
                </Button>
              </div>
              <Separator className="my-2" />
              <div className="grid gap-2 text-xs text-muted-foreground sm:grid-cols-3">
                <span>{t("health.tasks.interval", { seconds: task.intervalSeconds })}</span>
                <span>{t("health.tasks.successes", { count: task.successCount })}</span>
                <span>{task.lastCompletedAt ? formatDateTime(task.lastCompletedAt) : t("health.tasks.neverRun")}</span>
              </div>
              {task.lastError ? (
                <div className="mt-2 rounded-md bg-red-50 px-2 py-1 font-mono text-xs text-red-700 dark:bg-red-950/30">
                  {task.lastError}
                </div>
              ) : null}
            </div>
          )
        })}
      </div>
    </div>
  )
}

function TaskHistoryTable({ history }: { history: BackgroundTaskRunRecord[] }) {
  const { t } = useTranslation()

  if (history.length === 0) {
    return <EmptyState>{t("health.tasks.historyEmpty")}</EmptyState>
  }

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>{t("health.tasks.historyTask")}</TableHead>
          <TableHead>{t("health.tasks.historyStatus")}</TableHead>
          <TableHead>{t("health.tasks.historyCompleted")}</TableHead>
          <TableHead>{t("health.tasks.historyDuration")}</TableHead>
          <TableHead>{t("health.tasks.historyResult")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {history.slice(0, 12).map((run, index) => (
          <TableRow key={`${run.taskId}:${run.startedAt}:${index}`}>
            <TableCell>
              <div className="font-semibold">{run.name}</div>
              <div className="font-mono text-xs text-muted-foreground">{run.taskId}</div>
            </TableCell>
            <TableCell><StatusBadge status={run.status} /></TableCell>
            <TableCell>{formatDateTime(run.completedAt)}</TableCell>
            <TableCell>{formatNumber(run.durationMilliseconds)} ms</TableCell>
            <TableCell>
              <div className="max-w-96 truncate font-mono text-xs text-muted-foreground">
                {run.error ?? run.result ?? "-"}
              </div>
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  )
}

function HealthTile({
  icon: Icon,
  label,
  value,
  detail,
  tone,
}: {
  icon: ComponentType<{ className?: string }>
  label: string
  value: string
  detail: string
  tone: "neutral" | "ok" | "warn"
}) {
  return (
    <Surface className="min-h-32">
      <div className="flex items-center justify-between gap-3">
        <div className={cn(
          "grid size-9 place-items-center rounded-lg",
          tone === "ok" && "bg-emerald-50 text-emerald-700 dark:bg-emerald-950/30",
          tone === "warn" && "bg-amber-50 text-amber-700 dark:bg-amber-950/30",
          tone === "neutral" && "bg-blue-50 text-blue-700 dark:bg-blue-950/30"
        )}>
          <Icon className="size-4" />
        </div>
        <span className="text-[10px] font-semibold uppercase tracking-[0.14em] text-muted-foreground">{label}</span>
      </div>
      <div className="mt-5 text-3xl font-bold tracking-tight">{value}</div>
      <div className="mt-1 text-xs text-muted-foreground">{detail}</div>
    </Surface>
  )
}

function PageHeader({
  eyebrow,
  title,
  description,
  action,
}: {
  eyebrow: string
  title: string
  description: string
  action: ReactNode
}) {
  return (
    <div className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
      <div>
        <div className="text-xs font-semibold tracking-[0.18em] text-slate-500 uppercase">{eyebrow}</div>
        <h1 className="mt-1 text-2xl font-bold tracking-tight text-slate-950 dark:text-foreground">{title}</h1>
        <p className="text-sm text-muted-foreground">{description}</p>
      </div>
      {action}
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

function RepairStatusBadge({ status }: { status: string }) {
  const { t } = useTranslation()
  const normalized = status.toLowerCase()
  const failed = normalized === "failed"
  const retrying = normalized === "retryscheduled"

  return (
    <Badge
      variant="outline"
      className={cn(
        "rounded-full",
        failed && "border-amber-200 bg-amber-50 text-amber-700 dark:bg-amber-950/30",
        retrying && "border-sky-200 bg-sky-50 text-sky-700 dark:bg-sky-950/30",
        !failed && !retrying && "border-emerald-200 bg-emerald-50 text-emerald-700 dark:bg-emerald-950/30"
      )}
    >
      {t(`health.repairStatuses.${normalized}`, { defaultValue: status })}
    </Badge>
  )
}

function StatusBadge({ status }: { status: string }) {
  const { t } = useTranslation()
  const normalized = status.toLowerCase()
  const ok = normalized === "online" || normalized === "succeeded"
  const warn = normalized === "offline" || normalized === "failed" || normalized === "cancelled"

  return (
    <Badge
      variant="outline"
      className={cn(
        "rounded-full",
        ok && "border-emerald-200 bg-emerald-50 text-emerald-700 dark:bg-emerald-950/30",
        warn && "border-amber-200 bg-amber-50 text-amber-700 dark:bg-amber-950/30"
      )}
    >
      {t(`health.status.${normalized}`, { defaultValue: status })}
    </Badge>
  )
}

function EmptyState({ children }: { children: ReactNode }) {
  return (
    <div className="rounded-lg border border-dashed p-5 text-sm text-muted-foreground">
      {children}
    </div>
  )
}

function replaceTask(tasks: BackgroundTaskManagement, snapshot: BackgroundTaskSnapshot): BackgroundTaskManagement {
  return {
    tasks: tasks.tasks.map((task) => task.taskId === snapshot.taskId ? snapshot : task),
    groups: tasks.groups.map((group) => ({
      ...group,
      tasks: group.tasks.map((task) => task.taskId === snapshot.taskId ? snapshot : task),
    })),
    history: tasks.history,
  }
}
