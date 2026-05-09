import { ArrowUpRightIcon } from "lucide-react"

type MetricPanelProps = {
  label: string
  value: string
  detail: string
}

export function MetricPanel({ label, value, detail }: MetricPanelProps) {
  return (
    <section className="rounded-[1.15rem] border border-slate-200/80 bg-white p-4 text-slate-950 shadow-[0_18px_45px_rgba(15,23,42,0.05)] dark:border-border dark:bg-card dark:text-card-foreground">
      <div className="flex items-center justify-between gap-3">
        <span className="text-xs font-bold tracking-[0.08em] text-slate-500 uppercase">{label}</span>
        <ArrowUpRightIcon className="size-4 text-primary" />
      </div>
      <div className="mt-4 text-2xl font-bold tracking-normal">{value}</div>
      <div className="mt-1 text-xs text-muted-foreground">{detail}</div>
    </section>
  )
}
