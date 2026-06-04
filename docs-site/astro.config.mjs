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
      defaultColorScheme: 'dark',
      customCss: [
        './src/styles/tokens.css',
        './src/styles/typography.css',
        './src/styles/starlight-theme.css',
        './src/styles/motion.css',
      ],
      social: {
        github: 'https://github.com/kolatts/foundry-gate',
      },
      sidebar: [
        {
          label: 'Getting Started',
          autogenerate: { directory: 'getting-started' },
        },
        {
          label: 'Architecture',
          autogenerate: { directory: 'architecture' },
        },
        {
          label: 'Reference',
          autogenerate: { directory: 'reference' },
        },
        { label: 'Contributing', link: 'contributing' },
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
