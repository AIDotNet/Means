import { defineI18n, type I18nConfig } from 'fumadocs-core/i18n';
import { i18nProvider, uiTranslations } from 'fumadocs-ui/i18n';

export const languages = ['zh', 'en'] as const;
export type Language = (typeof languages)[number];

export const i18nConfig: I18nConfig<Language> = {
  languages: [...languages],
  defaultLanguage: 'zh',
  hideLocale: 'never',
  parser: 'dir',
};

export const i18n = defineI18n(i18nConfig);

export const translations = i18n
  .translations()
  .extend(uiTranslations())
  .add('ui', {
    zh: {
      displayName: '中文',
      search: '搜索文档',
      searchNoResult: '没有找到结果',
      searchOpen: '打开搜索',
      searchClose: '关闭搜索',
      toc: '本页内容',
      tocNoHeadings: '没有标题',
      tocInline: '本页内容',
      lastUpdate: '最后更新',
      chooseLanguage: '选择语言',
      nextPage: '下一页',
      previousPage: '上一页',
      chooseTheme: '选择主题',
      editOnGithub: '在 GitHub 编辑',
      themeToggle: '切换主题',
      themeLight: '浅色',
      themeDark: '深色',
      themeSystem: '跟随系统',
      codeBlockCopy: '复制',
      codeBlockCopied: '已复制',
      menuToggle: '打开菜单',
      pageActionsCopyMarkdown: '复制 Markdown',
      pageActionsOpen: '页面操作',
      pageActionsOpenGitHub: '在 GitHub 打开',
      pageActionsViewMarkdown: '查看 Markdown',
      sidebarOpen: '打开侧栏',
      sidebarCollapse: '收起侧栏',
      notFoundTitle: '页面不存在',
      notFoundDescription: '这个文档页面还没有创建。',
      notFoundLink: '返回文档首页',
    },
    en: {
      displayName: 'English',
    },
  });

export function getI18nProvider(lang: string) {
  return i18nProvider(translations, lang);
}

export function isSupportedLanguage(lang: string): lang is Language {
  return (languages as readonly string[]).includes(lang);
}
