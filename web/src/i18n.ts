import i18n from "i18next"
import { initReactI18next } from "react-i18next"

import enUS from "@/locales/en-US.json"
import zhCN from "@/locales/zh-CN.json"

const supportedLanguages = ["zh-CN", "en-US"] as const

type SupportedLanguage = (typeof supportedLanguages)[number]

function detectBrowserLanguage(): SupportedLanguage {
  if (typeof navigator === "undefined") {
    return "en-US"
  }

  const preferredLanguages = navigator.languages.length > 0 ? navigator.languages : [navigator.language]

  for (const language of preferredLanguages) {
    const normalized = language.toLowerCase()
    if (normalized.startsWith("zh")) {
      return "zh-CN"
    }

    if (normalized.startsWith("en")) {
      return "en-US"
    }
  }

  return "en-US"
}

void i18n.use(initReactI18next).init({
  resources: {
    "zh-CN": { translation: zhCN },
    "en-US": { translation: enUS },
  },
  lng: detectBrowserLanguage(),
  fallbackLng: "en-US",
  supportedLngs: supportedLanguages,
  nonExplicitSupportedLngs: true,
  interpolation: {
    escapeValue: false,
  },
  returnNull: false,
})

const setDocumentLanguage = (language: string) => {
  if (typeof document !== "undefined") {
    document.documentElement.lang = language
  }
}

setDocumentLanguage(i18n.resolvedLanguage ?? i18n.language)
i18n.on("languageChanged", setDocumentLanguage)

type TranslationOptions = Record<string, unknown>

export function translate(key: string, options: TranslationOptions = {}) {
  const language = i18n.resolvedLanguage ?? i18n.language ?? "en-US"
  const value =
    i18n.getResource(language, "translation", key) ??
    i18n.getResource("en-US", "translation", key) ??
    key

  if (typeof value !== "string") {
    return key
  }

  return value.replace(/\{\{(\w+)\}\}/g, (_, name: string) =>
    options[name] === undefined ? "" : String(options[name])
  )
}

export function useTranslation() {
  return { t: translate, i18n }
}

(i18n as unknown as { t: typeof translate }).t = translate

export default i18n
