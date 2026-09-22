#!/usr/bin/env python3
"""Capture settings tabs + trajectory view. Does not re-record the demo video."""
from pathlib import Path
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
MOCKUPS = ROOT / "mockups"
IMG = ROOT / "static" / "img"
IMG.mkdir(parents=True, exist_ok=True)
CHROME = "/usr/bin/google-chrome-stable"


def uri(name: str) -> str:
    return (MOCKUPS / name).resolve().as_uri()


def main() -> None:
    shots = [
        ("settings.html", "ui-settings-provider.png", "provider"),
        ("settings.html", "ui-settings-custom.png", "custom"),
        ("settings.html", "ui-settings-context.png", "context"),
        ("settings.html", "ui-settings-capabilities.png", "capabilities"),
        ("settings.html", "ui-settings-sandbox.png", "sandbox"),
        ("app.html", "ui-trajectory.png", None),
    ]
    with sync_playwright() as p:
        browser = p.chromium.launch(executable_path=CHROME, args=["--font-render-hinting=none"])
        for file, name, tab in shots:
            page = browser.new_page(viewport={"width": 1440, "height": 900}, device_scale_factor=2)
            url = uri(file)
            if file == "settings.html" and tab:
                url += f"?tab={tab}"
            page.goto(url, wait_until="networkidle")
            if file == "app.html":
                page.evaluate("() => { document.body.dataset.scene = 'trajectory'; }")
            page.wait_for_timeout(120)
            page.screenshot(path=str(IMG / name), type="png")
            page.close()
            print("wrote", name)
        browser.close()

    from PIL import Image
    for png_name in [s[1] for s in shots]:
        png = IMG / png_name
        Image.open(png).convert("RGB").save(png.with_suffix(".webp"), "WEBP", quality=86, method=6)
        print("wrote", png.with_suffix(".webp").name)


if __name__ == "__main__":
    main()
