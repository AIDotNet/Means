type PageHeaderProps = {
  eyebrow?: string
  title: string
  description: string
  actions?: React.ReactNode
}

export function PageHeader({ eyebrow, title, description, actions }: PageHeaderProps) {
  return (
    <div className="mb-4 flex flex-col gap-3 border-b border-border/70 pb-4 lg:flex-row lg:items-end lg:justify-between">
      <div className="max-w-3xl">
        {eyebrow ? (
          <div className="mb-1.5 text-[10px] font-semibold tracking-normal text-primary uppercase">
            {eyebrow}
          </div>
        ) : null}
        <h1 className="text-xl font-semibold tracking-normal text-foreground md:text-2xl">{title}</h1>
        <p className="mt-1 text-xs leading-5 text-muted-foreground md:text-sm">{description}</p>
      </div>
      {actions ? <div className="flex flex-wrap gap-2">{actions}</div> : null}
    </div>
  )
}
