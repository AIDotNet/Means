import {
  ActivityIcon,
  ArchiveIcon,
  BlocksIcon,
  ChartNoAxesColumnIcon,
  ChevronDownIcon,
  DatabaseIcon,
  HardDriveIcon,
  KeyRoundIcon,
  LogOutIcon,
  MoonIcon,
  NetworkIcon,
  SearchIcon,
  ServerIcon,
  SettingsIcon,
  ShieldCheckIcon,
  SunIcon,
} from "lucide-react"
import {
  useEffect,
  useState,
  type ComponentType,
  type CSSProperties,
  type ReactNode,
} from "react"

import { CommandSearch } from "@/components/layout/CommandSearch"
import { useTheme } from "@/components/theme-provider"
import { Avatar, AvatarBadge, AvatarFallback } from "@/components/ui/avatar"
import { Badge } from "@/components/ui/badge"
import {
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbLink,
  BreadcrumbList,
  BreadcrumbPage,
  BreadcrumbSeparator,
} from "@/components/ui/breadcrumb"
import { Button } from "@/components/ui/button"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuGroup,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import { Separator } from "@/components/ui/separator"
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarInset,
  SidebarMenu,
  SidebarMenuBadge,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarMenuSub,
  SidebarMenuSubButton,
  SidebarMenuSubItem,
  SidebarProvider,
  SidebarRail,
  SidebarSeparator,
  SidebarTrigger,
} from "@/components/ui/sidebar"
import { useTranslation } from "@/i18n"
import type { AppRoute } from "@/lib/routes"

type Translate = (key: string, options?: Record<string, unknown>) => string

type NavItem = {
  label: string
  description: string
  route: AppRoute
  icon: ComponentType<{ className?: string }>
  badge?: string
}

type NavGroup = {
  label: string
  items: NavItem[]
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
  const navGroups = getNavGroups(t)
  const currentRoute = routeSummary(route, t)
  const isActive = (item: AppRoute) =>
    item.name === route.name ||
    (item.name === "buckets" && route.name === "bucket")

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

  const toggleTheme = () => setTheme(theme === "dark" ? "light" : "dark")

  return (
    <SidebarProvider
      className="bg-muted/45"
      style={{ "--sidebar-width": "17rem" } as CSSProperties}
    >
      <Sidebar
        variant="inset"
        collapsible="icon"
        className="border-sidebar-border/80"
      >
        <SidebarHeader className="gap-2 border-b border-sidebar-border/70 px-2.5 py-2.5">
          <SidebarMenu>
            <SidebarMenuItem>
              <WorkspaceMenu onNavigate={onNavigate} />
            </SidebarMenuItem>
          </SidebarMenu>

          <SidebarMenu className="group-data-[collapsible=icon]:hidden">
            <SidebarMenuItem>
              <SidebarMenuButton
                variant="outline"
                tooltip={t("appShell.search.open")}
                onClick={() => setCommandOpen(true)}
                className="h-9 border-sidebar-border/80 bg-background/70 text-sidebar-foreground/75 shadow-none hover:bg-sidebar-accent"
              >
                <SearchIcon />
                <span>{t("appShell.search.shortLabel")}</span>
                <SidebarMenuBadge className="right-2 text-[10px] text-sidebar-foreground/45">
                  {t("appShell.search.shortcut")}
                </SidebarMenuBadge>
              </SidebarMenuButton>
            </SidebarMenuItem>
          </SidebarMenu>
        </SidebarHeader>

        <SidebarContent className="gap-0 px-2 py-2">
          <SidebarRuntimePanel />
          <SidebarSeparator className="my-2" />
          {navGroups.map((group) => (
            <SidebarGroup key={group.label} className="px-0 py-1">
              <SidebarGroupLabel className="h-7 px-2 text-[11px] font-semibold tracking-normal text-sidebar-foreground/50 uppercase">
                {group.label}
              </SidebarGroupLabel>
              <SidebarGroupContent>
                <SidebarMenu>
                  {group.items.map((item) => (
                    <SidebarNavItem
                      key={item.label}
                      item={item}
                      active={isActive(item.route)}
                      currentRoute={route}
                      onNavigate={onNavigate}
                    />
                  ))}
                </SidebarMenu>
              </SidebarGroupContent>
            </SidebarGroup>
          ))}
        </SidebarContent>

        <SidebarFooter className="border-t border-sidebar-border/70 px-2.5 py-2.5">
          <SidebarMenu>
            <SidebarMenuItem>
              <SidebarMenuButton
                tooltip={t("appShell.actions.toggleTheme")}
                onClick={toggleTheme}
              >
                {theme === "dark" ? <SunIcon /> : <MoonIcon />}
                <span>{t("appShell.actions.toggleTheme")}</span>
              </SidebarMenuButton>
            </SidebarMenuItem>
            <SidebarMenuItem>
              <UserDropdown
                userName={userName}
                onNavigate={onNavigate}
                onLogout={onLogout}
              >
                <SidebarMenuButton size="lg" className="h-12 px-2">
                  <UserAvatar userName={userName} />
                  <span className="grid min-w-0 flex-1 text-left leading-tight group-data-[collapsible=icon]:hidden">
                    <span className="truncate text-sm font-medium">
                      {userName}
                    </span>
                    <span className="truncate text-xs text-sidebar-foreground/60">
                      {t("appShell.userMenu.currentUser")}
                    </span>
                  </span>
                </SidebarMenuButton>
              </UserDropdown>
            </SidebarMenuItem>
          </SidebarMenu>
        </SidebarFooter>
        <SidebarRail />
      </Sidebar>

      <SidebarInset className="min-w-0 overflow-hidden border border-border/70 bg-background shadow-none">
        <header className="sticky top-0 z-20 flex h-14 shrink-0 items-center gap-2 border-b bg-background/95 px-3 backdrop-blur supports-[backdrop-filter]:bg-background/80">
          <SidebarTrigger className="-ml-1" />
          <Separator orientation="vertical" className="mr-2 h-4" />
          <div className="min-w-0 flex-1">
            <RouteBreadcrumb route={route} onNavigate={onNavigate} />
            <div className="min-w-0 md:hidden">
              <div className="truncate text-sm font-semibold">
                {currentRoute.title}
              </div>
              <div className="truncate text-xs text-muted-foreground">
                {currentRoute.description}
              </div>
            </div>
          </div>

          <div className="ml-auto flex min-w-0 items-center gap-2">
            <button
              className="hidden h-9 w-[min(26rem,38vw)] min-w-0 items-center gap-2 rounded-md border bg-background px-3 text-sm text-muted-foreground shadow-xs transition-colors hover:bg-accent hover:text-accent-foreground lg:flex"
              onClick={() => setCommandOpen(true)}
              type="button"
            >
              <SearchIcon className="size-4" />
              <span className="truncate text-left">
                {t("appShell.search.placeholder")}
              </span>
              <kbd className="ml-auto rounded border bg-muted px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground">
                {t("appShell.search.shortcut")}
              </kbd>
            </button>

            <Badge
              variant="outline"
              className="hidden gap-1.5 rounded-md lg:inline-flex"
            >
              <DatabaseIcon className="size-3" />
              {t("appShell.system.localMetadata")}
            </Badge>
            <Badge
              variant="outline"
              className="hidden gap-1.5 rounded-md md:inline-flex"
            >
              <ServerIcon className="size-3" />
              {t("appShell.system.s3Api")}
            </Badge>
            <Button
              variant="ghost"
              size="icon-sm"
              className="lg:hidden"
              onClick={() => setCommandOpen(true)}
            >
              <SearchIcon />
              <span className="sr-only">{t("appShell.search.open")}</span>
            </Button>
            <Button
              variant="ghost"
              size="icon-sm"
              className="hidden md:inline-flex"
              onClick={toggleTheme}
            >
              {theme === "dark" ? <SunIcon /> : <MoonIcon />}
              <span className="sr-only">
                {t("appShell.actions.toggleTheme")}
              </span>
            </Button>
            <UserDropdown
              userName={userName}
              onNavigate={onNavigate}
              onLogout={onLogout}
            >
              <Button
                variant="outline"
                size="sm"
                className="hidden gap-2 md:inline-flex"
              >
                <UserAvatar userName={userName} />
                <span className="max-w-32 truncate">{userName}</span>
              </Button>
            </UserDropdown>
          </div>
        </header>

        <div className="min-w-0 flex-1 overflow-auto">
          <main className="w-full px-3 py-3 sm:px-4 md:px-6 md:py-5">
            <div className="mx-auto w-full max-w-[1720px]">{children}</div>
          </main>
        </div>
      </SidebarInset>

      <CommandSearch
        open={commandOpen}
        onOpenChange={setCommandOpen}
        onNavigate={onNavigate}
      />
    </SidebarProvider>
  )
}

function WorkspaceMenu({
  onNavigate,
}: {
  onNavigate: (route: AppRoute) => void
}) {
  const { t } = useTranslation()

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <SidebarMenuButton
          size="lg"
          tooltip={t("common.brand.consoleName")}
          className="h-12 px-2 data-open:bg-sidebar-accent data-open:text-sidebar-accent-foreground"
        >
          <div className="flex aspect-square size-9 items-center justify-center rounded-md bg-sidebar-primary text-sidebar-primary-foreground shadow-xs">
            <BlocksIcon className="size-4" />
          </div>
          <span className="grid min-w-0 flex-1 text-left leading-tight group-data-[collapsible=icon]:hidden">
            <span className="truncate text-sm font-semibold">
              {t("common.brand.productName")}
            </span>
            <span className="truncate text-xs text-sidebar-foreground/60">
              {t("appShell.product.subtitle")}
            </span>
          </span>
          <ChevronDownIcon className="ml-auto size-4 text-sidebar-foreground/45 group-data-[collapsible=icon]:hidden" />
        </SidebarMenuButton>
      </DropdownMenuTrigger>
      <DropdownMenuContent side="right" align="start" className="w-64">
        <DropdownMenuLabel>
          <span className="block text-xs font-normal text-muted-foreground">
            {t("appShell.product.workspace")}
          </span>
          <span className="mt-1 block text-sm font-medium">
            {t("common.brand.consoleName")}
          </span>
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuGroup>
          <DropdownMenuItem onClick={() => onNavigate({ name: "dashboard" })}>
            <ChartNoAxesColumnIcon />
            {t("appShell.nav.dashboard")}
          </DropdownMenuItem>
          <DropdownMenuItem onClick={() => onNavigate({ name: "cluster" })}>
            <NetworkIcon />
            {t("appShell.nav.cluster")}
          </DropdownMenuItem>
          <DropdownMenuItem onClick={() => onNavigate({ name: "settings" })}>
            <SettingsIcon />
            {t("appShell.nav.settings")}
          </DropdownMenuItem>
        </DropdownMenuGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  )
}

function SidebarNavItem({
  item,
  active,
  currentRoute,
  onNavigate,
}: {
  item: NavItem
  active: boolean
  currentRoute: AppRoute
  onNavigate: (route: AppRoute) => void
}) {
  const Icon = item.icon
  const showBucketSub =
    item.route.name === "buckets" && currentRoute.name === "bucket"

  return (
    <SidebarMenuItem>
      <SidebarMenuButton
        size="lg"
        tooltip={item.label}
        isActive={active}
        onClick={() => onNavigate(item.route)}
        className="h-11 items-center gap-2 px-2.5 py-2 data-active:bg-sidebar-accent data-active:text-sidebar-accent-foreground data-active:shadow-xs"
      >
        <Icon className="size-4" />
        <span className="grid min-w-0 flex-1 text-left leading-tight group-data-[collapsible=icon]:hidden">
          <span className="truncate text-sm font-medium">{item.label}</span>
          <span className="truncate text-[11px] text-sidebar-foreground/55">
            {item.description}
          </span>
        </span>
      </SidebarMenuButton>
      {item.badge ? (
        <SidebarMenuBadge className="right-2 text-[10px] peer-data-active/menu-button:text-sidebar-accent-foreground/75">
          {item.badge}
        </SidebarMenuBadge>
      ) : null}
      {showBucketSub ? (
        <SidebarMenuSub className="mt-1">
          <SidebarMenuSubItem>
            <SidebarMenuSubButton asChild isActive>
              <button type="button" onClick={() => onNavigate(currentRoute)}>
                <ArchiveIcon />
                <span>{currentRoute.bucketName}</span>
              </button>
            </SidebarMenuSubButton>
          </SidebarMenuSubItem>
        </SidebarMenuSub>
      ) : null}
    </SidebarMenuItem>
  )
}

function SidebarRuntimePanel() {
  const { t } = useTranslation()

  return (
    <div className="px-1 py-1 group-data-[collapsible=icon]:hidden">
      <div className="rounded-md border border-sidebar-border/75 bg-background/65 p-2.5">
        <div className="mb-2 flex items-center justify-between gap-2">
          <div className="min-w-0">
            <div className="truncate text-xs font-semibold text-sidebar-foreground">
              {t("appShell.system.runtime")}
            </div>
            <div className="truncate text-[11px] text-sidebar-foreground/60">
              {t("appShell.system.localMetadata")}
            </div>
          </div>
          <span className="inline-flex items-center gap-1 rounded-md bg-emerald-500/10 px-1.5 py-1 text-[10px] font-medium text-emerald-700 dark:text-emerald-300">
            <span className="size-1.5 rounded-full bg-emerald-500" />
            {t("appShell.system.online")}
          </span>
        </div>
        <div className="grid grid-cols-2 gap-1.5">
          <RuntimeMetric
            icon={ServerIcon}
            label={t("appShell.system.s3Api")}
            value="S3"
          />
          <RuntimeMetric
            icon={HardDriveIcon}
            label={t("appShell.system.erasureCoding")}
            value="EC"
          />
        </div>
      </div>
    </div>
  )
}

function RuntimeMetric({
  icon: Icon,
  label,
  value,
}: {
  icon: ComponentType<{ className?: string }>
  label: string
  value: string
}) {
  return (
    <div className="min-w-0 rounded-md bg-sidebar-accent/60 px-2 py-1.5 text-sidebar-foreground/75">
      <div className="mb-1 flex items-center gap-1.5">
        <Icon className="size-3" />
        <span className="truncate text-[11px]">{label}</span>
      </div>
      <div className="text-xs font-semibold text-sidebar-foreground">
        {value}
      </div>
    </div>
  )
}

function RouteBreadcrumb({
  route,
  onNavigate,
}: {
  route: AppRoute
  onNavigate: (route: AppRoute) => void
}) {
  const { t } = useTranslation()
  const current = routeLabel(route, t)

  return (
    <Breadcrumb className="hidden min-w-0 md:flex">
      <BreadcrumbList>
        <BreadcrumbItem>
          <BreadcrumbLink
            type="button"
            onClick={() => onNavigate({ name: "dashboard" })}
          >
            {t("appShell.breadcrumbs.root")}
          </BreadcrumbLink>
        </BreadcrumbItem>
        {route.name !== "dashboard" ? (
          <>
            <BreadcrumbSeparator />
            {route.name === "bucket" ? (
              <>
                <BreadcrumbItem>
                  <BreadcrumbLink
                    type="button"
                    onClick={() => onNavigate({ name: "buckets" })}
                  >
                    {t("appShell.nav.buckets")}
                  </BreadcrumbLink>
                </BreadcrumbItem>
                <BreadcrumbSeparator />
              </>
            ) : null}
            <BreadcrumbItem>
              <BreadcrumbPage className="max-w-64 truncate">
                {current}
              </BreadcrumbPage>
            </BreadcrumbItem>
          </>
        ) : null}
      </BreadcrumbList>
    </Breadcrumb>
  )
}

function UserDropdown({
  userName,
  onNavigate,
  onLogout,
  children,
}: {
  userName: string
  onNavigate: (route: AppRoute) => void
  onLogout: () => void
  children: ReactNode
}) {
  const { t } = useTranslation()

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>{children}</DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-56">
        <DropdownMenuLabel>
          <span className="block text-xs font-normal text-muted-foreground">
            {t("appShell.userMenu.currentUser")}
          </span>
          <span className="mt-1 block truncate text-sm font-medium">
            {userName}
          </span>
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

function UserAvatar({ userName }: { userName: string }) {
  return (
    <Avatar size="sm">
      <AvatarFallback className="bg-primary/10 text-[11px] font-semibold text-primary">
        {getInitials(userName)}
      </AvatarFallback>
      <AvatarBadge className="bg-emerald-500" />
    </Avatar>
  )
}

function getNavGroups(t: Translate): NavGroup[] {
  return [
    {
      label: t("appShell.sections.overview"),
      items: [
        {
          label: t("appShell.nav.dashboard"),
          description: t("appShell.navDescriptions.dashboard"),
          route: { name: "dashboard" },
          icon: ChartNoAxesColumnIcon,
        },
      ],
    },
    {
      label: t("appShell.sections.storage"),
      items: [
        {
          label: t("appShell.nav.buckets"),
          description: t("appShell.navDescriptions.buckets"),
          route: { name: "buckets" },
          icon: ArchiveIcon,
        },
        {
          label: t("appShell.nav.accessKeys"),
          description: t("appShell.navDescriptions.accessKeys"),
          route: { name: "access-keys" },
          icon: KeyRoundIcon,
        },
      ],
    },
    {
      label: t("appShell.sections.distributed"),
      items: [
        {
          label: t("appShell.nav.cluster"),
          description: t("appShell.navDescriptions.cluster"),
          route: { name: "cluster" },
          icon: NetworkIcon,
          badge: "EC",
        },
        {
          label: t("appShell.nav.health"),
          description: t("appShell.navDescriptions.health"),
          route: { name: "health" },
          icon: ActivityIcon,
        },
      ],
    },
    {
      label: t("appShell.sections.governance"),
      items: [
        {
          label: t("appShell.nav.audit"),
          description: t("appShell.navDescriptions.audit"),
          route: { name: "audit" },
          icon: ShieldCheckIcon,
        },
        {
          label: t("appShell.nav.settings"),
          description: t("appShell.navDescriptions.settings"),
          route: { name: "settings" },
          icon: SettingsIcon,
        },
      ],
    },
  ]
}

function routeSummary(route: AppRoute, t: Translate) {
  if (route.name === "bucket") {
    return {
      title: route.bucketName,
      description: t("appShell.routeDescriptions.bucket"),
    }
  }

  return {
    title: routeLabel(route, t),
    description: t(`appShell.navDescriptions.${routeDescriptionKey(route)}`),
  }
}

function routeDescriptionKey(route: AppRoute) {
  if (route.name === "access-keys") {
    return "accessKeys"
  }

  return route.name
}

function routeLabel(route: AppRoute, t: Translate) {
  if (route.name === "dashboard") {
    return t("appShell.nav.dashboard")
  }

  if (route.name === "buckets") {
    return t("appShell.nav.buckets")
  }

  if (route.name === "cluster") {
    return t("appShell.nav.cluster")
  }

  if (route.name === "health") {
    return t("appShell.nav.health")
  }

  if (route.name === "bucket") {
    return route.bucketName
  }

  if (route.name === "access-keys") {
    return t("appShell.nav.accessKeys")
  }

  if (route.name === "settings") {
    return t("appShell.nav.settings")
  }

  return t("appShell.nav.audit")
}

function getInitials(userName: string) {
  const normalized = userName.trim()
  if (!normalized) {
    return "ME"
  }

  const parts = normalized.split(/\s+/)
  if (parts.length === 1) {
    return normalized.slice(0, 2).toUpperCase()
  }

  return `${parts[0]?.[0] ?? ""}${parts[parts.length - 1]?.[0] ?? ""}`.toUpperCase()
}
