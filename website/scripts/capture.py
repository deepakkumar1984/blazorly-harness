#!/usr/bin/env python3
from pathlib import Path
import subprocess
from playwright.sync_api import sync_playwright

ROOT = Path(__file__).resolve().parents[1]
MOCKUPS = ROOT / "mockups"
IMG = ROOT / "static" / "img"
VIDEO = ROOT / "static" / "video"
IMG.mkdir(parents=True, exist_ok=True)
VIDEO.mkdir(parents=True, exist_ok=True)

CHROME = "/usr/bin/google-chrome-stable"


def file_url(name: str) -> str:
    return (MOCKUPS / name).resolve().as_uri()


def main() -> None:
    with sync_playwright() as p:
        browser = p.chromium.launch(executable_path=CHROME, args=["--font-render-hinting=none"])
        shots = [
            ("app.html", "ui-chat.png", "chat", 1440, 900, 2),
            ("app.html", "ui-agents.png", "agents", 1440, 900, 2),
            ("app.html", "ui-terminal.png", "terminal", 1440, 900, 2),
            ("app.html", "ui-settings.png", "settings", 1440, 900, 2),
            ("app.html", "ui-cli.png", "cli", 1440, 900, 2),
            ("og.html", "og.png", None, 1200, 630, 2),
            ("og.html", "twitter.png", None, 1200, 630, 1),
        ]
        for file, name, scene, w, h, scale in shots:
            page = browser.new_page(viewport={"width": w, "height": h}, device_scale_factor=scale)
            page.goto(file_url(file), wait_until="networkidle")
            if scene:
                page.evaluate("(s) => { document.body.dataset.scene = s; }", scene)
            page.wait_for_timeout(150)
            page.screenshot(path=str(IMG / name), type="png")
            page.close()
            print("wrote", name)

        context = browser.new_context(
            viewport={"width": 1280, "height": 720},
            device_scale_factor=1,
            record_video_dir=str(VIDEO),
            record_video_size={"width": 1280, "height": 720},
        )
        page = context.new_page()
        page.goto(file_url("demo.html"), wait_until="networkidle")
        page.wait_for_timeout(11000)
        page.screenshot(path=str(IMG / "demo-poster.png"), type="png")
        video = page.video
        page.close()
        raw = Path(video.path()) if video else None
        context.close()
        browser.close()

    if not raw:
        raise SystemExit("no video recorded")
    mp4 = VIDEO / "demo.mp4"
    webm = VIDEO / "demo.webm"
    subprocess.run(
        [
            "ffmpeg", "-y", "-i", str(raw),
            "-an", "-vf", "fps=30,format=yuv420p",
            "-c:v", "libx264", "-preset", "slow", "-crf", "22",
            "-movflags", "+faststart", str(mp4),
        ],
        check=True,
    )
    subprocess.run(
        ["ffmpeg", "-y", "-i", str(mp4), "-c:v", "libvpx-vp9", "-b:v", "0", "-crf", "32", "-an", str(webm)],
        check=True,
    )
    raw.unlink(missing_ok=True)
    print("wrote demo.mp4 / demo.webm")

    try:
        from PIL import Image
        for png in IMG.glob("*.png"):
            im = Image.open(png).convert("RGB")
            im.save(png.with_suffix(".webp"), "WEBP", quality=86, method=6)
            print("wrote", png.with_suffix(".webp").name)
    except ImportError:
        print("pillow missing; skipped webp")


if __name__ == "__main__":
    main()
