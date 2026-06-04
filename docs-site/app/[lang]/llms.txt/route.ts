import { i18n, isSupportedLanguage } from '@/lib/i18n';
import { source } from '@/lib/source';
import { llms } from 'fumadocs-core/source';
import { notFound } from 'next/navigation';

export const revalidate = false;

export async function GET(
  _request: Request,
  { params }: { params: Promise<{ lang: string }> },
) {
  const { lang } = await params;
  if (!isSupportedLanguage(lang)) notFound();

  return new Response(llms(source).index(lang), {
    headers: {
      'Content-Type': 'text/markdown; charset=utf-8',
    },
  });
}

export function generateStaticParams() {
  return i18n.languages.map((lang) => ({ lang }));
}
