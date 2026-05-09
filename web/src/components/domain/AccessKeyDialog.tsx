import { useEffect, useState } from "react"
import { CopyIcon, KeyRoundIcon } from "lucide-react"
import { toast } from "sonner"

import { Button } from "@/components/ui/button"
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import type { AccessKeySecretResult } from "@/lib/api-client"
import { useTranslation } from "@/i18n"

type AccessKeyDialogProps = {
  created: AccessKeySecretResult | null
  open: boolean
  onOpenChange: (open: boolean) => void
}

export function AccessKeyDialog({ created, open, onOpenChange }: AccessKeyDialogProps) {
  const { t } = useTranslation()
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    if (open) {
      setCopied(false)
    }
  }, [created?.accessKey, open])

  const copySecret = async () => {
    if (!created) {
      return
    }

    await navigator.clipboard.writeText(created.secretKey)
    setCopied(true)
    toast.success(t("accessKeyDialog.toast.secretCopied"))
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <KeyRoundIcon className="size-4 text-primary" />
            {t("accessKeyDialog.title")}
          </DialogTitle>
          <DialogDescription>{t("accessKeyDialog.description")}</DialogDescription>
        </DialogHeader>
        {created ? (
          <div className="space-y-3">
            <label className="grid gap-1.5 text-sm">
              {t("accessKeyDialog.labels.accessKey")}
              <Input readOnly value={created.accessKey} />
            </label>
            <label className="grid gap-1.5 text-sm">
              {t("accessKeyDialog.labels.secretKey")}
              <Input readOnly value={created.secretKey} />
            </label>
          </div>
        ) : null}
        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>
            {t("common.actions.close")}
          </Button>
          <Button onClick={copySecret} disabled={!created || copied}>
            <CopyIcon />
            {copied ? t("common.actions.copied") : t("accessKeyDialog.actions.copySecret")}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
