type PageHeaderProps = {
  eyebrow?: string
  title: string
  description: string
  actions?: React.ReactNode
}

export function PageHeader({ eyebrow, title, description, actions }: PageHeaderProps) {
  return (
    <div className="mb-3 flex flex-col gap-3 rounded-[1.15rem] border border-slate-200/80 bg-white px-4 py-3 text-slate-950 shadow-[0_18px_45px_rgba(15,23,42,0.05)] lg:flex-row lg:items-end lg:justify-between dark:border-border dark:bg-card dark:text-card-foreground">
      <div className="max-w-3xl">
        {eyebrow ? (
          <div className="mb-1.5 text-[10px] font-bold tracking-[0.18em] text-primary uppercase">
            {eyebrow}
          </div>
        ) : null}
        <h1 className="text-xl font-bold tracking-normal text-slate-950 md:text-2xl dark:text-foreground">{title}</h1>
        <p className="mt-1 text-xs leading-5 text-muted-foreground md:text-sm">{description}</p>
      </div>
      {actions ? <div className="flex flex-wrap gap-2">{actions}</div> : null}
    </div>
  )
}
