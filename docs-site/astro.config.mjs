import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';

export default defineConfig({
  site: 'https://kolatts.github.io',
  base: '/foundry-gate',
  integrations: [
    starlight({
      title: 'FoundryGate',
      logo: {
        src: './src/assets/logo.png',
        alt: 'FoundryGate',
        replacesTitle: true,
      },
      favicon: '/favicon.svg',
      customCss: [
        './src/styles/tokens.css',
        './src/styles/typography.css',
        './src/styles/starlight-theme.css',
        './src/styles/motion.css',
      ],
      social: [
        { icon: 'github', label: 'GitHub', href: 'https://github.com/kolatts/foundry-gate' },
      ],
      sidebar: [
        {
          label: 'Getting Started',
          items: [
            { label: 'What is FoundryGate?', slug: 'index' },
            { label: 'Fork Guide', slug: 'getting-started/fork-guide' },
            { label: 'CLI Setup for Developers', slug: 'getting-started/cli-setup' },
          ],
        },
        {
          label: 'Architecture',
          items: [
            { label: 'System Overview', slug: 'architecture/overview' },
          ],
        },
        {
          label: 'Reference',
          items: [
            { label: 'Configuration Keys', slug: 'reference/configuration' },
            { label: 'API Surface', slug: 'reference/api' },
          ],
        },
        {
          label: 'Contributing',
          items: [
            { label: 'Contributing', slug: 'contributing' },
          ],
        },
      ],
      head: [
        {
          tag: 'meta',
          attrs: { property: 'og:image', content: 'https://kolatts.github.io/foundry-gate/og-logo.png' },
        },
      ],
    }),
  ],
});
