import { NextFetchEvent, NextRequest, NextResponse } from 'next/server';
import { isMarkdownPreferred, rewritePath } from 'fumadocs-core/negotiation';
import { createI18nMiddleware } from 'fumadocs-core/i18n/middleware';
import { i18nConfig } from '@/lib/i18n';
import { docsContentRoute, docsRoute } from '@/lib/shared';

const { rewrite: rewriteDocs } = rewritePath(
  `/{lang}${docsRoute}{/*path}`,
  `/{lang}${docsContentRoute}{/*path}/content.md`,
);
const { rewrite: rewriteSuffix } = rewritePath(
  `/{lang}${docsRoute}{/*path}.md`,
  `/{lang}${docsContentRoute}{/*path}/content.md`,
);
const handleI18n = createI18nMiddleware(i18nConfig);
const localizedNextAssetPattern = new RegExp(
  `^/(${i18nConfig.languages.join('|')})/(_next/.*)$`,
);

export default function proxy(request: NextRequest, event: NextFetchEvent) {
  const localizedNextAsset = localizedNextAssetPattern.exec(request.nextUrl.pathname);
  if (localizedNextAsset) {
    const url = request.nextUrl.clone();
    url.pathname = `/${localizedNextAsset[2]}`;
    return NextResponse.rewrite(url);
  }

  if (
    request.nextUrl.pathname.startsWith('/api/') ||
    request.nextUrl.pathname.startsWith('/_next/')
  ) {
    return NextResponse.next();
  }

  if (request.nextUrl.pathname === '/') {
    return NextResponse.redirect(new URL(`/${i18nConfig.defaultLanguage}${docsRoute}`, request.nextUrl));
  }

  const result = rewriteSuffix(request.nextUrl.pathname);
  if (result) {
    return NextResponse.rewrite(new URL(result, request.nextUrl));
  }

  if (isMarkdownPreferred(request)) {
    const result = rewriteDocs(request.nextUrl.pathname);

    if (result) {
      return NextResponse.rewrite(new URL(result, request.nextUrl));
    }
  }

  return handleI18n(request, event);
}
