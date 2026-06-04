import { getI18nProvider, i18n, isSupportedLanguage } from '@/lib/i18n';
import { RootProvider } from 'fumadocs-ui/provider/next';
import { notFound } from 'next/navigation';
import type { ReactNode } from 'react';

export default async function LangLayout({
  children,
  params,
}: {
  children: ReactNode;
  params: Promise<{ lang: string }>;
}) {
  const { lang } = await params;
  if (!isSupportedLanguage(lang)) notFound();

  return <RootProvider i18n={getI18nProvider(lang)}>{children}</RootProvider>;
}

export function generateStaticParams() {
  return i18n.languages.map((lang) => ({ lang }));
}
