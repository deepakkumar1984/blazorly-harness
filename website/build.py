#!/usr/bin/env python3
"""Build the static Blazorly site into ./public."""
from __future__ import annotations

import json
import shutil
from datetime import date
from pathlib import Path

ROOT = Path(__file__).resolve().parent
CONTENT = ROOT / "content"
TEMPLATES = ROOT / "templates"
STATIC = ROOT / "static"
PUBLIC = ROOT / "public"
ORIGIN = "https://blazorly.dev"
TODAY = date.today().isoformat()

PAGES = [
    {
        "src": "home.html",
        "path": "",
        "title": "Agentic Coding Harness for Local AI Agents | Blazorly",
        "og_title": "Blazorly — an agentic coding harness you run yourself",
        "description": "Run a local agentic coding harness with a chat UI, multi-agent swarms, durable sessions, and a single binary. Install on Linux, macOS, or Windows.",
        "og_type": "website",
        "nav": "home",
        "priority": "1.0",
        "schema": "home",
    },
    {
        "src": "features.html",
        "path": "features/",
        "title": "Blazorly Features: Chat UI, Swarms, Durable Sessions",
        "og_title": "Blazorly features",
        "description": "Chat-first UI, workspace tools, in-chat swarms, durable interruption, permission presets, System One, and a headless CLI — what the binary ships.",
        "og_type": "article",
        "nav": "features",
        "priority": "0.9",
        "schema": "software",
    },
    {
        "src": "install.html",
        "path": "install/",
        "title": "Install Blazorly: A Single Binary for Six Platforms",
        "og_title": "Install Blazorly",
        "description": "Install the Blazorly agentic coding harness with one command. Self-contained binary, no SDK or Docker. Configure a provider and open localhost:5080.",
        "og_type": "article",
        "nav": "install",
        "priority": "0.9",
        "schema": "software",
    },
    {
        "src": "compare.html",
        "path": "compare/",
        "title": "Compare Blazorly with Claude Code, Cursor, OpenCode",
        "og_title": "Blazorly compared to other coding agents",
        "description": "Compare Blazorly to Claude Code, Cursor, OpenCode, and Aider on model choice, UI, swarms, durable cancel, CI exit codes, and license. Honest matrix.",
        "og_type": "article",
        "nav": "compare",
        "priority": "0.8",
        "schema": "article",
        "headline": "Blazorly compared to other coding agents",
    },
    {
        "src": "about.html",
        "path": "about/",
        "title": "About Blazorly: Open-Source Agentic Coding Harness",
        "og_title": "About Blazorly",
        "description": "Blazorly is an MIT-licensed agentic coding harness. No hosted account. Keys and session logs stay on your machine. Source on GitHub.",
        "og_type": "article",
        "nav": "about",
        "priority": "0.5",
        "schema": "about",
    },
    {
        "src": "guides.html",
        "path": "guides/",
        "title": "Blazorly Guides: Install, Swarms, Sessions, and CI",
        "og_title": "Blazorly guides",
        "description": "Guides for the Blazorly agentic coding harness: what a harness is, getting started, multi-agent orchestration, durable sessions, permissions, and CI.",
        "og_type": "website",
        "nav": "guides",
        "priority": "0.8",
        "schema": "collection",
    },
    {
        "src": "guides-what.html",
        "path": "guides/what-is-an-agent-harness/",
        "title": "What Is an Agentic Coding Harness? | Blazorly Docs",
        "og_title": "What is an agentic coding harness?",
        "description": "An agentic coding harness is the runtime around a model that calls tools, keeps a session, and handles stop and crash. How Blazorly does that.",
        "og_type": "article",
        "nav": "guides",
        "priority": "0.9",
        "schema": "article",
        "headline": "What is an agentic coding harness?",
    },
    {
        "src": "guides-started.html",
        "path": "guides/getting-started/",
        "title": "Getting Started with Blazorly Agentic Coding Harness",
        "og_title": "Getting started with Blazorly",
        "description": "Install Blazorly, set a provider, open a workspace, and run the first verified change. Includes Stop behaviour and sandbox defaults.",
        "og_type": "article",
        "nav": "guides",
        "priority": "0.8",
        "schema": "article",
        "headline": "Getting started with Blazorly",
    },
    {
        "src": "guides-settings.html",
        "path": "guides/settings/",
        "title": "The Five Blazorly Settings Tabs With Screenshots",
        "og_title": "Settings tabs in Blazorly",
        "description": "Walk through Blazorly’s five Settings tabs: Model and API, custom providers, sessions, capabilities, and sandbox presets — with screenshots.",
        "og_type": "article",
        "nav": "guides",
        "priority": "0.8",
        "schema": "article",
        "headline": "Settings tabs in Blazorly",
    },
    {
        "src": "guides-views.html",
        "path": "guides/session-views/",
        "title": "Blazorly Chat vs Trajectory Tabs and Session Chrome",
        "og_title": "Chat, Trajectory, and the session chrome",
        "description": "Chat vs Trajectory in a Blazorly session, plus Terminal, Stats, and header chips for model, effort, permission, and context. With screenshots.",
        "og_type": "article",
        "nav": "guides",
        "priority": "0.8",
        "schema": "article",
        "headline": "Chat, Trajectory, and the session chrome",
    },
    {
        "src": "guides-multi.html",
        "path": "guides/multi-agent/",
        "title": "Blazorly Multi-Agent Orchestration: Swarms and Review",
        "og_title": "Multi-agent orchestration in Blazorly",
        "description": "How Blazorly runs swarms, reviewers, and teams inside the parent chat. Workers stay off the sidebar. Failed swarm tasks re-dispatch with review notes.",
        "og_type": "article",
        "nav": "guides",
        "priority": "0.7",
        "schema": "article",
        "headline": "Multi-agent orchestration in Blazorly",
    },
    {
        "src": "guides-durable.html",
        "path": "guides/durable-sessions/",
        "title": "Durable Sessions and Interruption in Blazorly Harness",
        "og_title": "Durable sessions and the interruption contract",
        "description": "Blazorly treats Stop, timeout, and kill as durable turn states. Measured cancel latency, log invariants, and what a crashed session does on reload.",
        "og_type": "article",
        "nav": "guides",
        "priority": "0.7",
        "schema": "article",
        "headline": "Durable sessions and the interruption contract",
    },
    {
        "src": "guides-permissions.html",
        "path": "guides/permissions/",
        "title": "Blazorly Permission Presets and Linux Landlock Sandbox",
        "og_title": "Permission presets and sandboxing",
        "description": "full-access, workspace-write, and read-only presets in Blazorly. Landlock on Linux, degraded sandboxes on other OSes, and mid-session /permission.",
        "og_type": "article",
        "nav": "guides",
        "priority": "0.6",
        "schema": "article",
        "headline": "Permission presets and sandboxing",
    },
    {
        "src": "guides-headless.html",
        "path": "guides/headless-ci/",
        "title": "Blazorly Headless CLI, CI Exit Codes, and ACP Server",
        "og_title": "Headless runs, CI, and editor protocols",
        "description": "Use blazorly run in CI with truthful exit codes. JSON-RPC and ACP servers for editors. Eval tasks that score the interruption contract.",
        "og_type": "article",
        "nav": "guides",
        "priority": "0.6",
        "schema": "article",
        "headline": "Headless runs, CI, and editor protocols",
    },
]


def org() -> dict:
    return {
        "@type": "Organization",
        "@id": f"{ORIGIN}/#org",
        "name": "Blazorly",
        "url": ORIGIN,
        "logo": f"{ORIGIN}/favicon.svg",
        "sameAs": ["https://github.com/deepakkumar1984/blazorly-harness"],
        "license": "https://opensource.org/licenses/MIT",
    }


def software() -> dict:
    return {
        "@type": "SoftwareApplication",
        "@id": f"{ORIGIN}/#app",
        "name": "Blazorly",
        "applicationCategory": "DeveloperApplication",
        "operatingSystem": "Linux, macOS, Windows",
        "softwareVersion": "latest",
        "license": "https://opensource.org/licenses/MIT",
        "url": ORIGIN,
        "downloadUrl": "https://github.com/deepakkumar1984/blazorly-harness/releases",
        "installUrl": f"{ORIGIN}/install/",
        "screenshot": [
            f"{ORIGIN}/assets/img/ui-chat.png",
            f"{ORIGIN}/assets/img/ui-agents.png",
            f"{ORIGIN}/assets/img/ui-cli.png",
        ],
        "featureList": [
            "Chat-first web UI",
            "Multi-agent swarms and review",
            "Durable session logs",
            "Permission presets",
            "Headless CLI and ACP",
            "Bring-your-own model",
        ],
        "offers": {"@type": "Offer", "price": "0", "priceCurrency": "USD"},
        "author": {"@id": f"{ORIGIN}/#org"},
    }


def website() -> dict:
    return {
        "@type": "WebSite",
        "@id": f"{ORIGIN}/#site",
        "url": ORIGIN,
        "name": "Blazorly",
        "description": "Open-source agentic coding harness with a local chat UI and headless CLI.",
        "publisher": {"@id": f"{ORIGIN}/#org"},
        "inLanguage": "en",
    }


def jsonld_for(page: dict) -> str:
    url = f"{ORIGIN}/{page['path']}"
    crumbs = [{"@type": "ListItem", "position": 1, "name": "Home", "item": ORIGIN + "/"}]
    if page["path"]:
        name = page.get("headline") or page["og_title"]
        crumbs.append({"@type": "ListItem", "position": 2, "name": name, "item": url})
        if page["path"].startswith("guides/") and page["path"] != "guides/":
            crumbs = [
                {"@type": "ListItem", "position": 1, "name": "Home", "item": ORIGIN + "/"},
                {"@type": "ListItem", "position": 2, "name": "Guides", "item": ORIGIN + "/guides/"},
                {"@type": "ListItem", "position": 3, "name": name, "item": url},
            ]
    graph: list[dict] = [org(), website(), {
        "@type": "BreadcrumbList",
        "itemListElement": crumbs,
    }]
    kind = page["schema"]
    if kind in {"home", "software"}:
        graph.append(software())
    if kind == "home":
        graph.append({
            "@type": "VideoObject",
            "name": "Blazorly adds a health endpoint",
            "description": "Product demo: Blazorly reads routing, edits a health endpoint, writes a test, runs the suite, and a reviewer passes.",
            "thumbnailUrl": f"{ORIGIN}/assets/img/demo-poster.png",
            "contentUrl": f"{ORIGIN}/assets/video/demo.mp4",
            "embedUrl": f"{ORIGIN}/#demo",
            "uploadDate": TODAY,
        })
        graph.append({
            "@type": "WebPage",
            "@id": url + "#page",
            "url": url,
            "name": page["title"],
            "description": page["description"],
            "isPartOf": {"@id": f"{ORIGIN}/#site"},
            "about": {"@id": f"{ORIGIN}/#app"},
        })
    if kind == "article":
        graph.append({
            "@type": "Article",
            "headline": page.get("headline") or page["og_title"],
            "description": page["description"],
            "datePublished": "2026-09-20",
            "dateModified": TODAY,
            "author": {"@id": f"{ORIGIN}/#org"},
            "publisher": {"@id": f"{ORIGIN}/#org"},
            "mainEntityOfPage": url,
            "image": f"{ORIGIN}/assets/img/og.png",
            "inLanguage": "en",
        })
    if kind == "about":
        graph.append({
            "@type": "AboutPage",
            "url": url,
            "name": page["title"],
            "description": page["description"],
            "mainEntity": {"@id": f"{ORIGIN}/#org"},
        })
    return json.dumps({"@context": "https://schema.org", "@graph": graph}, ensure_ascii=False)


def render(page: dict, layout: str) -> str:
    body = (CONTENT / page["src"]).read_text(encoding="utf-8")
    canonical = f"{ORIGIN}/{page['path']}"
    nav = {k: "" for k in ("features", "install", "guides", "compare")}
    if page["nav"] in nav:
        nav[page["nav"]] = ' aria-current="page"'
    html = layout
    replacements = {
        "{{ title }}": page["title"],
        "{{ description }}": page["description"],
        "{{ canonical }}": canonical,
        "{{ origin }}": ORIGIN,
        "{{ og_type }}": page["og_type"],
        "{{ og_title }}": page["og_title"],
        "{{ jsonld }}": jsonld_for(page),
        "{{ content }}": body,
        "{{ date }}": TODAY,
        "{{ nav_features }}": nav["features"],
        "{{ nav_install }}": nav["install"],
        "{{ nav_guides }}": nav["guides"],
        "{{ nav_compare }}": nav["compare"],
    }
    for k, v in replacements.items():
        html = html.replace(k, v)
    return html


def write_robots() -> None:
    (PUBLIC / "robots.txt").write_text(
        f"""User-agent: *
Allow: /

User-agent: GPTBot
Allow: /

User-agent: OAI-SearchBot
Allow: /

User-agent: ChatGPT-User
Allow: /

User-agent: ClaudeBot
Allow: /

User-agent: PerplexityBot
Allow: /

User-agent: Google-Extended
Allow: /

Sitemap: {ORIGIN}/sitemap.xml
""",
        encoding="utf-8",
    )


def write_sitemap() -> None:
    urls = []
    for page in PAGES:
        loc = f"{ORIGIN}/{page['path']}"
        urls.append(
            f"  <url>\n    <loc>{loc}</loc>\n    <lastmod>{TODAY}</lastmod>\n    <changefreq>weekly</changefreq>\n    <priority>{page['priority']}</priority>\n  </url>"
        )
    xml = (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n'
        + "\n".join(urls)
        + "\n</urlset>\n"
    )
    (PUBLIC / "sitemap.xml").write_text(xml, encoding="utf-8")


def write_headers() -> None:
    (PUBLIC / "_headers").write_text(
        """/*
  X-Content-Type-Options: nosniff
  Referrer-Policy: strict-origin-when-cross-origin
  X-Frame-Options: SAMEORIGIN
  Permissions-Policy: camera=(), microphone=(), geolocation=()
  Cross-Origin-Opener-Policy: same-origin

/assets/*
  Cache-Control: public, max-age=31536000, immutable

https://:version.:subdomain.workers.dev/*
  X-Robots-Tag: noindex, nofollow
""",
        encoding="utf-8",
    )


def write_404(layout: str) -> None:
    page = {
        "src": "",
        "path": "404.html",
        "title": "Page not found | Blazorly",
        "og_title": "Page not found",
        "description": "That URL is not a Blazorly page. Try Features, Install, or Guides.",
        "og_type": "website",
        "nav": "",
        "schema": "about",
    }
    body = """<article class="prose prose-wrap" style="padding:64px 0">
  <h1>Page not found</h1>
  <p>That path is not on this site. Useful doors:</p>
  <ul>
    <li><a href="/">Home</a></li>
    <li><a href="/install/">Install</a></li>
    <li><a href="/features/">Features</a></li>
    <li><a href="/guides/">Guides</a></li>
  </ul>
</article>"""
    html = layout
    replacements = {
        "{{ title }}": page["title"],
        "{{ description }}": page["description"],
        "{{ canonical }}": f"{ORIGIN}/404",
        "{{ origin }}": ORIGIN,
        "{{ og_type }}": "website",
        "{{ og_title }}": page["og_title"],
        "{{ jsonld }}": jsonld_for({**page, "path": "404", "schema": "about"}),
        "{{ content }}": body,
        "{{ date }}": TODAY,
        "{{ nav_features }}": "",
        "{{ nav_install }}": "",
        "{{ nav_guides }}": "",
        "{{ nav_compare }}": "",
    }
    for k, v in replacements.items():
        html = html.replace(k, v)
    (PUBLIC / "404.html").write_text(html, encoding="utf-8")


def copy_static() -> None:
    assets = PUBLIC / "assets"
    if assets.exists():
        shutil.rmtree(assets)
    assets.mkdir(parents=True)
    for name in ("css", "js", "img", "video"):
        src = STATIC / name
        if src.exists():
            shutil.copytree(src, assets / name)
    fav = STATIC / "favicon.svg"
    if fav.exists():
        shutil.copy2(fav, PUBLIC / "favicon.svg")
    for extra in ("llms.txt", "llms-full.txt"):
        src = STATIC / extra
        if src.exists():
            shutil.copy2(src, PUBLIC / extra)


def main() -> None:
    if PUBLIC.exists():
        shutil.rmtree(PUBLIC)
    PUBLIC.mkdir()
    copy_static()
    layout = (TEMPLATES / "layout.html").read_text(encoding="utf-8")
    for page in PAGES:
        html = render(page, layout)
        dest_dir = PUBLIC / page["path"]
        dest_dir.mkdir(parents=True, exist_ok=True)
        (dest_dir / "index.html").write_text(html, encoding="utf-8")
        print("wrote", page["path"] or "/")
    write_404(layout)
    write_robots()
    write_sitemap()
    write_headers()
    print("built", PUBLIC)


if __name__ == "__main__":
    main()
