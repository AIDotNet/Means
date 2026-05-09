import {
  ArchiveIcon,
  ChartNoAxesColumnIcon,
  ChevronRightIcon,
  ChevronsLeftIcon,
  ChevronsRightIcon,
  DatabaseIcon,
  HardDriveIcon,
  KeyRoundIcon,
  LogOutIcon,
  MoonIcon,
  SearchIcon,
  ServerIcon,
  SettingsIcon,
  ShieldCheckIcon,
  SunIcon,
  UserCircleIcon,
} from "lucide-react"
import { useEffect, useState, type ComponentType, type ReactNode } from "react"

import { CommandSearch } from "@/components/layout/CommandSearch"
import { useTheme } from "@/components/theme-provider"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import { Input } from "@/components/ui/input"
import type { AppRoute } from "@/lib/routes"
import { cn } from "@/lib/utils"
import { useTranslation } from "@/i18n"

type NavItem = {
  label: string
  route: AppRoute
  icon: ComponentType<{ className?: string }>
}

type AppShellProps = {
  route: AppRoute
  userName: string
  children: ReactNode
  onNavigate: (route: AppRoute) => void
  onLogout: () => void
}

export function AppShell({
  route,
  userName,
  children,
  onNavigate,
  onLogout,
}: AppShellProps) {
  const { t } = useTranslation()
  const { theme, setTheme } = useTheme()
  const [commandOpen, setCommandOpen] = useState(false)
  const [sidebarExpanded, setSidebarExpanded] = useState(() => {
    if (typeof window === "undefined") {
      return false
    }

    return window.localStorage.getItem("means-console-sidebar") === "expanded"
  })
  const navItems: NavItem[] = [
    { label: t("appShell.nav.dashboard"), route: { name: "dashboard" }, icon: ChartNoAxesColumnIcon },
    { label: t("appShell.nav.cluster"), route: { name: "cluster" }, icon: ServerIcon },
    { label: t("appShell.nav.health"), route: { name: "health" }, icon: HardDriveIcon },
    { label: t("appShell.nav.buckets"), route: { name: "buckets" }, icon: ArchiveIcon },
    { label: t("appShell.nav.accessKeys"), route: { name: "access-keys" }, icon: KeyRoundIcon },
    { label: t("appShell.nav.audit"), route: { name: "audit" }, icon: ShieldCheckIcon },
    { label: t("appShell.nav.settings"), route: { name: "settings" }, icon: SettingsIcon },
  ]
  const isActive = (item: AppRoute) =>
    item.name === route.name || (item.name === "buckets" && route.name === "bucket")

  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === "k") {
        event.preventDefault()
        setCommandOpen(true)
      }
    }

    window.addEventListener("keydown", handleKeyDown)
    return () => window.removeEventListener("keydown", handleKeyDown)
  }, [])

  useEffect(() => {
    window.localStorage.setItem("means-console-sidebar", sidebarExpanded ? "expanded" : "collapsed")
  }, [sidebarExpanded])

  const routeLabel = (nextRoute: AppRoute) => {
    if (nextRoute.name === "dashboard") {
      return t("appShell.nav.dashboard")
    }

    if (nextRoute.name === "buckets") {
      return t("appShell.nav.buckets")
    }

    if (nextRoute.name === "cluster") {
      return t("appShell.nav.cluster")
    }

    if (nextRoute.name === "health") {
      return t("appShell.nav.health")
    }

    if (nextRoute.name === "bucket") {
      return nextRoute.bucketName
    }

    if (nextRoute.name === "access-keys") {
      return t("appShell.nav.accessKeys")
    }

    if (nextRoute.name === "settings") {
      return t("appShell.nav.settings")
    }

    return t("appShell.nav.audit")
  }

  return (
    <div
      className={cn(
        "min-h-svh bg-background text-foreground md:grid",
        sidebarExpanded ? "md:grid-cols-[14.5rem_1fr]" : "md:grid-cols-[4.25rem_1fr]"
      )}
    >
      <aside
        className={cn(
          "sticky top-0 hidden h-svh border-r border-sidebar-border bg-sidebar px-2 py-3 transition-[width] duration-200 md:flex md:flex-col",
          sidebarExpanded ? "items-stretch" : "items-center"
        )}
      >
        <div className={cn("flex items-center gap-2", sidebarExpanded ? "justify-between" : "flex-col")}>
          <button
            className={cn(
              "grid min-w-10 place-items-center rounded-lg bg-white text-[13px] font-black tracking-normal text-slate-950 shadow-xs ring-1 ring-slate-200 transition-transform hover:-translate-y-0.5 dark:bg-card dark:text-card-foreground",
              sidebarExpanded ? "h-10 flex-1 grid-cols-[2.5rem_1fr] justify-items-start pr-3 text-left" : "size-10"
            )}
            onClick={() => onNavigate({ name: "dashboard" })}
            aria-label={t("common.brand.consoleName")}
          >
            <span className="grid size-10 place-items-center">M</span>
            {sidebarExpanded ? (
              <span className="min-w-0 truncate text-sm font-bold">{t("common.brand.productName")}</span>
            ) : null}
          </button>
          <Button
            variant="ghost"
            size="icon-sm"
            className="rounded-lg"
            onClick={() => setSidebarExpanded((expanded) => !expanded)}
          >
            {sidebarExpanded ? <ChevronsLeftIcon /> : <ChevronsRightIcon />}
            <span className="sr-only">
              {sidebarExpanded ? t("appShell.actions.collapseSidebar") : t("appShell.actions.expandSidebar")}
            </span>
          </Button>
        </div>

        <nav
          className={cn("mt-7 flex flex-1 flex-col gap-2", sidebarExpanded ? "items-stretch" : "items-center")}
          aria-label={t("appShell.aria.mainNav")}
        >
          {navItems.map((item) => (
            <RailButton
              key={item.label}
              item={item}
              active={isActive(item.route)}
              expanded={sidebarExpanded}
              onNavigate={onNavigate}
            />
          ))}
        </nav>

        <div className={cn("flex flex-col gap-2", sidebarExpanded ? "items-stretch" : "items-center")}>
          <Button
            variant="ghost"
            size={sidebarExpanded ? "sm" : "icon-sm"}
            className={cn("rounded-lg", sidebarExpanded && "justify-start")}
            onClick={() => setTheme(theme === "dark" ? "light" : "dark")}
          >
            {theme === "dark" ? <SunIcon /> : <MoonIcon />}
            <span className={sidebarExpanded ? "" : "sr-only"}>{t("appShell.actions.toggleTheme")}</span>
          </Button>
          <UserMenu compact={!sidebarExpanded} userName={userName} onNavigate={onNavigate} onLogout={onLogout} />
        </div>
      </aside>

      <div className="min-w-0">
        <header className="sticky top-0 z-20 border-b border-slate-200/70 bg-background/88 px-3 py-2.5 backdrop-blur-xl supports-[backdrop-filter]:bg-background/76 md:px-4">
          <div className="flex items-center gap-2">
            <button
              className="grid size-9 place-items-center rounded-lg bg-white text-xs font-black shadow-xs ring-1 ring-slate-200 md:hidden"
              onClick={() => onNavigate({ name: "dashboard" })}
              aria-label={t("common.brand.consoleName")}
            >
              M
            </button>
            <RouteBreadcrumb route={route} onNavigate={onNavigate} routeLabel={routeLabel} />
            <div className="relative hidden w-full max-w-xl md:block">
              <SearchIcon className="pointer-events-none absolute top-1/2 left-3 size-3.5 -translate-y-1/2 text-muted-foreground" />
              <Input
                readOnly
                className="h-9 cursor-pointer rounded-md border-slate-200 bg-white pl-9 pr-16 text-xs shadow-xs"
                placeholder={t("appShell.search.placeholder")}
                onFocus={() => setCommandOpen(true)}
                onClick={() => setCommandOpen(true)}
              />
              <span className="pointer-events-none absolute top-1/2 right-2 -translate-y-1/2 rounded-full border border-slate-200 bg-slate-50 px-2 py-0.5 text-[10px] font-medium text-slate-500">
                Ctrl K
              </span>
            </div>

            <div className="ml-auto flex items-center gap-2">
              <Button
                variant="ghost"
                size="icon-sm"
                className="rounded-lg md:hidden"
                onClick={() => setCommandOpen(true)}
              >
                <SearchIcon />
                <span className="sr-only">{t("appShell.search.open")}</span>
              </Button>
              <Badge
                variant="outline"
                className="hidden rounded-full border-primary/20 bg-white px-2.5 text-primary shadow-xs md:inline-flex dark:bg-card"
              >
                <DatabaseIcon className="size-3" />
                {t("appShell.badges.singleNode")}
              </Badge>
              <Button
                variant="ghost"
                size="icon-sm"
                className="rounded-lg md:hidden"
                onClick={() => setTheme(theme === "dark" ? "light" : "dark")}
              >
                {theme === "dark" ? <SunIcon /> : <MoonIcon />}
                <span className="sr-only">{t("appShell.actions.toggleTheme")}</span>
              </Button>
              <UserMenu userName={userName} onNavigate={onNavigate} onLogout={onLogout} />
            </div>
          </div>
        </header>

        <main className="min-w-0 px-3 py-3 md:px-4 md:py-4 xl:px-5">{children}</main>
      </div>

      <CommandSearch open={commandOpen} onOpenChange={setCommandOpen} onNavigate={onNavigate} />
    </div>
  )
}

function RailButton({
  item,
  active,
  expanded,
  onNavigate,
}: {
  item: NavItem
  active: boolean
  expanded: boolean
  onNavigate: (route: AppRoute) => void
}) {
  const Icon = item.icon

  return (
    <button
      className={cn(
        "group relative flex h-10 items-center rounded-lg text-slate-500 transition-all hover:-translate-y-0.5 hover:bg-white hover:text-primary hover:shadow-xs dark:hover:bg-card",
        expanded ? "w-full justify-start gap-3 px-3" : "size-10 justify-center",
        active && "bg-primary text-primary-foreground shadow-xs hover:bg-primary hover:text-primary-foreground"
      )}
      onClick={() => onNavigate(item.route)}
      aria-label={item.label}
      title={item.label}
    >
      <Icon className="size-4" />
      {expanded ? <span className="truncate text-sm font-semibold">{item.label}</span> : null}
      {!expanded ? (
        <span
          className={cn(
            "absolute left-[calc(100%+0.55rem)] z-30 hidden whitespace-nowrap rounded-full border bg-white px-2.5 py-1 text-[11px] font-semibold text-slate-700 opacity-0 shadow-lg transition-opacity group-hover:opacity-100 md:block dark:bg-popover dark:text-popover-foreground",
            active && "text-primary"
          )}
        >
          {item.label}
        </span>
      ) : null}
    </button>
  )
}

function RouteBreadcrumb({
  route,
  onNavigate,
  routeLabel,
}: {
  route: AppRoute
  onNavigate: (route: AppRoute) => void
  routeLabel: (route: AppRoute) => string
}) {
  const { t } = useTranslation()
  const current = routeLabel(route)

  return (
    <nav className="hidden min-w-0 shrink-0 items-center gap-1 text-xs md:flex" aria-label={t("appShell.aria.currentPosition")}>
      <button
        className="font-semibold text-slate-500 transition-colors hover:text-primary"
        onClick={() => onNavigate({ name: "dashboard" })}
      >
        Means
      </button>
      {route.name !== "dashboard" ? (
        <>
          <ChevronRightIcon className="size-3 text-slate-400" />
          {route.name === "bucket" ? (
            <>
              <button
                className="text-slate-500 transition-colors hover:text-primary"
                onClick={() => onNavigate({ name: "buckets" })}
              >
                {t("appShell.nav.buckets")}
              </button>
              <ChevronRightIcon className="size-3 text-slate-400" />
            </>
          ) : null}
          <span className="max-w-52 truncate font-semibold text-slate-950 dark:text-foreground">
            {current}
          </span>
        </>
      ) : null}
    </nav>
  )
}

function UserMenu({
  userName,
  onNavigate,
  onLogout,
  compact = false,
}: {
  userName: string
  onNavigate: (route: AppRoute) => void
  onLogout: () => void
  compact?: boolean
}) {
  const { t } = useTranslation()

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button
          variant={compact ? "ghost" : "outline"}
          size={compact ? "icon-sm" : "sm"}
          className="rounded-lg"
        >
          <UserCircleIcon />
          {!compact ? <span className="hidden sm:inline">{userName}</span> : null}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-52">
        <DropdownMenuLabel>
          <span className="block text-[10px] font-semibold uppercase tracking-[0.16em] text-muted-foreground">
            {t("appShell.userMenu.currentUser")}
          </span>
          <span className="mt-1 block truncate text-sm text-foreground">{userName}</span>
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuItem onClick={() => onNavigate({ name: "settings" })}>
          <SettingsIcon />
          {t("appShell.userMenu.settings")}
        </DropdownMenuItem>
        <DropdownMenuItem variant="destructive" onClick={onLogout}>
          <LogOutIcon />
          {t("appShell.userMenu.logout")}
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}
