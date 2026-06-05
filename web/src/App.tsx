import { Suspense, lazy, useEffect, useState, type ComponentType } from "react"
import { toast } from "sonner"

import { AppShell } from "@/components/layout/AppShell"
import { Toaster } from "@/components/ui/sonner"
import { Spinner } from "@/components/ui/spinner"
import { TooltipProvider } from "@/components/ui/tooltip"
import { api, type Session } from "@/lib/api-client"
import { parseRoute, routeHref, type AppRoute } from "@/lib/routes"
import { useTranslation } from "@/i18n"

type NamedLazyComponent<T> = T extends ComponentType<infer TProps> ? ComponentType<TProps> : never

function lazyNamedPage<M, K extends keyof M>(
  importer: () => Promise<M>,
  exportName: K
) {
  return lazy(() =>
    importer().then((module) => ({
      default: module[exportName] as NamedLazyComponent<M[K]>,
    }))
  )
}

const DashboardPage = lazyNamedPage(() => import("@/features/dashboard/DashboardPage"), "DashboardPage")
const BucketsPage = lazyNamedPage(() => import("@/features/buckets/BucketsPage"), "BucketsPage")
const BucketDetailPage = lazyNamedPage(() => import("@/features/buckets/BucketDetailPage"), "BucketDetailPage")
const ClusterPage = lazyNamedPage(() => import("@/features/cluster/ClusterPage"), "ClusterPage")
const HealthPage = lazyNamedPage(() => import("@/features/cluster/HealthPage"), "HealthPage")
const AccessKeysPage = lazyNamedPage(() => import("@/features/access-keys/AccessKeysPage"), "AccessKeysPage")
const SettingsPage = lazyNamedPage(() => import("@/features/settings/SettingsPage"), "SettingsPage")
const AuditPage = lazyNamedPage(() => import("@/features/audit/AuditPage"), "AuditPage")
const LoginPage = lazyNamedPage(() => import("@/features/auth/LoginPage"), "LoginPage")

export function App() {
  const { t } = useTranslation()
  const [session, setSession] = useState<Session | null>(null)
  const [checkingSession, setCheckingSession] = useState(true)
  const [route, setRoute] = useState<AppRoute>(() => parseRoute(window.location.pathname))

  useEffect(() => {
    api
      .session()
      .then(setSession)
      .catch(() => setSession(null))
      .finally(() => setCheckingSession(false))
  }, [])

  useEffect(() => {
    const handlePopState = () => setRoute(parseRoute(window.location.pathname))
    window.addEventListener("popstate", handlePopState)
    return () => window.removeEventListener("popstate", handlePopState)
  }, [])

  const navigate = (nextRoute: AppRoute) => {
    const href = routeHref(nextRoute)
    window.history.pushState(null, "", href)
    setRoute(nextRoute)
  }

  const login = async (userName: string, password: string) => {
    const nextSession = await api.login(userName, password)
    setSession(nextSession)
    toast.success(t("app.toast.loginSuccess"))
  }

  const logout = async () => {
    await api.logout()
    setSession(null)
    window.history.pushState(null, "", "/")
    setRoute({ name: "dashboard" })
    toast.success(t("app.toast.logoutSuccess"))
  }

  const content = () => {
    if (route.name === "buckets") {
      return <BucketsPage onOpenBucket={(bucketName) => navigate({ name: "bucket", bucketName })} />
    }

    if (route.name === "cluster") {
      return <ClusterPage />
    }

    if (route.name === "health") {
      return <HealthPage />
    }

    if (route.name === "bucket") {
      return <BucketDetailPage bucketName={route.bucketName} />
    }

    if (route.name === "access-keys") {
      return <AccessKeysPage />
    }

    if (route.name === "settings") {
      return <SettingsPage />
    }

    if (route.name === "audit") {
      return <AuditPage />
    }

    return <DashboardPage />
  }

  if (checkingSession) {
    return (
      <div className="grid min-h-svh place-items-center bg-background text-sm text-muted-foreground">
        {t("app.checkingSession")}
      </div>
    )
  }

  return (
    <TooltipProvider>
      {session ? (
        <AppShell route={route} userName={session.userName} onNavigate={navigate} onLogout={logout}>
          <Suspense fallback={<RouteLoading />}>{content()}</Suspense>
        </AppShell>
      ) : (
        <Suspense fallback={<FullscreenLoading />}>
          <LoginPage onLogin={login} />
        </Suspense>
      )}
      <Toaster richColors />
    </TooltipProvider>
  )
}

export default App

function RouteLoading() {
  const { t } = useTranslation()

  return (
    <div className="grid min-h-[40vh] place-items-center rounded-2xl border border-dashed border-border/60 bg-muted/20 text-muted-foreground">
      <div className="flex items-center gap-2 text-sm font-medium">
        <Spinner className="size-5" />
        <span>{t("app.loadingPage")}</span>
      </div>
    </div>
  )
}

function FullscreenLoading() {
  const { t } = useTranslation()

  return (
    <div className="grid min-h-svh place-items-center bg-background text-sm text-muted-foreground">
      <div className="flex items-center gap-2 font-medium">
        <Spinner className="size-5" />
        <span>{t("app.loadingPage")}</span>
      </div>
    </div>
  )
}
