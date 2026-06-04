import {
  ArchiveIcon,
  ChartNoAxesColumnIcon,
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
  SidebarProvider,
  SidebarRail,
  SidebarSeparator,
  SidebarTrigger,
} from "@/components/ui/sidebar"
import type { AppRoute } from "@/lib/routes"
import { useTranslation } from "@/i18n"

type NavItem = {
  label: string
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
  const navGroups: NavGroup[] = [
    {
      label: t("appShell.sections.overview"),
      items: [
        { label: t("appShell.nav.dashboard"), route: { name: "dashboard" }, icon: ChartNoAxesColumnIcon },
      ],
    },
    {
      label: t("appShell.sections.storage"),
      items: [
        { label: t("appShell.nav.buckets"), route: { name: "buckets" }, icon: ArchiveIcon },
        { label: t("appShell.nav.accessKeys"), route: { name: "access-keys" }, icon: KeyRoundIcon },
      ],
    },
    {
      label: t("appShell.sections.distributed"),
      items: [
        { label: t("appShell.nav.cluster"), route: { name: "cluster" }, icon: ServerIcon, badge: "EC" },
        { label: t("appShell.nav.health"), route: { name: "health" }, icon: HardDriveIcon },
      ],
    },
    {
      label: t("appShell.sections.governance"),
      items: [
        { label: t("appShell.nav.audit"), route: { name: "audit" }, icon: ShieldCheckIcon },
        { label: t("appShell.nav.settings"), route: { name: "settings" }, icon: SettingsIcon },
      ],
    },
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

  const currentLabel = routeLabel(route, t)

  return (
    <SidebarProvider className="bg-muted/40">
      <Sidebar variant="inset" collapsible="icon">
        <SidebarHeader>
          <SidebarMenu>
            <SidebarMenuItem>
              <SidebarMenuButton
                size="lg"
                tooltip={t("common.brand.consoleName")}
                onClick={() => onNavigate({ name: "dashboard" })}
                className="h-12"
              >
                <div className="flex aspect-square size-8 items-center justify-center rounded-lg bg-sidebar-primary text-sm font-black text-sidebar-primary-foreground">
                  M
                </div>
                <div className="grid flex-1 text-left text-sm leading-tight group-data-[collapsible=icon]:hidden">
                  <span className="truncate font-semibold">{t("common.brand.productName")}</span>
                  <span className="truncate text-xs text-sidebar-foreground/65">{t("appShell.product.subtitle")}</span>
                </div>
              </SidebarMenuButton>
            </SidebarMenuItem>
            <SidebarMenuItem>
              <SidebarMenuButton
                variant="outline"
                tooltip={t("appShell.search.open")}
                onClick={() => setCommandOpen(true)}
              >
                <SearchIcon />
                <span>{t("appShell.search.shortLabel")}</span>
                <SidebarMenuBadge>{t("appShell.search.shortcut")}</SidebarMenuBadge>
              </SidebarMenuButton>
            </SidebarMenuItem>
          </SidebarMenu>
        </SidebarHeader>

        <SidebarContent>
          {navGroups.map((group) => (
            <SidebarGroup key={group.label}>
              <SidebarGroupLabel>{group.label}</SidebarGroupLabel>
              <SidebarGroupContent>
                <SidebarMenu>
                  {group.items.map((item) => (
                    <SidebarNavItem
                      key={item.label}
                      item={item}
                      active={isActive(item.route)}
                      onNavigate={onNavigate}
                    />
                  ))}
                </SidebarMenu>
              </SidebarGroupContent>
            </SidebarGroup>
          ))}
        </SidebarContent>

        <SidebarFooter>
          <SidebarSeparator />
          <SidebarMenu>
            <SidebarMenuItem>
              <SidebarMenuButton
                tooltip={t("appShell.actions.toggleTheme")}
                onClick={() => setTheme(theme === "dark" ? "light" : "dark")}
              >
                {theme === "dark" ? <SunIcon /> : <MoonIcon />}
                <span>{t("appShell.actions.toggleTheme")}</span>
              </SidebarMenuButton>
            </SidebarMenuItem>
            <SidebarMenuItem>
              <UserDropdown userName={userName} onNavigate={onNavigate} onLogout={onLogout}>
                <SidebarMenuButton size="lg" className="h-12">
                  <UserCircleIcon />
                  <div className="grid flex-1 text-left text-sm leading-tight group-data-[collapsible=icon]:hidden">
                    <span className="truncate font-medium">{userName}</span>
                    <span className="truncate text-xs text-sidebar-foreground/65">
                      {t("appShell.userMenu.currentUser")}
                    </span>
                  </div>
                </SidebarMenuButton>
              </UserDropdown>
            </SidebarMenuItem>
          </SidebarMenu>
        </SidebarFooter>
        <SidebarRail />
      </Sidebar>

      <SidebarInset className="overflow-hidden">
        <header className="sticky top-0 z-20 flex h-14 shrink-0 items-center gap-2 border-b bg-background/95 px-3 backdrop-blur supports-[backdrop-filter]:bg-background/80">
          <SidebarTrigger className="-ml-1" />
          <Separator orientation="vertical" className="mr-2 h-4" />
          <RouteBreadcrumb route={route} onNavigate={onNavigate} />

          <button
            className="ml-auto hidden h-9 w-full max-w-md items-center gap-2 rounded-md border bg-background px-3 text-sm text-muted-foreground shadow-xs transition-colors hover:bg-accent hover:text-accent-foreground md:flex"
            onClick={() => setCommandOpen(true)}
          >
            <SearchIcon className="size-4" />
            <span className="truncate text-left">{t("appShell.search.placeholder")}</span>
            <kbd className="ml-auto rounded border bg-muted px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground">
              {t("appShell.search.shortcut")}
            </kbd>
          </button>

          <div className="flex items-center gap-2">
            <Badge variant="secondary" className="hidden rounded-md md:inline-flex">
              <DatabaseIcon className="size-3" />
              {t("appShell.badges.xlfs")}
            </Badge>
            <Button
              variant="ghost"
              size="icon-sm"
              className="md:hidden"
              onClick={() => setCommandOpen(true)}
            >
              <SearchIcon />
              <span className="sr-only">{t("appShell.search.open")}</span>
            </Button>
            <Button
              variant="ghost"
              size="icon-sm"
              className="md:hidden"
              onClick={() => setTheme(theme === "dark" ? "light" : "dark")}
            >
              {theme === "dark" ? <SunIcon /> : <MoonIcon />}
              <span className="sr-only">{t("appShell.actions.toggleTheme")}</span>
            </Button>
            <UserDropdown userName={userName} onNavigate={onNavigate} onLogout={onLogout}>
              <Button variant="outline" size="sm" className="hidden gap-2 md:inline-flex">
                <UserCircleIcon />
                <span className="max-w-32 truncate">{userName}</span>
              </Button>
            </UserDropdown>
          </div>
        </header>

        <div className="min-w-0 flex-1 overflow-auto">
          <main className="mx-auto w-full max-w-[1600px] px-3 py-3 sm:px-4 md:px-6 md:py-5">
            <div className="mb-4 flex flex-col gap-1 md:hidden">
              <span className="text-xs font-medium text-muted-foreground">{t("common.brand.consoleName")}</span>
              <h1 className="text-xl font-semibold tracking-tight">{currentLabel}</h1>
            </div>
            {children}
          </main>
        </div>
      </SidebarInset>

      <CommandSearch open={commandOpen} onOpenChange={setCommandOpen} onNavigate={onNavigate} />
    </SidebarProvider>
  )
}

function SidebarNavItem({
  item,
  active,
  onNavigate,
}: {
  item: NavItem
  active: boolean
  onNavigate: (route: AppRoute) => void
}) {
  const Icon = item.icon

  return (
    <SidebarMenuItem>
      <SidebarMenuButton
        tooltip={item.label}
        isActive={active}
        onClick={() => onNavigate(item.route)}
      >
        <Icon />
        <span>{item.label}</span>
      </SidebarMenuButton>
      {item.badge ? <SidebarMenuBadge>{item.badge}</SidebarMenuBadge> : null}
    </SidebarMenuItem>
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
          <BreadcrumbLink type="button" onClick={() => onNavigate({ name: "dashboard" })}>
            {t("appShell.breadcrumbs.root")}
          </BreadcrumbLink>
        </BreadcrumbItem>
        {route.name !== "dashboard" ? (
          <>
            <BreadcrumbSeparator />
            {route.name === "bucket" ? (
              <>
                <BreadcrumbItem>
                  <BreadcrumbLink type="button" onClick={() => onNavigate({ name: "buckets" })}>
                    {t("appShell.nav.buckets")}
                  </BreadcrumbLink>
                </BreadcrumbItem>
                <BreadcrumbSeparator />
              </>
            ) : null}
            <BreadcrumbItem>
              <BreadcrumbPage className="max-w-64 truncate">{current}</BreadcrumbPage>
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
          <span className="mt-1 block truncate text-sm font-medium">{userName}</span>
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

function routeLabel(route: AppRoute, t: (key: string) => string) {
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
