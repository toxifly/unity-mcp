---
id: docs
slug: /contributing/docs
title: Docs Workflow
sidebar_label: Docs Workflow
description: How the documentation site is built, what's auto-generated, and how to ship a docs change.
---

# Docs Workflow

The documentation site you're reading is built with **Docusaurus 3** and lives under `/website` in the same repo as the code. Every doc change goes through normal PRs.

## What's hand-written vs. auto-generated

| Section | Source | Authored by |
|---|---|---|
| Getting Started, Guides, Architecture, Contributing, Migrations | `website/docs/<section>/*.md` | hand-written |
| **Tool reference** (`/reference/tools/**`) | generated from `Server/src/services/tools/` | `tools/generate_docs_reference.py` |
| **Resource catalog** (`/reference/resources/`) | generated from `Server/src/services/resources/` | same generator |

The generator owns everything inside the front-matter banner. The **examples block** between `<!-- examples:start -->` and `<!-- examples:end -->` is hand-written and **preserved across regenerations**.

## Editing a hand-written page

1. Find the file under `website/docs/`.
2. Edit the markdown.
3. Local preview: `cd website && npm run start` → http://localhost:3000/unity-mcp/
4. Commit and open a PR against `develop`.

Before opening the PR, run `cd website && npm run build`. The build fails on dead links and missing pages, so they surface before merge.

## Adding examples to a generated tool page

Find the page at `website/docs/reference/tools/<group>/<tool-name>.md`. Look for:

```html
<!-- examples:start -->
*No examples yet. Add usage examples here — they will be preserved across regenerations.*
<!-- examples:end -->
```

Replace the placeholder text with your example. **Don't move or rename the markers** — the generator uses them to know where your content is.

Run the generator to verify your examples survive a regeneration round-trip:

```bash
cd Server && uv run python ../tools/generate_docs_reference.py
git diff website/docs/reference/tools/<group>/<tool-name>.md
# Should show only your additions; the rest of the file is regenerated identically.
```

## Adding a new doc page

1. Create the `.md` file under the appropriate `website/docs/<section>/`. Front-matter at minimum:

   ```yaml
   ---
   id: my-page
   slug: /<section>/my-page
   title: Page Title
   sidebar_label: Short Label
   description: One-line description (used in search and OG).
   ---
   ```

2. Add the page to `website/sidebars.js` under the right category.
3. Use **brand-neutral slugs** — never put `mcp-for-unity` or `unity-mcp` in a URL path. The product name lives in `docusaurus.config.js`.

## When a slug must change

URL changes break external links. Always add a redirect:

```js
// website/docusaurus.config.js → plugins → @docusaurus/plugin-client-redirects
{
  redirects: [
    { from: '/old/slug', to: '/new/slug' },
  ],
}
```

## When tool/resource registries change

The pre-commit hook (installed via `tools/install-hooks.sh`) auto-regenerates `website/docs/reference/` whenever you stage a change under `Server/src/services/{tools,resources,registry}/`. Without the hook:

```bash
cd Server && uv run python ../tools/generate_docs_reference.py
git add website/docs/reference/
```

To check for drift without writing, add `--check`; it exits non-zero when the committed reference is stale:

```bash
cd Server && uv run python ../tools/generate_docs_reference.py --check
```

If the change affects how agents should use a tool, update `unity-mcp-skill/` in the same change.

## Deploy

This fork does not publish the site. Build it with `npm run build` and preview the build with `npm run serve`.

## Markdown format

Pages use **CommonMark** (Docusaurus `format: 'detect'` mode), not full MDX. That means `{...}` and `<tag>` in body text are literals — no JS-expression parsing. If you need React components, name the file `.mdx` instead of `.md`.

## Translations

Chinese versions of the README and the developer guide live at `docs/i18n/README-zh.md` and `docs/development/README-DEV-zh.md`. Full Docusaurus i18n setup (with `locales: ['en', 'zh']`) is deferred until the English site is stable.

## Search

Local lunr search ships via `@easyops-cn/docusaurus-search-local`.

## Typography

The site uses **Satoshi** (Fontshare) for body and headings and **JetBrains Mono** (Google Fonts) for code. Both load via stylesheet links in `docusaurus.config.js`. Theme overrides live in `website/src/css/custom.css`.
