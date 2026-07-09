import { useState } from "react"
import { ChevronDownIcon, FileJsonIcon } from "lucide-react"

import { Button } from "@/components/ui/button"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import { Textarea } from "@/components/ui/textarea"
import { useTranslation } from "@/i18n"

const policyPlaceholder = `{
  "Version": "2012-10-17",
  "Statement": []
}`

type PolicyEditorProps = {
  value: string
  onChange: (value: string) => void
  onSave: () => void | Promise<void>
  onDelete: () => void | Promise<void>
  mode?: "bucket" | "accessKey"
  bucketName?: string
  title?: string
  description?: string
  compact?: boolean
}

type PolicyTemplateId =
  | "publicRead"
  | "publicReadAuthenticatedWrite"
  | "denyDelete"
  | "authenticatedReadWrite"
  | "scopedAllow"
  | "readOnly"
  | "listBucketsOnly"
  | "fullBucketAccess"

export function PolicyEditor({
  value,
  onChange,
  onSave,
  onDelete,
  mode = "bucket",
  bucketName,
  title,
  description,
  compact = false,
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

  const applyTemplate = (templateId: PolicyTemplateId) => {
    onChange(JSON.stringify(buildPolicyTemplate(templateId, bucketName || "bucket"), null, 2))
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

  const templates =
    mode === "accessKey"
      ? ([
          ["scopedAllow", t("policyEditor.templates.scopedAllow")],
          ["readOnly", t("policyEditor.templates.readOnly")],
          ["listBucketsOnly", t("policyEditor.templates.listBucketsOnly")],
          ["fullBucketAccess", t("policyEditor.templates.fullBucketAccess")],
        ] as const)
      : ([
          ["publicRead", t("policyEditor.templates.publicRead")],
          ["publicReadAuthenticatedWrite", t("policyEditor.templates.publicReadAuthenticatedWrite")],
          ["authenticatedReadWrite", t("policyEditor.templates.authenticatedReadWrite")],
          ["denyDelete", t("policyEditor.templates.denyDelete")],
        ] as const)

  return (
    <section className="rounded-lg border bg-card text-card-foreground shadow-xs">
      <div className="flex flex-wrap items-center justify-between gap-3 border-b p-4">
        <div>
          <div className="flex items-center gap-2 font-medium">
            <FileJsonIcon className="size-4 text-primary" />
            {title ?? t("policyEditor.title")}
          </div>
          <p className="mt-1 text-sm text-muted-foreground">
            {description ??
              (mode === "accessKey"
                ? t("policyEditor.accessKeyDescription")
                : t("policyEditor.description"))}
          </p>
        </div>
        <div className="flex gap-2">
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="outline" size="sm">
                {t("policyEditor.actions.templates")}
                <ChevronDownIcon />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              {templates.map(([id, label]) => (
                <DropdownMenuItem key={id} onClick={() => applyTemplate(id)}>
                  {label}
                </DropdownMenuItem>
              ))}
            </DropdownMenuContent>
          </DropdownMenu>
          <Button variant="outline" size="sm" onClick={format}>
            {t("policyEditor.actions.format")}
          </Button>
        </div>
      </div>
      <div className="p-4">
        <Textarea
          className={`${compact ? "min-h-56" : "min-h-80"} font-mono text-xs`}
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

function buildPolicyTemplate(templateId: PolicyTemplateId, bucketName: string) {
  switch (templateId) {
    case "publicRead":
      return {
        Version: "2012-10-17",
        Statement: [
          {
            Effect: "Allow",
            Principal: "*",
            Action: "s3:GetObject",
            Resource: `arn:aws:s3:::${bucketName}/*`,
          },
        ],
      }
    case "publicReadAuthenticatedWrite":
      return {
        Version: "2012-10-17",
        Statement: [
          {
            Effect: "Allow",
            Principal: "*",
            Action: ["s3:ListBucket", "s3:GetObject"],
            Resource: [`arn:aws:s3:::${bucketName}`, `arn:aws:s3:::${bucketName}/*`],
          },
          {
            Effect: "Allow",
            Principal: "YOUR_ACCESS_KEY",
            Action: ["s3:PutObject", "s3:DeleteObject", "s3:AbortMultipartUpload", "s3:ListMultipartUploadParts"],
            Resource: `arn:aws:s3:::${bucketName}/*`,
          },
        ],
      }
    case "authenticatedReadWrite":
      return {
        Version: "2012-10-17",
        Statement: [
          {
            Effect: "Allow",
            Principal: "YOUR_ACCESS_KEY",
            Action: ["s3:ListBucket", "s3:GetObject", "s3:PutObject", "s3:DeleteObject"],
            Resource: [`arn:aws:s3:::${bucketName}`, `arn:aws:s3:::${bucketName}/*`],
          },
        ],
      }
    case "denyDelete":
      return {
        Version: "2012-10-17",
        Statement: [
          {
            Effect: "Allow",
            Principal: "*",
            Action: ["s3:ListBucket", "s3:GetObject"],
            Resource: [`arn:aws:s3:::${bucketName}`, `arn:aws:s3:::${bucketName}/*`],
          },
          {
            Effect: "Deny",
            Principal: "*",
            Action: "s3:DeleteObject",
            Resource: `arn:aws:s3:::${bucketName}/*`,
          },
        ],
      }
    case "scopedAllow":
      return {
        Version: "2012-10-17",
        Statement: [
          {
            Effect: "Allow",
            Action: ["s3:ListBucket", "s3:GetObject", "s3:PutObject"],
            Resource: [
              `arn:aws:s3:::${bucketName}`,
              `arn:aws:s3:::${bucketName}/*`,
            ],
          },
        ],
      }
    case "readOnly":
      return {
        Version: "2012-10-17",
        Statement: [
          {
            Effect: "Allow",
            Action: ["s3:ListBucket", "s3:GetObject"],
            Resource: [
              `arn:aws:s3:::${bucketName}`,
              `arn:aws:s3:::${bucketName}/*`,
            ],
          },
        ],
      }
    case "listBucketsOnly":
      return {
        Version: "2012-10-17",
        Statement: [
          {
            Effect: "Allow",
            Action: "s3:ListAllMyBuckets",
            Resource: ["arn:aws:s3:::*", "*"],
          },
        ],
      }
    case "fullBucketAccess":
      return {
        Version: "2012-10-17",
        Statement: [
          {
            Effect: "Allow",
            Action: [
              "s3:ListBucket",
              "s3:GetObject",
              "s3:PutObject",
              "s3:DeleteObject",
              "s3:GetObjectTagging",
              "s3:PutObjectTagging",
              "s3:DeleteObjectTagging",
              "s3:AbortMultipartUpload",
              "s3:ListMultipartUploadParts",
            ],
            Resource: [
              `arn:aws:s3:::${bucketName}`,
              `arn:aws:s3:::${bucketName}/*`,
            ],
          },
        ],
      }
  }
}
