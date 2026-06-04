import { i18n, isSupportedLanguage } from '@/lib/i18n';
import { getLLMText, source } from '@/lib/source';
import { notFound } from 'next/navigation';

export const revalidate = false;

export async function GET(
  _request: Request,
  { params }: { params: Promise<{ lang: string }> },
) {
  const { lang } = await params;
  if (!isSupportedLanguage(lang)) notFound();

  const pages = source.getPages(lang);
  const scanned = await Promise.all(pages.map(getLLMText));

  return new Response(scanned.join('\n\n'), {
    headers: {
      'Content-Type': 'text/markdown; charset=utf-8',
    },
  });
}

export function generateStaticParams() {
  return i18n.languages.map((lang) => ({ lang }));
}
