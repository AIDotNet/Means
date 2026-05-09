import { useState } from "react"
import { FileJsonIcon, WandSparklesIcon } from "lucide-react"

import { Button } from "@/components/ui/button"
import { Textarea } from "@/components/ui/textarea"
import { useTranslation } from "@/i18n"

const policyPlaceholder = `{
  "Version": "2012-10-17",
  "Statement": []
}`

type PolicyEditorProps = {
  bucketName: string
  value: string
  onChange: (value: string) => void
  onSave: () => void | Promise<void>
  onDelete: () => void | Promise<void>
}

export function PolicyEditor({
  bucketName,
  value,
  onChange,
  onSave,
  onDelete,
}: PolicyEditorProps) {
  const { t } = useTranslation()
  const [error, setError] = useState<string | null>(null)

  const format = () => {
    try {
      onChange(JSON.stringify(JSON.parse(value || "{}"), null, 2))
      setError(null)
    } catch {
      setError(t("policyEditor.errors.invalidForFormat"))
    }
  }

  const fillPublicRead = () => {
    onChange(
      JSON.stringify(
        {
          Version: "2012-10-17",
          Statement: [
            {
              Effect: "Allow",
              Principal: "*",
              Action: "s3:GetObject",
              Resource: `arn:aws:s3:::${bucketName}/*`,
            },
          ],
        },
        null,
        2
      )
    )
    setError(null)
  }

  const save = () => {
    try {
      JSON.parse(value || "{}")
      setError(null)
      void onSave()
    } catch {
      setError(t("policyEditor.errors.invalidForSave"))
    }
  }

  return (
    <section className="rounded-lg border bg-card text-card-foreground shadow-xs">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b p-4">
        <div>
          <div className="flex items-center gap-2 font-medium">
            <FileJsonIcon className="size-4 text-primary" />
            {t("policyEditor.title")}
          </div>
          <p className="mt-1 text-sm text-muted-foreground">
            {t("policyEditor.description")}
          </p>
        </div>
        <div className="flex gap-2">
          <Button variant="outline" size="sm" onClick={fillPublicRead}>
            <WandSparklesIcon />
            {t("policyEditor.actions.publicReadTemplate")}
          </Button>
          <Button variant="outline" size="sm" onClick={format}>
            {t("policyEditor.actions.format")}
          </Button>
        </div>
      </div>
      <div className="p-4">
        <Textarea
          className="min-h-80 font-mono text-xs"
          placeholder={policyPlaceholder}
          value={value}
          onChange={(event) => onChange(event.target.value)}
        />
        {error ? <p className="mt-2 text-sm text-destructive">{error}</p> : null}
      </div>
      <div className="flex justify-end gap-2 border-t p-4">
        <Button variant="outline" onClick={onDelete}>
          {t("policyEditor.actions.deletePolicy")}
        </Button>
        <Button onClick={save}>{t("policyEditor.actions.savePolicy")}</Button>
      </div>
    </section>
  )
}
