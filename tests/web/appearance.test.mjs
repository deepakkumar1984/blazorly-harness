import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const source = readFileSync(new URL("../../src/Blazorly.Harness.Web/wwwroot/appearance.js", import.meta.url), "utf8");

function browser({ stored = {}, blocked = false, writeBlocked = false, light = false, url = "https://example.test/settings" } = {}) {
    const storage = new Map(Object.entries(stored));
    const styles = new Map();
    const listeners = new Map();
    const classes = new Set();
    const device = { matches: light, addEventListener: (name, handler) => listeners.set("device:" + name, handler) };
    const document = {
        documentElement: {
            dataset: {},
            style: {},
            classList: { toggle: (name, on) => { if (on) classes.add(name); else classes.delete(name); } }
        },
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
        classes,
        css: () => styles.get("blazorly-appearance-overrides").textContent,
        rootSize: () => document.documentElement.style.fontSize,
        changeDevice(isLight) { device.matches = isLight; listeners.get("device:change")(); },
        storageChanged(key = "blazorly.appearance") { listeners.get("storage")({ key }); }
    };
}

test("restores the old theme preference before paint and migrates it on the next change", () => {
    const app = browser({ stored: { "blazorly.theme": "light" } });
    assert.equal(app.document.documentElement.dataset.theme, "mist");
    app.api.update({ font: "friendly" });
    assert.equal(app.storage.has("blazorly.theme"), false);
    const reloaded = browser({ stored: Object.fromEntries(app.storage) });
    assert.equal(reloaded.document.documentElement.dataset.theme, "mist");
    assert.equal(reloaded.document.documentElement.dataset.font, "friendly");
});

test("saved palettes, fonts and accents from the previous scheme keep the user's look", () => {
    const app = browser({ stored: { "blazorly.appearance": JSON.stringify({
        theme: "dark", interfaceFont: "inter", codeFont: "jetbrains", accent: ""
    }) } });
    assert.equal(app.document.documentElement.dataset.theme, "ocean");
    assert.equal(app.document.documentElement.dataset.font, "clean");
    assert.equal(app.document.documentElement.dataset.codeFont, "jetbrains");
    assert.equal(app.api.get().preferences.accent, "theme");

    for (const [old, current] of [["dusk", "midnight"], ["graphite", "graphite"], ["forest", "forest"]]) {
        assert.equal(browser({ stored: { "blazorly.appearance": JSON.stringify({ theme: old }) } }).api.get().preferences.theme, current);
    }
    for (const [old, current] of [["serif", "editorial"], ["manrope", "friendly"], ["system", "system"]]) {
        assert.equal(browser({ stored: { "blazorly.appearance": JSON.stringify({ interfaceFont: old }) } }).api.get().preferences.font, current);
    }
});

test("a saved accent colour survives the upgrade, either as a preset or as a custom colour", () => {
    // Comfortably close to Iris is recognised as Iris.
    const preset = browser({ stored: { "blazorly.appearance": JSON.stringify({ accent: "#7047e8" }) } });
    assert.equal(preset.api.get().preferences.accent, "iris");
    assert.match(preset.css(), /--accent: hsl\(256 80% 60%\)/);

    // Anything else keeps the exact colour the user picked.
    const custom = browser({ stored: { "blazorly.appearance": JSON.stringify({ accent: "#A1B0FF" }) } });
    const preferences = custom.api.get().preferences;
    assert.equal(preferences.accent, "custom");
    assert.equal(preferences.customAccent, "#a1b0ff");
    assert.match(custom.css(), /--accent: hsl\(/);
});

test("malformed or invalid saved values cannot break startup or inject an accent declaration", () => {
    for (const invalid of ["{broken", "null", "false", "42", "[]"]) {
        const app = browser({ stored: { "blazorly.appearance": invalid } });
        assert.equal(app.api.get().preferences.theme, "system");
        assert.equal(app.api.get().resolvedTheme, "cream");
    }
    const app = browser({ stored: { "blazorly.appearance": JSON.stringify({
        theme: "missing", accent: "red; } body { display: none", font: {}, codeFont: null,
        radius: "spiky", textSize: "huge", reduceMotion: "false",
        customAccent: "javascript:alert(1)", customCss: 23, customCssEnabled: "false"
    }) } });
    assert.equal(app.document.documentElement.dataset.font, "clean");
    assert.equal(app.document.documentElement.dataset.codeFont, "jetbrains");
    assert.equal(app.document.documentElement.dataset.radius, "round");
    assert.equal(app.api.get().preferences.textSize, "md");
    assert.equal(app.api.get().preferences.customAccent, "#6d4aff");
    assert.equal(app.api.get().preferences.customCssEnabled, true);
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
    assert.equal(app.document.documentElement.dataset.theme, "midnight");
    assert.equal(app.api.get().preferences.theme, "system");
    app.changeDevice(false);
    assert.equal(app.document.documentElement.dataset.theme, "cream");
    app.api.update({ theme: "obsidian" });
    app.changeDevice(true);
    assert.equal(app.document.documentElement.dataset.theme, "obsidian");
});

test("text size, corner style and reduced motion reach the document", () => {
    const app = browser();
    assert.equal(app.rootSize(), "16px");
    for (const [id, px] of [["sm", 14], ["lg", 17.5], ["xl", 19], ["md", 16]]) {
        app.api.update({ textSize: id });
        assert.equal(app.rootSize(), `${px}px`);
    }
    for (const radius of ["sharp", "soft", "round", "pill"]) {
        app.api.update({ radius });
        assert.equal(app.document.documentElement.dataset.radius, radius);
    }
    assert.equal(app.classes.has("reduce-motion"), false);
    app.api.update({ reduceMotion: true });
    assert.equal(app.classes.has("reduce-motion"), true);
    const reloaded = browser({ stored: Object.fromEntries(app.storage) });
    assert.equal(reloaded.classes.has("reduce-motion"), true);
    assert.equal(reloaded.document.documentElement.dataset.radius, "pill");
});

test("quick looks set theme, accent, font, corner style and text size in one update", () => {
    const app = browser();
    const looks = app.api.get().quickLooks.map(look => look.id);
    assert.equal(looks.join(","), "cozy,focus,hacker,sunset,ocean,storybook");
    for (const look of app.api.get().quickLooks) {
        app.api.update(look.patch);
        const preferences = app.api.get().preferences;
        for (const [key, value] of Object.entries(look.patch)) assert.equal(preferences[key], value);
        assert.equal(app.document.documentElement.dataset.theme, look.patch.theme);
        assert.equal(app.document.documentElement.dataset.font, look.patch.font);
        assert.equal(app.document.documentElement.dataset.radius, look.patch.radius);
    }
    // A quick look leaves the code font and any custom stylesheet alone.
    app.api.update({ codeFont: "consolas", customCss: ":root { --radius: 3px; }" });
    app.api.update(app.api.get().quickLooks[2].patch);
    assert.equal(app.api.get().preferences.codeFont, "consolas");
    assert.match(app.css(), /--radius: 3px/);
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

test("the theme default writes no accent, and a chosen accent gets a legible foreground", () => {
    const app = browser({ stored: { "blazorly.appearance": JSON.stringify({ customCss: ":root { --radius: 16px; }" }) } });
    app.api.update({ accent: "theme" });
    assert.doesNotMatch(app.css(), /--accent:/);
    assert.match(app.css(), /--radius: 16px/);

    const rgb = value => {
        const m = /hsl\(\s*([\d.]+)\s+([\d.]+)%\s+([\d.]+)%/.exec(value);
        assert.ok(m, `not an hsl colour: ${value}`);
        const [h, s, l] = [Number(m[1]), Number(m[2]) / 100, Number(m[3]) / 100];
        const k = n => (n + h / 30) % 12;
        const a = s * Math.min(l, 1 - l);
        const f = n => l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
        return [f(0), f(8), f(4)].map(v => Math.round(Math.max(0, Math.min(1, v)) * 255));
    };
    const channel = value => { const c = value / 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
    const luminance = ([r, g, b]) => 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
    const contrast = (a, b) => {
        const [high, low] = [luminance(a), luminance(b)].sort((x, y) => y - x);
        return (high + 0.05) / (low + 0.05);
    };

    // Every accent, on both a light and a dark palette, must keep its button text at AA.
    for (const accent of app.api.get().accents) {
        for (const theme of ["paper", "midnight"]) {
            const state = app.api.update({ theme, accent: accent.Id ?? accent.id });
            const declaration = app.css();
            const background = /--accent: (hsl\([^)]+\))/.exec(declaration)[1];
            const foreground = /--accent-contrast: (hsl\([^)]+\))/.exec(declaration)[1];
            assert.equal(foreground, state.resolvedAccent.contrast);
            const ratio = contrast(rgb(background), rgb(foreground));
            assert.ok(ratio >= 4.5, `${accent.name} on ${theme}: ${ratio.toFixed(2)}:1`);
        }
    }
});

test("a custom accent is pulled into a readable band and reports its own colour", () => {
    const app = browser();
    const light = app.api.update({ theme: "paper", accent: "custom", customAccent: "#22dd88" });
    assert.equal(light.resolvedAccent.custom, true);
    assert.equal(light.resolvedAccent.name, "Custom");
    // Very light and very dark picks are clamped rather than left unreadable.
    app.api.update({ customAccent: "#ffff00" });
    assert.match(app.api.get().resolvedAccent.hex, /^#([0-9a-f]{2})[0-9a-f]{4}$/);
    app.api.update({ theme: "midnight", customAccent: "#101010" });
    assert.match(app.api.get().resolvedAccent.hex, /^#[0-9a-f]{6}$/);
    assert.notEqual(app.api.get().resolvedAccent.hex, "#101010");
});

test("another tab's updates and clearing storage refresh both the document and subscribers", async () => {
    const app = browser();
    const changes = [];
    const id = app.api.subscribe({ invokeMethodAsync: async (_, state) => changes.push(state.resolvedTheme) });
    app.storage.set("blazorly.appearance", JSON.stringify({ theme: "obsidian" }));
    app.storageChanged();
    assert.equal(app.document.documentElement.dataset.theme, "obsidian");
    app.storage.clear();
    app.storageChanged(null);
    assert.equal(app.document.documentElement.dataset.theme, "cream");
    assert.deepEqual(changes, ["obsidian", "cream"]);
    app.api.unsubscribe(id);
    app.storageChanged();
    assert.equal(changes.length, 2);
});

test("recovery resets all preferences before paint and removes only its query parameter", () => {
    const app = browser({
        stored: { "blazorly.appearance": JSON.stringify({
            theme: "dusk", interfaceFont: "serif", codeFont: "consolas", accent: "#ff0000",
            radius: "pill", textSize: "xl", reduceMotion: true, customCss: "body { display: none; }"
        }) },
        url: "https://example.test/settings?tab=appearance&reset-appearance=1&keep=1#settings"
    });
    assert.equal(app.css(), "");
    assert.equal(app.document.documentElement.dataset.theme, "cream");
    assert.equal(app.document.documentElement.dataset.font, "clean");
    assert.equal(app.document.documentElement.dataset.codeFont, "jetbrains");
    assert.equal(app.document.documentElement.dataset.radius, "round");
    assert.equal(app.rootSize(), "16px");
    assert.equal(app.classes.has("reduce-motion"), false);
    assert.equal(app.window.location.href, "https://example.test/settings?tab=appearance&keep=1#settings");
    assert.equal(JSON.parse(app.storage.get("blazorly.appearance")).customCss, "");
});
test("the token file keeps the document fallback ahead of every palette", () => {
    const css = readFileSync(new URL("../../src/Blazorly.Harness.Web/wwwroot/appearance.css", import.meta.url), "utf8");
    const palettes = [...css.matchAll(/^\[data-theme="([a-z]+)"\] \{/gm)];
    assert.deepEqual(palettes.map(m => m[1]).join(","),
        "cream,paper,mist,blossom,sage,midnight,graphite,obsidian,forest,ocean,ember,terminal");

    // A bare `:root` rule competes with `[data-theme]` at equal specificity, so a rule that
    // declares palette tokens must either be wrapped in `:where` (zero specificity) or come
    // before every palette. Anything else silently paints light palettes with its own colours.
    const paletteToken = /--(bg|bg-raised|text|text-dim|accent|accent-contrast|danger|success|warn|tint|border|shadow-ink):/;
    for (const [index, rule] of [...css.matchAll(/([^{}]+)\{([^{}]*)\}/g)].entries()) {
        const selectors = rule[1].split(",").map(part => part.trim()).filter(Boolean);
        if (!paletteToken.test(rule[2])) continue;
        for (const selector of selectors) {
            if (!selector.startsWith(":root")) continue;
            assert.ok(selector.includes(":where(:root)") || index < palettes[0].index,
                `a bare :root rule declaring palette tokens must be zero-specificity or come first: ${selector}`);
        }
    }

    const fallback = css.indexOf(":where(:root) {");
    const firstPalette = css.indexOf('[data-theme="cream"] {');
    assert.ok(fallback > -1 && fallback < firstPalette, "the :where(:root) fallback must precede the palettes");

    // Every palette has to be self-sufficient: these four tokens carry the whole UI.
    for (const [, id, body] of css.matchAll(/\[data-theme="([a-z]+)"\] \{([\s\S]*?)\n\}/g)) {
        for (const token of ["--bg:", "--text:", "--accent:", "--accent-contrast:"]) {
            assert.ok(body.includes(token), `${id} is missing ${token}`);
        }
    }
});
