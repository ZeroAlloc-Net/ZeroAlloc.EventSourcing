/**
 * This is a TEMPLATE/EXAMPLE file showing how the Docusaurus configuration
 * should be set up in the .website monorepo at:
 * apps/docs-eventsourcing/docusaurus.config.ts
 *
 * This file is NOT used directly. It serves as documentation for the
 * actual configuration that will be created in the .website monorepo.
 *
 * See WEBSITE_INTEGRATION.md for detailed setup instructions.
 */

import type { Config } from '@docusaurus/types';
import { sharedNavbar, sharedFooter, sharedThemeConfig } from '@zeroalloc/theme/docusaurus';

const config: Config = {
  title: 'ZeroAlloc.EventSourcing',
  favicon: 'icon.png',
  staticDirectories: ['static', '../../repos/eventsourcing/assets'],
  tagline: 'High-performance, zero-allocation event sourcing for .NET',
  url: 'https://eventsourcing.zeroalloc.net',
  baseUrl: '/',
  organizationName: 'ZeroAlloc-Net',
  projectName: 'ZeroAlloc.EventSourcing',
  trailingSlash: false,
  onBrokenLinks: 'warn',
  i18n: { defaultLocale: 'en', locales: ['en'] },
  markdown: { format: 'detect', mermaid: true },
  themes: ['@docusaurus/theme-mermaid'],
  themeConfig: {
    ...sharedThemeConfig,
    navbar: {
      ...sharedNavbar,
      title: 'ZeroAlloc.EventSourcing',
      logo: { ...sharedNavbar.logo, src: 'icon.png' },
    },
    footer: sharedFooter,
  },
  presets: [
    [
      'classic',
      {
        docs: {
          routeBasePath: '/',
          path: '../../repos/eventsourcing/docs',
          sidebarPath: './sidebars.ts',
          exclude: ['**/README.md', '**/pre-push-review*.md', '**/plans/**'],
        },
        blog: false,
        theme: { customCss: './src/css/custom.css' },
      },
    ],
  ],
};

export default config;
