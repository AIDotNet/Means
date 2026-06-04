import type { BaseLayoutProps } from 'fumadocs-ui/layouts/shared';
import { appName, gitConfig } from './shared';
import { i18nConfig } from './i18n';

export function baseOptions(lang: string = i18nConfig.defaultLanguage): BaseLayoutProps {
  const prefix = `/${lang}`;

  return {
    nav: {
      title: appName,
      url: `${prefix}/docs`,
    },
    githubUrl: `https://github.com/${gitConfig.user}/${gitConfig.repo}`,
    i18n: i18nConfig,
    links: [
      {
        text: 'Docs',
        url: `${prefix}/docs`,
        active: 'nested-url',
      },
      {
        text: 'Reference',
        url: `${prefix}/docs/reference/configuration`,
        active: 'nested-url',
      },
      {
        text: 'Console',
        url: `${prefix}/docs/guides/manage-buckets-and-objects`,
        active: 'nested-url',
      },
    ],
  };
}
