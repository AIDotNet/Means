import i18n from "@/i18n"

function currentLocale(): string {
  const language = (i18n.resolvedLanguage ?? i18n.language ?? "en-US").toLowerCase()
  if (language.startsWith("zh")) {
    return "zh-CN"
  }

  if (language.startsWith("en")) {
    return "en-US"
  }

  return "en-US"
}

export function formatBytes(value: number): string {
  if (value === 0) {
    return "0 B"
  }

  const units = ["B", "KB", "MB", "GB", "TB"]
  const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1)
  return `${(value / 1024 ** index).toFixed(index === 0 ? 0 : 1)} ${units[index]}`
}

export function formatNumber(value: number): string {
  return new Intl.NumberFormat(currentLocale()).format(value)
}

export function formatDateTime(value: string): string {
  return new Intl.DateTimeFormat(currentLocale(), {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(new Date(value))
}

export function fileNameFromKey(key: string): string {
  const parts = key.split("/").filter(Boolean)
  return parts.at(-1) ?? key
}
