import { StrictMode } from "react"
import { createRoot } from "react-dom/client"

import "@/i18n"
import "./index.css"
import App from "./App.tsx"
import { ThemeProvider } from "@/components/theme-provider.tsx"

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <ThemeProvider defaultTheme="light" storageKey="means-console-theme">
      <App />
    </ThemeProvider>
  </StrictMode>
)
