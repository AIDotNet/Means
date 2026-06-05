import { useEffect, useMemo, useState, type ComponentType } from "react"
import {
  ArchiveIcon,
  ChartNoAxesColumnIcon,
  FileSearchIcon,
  HardDriveIcon,
  KeyRoundIcon,
  ServerIcon,
  SettingsIcon,
  ShieldCheckIcon,
} from "lucide-react"

import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
  CommandSeparator,
} from "@/components/ui/command"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import { api, type BucketUsage } from "@/lib/api-client"
import type { AppRoute } from "@/lib/routes"
import { useTranslation } from "@/i18n"

type CommandSearchProps = {
  open: boolean
  onOpenChange: (open: boolean) => void
  onNavigate: (route: AppRoute) => void
}

type QuickRoute = {
  label: string
  description: string
  route: AppRoute
  icon: ComponentType<{ className?: string }>
  keywords: string
}

export function CommandSearch({
  open,
  onOpenChange,
  onNavigate,
}: CommandSearchProps) {
  const { t } = useTranslation()
  const [query, setQuery] = useState("")
  const [buckets, setBuckets] = useState<BucketUsage[]>([])

  const quickRoutes = useMemo<QuickRoute[]>(
    () => [
      {
        label: t("commandSearch.routes.dashboard.label"),
        description: t("commandSearch.routes.dashboard.description"),
        route: { name: "dashboard" },
        icon: ChartNoAxesColumnIcon,
        keywords: t("commandSearch.routes.dashboard.keywords"),
      },
      {
        label: t("commandSearch.routes.buckets.label"),
        description: t("commandSearch.routes.buckets.description"),
        route: { name: "buckets" },
        icon: ArchiveIcon,
        keywords: t("commandSearch.routes.buckets.keywords"),
      },
      {
        label: t("commandSearch.routes.cluster.label"),
        description: t("commandSearch.routes.cluster.description"),
        route: { name: "cluster" },
        icon: ServerIcon,
        keywords: t("commandSearch.routes.cluster.keywords"),
      },
      {
        label: t("commandSearch.routes.health.label"),
        description: t("commandSearch.routes.health.description"),
        route: { name: "health" },
        icon: HardDriveIcon,
        keywords: t("commandSearch.routes.health.keywords"),
      },
      {
        label: t("commandSearch.routes.accessKeys.label"),
        description: t("commandSearch.routes.accessKeys.description"),
        route: { name: "access-keys" },
        icon: KeyRoundIcon,
        keywords: t("commandSearch.routes.accessKeys.keywords"),
      },
      {
        label: t("commandSearch.routes.audit.label"),
        description: t("commandSearch.routes.audit.description"),
        route: { name: "audit" },
        icon: ShieldCheckIcon,
        keywords: t("commandSearch.routes.audit.keywords"),
      },
      {
        label: t("commandSearch.routes.settings.label"),
        description: t("commandSearch.routes.settings.description"),
        route: { name: "settings" },
        icon: SettingsIcon,
        keywords: t("commandSearch.routes.settings.keywords"),
      },
    ],
    [t]
  )

  useEffect(() => {
    if (!open) {
      return
    }

    setQuery("")
    api
      .buckets()
      .then(setBuckets)
      .catch(() => setBuckets([]))
  }, [open])

  const normalizedQuery = query.trim().toLowerCase()
  const filteredRoutes = useMemo(() => {
    if (!normalizedQuery) {
      return quickRoutes
    }

    return quickRoutes.filter((item) =>
      `${item.label} ${item.description} ${item.keywords}`
        .toLowerCase()
        .includes(normalizedQuery)
    )
  }, [normalizedQuery, quickRoutes])

  const filteredBuckets = useMemo(() => {
    if (!normalizedQuery) {
      return buckets.slice(0, 5)
    }

    return buckets
      .filter((bucket) =>
        bucket.bucketName.toLowerCase().includes(normalizedQuery)
      )
      .slice(0, 8)
  }, [buckets, normalizedQuery])

  const navigate = (route: AppRoute) => {
    onNavigate(route)
    onOpenChange(false)
  }

  const submit = () => {
    const firstRoute = filteredRoutes[0]
    const firstBucket = filteredBuckets[0]

    if (firstRoute) {
      navigate(firstRoute.route)
      return
    }

    if (firstBucket) {
      navigate({ name: "bucket", bucketName: firstBucket.bucketName })
    }
  }

  const hasResults = filteredRoutes.length > 0 || filteredBuckets.length > 0

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="gap-0 overflow-hidden p-0 sm:max-w-2xl">
        <DialogHeader className="sr-only">
          <DialogTitle>
            <FileSearchIcon className="size-4 text-primary" />
            {t("commandSearch.title")}
          </DialogTitle>
          <DialogDescription>
            {t("commandSearch.description")}
          </DialogDescription>
        </DialogHeader>
        <Command shouldFilter={false}>
          <CommandInput
            autoFocus
            placeholder={t("commandSearch.inputPlaceholder")}
            value={query}
            onValueChange={setQuery}
            onKeyDown={(event) => {
              if (event.key === "Enter") {
                event.preventDefault()
                submit()
              }
            }}
          />
          <CommandList>
            {!hasResults ? (
              <CommandEmpty>{t("commandSearch.empty")}</CommandEmpty>
            ) : (
              <>
                <CommandGroup heading={t("commandSearch.sections.pages")}>
                  {filteredRoutes.map((item) => (
                    <ResultButton
                      key={item.label}
                      icon={item.icon}
                      title={item.label}
                      description={item.description}
                      onClick={() => navigate(item.route)}
                    />
                  ))}
                  {filteredRoutes.length === 0 ? (
                    <div className="rounded-md border border-dashed p-4 text-sm text-muted-foreground">
                      {t("commandSearch.emptyPages")}
                    </div>
                  ) : null}
                </CommandGroup>
                <CommandSeparator />
                <CommandGroup heading={t("commandSearch.sections.buckets")}>
                  {filteredBuckets.map((bucket) => (
                    <ResultButton
                      key={bucket.bucketName}
                      icon={ArchiveIcon}
                      title={bucket.bucketName}
                      description={t("commandSearch.bucketDescription", {
                        count: bucket.objectCount,
                      })}
                      onClick={() =>
                        navigate({
                          name: "bucket",
                          bucketName: bucket.bucketName,
                        })
                      }
                    />
                  ))}
                  {filteredBuckets.length === 0 ? (
                    <div className="rounded-md border border-dashed p-4 text-sm text-muted-foreground">
                      {t("commandSearch.emptyBuckets")}
                    </div>
                  ) : null}
                </CommandGroup>
              </>
            )}
          </CommandList>
        </Command>
      </DialogContent>
    </Dialog>
  )
}

function ResultButton({
  icon: Icon,
  title,
  description,
  onClick,
}: {
  icon: ComponentType<{ className?: string }>
  title: string
  description: string
  onClick: () => void
}) {
  return (
    <CommandItem
      value={`${title} ${description}`}
      onSelect={onClick}
      className="items-start gap-3 px-3 py-2.5"
    >
      <Icon className="mt-0.5 size-4 text-primary" />
      <span className="min-w-0">
        <span className="block truncate text-sm font-medium">{title}</span>
        <span className="block truncate text-xs text-muted-foreground">
          {description}
        </span>
      </span>
    </CommandItem>
  )
}
