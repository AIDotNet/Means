import { ArchiveIcon, MoreHorizontalIcon, Trash2Icon } from "lucide-react"

import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table"
import type { BucketUsage } from "@/lib/api-client"
import { formatBytes, formatDateTime, formatNumber } from "@/lib/formatters"
import { useTranslation } from "@/i18n"

type BucketTableProps = {
  buckets: BucketUsage[]
  loading?: boolean
  onOpen: (bucketName: string) => void
  onDelete: (bucketName: string) => void
}

export function BucketTable({ buckets, loading = false, onOpen, onDelete }: BucketTableProps) {
  const { t } = useTranslation()

  return (
    <div className="overflow-hidden rounded-[1.15rem] border border-slate-200/80 bg-white text-slate-950 shadow-[0_18px_45px_rgba(15,23,42,0.05)] dark:border-border dark:bg-card dark:text-card-foreground">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{t("bucketTable.columns.bucket")}</TableHead>
            <TableHead>{t("bucketTable.columns.objectCount")}</TableHead>
            <TableHead>{t("bucketTable.columns.size")}</TableHead>
            <TableHead>{t("bucketTable.columns.createdAt")}</TableHead>
            <TableHead className="w-12" />
          </TableRow>
        </TableHeader>
        <TableBody>
          {buckets.map((bucket) => (
            <TableRow key={bucket.bucketName}>
              <TableCell>
                <button
                  className="flex items-center gap-2 text-left font-medium hover:text-primary"
                  onClick={() => onOpen(bucket.bucketName)}
                >
                  <ArchiveIcon className="size-4 text-primary" />
                  {bucket.bucketName}
                </button>
              </TableCell>
              <TableCell>{formatNumber(bucket.objectCount)}</TableCell>
              <TableCell>{formatBytes(bucket.totalBytes)}</TableCell>
              <TableCell className="text-muted-foreground">{formatDateTime(bucket.createdAt)}</TableCell>
              <TableCell>
                <DropdownMenu>
                  <DropdownMenuTrigger asChild>
                    <Button variant="ghost" size="icon-sm">
                      <MoreHorizontalIcon />
                      <span className="sr-only">{t("bucketTable.aria.openActionsMenu")}</span>
                    </Button>
                  </DropdownMenuTrigger>
                  <DropdownMenuContent align="end">
                    <DropdownMenuItem onClick={() => onOpen(bucket.bucketName)}>
                      {t("bucketTable.actions.openObjects")}
                    </DropdownMenuItem>
                    <DropdownMenuItem
                      variant="destructive"
                      onClick={() => onDelete(bucket.bucketName)}
                    >
                      <Trash2Icon />
                      {t("bucketTable.actions.deleteBucket")}
                    </DropdownMenuItem>
                  </DropdownMenuContent>
                </DropdownMenu>
              </TableCell>
            </TableRow>
          ))}
          {loading && buckets.length === 0 ? (
            <TableRow>
              <TableCell colSpan={5} className="h-32 text-center text-muted-foreground">
                {t("bucketTable.states.loading")}
              </TableCell>
            </TableRow>
          ) : null}
          {!loading && buckets.length === 0 ? (
            <TableRow>
              <TableCell colSpan={5} className="h-32 text-center text-muted-foreground">
                {t("bucketTable.states.empty")}
              </TableCell>
            </TableRow>
          ) : null}
        </TableBody>
      </Table>
      <div className="border-t border-slate-100 px-4 py-3 dark:border-border">
        <Badge variant="outline">{t("bucketTable.footer.namingRule")}</Badge>
      </div>
    </div>
  )
}
