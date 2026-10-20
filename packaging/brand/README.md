# Weir brand mark

The mark is water going over a weir: the outer stream runs up to the crest, turns, and tips over
it; the inner stream turns a quarter and runs off to the right as the tailrace, with the apron
bar underneath. One colour, all fills, so the Windows tray icon — a single-colour mask — is the
same mark as the one in the app.

## Two optical sizes, on purpose

There are **two drawings of this mark in the repo, and that is deliberate, not drift**:

- **Three streams — the primary mark.** This is the mark exactly as traced from the accepted
  render (`source.png` in this folder). It replaced the two-stream mark that first shipped. It is
  used everywhere the mark is seen at a size that can actually show it: `weir-mark.svg`,
  `weir-mark-light.svg`, `weir-app-icon.svg` below, the SVG favicon
  (`apps/web/public/favicon.svg`), the apple touch icon, the in-app sidebar mark
  (`apps/web/src/components/brand/weir-logo.tsx`), the docs logo, and every 512/256/128/64 raster.
- **Two streams — the small-size fallback, `weir-app-icon-small.svg` below.** Three bands do not
  survive being rasterised down to 16px: a 1.85-unit band on a 24-unit grid is 1.23 device pixels
  there, sub-pixel by construction, and the arcs fuse into a smear (see `gate-16px.png` in this
  folder). This drawing exists only to be the 16px frame
  inside the `.ico` files: the favicon `.ico`, the docs `.ico`, and the Windows tray icon. It is
  not used anywhere a person looks at an SVG or a large raster directly.

This is the standard type-design move of shipping separate "text" and "display" masters of one
typeface: the identity is one mark, traced once, but which optical size represents it changes
with how small it will actually be shown. Nothing about the underlying geometry differs between
the two (the three-stream drawing is the two-stream one plus one more band on the same radius ladder), so
"fixing" the two to match by deleting one of them would be removing a deliberate accommodation,
not tidying up an inconsistency.

**Where the cutoff sits.** Only the 16px frame falls back. `gate-16px.png` in this folder
renders both drawings at 16 and 32 in dark, light and single colour: at 32 a band is 1.33 device
pixels, the three arcs separate cleanly and the crest holds in every rendering including the
single-colour one the tray icon uses; at 16 a band is sub-pixel and they fuse. An earlier revision
of this put the cutoff at 32 out of caution, which cost the real mark two of the three sizes a
person actually sees in a tab strip or a system tray for no legibility gain. See
`wiring-comparison.png` in this folder for the actual rendered frames at each size.

| File | Streams | Use |
| --- | --- | --- |
| `weir-mark.svg` | three (primary) | Mark for dark backgrounds: accent `#3ed7c8`. |
| `weir-mark-light.svg` | three (primary) | Mark for light backgrounds: accent `#0d6f68`. |
| `weir-app-icon.svg` | three (primary) | The mark on the `#0b1418` rounded tile. Source of every favicon/Windows icon frame **24px and above**. |
| `weir-app-icon-small.svg` | two (fallback) | The same tile with the small-size fallback geometry. Source of the favicon/Windows icon frame at **16px only**. |

Those accents are the Tailrace theme tokens from `apps/web/src/styles/weir-tokens.css`.
The web app draws the same geometry inline (`apps/web/src/components/brand/weir-logo.tsx`) so it
follows the theme tokens `--mm-brand-water` and `--mm-brand-wordmark` instead — always the
three-stream primary mark, since the sidebar never renders it anywhere near 16px. The wordmark is
the word "Weir" set in the app font (Inter), not outlines.

## The border inside the tile

The app icon has a thin accent border drawn inside the tile, not on its edge, so a margin of the dark tile shows
between the border and the edge. It matches Deluno's app icon (`deluno-route-primary.svg`) as shares of the tile:
the border is centred 76/1024 in from the edge, is 20/1024 thick, has a corner radius of 192/1024 and is the mark's
colour at 38% opacity. On the 24-unit grid that is `x=1.78`, `stroke-width=0.47`, `rx=4.5`. To keep the same room
between the mark and the border that Deluno's mark has, the mark is drawn at 0.8 of its size about the tile's centre
(0.9 in the 16px tile, where every pixel of the mark counts and the border is under one pixel thick). The two app
icons and `apps/web/public/favicon.svg` carry this; the bare marks (`weir-mark*.svg`, the docs logos, the in-app
mark) do not, because they have no tile.

## Where the geometry comes from (#581)

The SVGs here were generated, not drawn. The accepted render is kept at `source.png` in this folder; its band
edges were traced (thresholded to two colours, contours walked, a circle fitted to every edge, residuals under
1.3px on a 1024px render), put on a 24-unit grid with a 20-unit live area, and corrected for two defects in the
render: a third-colour sliver inside the middle stream, and a middle stream nearly twice the width of the other two.

**The mark is the three-stream render exactly as traced.** See the "Two optical sizes" section above for why a
two-stream fallback also exists and where it is and is not used: that split is about small `.ico` frames only, not
about which geometry is "the" mark.

**The SVGs are the source.** The Python that traced and built them was removed with the rest of the repository's
Python (#892). Everything below is committed, so nothing needs regenerating to build Weir. To change the mark, edit the
paths in the four SVGs here and keep the copies (`apps/web/public/favicon.svg`,
`apps/web/src/components/brand/weir-logo.tsx`, `docs-site/static/img/logo*.svg`) identical, then render the raster
icons again:

- `npm ci` in `apps/web` (the renderer, `@resvg/resvg-js`, is a dev dependency there).
- `node scripts/build-brand-icons.mjs` renders every raster icon below from the SVGs and overwrites the committed files.
- `node scripts/build-brand-icons.mjs --check` renders to a temporary folder and compares each frame with the committed
  file, changing nothing. It reports the largest per-channel difference per frame and fails if one is over 96 of 255
  (`--tolerance=N` changes that). Edge pixels differ slightly from the committed icons, which a browser drew; a changed
  mark differs by much more.

The raster icons derived from them, all committed:

- `apps/web/public/favicon.svg`, `favicon.ico`, `apple-touch-icon.png`
- `packaging/windows/assets/weir-tray-icon.ico` (tray, executable and installer icon)
- `docs-site/static/img/favicon.ico`, `logo.svg`, `logo-dark.svg`

## Social preview

`social-preview.png` is the card GitHub and chat apps show when someone shares a link to the repository. It is 1280×640, set
under Settings > General > Social preview. GitHub does not read it from the repository, so a new card is uploaded there by hand.
Its source is `social-preview.html`: the app icon and name large, the one-line description, and EJM Digital's mark in the
corner, with at least 40px of margin because the card is cropped. After `npm ci` in `apps/web`, which brings in the Inter font
it uses, render it with any Chromium browser, for example
`msedge --headless=new --allow-file-access-from-files --window-size=1280,640 --screenshot=social-preview.png social-preview.html`.
