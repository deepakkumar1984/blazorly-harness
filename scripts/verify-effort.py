"""Verify: settings scrolls + model switching; session effort picker; /effort command."""
import time, json
from playwright.sync_api import sync_playwright

BASE = "http://127.0.0.1:5080"
results = []
def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    print(("PASS " if ok else "FAIL ") + name + ("  — " + str(detail) if detail else ""))

with sync_playwright() as p:
    browser = p.chromium.launch(executable_path="/snap/bin/chromium")
    pg = browser.new_page(viewport={"width": 1440, "height": 700})  # short window forces scroll
    pg.goto(BASE + "/settings", wait_until="networkidle")
    time.sleep(1.5)

    # 1. scrolling: pane must be scrollable and Save reachable
    scroll = pg.evaluate("""() => {
        const pane = document.querySelector('.chat-pane');
        const before = pane.scrollTop;
        pane.scrollTop = pane.scrollHeight;
        const after = pane.scrollTop;
        const save = [...document.querySelectorAll('button')].find(b => b.textContent.trim() === 'Save');
        const r = save ? save.getBoundingClientRect() : null;
        return { scrollable: pane.scrollHeight > pane.clientHeight, moved: after > before, saveVisible: r ? (r.top < window.innerHeight && r.bottom > 0) : false };
    }""")
    check("settings pane scrollable", scroll["scrollable"], scroll)
    check("scrolled to bottom", scroll["moved"])
    check("Save button visible after scroll", scroll["saveVisible"])
    pg.screenshot(path="/tmp/ui-effort/01-settings-bottom.png")

    # 2. model switching on settings
    sel = pg.locator(".settings-form select").first
    sel.select_option("openai")
    time.sleep(0.6)
    models = pg.locator(".settings-form select").nth(1).locator("option").all_inner_texts()
    check("model list follows provider switch", any("o4-mini" in m for m in models), models)
    sel.select_option("deepseek")
    time.sleep(0.6)
    models = pg.locator(".settings-form select").nth(1).locator("option").all_inner_texts()
    check("switching back shows deepseek models", any("V4" in m for m in models), models)

    # 3. session header: model picker shows effort group; choose max
    pg.goto(BASE, wait_until="networkidle"); time.sleep(1.8)
    pg.locator(".model-chip").click()
    pg.wait_for_selector(".popover", timeout=5000)
    time.sleep(0.4)
    groups = pg.locator(".popover-group").all_inner_texts()
    check("picker has reasoning effort group", any("reasoning effort" in g.lower() for g in groups), groups)
    pg.screenshot(path="/tmp/ui-effort/02-effort-picker.png")
    pg.locator(".popover-item", has_text="max").first.click()
    time.sleep(0.5)

    # 4. /effort command reflects + sets
    composer = pg.locator(".composer textarea")
    composer.click(); composer.type("/effort", delay=5)
    pg.keyboard.press("Enter"); time.sleep(0.8)
    outcome = pg.locator(".command-row").last.inner_text()
    check("/effort (no args) reports current + options", "reasoning effort:" in outcome, outcome.strip()[:120])
    composer.click(); composer.type("/effort max", delay=5)
    pg.keyboard.press("Enter"); time.sleep(0.8)
    outcome = pg.locator(".command-row").last.inner_text()
    check("/effort max sets effort", "set to max" in outcome, outcome.strip()[:120])

    browser.close()

fails = [r for r in results if not r[1]]
print(json.dumps({"passed": len(results) - len(fails), "failed": len(fails)}))
raise SystemExit(1 if fails else 0)
