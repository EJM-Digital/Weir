import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type {Options as DocsOptions} from '@docusaurus/plugin-content-docs';
import type {Options as ThemeOptions} from '@docusaurus/theme-classic';
import type {UserThemeConfig} from '@docusaurus/theme-common';

const config: Config = {
  title: 'Weir',
  tagline: 'Keep the audio and subtitle tracks you want. Drop the rest.',
  favicon: 'img/favicon.ico',

  future: {
    v4: true,
  },

  url: 'https://ejm-digital.github.io',
  baseUrl: '/Weir/',

  organizationName: 'EJM-Digital',
  projectName: 'Weir',

  onBrokenLinks: 'throw',

  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  markdown: {
    mermaid: true,
    hooks: {
      onBrokenMarkdownLinks: 'warn',
    },
  },

  // The parts of Docusaurus's classic preset the site uses, named one by one: the preset also brings plugin-svgr, which turns
  // SVG files imported into React code into components. The site imports none, and plugin-svgr carries deepmerge, which has an
  // advisory with no fixed release (GHSA-gcqx-w48m-w2h4). The sitemap is made for the published build only, as the preset did.
  themes: [
    ['@docusaurus/theme-classic', {customCss: './src/css/custom.css'} satisfies ThemeOptions],
    '@docusaurus/theme-mermaid',
  ],

  plugins: [
    '@docusaurus/plugin-css-cascade-layers',
    [
      '@docusaurus/plugin-content-docs',
      {
        sidebarPath: './sidebars.ts',
        editUrl:
          'https://github.com/EJM-Digital/Weir/tree/main/docs-site/',
      } satisfies DocsOptions,
    ],
    '@docusaurus/plugin-content-pages',
    ...(process.env.NODE_ENV === 'production' ? ['@docusaurus/plugin-sitemap'] : []),
  ],

  themeConfig: {
    colorMode: {
      defaultMode: 'dark',
      respectPrefersColorScheme: true,
    },
    navbar: {
      title: 'Weir',
      logo: {
        alt: 'Weir',
        src: 'img/logo.svg',
        srcDark: 'img/logo-dark.svg',
      },
      items: [
        {
          type: 'docSidebar',
          sidebarId: 'docsSidebar',
          position: 'left',
          label: 'Docs',
        },
        {
          type: 'docSidebar',
          sidebarId: 'apiSidebar',
          position: 'left',
          label: 'API',
        },
        {
          href: 'https://github.com/EJM-Digital/Weir',
          label: 'GitHub',
          position: 'right',
        },
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {
          title: 'Docs',
          items: [
            {label: 'Quickstart', to: '/docs/quickstart'},
            {label: 'Docker', to: '/docs/deployment/docker'},
            {label: 'Windows Installer', to: '/docs/deployment/windows'},
          ],
        },
        {
          title: 'Project',
          items: [
            {
              label: 'GitHub',
              href: 'https://github.com/EJM-Digital/Weir',
            },
            {
              label: 'Releases',
              href: 'https://github.com/EJM-Digital/Weir/releases',
            },
            {
              label: 'Issues',
              href: 'https://github.com/EJM-Digital/Weir/issues',
            },
          ],
        },
        {
          title: 'More',
          items: [
            {label: 'Architecture', to: '/docs/architecture/overview'},
            {label: 'Security', to: '/docs/guides/security'},
          ],
        },
      ],
      copyright: `Copyright (C) 2026 jampat000 and the Weir contributors. Licensed under AGPL-3.0-or-later.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
      additionalLanguages: ['bash', 'powershell', 'python', 'docker', 'yaml', 'toml', 'ini'],
    },
  } satisfies UserThemeConfig,
};

export default config;
