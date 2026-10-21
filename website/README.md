# Blazorly website

Static HTML for [blazorly.dev](https://blazorly.dev/), deployed as a Cloudflare Worker with [Workers Static Assets](https://developers.cloudflare.com/workers/static-assets/).

## Layout

- `content/` — page bodies
- `templates/layout.html` — shared chrome, meta, JSON-LD slots
- `static/` — CSS, JS, images, video, `llms.txt`
- `build.py` — writes `public/`
- `src/index.ts` — Worker: security headers, noindex on `*.workers.dev`, long cache on `/assets/`
- `mockups/` + `scripts/capture.py` — product UI mockups → screenshots and demo video

Canonical origin is set in `build.py` (`ORIGIN = "https://blazorly.dev"`). Change that string before the first production deploy if the domain is different, then rebuild.

## Commands

```bash
cd website
python3 scripts/capture.py    # optional; needs Chrome + ffmpeg + playwright
python3 build.py
npx wrangler dev              # http://127.0.0.1:8787
npx wrangler deploy           # account login required
```

Attach a custom domain in the Cloudflare dashboard (Workers → blazorly-site → Settings → Domains). Preview `*.workers.dev` URLs send `X-Robots-Tag: noindex`.

## SEO / AEO

Every page is server-rendered HTML. `robots.txt` allows Google and the main AI search crawlers. `sitemap.xml`, `llms.txt`, and `llms-full.txt` are emitted at the site root. JSON-LD covers Organization, SoftwareApplication, WebSite, Article, VideoObject, and BreadcrumbList.
