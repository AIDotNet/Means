import { useState } from "react"
import { DatabaseIcon, LockKeyholeIcon } from "lucide-react"

import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { useTranslation } from "@/i18n"

type LoginPageProps = {
  onLogin: (userName: string, password: string) => Promise<void>
}

export function LoginPage({ onLogin }: LoginPageProps) {
  const { t } = useTranslation()
  const [userName, setUserName] = useState("admin")
  const [password, setPassword] = useState("meansadmin")
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    setLoading(true)
    setError(null)
    try {
      await onLogin(userName, password)
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : t("login.error.fallback"))
    } finally {
      setLoading(false)
    }
  }

  return (
    <main className="grid min-h-svh bg-background lg:grid-cols-[1.05fr_0.95fr]">
      <section className="relative hidden overflow-hidden border-r bg-sidebar text-sidebar-foreground lg:block">
        <div className="relative flex h-full flex-col justify-between p-12">
          <div className="flex items-center gap-3">
            <span className="flex size-11 items-center justify-center rounded-lg bg-sidebar-primary text-lg font-bold text-sidebar-primary-foreground">
              M
            </span>
            <div>
              <div className="font-semibold">{t("common.brand.consoleName")}</div>
              <div className="text-sm text-sidebar-foreground/65">{t("login.sidebar.subtitle")}</div>
            </div>
          </div>
          <div className="max-w-xl">
            <div className="mb-4 inline-flex items-center gap-2 rounded-full border border-primary/25 bg-primary/10 px-3 py-1 text-sm text-primary">
              <DatabaseIcon className="size-4" />
              {t("login.sidebar.badge")}
            </div>
            <h1 className="text-5xl font-semibold tracking-normal text-sidebar-foreground">
              {t("login.sidebar.title")}
            </h1>
            <p className="mt-5 text-base leading-7 text-sidebar-foreground/70">
              {t("login.sidebar.description")}
            </p>
          </div>
          <div className="text-sm text-sidebar-foreground/55">{t("login.sidebar.footnote")}</div>
        </div>
      </section>
      <section className="flex items-center justify-center p-6">
        <form className="w-full max-w-md rounded-lg border bg-card p-6 text-card-foreground shadow-sm" onSubmit={submit}>
          <div className="mb-6">
            <div className="mb-3 flex size-10 items-center justify-center rounded-lg bg-primary text-primary-foreground">
              <LockKeyholeIcon className="size-5" />
            </div>
            <h2 className="text-2xl font-semibold tracking-normal">{t("login.form.title")}</h2>
            <p className="mt-2 text-sm text-muted-foreground">{t("login.form.description")}</p>
          </div>
          {error ? (
            <Alert variant="destructive" className="mb-4">
              <AlertTitle>{t("login.error.title")}</AlertTitle>
              <AlertDescription>{error}</AlertDescription>
            </Alert>
          ) : null}
          <div className="space-y-4">
            <label className="grid gap-1.5 text-sm">
              {t("login.form.userNameLabel")}
              <Input value={userName} onChange={(event) => setUserName(event.target.value)} />
            </label>
            <label className="grid gap-1.5 text-sm">
              {t("login.form.passwordLabel")}
              <Input
                type="password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
              />
            </label>
          </div>
          <Button className="mt-6 w-full" disabled={loading}>
            {loading ? t("login.form.submitting") : t("login.form.submit")}
          </Button>
        </form>
      </section>
    </main>
  )
}
