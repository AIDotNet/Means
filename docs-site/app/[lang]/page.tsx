import { isSupportedLanguage } from '@/lib/i18n';
import { redirect } from 'next/navigation';

export default async function LangHome({
  params,
}: {
  params: Promise<{ lang: string }>;
}) {
  const { lang } = await params;
  redirect(`/${isSupportedLanguage(lang) ? lang : 'zh'}/docs`);
}
