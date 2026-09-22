import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const source = readFileSync(new URL("../../src/Blazorly.Harness.Web/wwwroot/appearance.js", import.meta.url), "utf8");

function browser({ stored = {}, blocked = false, writeBlocked = false, light = false, url = "https://example.test/settings" } = {}) {
    const storage = new Map(Object.entries(stored));
    const styles = new Map();
    const listeners = new Map();
    const device = { matches: light, addEventListener: (name, handler) => listeners.set("device:" + name, handler) };
    const document = {
        documentElement: { dataset: {} },
        getElementById: id => styles.get(id),
        createElement(tag) {
            assert.equal(tag, "style");
            return {
                textContent: "", setAttribute() {},
                set innerHTML(_) { assert.fail("Custom styles must be inserted as text, never HTML."); }
            };
        },
        head: { appendChild: element => styles.set(element.id, element) }
    };
    const window = {
        location: { href: url },
        history: { state: null, replaceState: (_, __, value) => { window.location.href = value.toString(); } },
        matchMedia: () => device,
        addEventListener: (name, handler) => listeners.set(name, handler)
    };
    const localStorage = {
        getItem(key) { if (blocked) throw new Error("Storage denied"); return storage.get(key) ?? null; },
        setItem(key, value) { if (blocked || writeBlocked) throw new Error("Storage denied"); storage.set(key, value); },
        removeItem(key) { if (blocked || writeBlocked) throw new Error("Storage denied"); storage.delete(key); }
    };
    vm.runInNewContext(source, { window, document, localStorage, URL });
    return {
        api: window.blazorlyAppearance, storage, document, window, styles,
        css: () => styles.get("blazorly-appearance-overrides").textContent,
        changeDevice(isLight) { device.matches = isLight; listeners.get("device:change")(); },
        storageChanged(key = "blazorly.appearance") { listeners.get("storage")({ key }); }
    };
}

test("restores the old light preference before paint and migrates it on the next change", () => {
    const app = browser({ stored: { "blazorly.theme": "light" } });
    assert.equal(app.document.documentElement.dataset.theme, "light");
    app.api.update({ interfaceFont: "manrope" });
    assert.equal(app.storage.has("blazorly.theme"), false);
    const reloaded = browser({ stored: Object.fromEntries(app.storage) });
    assert.equal(reloaded.document.documentElement.dataset.theme, "light");
    assert.equal(reloaded.document.documentElement.dataset.font, "manrope");
});

test("malformed or invalid saved values cannot break startup or inject an accent declaration", () => {
    for (const invalid of ["{broken", "null", "false", "42", "[]"]) {
        const app = browser({ stored: { "blazorly.appearance": invalid } });
        assert.equal(app.api.get().preferences.theme, "dark");
    }
    const app = browser({ stored: { "blazorly.appearance": JSON.stringify({
        theme: "missing", interfaceFont: {}, codeFont: null,
        accent: "red; } body { display: none", customCss: 23, customCssEnabled: "false"
    }) } });
    assert.equal(app.document.documentElement.dataset.font, "inter");
    assert.equal(app.document.documentElement.dataset.codeFont, "jetbrains");
    assert.equal(app.css(), "");
});

test("blocked storage and quota errors still apply changes and report that they are temporary", () => {
    for (const options of [{ blocked: true }, { writeBlocked: true }]) {
        const app = browser(options);
        const result = app.api.update({ theme: "forest", codeFont: "consolas" });
        assert.equal(result.persisted, false);
        assert.equal(app.document.documentElement.dataset.theme, "forest");
        assert.equal(app.document.documentElement.dataset.codeFont, "consolas");
    }
});

test("device theme follows OS changes only while the system option is selected", () => {
    const app = browser();
    app.api.update({ theme: "system" });
    app.changeDevice(true);
    assert.equal(app.document.documentElement.dataset.theme, "light");
    assert.equal(app.api.get().preferences.theme, "system");
    app.changeDevice(false);
    assert.equal(app.document.documentElement.dataset.theme, "dark");
    app.api.update({ theme: "sandstone" });
    app.changeDevice(true);
    assert.equal(app.document.documentElement.dataset.theme, "sandstone");
});

test("custom CSS is text, can be disabled without deletion, and survives reloads", () => {
    const app = browser();
    const css = ':root { --radius: 18px; } /* </style><script>alert(1)</script> */';
    app.api.update({ customCss: css });
    assert.equal(app.css(), css);
    assert.equal(app.styles.size, 1);
    app.api.update({ customCssEnabled: false });
    assert.equal(app.css(), "");
    assert.equal(app.api.get().preferences.customCss, css);
    const reloaded = browser({ stored: Object.fromEntries(app.storage) });
    assert.equal(reloaded.css(), "");
    reloaded.api.update({ customCssEnabled: true });
    assert.equal(reloaded.css(), css);
});

test("accent overrides select a legible button foreground and can return to the theme", () => {
    const app = browser();
    for (const accent of ["#ffffff", "#00ff00", "#91b0ff"]) {
        app.api.update({ accent });
        assert.match(app.css(), /--accent-contrast: #000000/);
    }
    for (const accent of ["#000000", "#0000ff", "#365ec9"]) {
        app.api.update({ accent });
        assert.match(app.css(), /--accent-contrast: #ffffff/);
    }
    app.api.update({ accent: "", customCss: ":root { --radius: 16px; }" });
    assert.doesNotMatch(app.css(), /--accent:/);
    assert.match(app.css(), /--radius: 16px/);
});

test("another tab's updates and clearing storage refresh both the document and subscribers", async () => {
    const app = browser();
    const changes = [];
    const id = app.api.subscribe({ invokeMethodAsync: async (_, state) => changes.push(state.preferences.theme) });
    app.storage.set("blazorly.appearance", JSON.stringify({ theme: "dusk" }));
    app.storageChanged();
    assert.equal(app.document.documentElement.dataset.theme, "dusk");
    app.storage.clear();
    app.storageChanged(null);
    assert.equal(app.document.documentElement.dataset.theme, "dark");
    assert.deepEqual(changes, ["dusk", "dark"]);
    app.api.unsubscribe(id);
    app.storageChanged();
    assert.equal(changes.length, 2);
});

test("recovery resets all preferences before paint and removes only its query parameter", () => {
    const app = browser({
        stored: { "blazorly.appearance": JSON.stringify({
            theme: "dusk", interfaceFont: "serif", codeFont: "consolas",
            accent: "#ff0000", customCss: "body { display: none; }"
        }) },
        url: "https://example.test/settings?tab=appearance&reset-appearance=1&keep=1#settings"
    });
    assert.equal(app.css(), "");
    assert.equal(app.document.documentElement.dataset.theme, "dark");
    assert.equal(app.document.documentElement.dataset.font, "inter");
    assert.equal(app.document.documentElement.dataset.codeFont, "jetbrains");
    assert.equal(app.window.location.href, "https://example.test/settings?tab=appearance&keep=1#settings");
    assert.equal(JSON.parse(app.storage.get("blazorly.appearance")).customCss, "");
});
