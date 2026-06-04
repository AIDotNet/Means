# Means Docs Site

Independent bilingual documentation site for Means, built with Next.js and
Fumadocs.

## Requirements

- Node.js 22+
- npm

## Development

```bash
npm install
npm run dev
```

The default development server uses port `3000`. During local verification in
this workspace the site was run on `http://localhost:3030`.

## Verification

```bash
npm run typecheck
npm run build
```

`npm run typecheck` regenerates Fumadocs MDX sources, Next route types, and then
runs TypeScript. `npm run build` creates the production Next.js build.

## Routes

- `/` redirects to `/zh/docs`
- `/zh/docs` is the default Chinese documentation hub
- `/en/docs` is the English documentation hub
- `/api/search` serves Fumadocs Orama search
- `/zh/llms.txt` and `/en/llms.txt` expose AI-readable document indexes

## Content

Documentation lives in `content/docs/{zh,en}`. Keep the two language trees in
the same shape and use each section's `meta.json` to control sidebar order.

Primary sections:

- `get-started`
- `concepts`
- `guides`
- `reference`
- `operations`
