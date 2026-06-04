import { useEffect, useMemo, useState, type ComponentType, type ReactNode } from "react"
import {
  ActivityIcon,
  CableIcon,
  CircleAlertIcon,
  CircleCheckIcon,
  CircleDashedIcon,
  DatabaseIcon,
  GaugeIcon,
  HardDriveIcon,
  NetworkIcon,
  RefreshCwIcon,
  RotateCwIcon,
  RouteIcon,
  ServerIcon,
  ShieldCheckIcon,
  WrenchIcon,
  WorkflowIcon,
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
  type BackgroundTaskSnapshot,
  type ClusterDiagnostics,
  type ClusterNodeInfo,
  type ClusterTopology,
  type StorageDiskInfo,
  type StoragePoolInfo,
} from "@/lib/api-client"
import { formatBytes, formatDateTime, formatNumber } from "@/lib/formatters"
import { cn } from "@/lib/utils"

type ClusterPageState = {
  topology: ClusterTopology
  diagnostics: ClusterDiagnostics
  tasks: BackgroundTaskManagement
}

type ReadinessTone = "ready" | "partial" | "pending" | "attention"

type ReadinessStage = {
  id: string
  title: string
  stateLabel: string
  detail: string
  tone: ReadinessTone
  icon: ComponentType<{ className?: string }>
}

export function ClusterPage() {
  const { t } = useTranslation()
  const [state, setState] = useState<ClusterPageState | null>(null)
  const [loading, setLoading] = useState(true)
  const [runningTaskId, setRunningTaskId] = useState<string | null>(null)

  const load = async (showLoading: boolean) => {
    if (showLoading) {
      setLoading(true)
    }

    try {
      const [topology, diagnostics, tasks] = await Promise.all([
        api.cluster(),
        api.diagnostics(),
        api.backgroundTasks(),
      ])
      setState({ topology, diagnostics, tasks })
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("cluster.errors.loadFailed"))
    } finally {
      if (showLoading) {
        setLoading(false)
      }
    }
  }

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    Promise.all([api.cluster(), api.diagnostics(), api.backgroundTasks()])
      .then(([topology, diagnostics, tasks]) => {
        if (!cancelled) {
          setState({ topology, diagnostics, tasks })
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
      toast.success(t("cluster.tasks.runSucceeded", { task: snapshot.name }))
      await load(false)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : t("cluster.tasks.runFailed"))
    } finally {
      setRunningTaskId(null)
    }
  }

  const topology = state?.topology
  const diagnostics = state?.diagnostics
  const summary = diagnostics?.summary
  const tasks = state?.tasks.groups ?? []
  const taskSnapshots = state?.tasks.tasks ?? []
  const nodes = topology?.nodes ?? []
  const pools = topology?.pools ?? []
  const onlineNodes = summary?.onlineNodeCount ?? countByStatus(nodes, "Online")
  const offlineNodes = summary?.offlineNodeCount ?? Math.max(0, nodes.length - onlineNodes)
  const onlineDisks = summary?.onlineDiskCount ?? nodes.flatMap((node) => node.disks).filter((disk) => disk.status === "Online").length
  const offlineDisks = summary?.offlineDiskCount ?? Math.max(0, nodes.flatMap((node) => node.disks).length - onlineDisks)

  const taskCounts = useMemo(() => {
    return {
      running: taskSnapshots.filter((task) => task.status === "Running").length,
      failed: taskSnapshots.filter((task) => task.status === "Failed").length,
      total: taskSnapshots.length,
    }
  }, [taskSnapshots])

  return (
    <div className="space-y-3">
      <PageHeader
        eyebrow={t("cluster.page.eyebrow")}
        title={topology?.cluster.name ?? t("cluster.page.title")}
        description={t("cluster.page.description")}
        action={
          <Button variant="outline" size="sm" onClick={() => load(true)} disabled={loading}>
            <RefreshCwIcon className={loading ? "animate-spin" : ""} />
            {t("common.actions.refresh")}
          </Button>
        }
      />

      <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
        <MetricTile
          icon={ServerIcon}
          label={t("cluster.metrics.nodes")}
          value={formatNumber(nodes.length)}
          detail={t("cluster.metrics.onlineOffline", { online: onlineNodes, offline: offlineNodes })}
          tone={offlineNodes > 0 ? "warn" : "ok"}
        />
        <MetricTile
          icon={DatabaseIcon}
          label={t("cluster.metrics.pools")}
          value={formatNumber(pools.length)}
          detail={t("cluster.metrics.disks", { online: onlineDisks, offline: offlineDisks })}
          tone={offlineDisks > 0 ? "warn" : "ok"}
        />
        <MetricTile
          icon={GaugeIcon}
          label={t("cluster.metrics.capacity")}
          value={formatBytes(summary?.totalCapacityBytes ?? sum(pools, (pool) => pool.totalBytes))}
          detail={t("cluster.metrics.available", {
            value: formatBytes(summary?.availableCapacityBytes ?? sum(pools, (pool) => pool.availableBytes)),
          })}
        />
        <MetricTile
          icon={WorkflowIcon}
          label={t("cluster.metrics.tasks")}
          value={formatNumber(taskCounts.total)}
          detail={t("cluster.metrics.taskHealth", { running: taskCounts.running, failed: taskCounts.failed })}
          tone={taskCounts.failed > 0 ? "warn" : "neutral"}
        />
      </div>

      <DistributedReadiness diagnostics={diagnostics} tasks={taskSnapshots} loading={loading && !state} />

      <div className="grid gap-3 xl:grid-cols-[1.2fr_0.8fr]">
        <TopologyMap topology={topology} loading={loading && !state} />
        <TransportSummary diagnostics={diagnostics} tasks={taskSnapshots} loading={loading && !state} />
      </div>

      <div className="grid gap-3 xl:grid-cols-[1.15fr_0.85fr]">
        <Surface>
          <PanelTitle icon={ServerIcon} title={t("cluster.nodes.title")} value={topology?.cluster.clusterId ?? "-"} />
          <div className="mt-4">
            <NodeTable nodes={nodes} loading={loading && !state} />
          </div>
        </Surface>
        <Surface>
          <PanelTitle icon={DatabaseIcon} title={t("cluster.pools.title")} value={formatNumber(pools.length)} />
          <div className="mt-4 space-y-3">
            {pools.length > 0 ? (
              pools.map((pool) => <PoolCard key={pool.poolId} pool={pool} />)
            ) : (
              <EmptyState>{t("cluster.pools.empty")}</EmptyState>
            )}
          </div>
        </Surface>
      </div>

      <div className="grid gap-3 xl:grid-cols-2">
        {tasks.map((group) => (
          <Surface key={group.category}>
            <PanelTitle icon={ActivityIcon} title={group.name} value={t("cluster.tasks.count", { count: group.tasks.length })} />
            <div className="mt-4">
              <TaskTable tasks={group.tasks} runningTaskId={runningTaskId} onRun={runTask} />
            </div>
          </Surface>
        ))}
      </div>
    </div>
  )
}

function DistributedReadiness({
  diagnostics,
  tasks,
  loading,
}: {
  diagnostics: ClusterDiagnostics | undefined
  tasks: BackgroundTaskSnapshot[]
  loading: boolean
}) {
  const { t } = useTranslation()

  if (loading || !diagnostics) {
    return (
      <Surface>
        <PanelTitle icon={NetworkIcon} title={t("cluster.readiness.title")} />
        <div className="mt-4">
          <EmptyState>{t("cluster.readiness.loading")}</EmptyState>
        </div>
      </Surface>
    )
  }

  const stages = buildReadinessStages(diagnostics, tasks, t)

  return (
    <Surface>
      <PanelTitle
        icon={NetworkIcon}
        title={t("cluster.readiness.title")}
        value={t("cluster.readiness.generated", { value: formatDateTime(diagnostics.generatedAt) })}
      />
      <div className="mt-4 grid gap-2 md:grid-cols-5">
        {stages.map((stage) => (
          <ReadinessStageCell key={stage.id} stage={stage} />
        ))}
      </div>
    </Surface>
  )
}

function TransportSummary({
  diagnostics,
  tasks,
  loading,
}: {
  diagnostics: ClusterDiagnostics | undefined
  tasks: BackgroundTaskSnapshot[]
  loading: boolean
}) {
  const { t } = useTranslation()

  if (loading || !diagnostics) {
    return (
      <Surface>
        <PanelTitle icon={CableIcon} title={t("cluster.transport.title")} />
        <div className="mt-4">
          <EmptyState>{t("cluster.transport.loading")}</EmptyState>
        </div>
      </Surface>
    )
  }

  const failedTasks = tasks.filter((task) => task.status === "Failed").length
  const runningTasks = tasks.filter((task) => task.status === "Running").length
  const enabledEcProfiles = diagnostics.erasureCoding.enabledProfileCount
  const repairQueue = diagnostics.repairQueue

  return (
    <Surface>
      <PanelTitle icon={CableIcon} title={t("cluster.transport.title")} value={t("cluster.transport.value")} />
      <div className="mt-4 grid gap-3">
        <TransportFact
          icon={CableIcon}
          label={t("cluster.transport.shardRpc")}
          value={diagnostics.internalTransport.shardRpcEnabled ? t("cluster.transport.enabled") : t("cluster.transport.disabled")}
          detail={t("cluster.transport.maxTransfer", {
            value: formatBytes(diagnostics.internalTransport.maxShardTransferBytes),
          })}
          tone={diagnostics.internalTransport.shardRpcEnabled ? "partial" : "pending"}
        />
        <TransportFact
          icon={ShieldCheckIcon}
          label={t("cluster.transport.erasureCoding")}
          value={t("cluster.transport.ecProfiles", {
            enabled: enabledEcProfiles,
            total: diagnostics.erasureCoding.profileCount,
          })}
          detail={t("cluster.transport.ecDetail")}
          tone={enabledEcProfiles > 0 ? "partial" : "pending"}
        />
        <TransportFact
          icon={WrenchIcon}
          label={t("cluster.transport.repair")}
          value={t("cluster.transport.repairQueue", {
            pending: repairQueue.pendingCount,
            failed: repairQueue.failedCount,
          })}
          detail={t("cluster.transport.taskHealth", { running: runningTasks, failed: failedTasks })}
          tone={failedTasks > 0 || repairQueue.failedCount > 0 ? "attention" : "partial"}
        />
        <TransportFact
          icon={RouteIcon}
          label={t("cluster.transport.routing")}
          value={t("cluster.transport.pending")}
          detail={t("cluster.transport.routingDetail")}
          tone="pending"
        />
      </div>
    </Surface>
  )
}

function TopologyMap({
  topology,
  loading,
}: {
  topology: ClusterTopology | undefined
  loading: boolean
}) {
  const { t } = useTranslation()

  if (loading || !topology) {
    return (
      <Surface>
        <PanelTitle icon={NetworkIcon} title={t("cluster.topologyMap.title")} />
        <div className="mt-4">
          <EmptyState>{t("cluster.topologyMap.loading")}</EmptyState>
        </div>
      </Surface>
    )
  }

  if (topology.pools.length === 0) {
    return (
      <Surface>
        <PanelTitle icon={NetworkIcon} title={t("cluster.topologyMap.title")} />
        <div className="mt-4">
          <EmptyState>{t("cluster.topologyMap.empty")}</EmptyState>
        </div>
      </Surface>
    )
  }

  return (
    <Surface>
      <PanelTitle
        icon={NetworkIcon}
        title={t("cluster.topologyMap.title")}
        value={t("cluster.topologyMap.value", { pools: topology.pools.length, nodes: topology.nodes.length })}
      />
      <div className="mt-4 space-y-3">
        {topology.pools.map((pool) => {
          const poolNodes = topology.nodes.filter((node) => node.disks.some((disk) => disk.poolId === pool.poolId))
          return <PoolTopology key={pool.poolId} pool={pool} nodes={poolNodes} />
        })}
      </div>
    </Surface>
  )
}

function PoolTopology({ pool, nodes }: { pool: StoragePoolInfo; nodes: ClusterNodeInfo[] }) {
  const { t } = useTranslation()
  const usedBytes = Math.max(0, pool.totalBytes - pool.availableBytes)
  const usedPercent = pool.totalBytes > 0 ? (usedBytes / pool.totalBytes) * 100 : 0

  return (
    <div className="rounded-lg border border-slate-200 bg-slate-50/70 p-3 dark:border-border dark:bg-muted/20">
      <div className="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
        <div className="min-w-0">
          <div className="truncate text-sm font-bold">{pool.name}</div>
          <div className="mt-1 text-xs text-muted-foreground">
            {t("cluster.topologyMap.poolMeta", { nodes: pool.nodeCount, disks: pool.diskCount })}
          </div>
        </div>
        <div className="w-full md:w-48">
          <div className="flex items-center justify-between text-[11px] text-muted-foreground">
            <span>{formatBytes(usedBytes)}</span>
            <span>{formatBytes(pool.totalBytes)}</span>
          </div>
          <div className="mt-1 h-2 overflow-hidden rounded-full bg-slate-200 dark:bg-slate-800">
            <div className="h-full bg-sky-600" style={{ width: `${clampPercent(usedPercent)}%` }} />
          </div>
        </div>
      </div>
      <div className="mt-3 grid gap-2 lg:grid-cols-2">
        {nodes.length > 0 ? (
          nodes.map((node) => <TopologyNode key={`${pool.poolId}:${node.nodeId}`} node={node} poolId={pool.poolId} />)
        ) : (
          <EmptyState>{t("cluster.topologyMap.noNodes")}</EmptyState>
        )}
      </div>
    </div>
  )
}

function TopologyNode({ node, poolId }: { node: ClusterNodeInfo; poolId: string }) {
  const { t } = useTranslation()
  const disks = node.disks.filter((disk) => disk.poolId === poolId)

  return (
    <div className="min-w-0 rounded-lg border border-slate-200 bg-white p-3 dark:border-border dark:bg-card">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <StatusDot status={node.status} />
            <span className="truncate text-sm font-semibold">{node.nodeId}</span>
          </div>
          <div className="mt-1 truncate font-mono text-[11px] text-muted-foreground">{node.endpoint}</div>
        </div>
        <Badge variant="outline" className="shrink-0 rounded-full">
          {t("cluster.topologyMap.diskCount", { count: disks.length })}
        </Badge>
      </div>
      <div className="mt-3 grid gap-2 sm:grid-cols-2">
        {disks.map((disk) => <DiskPill key={disk.diskId} disk={disk} />)}
      </div>
    </div>
  )
}

function DiskPill({ disk }: { disk: StorageDiskInfo }) {
  const { t } = useTranslation()
  const usedBytes = Math.max(0, disk.totalBytes - disk.availableBytes)
  const usedPercent = disk.totalBytes > 0 ? (usedBytes / disk.totalBytes) * 100 : 0

  return (
    <div className="min-w-0 rounded-md border border-slate-200 px-2.5 py-2 text-xs dark:border-border">
      <div className="flex items-center gap-2">
        <HardDriveIcon className="size-3.5 shrink-0 text-slate-500" />
        <span className="truncate font-semibold">{disk.diskId}</span>
        <StatusDot status={disk.status} />
      </div>
      <div className="mt-2 h-1.5 overflow-hidden rounded-full bg-slate-200 dark:bg-slate-800">
        <div className="h-full bg-emerald-600" style={{ width: `${clampPercent(usedPercent)}%` }} />
      </div>
      <div className="mt-1 truncate text-[11px] text-muted-foreground">
        {t("cluster.topologyMap.diskFree", { value: formatBytes(disk.availableBytes) })}
      </div>
    </div>
  )
}

function NodeTable({ nodes, loading }: { nodes: ClusterNodeInfo[]; loading: boolean }) {
  const { t } = useTranslation()

  if (loading) {
    return <EmptyState>{t("cluster.nodes.loading")}</EmptyState>
  }

  if (nodes.length === 0) {
    return <EmptyState>{t("cluster.nodes.empty")}</EmptyState>
  }

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>{t("cluster.nodes.columns.node")}</TableHead>
          <TableHead>{t("cluster.nodes.columns.endpoint")}</TableHead>
          <TableHead>{t("cluster.nodes.columns.disks")}</TableHead>
          <TableHead>{t("cluster.nodes.columns.lastHeartbeat")}</TableHead>
          <TableHead>{t("cluster.nodes.columns.status")}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {nodes.map((node) => (
          <TableRow key={node.nodeId}>
            <TableCell>
              <div className="font-semibold">{node.nodeId}</div>
              <div className="text-xs text-muted-foreground">{node.hostName}</div>
            </TableCell>
            <TableCell className="max-w-72 truncate font-mono text-xs">{node.endpoint}</TableCell>
            <TableCell>{formatNumber(node.disks.length)}</TableCell>
            <TableCell>{formatDateTime(node.lastHeartbeatAt)}</TableCell>
            <TableCell><StatusBadge status={node.status} /></TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  )
}

function PoolCard({ pool }: { pool: StoragePoolInfo }) {
  const { t } = useTranslation()
  const usedBytes = Math.max(0, pool.totalBytes - pool.availableBytes)
  const usedPercent = pool.totalBytes > 0 ? (usedBytes / pool.totalBytes) * 100 : 0

  return (
    <div className="rounded-xl border border-slate-200 bg-slate-50/80 p-3 dark:border-border dark:bg-muted/30">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="truncate text-sm font-bold">{pool.name}</div>
          <div className="mt-1 text-xs text-muted-foreground">
            {t("cluster.pools.meta", { nodes: pool.nodeCount, disks: pool.diskCount })}
          </div>
        </div>
        <Badge variant="outline">{formatBytes(pool.totalBytes)}</Badge>
      </div>
      <div className="mt-3 h-2 overflow-hidden rounded-full bg-slate-200 dark:bg-slate-800">
        <div className="h-full bg-blue-600" style={{ width: `${clampPercent(usedPercent)}%` }} />
      </div>
      <div className="mt-2 flex items-center justify-between text-xs text-muted-foreground">
        <span>{t("cluster.pools.used", { value: formatBytes(usedBytes) })}</span>
        <span>{t("cluster.pools.available", { value: formatBytes(pool.availableBytes) })}</span>
      </div>
    </div>
  )
}

function TaskTable({
  tasks,
  runningTaskId,
  onRun,
}: {
  tasks: BackgroundTaskSnapshot[]
  runningTaskId: string | null
  onRun: (taskId: string) => void
}) {
  const { t } = useTranslation()

  return (
    <div className="space-y-3">
      {tasks.map((task) => {
        const running = runningTaskId === task.taskId || task.status === "Running"
        return (
          <div key={task.taskId} className="rounded-xl border border-slate-200 bg-white p-3 dark:border-border dark:bg-card">
            <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2">
                  <div className="font-semibold">{task.name}</div>
                  <StatusBadge status={task.status} />
                </div>
                <div className="mt-1 font-mono text-xs text-muted-foreground">{task.taskId}</div>
              </div>
              <Button
                size="sm"
                variant="outline"
                disabled={!task.manualRunSupported || running}
                onClick={() => onRun(task.taskId)}
              >
                <RotateCwIcon className={running ? "animate-spin" : ""} />
                {t("cluster.tasks.runNow")}
              </Button>
            </div>
            <Separator className="my-3" />
            <div className="grid gap-2 text-xs text-muted-foreground sm:grid-cols-3">
              <TaskFact label={t("cluster.tasks.interval")} value={t("cluster.tasks.seconds", { seconds: task.intervalSeconds })} />
              <TaskFact label={t("cluster.tasks.successFailure")} value={`${formatNumber(task.successCount)} / ${formatNumber(task.failureCount)}`} />
              <TaskFact label={t("cluster.tasks.lastRun")} value={task.lastCompletedAt ? formatDateTime(task.lastCompletedAt) : "-"} />
            </div>
            {task.lastResult || task.lastError ? (
              <div className={cn(
                "mt-3 rounded-lg px-3 py-2 font-mono text-xs",
                task.lastError ? "bg-red-50 text-red-700 dark:bg-red-950/30" : "bg-slate-50 text-slate-600 dark:bg-muted/30 dark:text-muted-foreground"
              )}>
                {task.lastError ?? task.lastResult}
              </div>
            ) : null}
          </div>
        )
      })}
    </div>
  )
}

function ReadinessStageCell({ stage }: { stage: ReadinessStage }) {
  const Icon = stage.icon
  const StatusIcon = stage.tone === "ready"
    ? CircleCheckIcon
    : stage.tone === "attention"
      ? CircleAlertIcon
      : CircleDashedIcon

  return (
    <div className={cn(
      "min-h-32 rounded-lg border p-3 transition-colors",
      toneSurfaceClass(stage.tone)
    )}>
      <div className="flex items-center justify-between gap-2">
        <Icon className="size-4 shrink-0" />
        <div className="flex min-w-0 items-center gap-1.5 text-[11px] font-semibold">
          <StatusIcon className="size-3.5 shrink-0" />
          <span className="truncate">{stage.stateLabel}</span>
        </div>
      </div>
      <div className="mt-4 text-sm font-bold">{stage.title}</div>
      <div className="mt-1 text-xs leading-5 text-muted-foreground">{stage.detail}</div>
    </div>
  )
}

function TransportFact({
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
  tone: ReadinessTone
}) {
  return (
    <div className={cn(
      "flex items-start gap-3 rounded-lg border p-3",
      toneSurfaceClass(tone)
    )}>
      <div className="grid size-8 shrink-0 place-items-center rounded-md bg-white/70 dark:bg-slate-950/20">
        <Icon className="size-4" />
      </div>
      <div className="min-w-0">
        <div className="text-[11px] font-semibold text-muted-foreground">{label}</div>
        <div className="mt-0.5 truncate text-sm font-bold">{value}</div>
        <div className="mt-1 text-xs leading-5 text-muted-foreground">{detail}</div>
      </div>
    </div>
  )
}

function StatusDot({ status }: { status: string }) {
  const online = status === "Online"
  return (
    <span
      className={cn(
        "size-2 shrink-0 rounded-full",
        online ? "bg-emerald-500" : "bg-amber-500"
      )}
    />
  )
}

function MetricTile({
  icon: Icon,
  label,
  value,
  detail,
  tone = "neutral",
}: {
  icon: ComponentType<{ className?: string }>
  label: string
  value: string
  detail: string
  tone?: "neutral" | "ok" | "warn"
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
      {t(`cluster.status.${normalized}`, { defaultValue: status })}
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

function TaskFact({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div className="font-medium text-slate-500">{label}</div>
      <div className="mt-0.5 truncate font-semibold text-foreground">{value}</div>
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

function buildReadinessStages(
  diagnostics: ClusterDiagnostics,
  tasks: BackgroundTaskSnapshot[],
  t: (key: string, options?: Record<string, unknown>) => string
): ReadinessStage[] {
  const summary = diagnostics.summary
  const topologyHealthy = summary.onlineNodeCount > 0 && summary.onlineDiskCount > 0
  const topologyDegraded = summary.offlineNodeCount > 0 || summary.offlineDiskCount > 0
  const repairTaskCount = tasks.filter((task) => isMaintenanceTask(task)).length
  const failedTaskCount = tasks.filter((task) => task.status === "Failed").length
  const ecEnabled = diagnostics.erasureCoding.enabledProfileCount > 0

  return [
    {
      id: "topology",
      icon: NetworkIcon,
      title: t("cluster.readiness.topology.title"),
      stateLabel: topologyDegraded
        ? t("cluster.readiness.states.attention")
        : topologyHealthy
          ? t("cluster.readiness.states.ready")
          : t("cluster.readiness.states.pending"),
      detail: topologyDegraded
        ? t("cluster.readiness.topology.degraded", {
          nodes: summary.offlineNodeCount,
          disks: summary.offlineDiskCount,
        })
        : t("cluster.readiness.topology.detail", {
          nodes: summary.onlineNodeCount,
          disks: summary.onlineDiskCount,
        }),
      tone: topologyDegraded ? "attention" : topologyHealthy ? "ready" : "pending",
    },
    {
      id: "transport",
      icon: CableIcon,
      title: t("cluster.readiness.transport.title"),
      stateLabel: diagnostics.internalTransport.shardRpcEnabled
        ? t("cluster.readiness.states.configured")
        : t("cluster.readiness.states.disabled"),
      detail: diagnostics.internalTransport.shardRpcEnabled
        ? t("cluster.readiness.transport.enabled", {
          limit: formatBytes(diagnostics.internalTransport.maxShardTransferBytes),
        })
        : t("cluster.readiness.transport.disabled"),
      tone: diagnostics.internalTransport.shardRpcEnabled ? "partial" : "pending",
    },
    {
      id: "erasure-coding",
      icon: ShieldCheckIcon,
      title: t("cluster.readiness.erasureCoding.title"),
      stateLabel: ecEnabled
        ? t("cluster.readiness.states.partial")
        : t("cluster.readiness.states.pending"),
      detail: ecEnabled
        ? t("cluster.readiness.erasureCoding.enabled", {
          count: diagnostics.erasureCoding.enabledProfileCount,
        })
        : t("cluster.readiness.erasureCoding.disabled"),
      tone: ecEnabled ? "partial" : "pending",
    },
    {
      id: "repair",
      icon: WrenchIcon,
      title: t("cluster.readiness.repair.title"),
      stateLabel: failedTaskCount > 0
        ? t("cluster.readiness.states.attention")
        : repairTaskCount > 0
          ? t("cluster.readiness.states.partial")
          : t("cluster.readiness.states.pending"),
      detail: t("cluster.readiness.repair.detail", {
        tasks: repairTaskCount,
        pending: diagnostics.repairQueue.pendingCount,
      }),
      tone: failedTaskCount > 0 ? "attention" : repairTaskCount > 0 ? "partial" : "pending",
    },
    {
      id: "routing",
      icon: RouteIcon,
      title: t("cluster.readiness.routing.title"),
      stateLabel: t("cluster.readiness.states.pending"),
      detail: t("cluster.readiness.routing.detail"),
      tone: "pending",
    },
  ]
}

function isMaintenanceTask(task: BackgroundTaskSnapshot) {
  const category = task.category.toLowerCase()
  return category === "repair" || category === "rebalance" || category === "replication"
}

function toneSurfaceClass(tone: ReadinessTone) {
  switch (tone) {
    case "ready":
      return "border-emerald-200 bg-emerald-50/80 text-emerald-900 dark:border-emerald-900/50 dark:bg-emerald-950/25 dark:text-emerald-100"
    case "partial":
      return "border-sky-200 bg-sky-50/80 text-sky-950 dark:border-sky-900/50 dark:bg-sky-950/25 dark:text-sky-100"
    case "attention":
      return "border-amber-200 bg-amber-50/80 text-amber-950 dark:border-amber-900/50 dark:bg-amber-950/25 dark:text-amber-100"
    default:
      return "border-slate-200 bg-slate-50/80 text-slate-800 dark:border-border dark:bg-muted/25 dark:text-slate-100"
  }
}

function countByStatus(nodes: ClusterNodeInfo[], status: string) {
  return nodes.filter((node) => node.status === status).length
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
