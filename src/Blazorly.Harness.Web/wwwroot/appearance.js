// Loaded in <head> so the saved appearance is in place before the first paint.
(() => {
    "use strict";

    const storageKey = "blazorly.appearance";
    const legacyThemeKey = "blazorly.theme";
    const ink = "hsl(250 40% 8%)";
    const paper = "hsl(0 0% 100%)";

    const defaults = Object.freeze({
        theme: "system", accent: "theme", customAccent: "#6d4aff",
        font: "clean", codeFont: "jetbrains", textSize: "md", radius: "round",
        reduceMotion: false, customCss: "", customCssEnabled: true
    });

    // Palettes, accents, type and corner styles come from the design-token set.
    const themes = [
        { id: "cream", name: "Cream", description: "Warm & cozy", dark: false, accent: "iris" },
        { id: "paper", name: "Paper", description: "Clean neutral", dark: false, accent: "mono" },
        { id: "mist", name: "Mist", description: "Cool & airy", dark: false, accent: "sky" },
        { id: "blossom", name: "Blossom", description: "Soft pink", dark: false, accent: "rose" },
        { id: "sage", name: "Sage", description: "Calm green", dark: false, accent: "mint" },
        { id: "midnight", name: "Midnight", description: "Deep violet", dark: true, accent: "iris" },
        { id: "graphite", name: "Graphite", description: "Sleek dark gray", dark: true, accent: "mono" },
        { id: "obsidian", name: "Obsidian", description: "True black", dark: true, accent: "mono" },
        { id: "forest", name: "Forest", description: "Mossy dark", dark: true, accent: "mint" },
        { id: "ocean", name: "Ocean", description: "Deep blue", dark: true, accent: "sky" },
        { id: "ember", name: "Ember", description: "Warm dark", dark: true, accent: "coral" },
        { id: "terminal", name: "Terminal", description: "Hacker green", dark: true, accent: "matrix" }
    ];

    // Each accent carries a light-theme and a dark-theme lightness so the same
    // choice stays legible on both families of palettes.
    const accents = [
        { id: "iris", name: "Iris", h: 256, s: 80, l: 60, ld: 70, hex: "#7347eb", hexDark: "#9675f0" },
        { id: "coral", name: "Coral", h: 12, s: 90, l: 56, ld: 64, hex: "#f4522a", hexDark: "#f67251" },
        { id: "rose", name: "Rose", h: 335, s: 80, l: 55, ld: 66, hex: "#e8307d", hexDark: "#ee639d" },
        { id: "sun", name: "Sunny", h: 40, s: 96, l: 48, ld: 56, hex: "#f0a205", hexDark: "#fbb323" },
        { id: "lime", name: "Lime", h: 85, s: 70, l: 38, ld: 52, hex: "#6ca51d", hexDark: "#93da2f" },
        { id: "mint", name: "Mint", h: 160, s: 75, l: 34, ld: 48, hex: "#16986c", hexDark: "#1fd699" },
        { id: "matrix", name: "Matrix", h: 135, s: 90, l: 36, ld: 50, hex: "#09ae32", hexDark: "#0df246" },
        { id: "sky", name: "Sky", h: 208, s: 92, l: 48, ld: 60, hex: "#0a82eb", hexDark: "#3b9ff7" },
        { id: "indigo", name: "Indigo", h: 228, s: 80, l: 55, ld: 68, hex: "#3055e8", hexDark: "#6c86ef" },
        { id: "mono", name: "Mono", h: 250, s: 8, l: 14, ld: 92, hex: "#222127", hexDark: "#eae9ec" }
    ];

    const fonts = [
        { id: "playful", name: "Playful", description: "Bricolage Grotesque + Inter" },
        { id: "clean", name: "Clean", description: "Inter, crisp and familiar" },
        { id: "friendly", name: "Friendly", description: "Nunito, soft and round" },
        { id: "techy", name: "Techy", description: "Space Grotesk + DM Sans" },
        { id: "editorial", name: "Editorial", description: "Fraunces + Lora" },
        { id: "mono", name: "Mono", description: "JetBrains Mono everywhere" },
        { id: "system", name: "System", description: "Your device's fonts" }
    ];

    const codeFonts = [
        { id: "jetbrains", name: "JetBrains Mono", description: "Made for code" },
        { id: "system", name: "System mono", description: "Your device's default" },
        { id: "consolas", name: "Consolas / Menlo", description: "Familiar editor type" }
    ];

    const textSizes = [
        { id: "sm", name: "Compact", px: 14, sample: "0.45rem" },
        { id: "md", name: "Default", px: 16, sample: "0.58rem" },
        { id: "lg", name: "Large", px: 17.5, sample: "0.72rem" },
        { id: "xl", name: "Extra large", px: 19, sample: "0.86rem" }
    ];

    const radii = [
        { id: "sharp", name: "Sharp", sample: 4 },
        { id: "soft", name: "Soft", sample: 10 },
        { id: "round", name: "Round", sample: 16 },
        { id: "pill", name: "Bubbly", sample: 22 }
    ];

    // One-click combinations of the choices above. The code font is left alone:
    // it is a deliberate, separate decision.
    const quickLooks = [
        { id: "cozy", name: "Cozy", patch: { theme: "cream", accent: "iris", font: "playful", radius: "pill", textSize: "md" } },
        { id: "focus", name: "Focus", patch: { theme: "paper", accent: "mono", font: "clean", radius: "soft", textSize: "md" } },
        { id: "hacker", name: "Hacker", patch: { theme: "terminal", accent: "matrix", font: "mono", radius: "sharp", textSize: "sm" } },
        { id: "sunset", name: "Sunset", patch: { theme: "ember", accent: "coral", font: "friendly", radius: "round", textSize: "md" } },
        { id: "ocean", name: "Ocean", patch: { theme: "ocean", accent: "sky", font: "techy", radius: "round", textSize: "md" } },
        { id: "storybook", name: "Storybook", patch: { theme: "blossom", accent: "rose", font: "editorial", radius: "soft", textSize: "lg" } }
    ];

    // Saved preferences from the previous palette and font scheme, mapped onto the
    // closest current choice so an upgrade keeps the look the user chose.
    const legacyThemes = { dark: "ocean", light: "mist", graphite: "graphite", forest: "forest", dusk: "midnight" };
    const legacyFonts = { inter: "clean", manrope: "friendly", system: "system", serif: "editorial" };

    const deviceTheme = window.matchMedia("(prefers-color-scheme: light)");
    const subscribers = new Map();
    let nextSubscriber = 0;
    let persisted = true;

    function themeById(id) {
        return themes.find(t => t.id === id) ?? themes[0];
    }

    function accentById(id) {
        return accents.find(a => a.id === id) ?? accents[0];
    }

    function sizeById(id) {
        return textSizes.find(s => s.id === id) ?? textSizes[1];
    }

    function isHex(value) {
        return typeof value === "string" && /^#[0-9a-f]{6}$/i.test(value);
    }

    function hexToHsl(hex) {
        const n = parseInt(hex.slice(1), 16);
        const r = ((n >> 16) & 255) / 255, g = ((n >> 8) & 255) / 255, b = (n & 255) / 255;
        const max = Math.max(r, g, b), min = Math.min(r, g, b);
        const l = (max + min) / 2, d = max - min;
        let h = 0, s = 0;
        if (d) {
            s = d / (1 - Math.abs(2 * l - 1));
            h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
            h = Math.round(h * 60);
            if (h < 0) h += 360;
        }
        return { h, s: Math.round(s * 100), l: Math.round(l * 100) };
    }

    function hexToRgb(hex) {
        const n = parseInt(hex.slice(1), 16);
        return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
    }

    function hslToRgb(h, s, l) {
        const S = s / 100, L = l / 100;
        const k = n => (n + h / 30) % 12;
        const a = S * Math.min(L, 1 - L);
        const f = n => L - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
        return [f(0), f(8), f(4)].map(v => Math.round(Math.max(0, Math.min(1, v)) * 255));
    }

    function hslToHex(h, s, l) {
        return "#" + hslToRgb(h, s, l).map(v => v.toString(16).padStart(2, "0")).join("");
    }

    function channelLuminance(value) {
        const c = value / 255;
        return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
    }

    function luminance(h, s, l) {
        const [r, g, b] = hslToRgb(h, s, l);
        return 0.2126 * channelLuminance(r) + 0.7152 * channelLuminance(g) + 0.0722 * channelLuminance(b);
    }

    function contrastOf(h, s, l) {
        const lum = luminance(h, s, l);
        const white = 1.05 / (lum + 0.05);
        const black = (lum + 0.05) / 0.05;
        // Whichever reads better, so accent-filled buttons stay legible on every palette.
        return white >= black ? paper : ink;
    }

    function resolveTheme() {
        if (current.theme !== "system") return themeById(current.theme);
        return deviceTheme.matches ? themeById("midnight") : themeById("cream");
    }

    function resolveAccent(theme) {
        if (current.accent === "custom") {
            const c = hexToHsl(isHex(current.customAccent) ? current.customAccent : defaults.customAccent);
            // Pull a custom colour into the readable band for the current family.
            return {
                h: c.h, s: Math.max(c.s, 20),
                l: theme.dark ? Math.max(c.l, 58) : Math.min(Math.max(c.l, 28), 62)
            };
        }
        const preset = accentById(current.accent === "theme" ? theme.accent : current.accent);
        return { h: preset.h, s: preset.s, l: theme.dark ? preset.ld : preset.l };
    }

    function accentCss(theme) {
        const { h, s, l } = resolveAccent(theme);
        const mono = s < 20;
        return `:root { --accent: hsl(${h} ${s}% ${l}%); --accent-contrast: ${contrastOf(h, s, l)};`
            + ` --brand-2: ${mono ? `hsl(${h} ${s}% ${Math.min(l + 18, 60)}%)` : `hsl(${(h + 28) % 360} ${Math.min(s, 90)}% ${Math.min(l + 3, 66)}%)`};`
            + ` --brand-3: ${mono ? `hsl(${h} ${s}% ${Math.min(l + 34, 72)}%)` : `hsl(${(h + 135) % 360} 100% 68%)`}; }\n`;
    }

    function resolveAccentOption(theme) {
        const { h, s, l } = resolveAccent(theme);
        return {
            id: current.accent, name: accentLabel(theme), hex: hslToHex(h, s, l),
            contrast: contrastOf(h, s, l),
            themeDefault: current.accent === "theme", custom: current.accent === "custom"
        };
    }

    // Accent chips carry the tick colour for the palette they are drawn on.
    function accentOptions(theme) {
        return accents.map(accent => ({
            id: accent.id, name: accent.name, hex: accent.hex, hexDark: accent.hexDark,
            contrast: contrastOf(accent.h, accent.s, theme.dark ? accent.ld : accent.l)
        }));
    }

    function accentLabel(theme) {
        if (current.accent === "theme") return `${theme.name} default`;
        if (current.accent === "custom") return "Custom";
        return accentById(current.accent).name;
    }

    function migrate(source) {
        const result = { ...source };
        if (legacyThemes[result.theme]) result.theme = legacyThemes[result.theme];
        if (result.interfaceFont !== undefined) result.font = result.interfaceFont;
        if (legacyFonts[result.font]) result.font = legacyFonts[result.font];
        // An accent saved as a hex either snaps to a preset or stays as a custom
        // colour, so the exact colour the user picked is never lost.
        if (isHex(result.accent)) {
            const saved = result.accent.toLowerCase();
            const rgb = hexToRgb(saved);
            let best = null, bestDistance = Infinity;
            for (const preset of accents) {
                const candidate = hexToRgb(preset.hex);
                const distance = Math.hypot(candidate[0] - rgb[0], candidate[1] - rgb[1], candidate[2] - rgb[2]);
                if (distance < bestDistance) { best = preset; bestDistance = distance; }
            }
            if (best && bestDistance < 42) result.accent = best.id;
            else { result.accent = "custom"; result.customAccent = saved; }
        }
        delete result.interfaceFont;
        return result;
    }

    function normalize(value) {
        const source = value && typeof value === "object" ? migrate(value) : {};
        return {
            theme: source.theme === "system" || themes.some(t => t.id === source.theme) ? source.theme : defaults.theme,
            accent: source.accent === "theme" || source.accent === "custom" || accents.some(a => a.id === source.accent) ? source.accent : defaults.accent,
            customAccent: isHex(source.customAccent) ? source.customAccent.toLowerCase() : defaults.customAccent,
            font: fonts.some(f => f.id === source.font) ? source.font : defaults.font,
            codeFont: codeFonts.some(f => f.id === source.codeFont) ? source.codeFont : defaults.codeFont,
            textSize: textSizes.some(s => s.id === source.textSize) ? source.textSize : defaults.textSize,
            radius: radii.some(r => r.id === source.radius) ? source.radius : defaults.radius,
            reduceMotion: typeof source.reduceMotion === "boolean" ? source.reduceMotion : defaults.reduceMotion,
            customCss: typeof source.customCss === "string" ? source.customCss.slice(0, 20000) : "",
            customCssEnabled: typeof source.customCssEnabled === "boolean" ? source.customCssEnabled : defaults.customCssEnabled
        };
    }

    function read() {
        try {
            const stored = localStorage.getItem(storageKey);
            if (stored !== null) {
                try { return normalize(JSON.parse(stored)); }
                catch { /* A damaged preference must not prevent the app from starting. */ }
            }
            return normalize({ theme: localStorage.getItem(legacyThemeKey) });
        } catch {
            persisted = false;
            return { ...defaults };
        }
    }

    let current = read();

    function apply() {
        const root = document.documentElement;
        const theme = resolveTheme();
        root.dataset.theme = theme.id;
        root.dataset.font = current.font;
        root.dataset.codeFont = current.codeFont;
        root.dataset.radius = current.radius;
        root.classList.toggle("reduce-motion", current.reduceMotion);
        root.style.fontSize = `${sizeById(current.textSize).px}px`;
        // A stylesheet (rather than inline root properties) lets custom CSS override tokens.
        let style = document.getElementById("blazorly-appearance-overrides");
        if (!style) {
            style = document.createElement("style");
            style.id = "blazorly-appearance-overrides";
            style.setAttribute("data-permanent", "");
            document.head.appendChild(style);
        }
        // textContent deliberately treats CSS as text, including any HTML-like content.
        const accent = current.accent === "theme" ? "" : accentCss(theme);
        style.textContent = accent + (current.customCssEnabled ? current.customCss : "");
    }

    function snapshot() {
        const theme = resolveTheme();
        return {
            preferences: { ...current },
            resolvedTheme: theme.id,
            resolvedAccent: resolveAccentOption(theme),
            persisted,
            themes,
            accents: accentOptions(theme),
            fonts,
            codeFonts,
            textSizes,
            radii,
            quickLooks
        };
    }

    function persist() {
        try {
            localStorage.setItem(storageKey, JSON.stringify(current));
            localStorage.removeItem(legacyThemeKey);
            persisted = true;
        } catch {
            persisted = false;
        }
    }

    function notify() {
        const state = snapshot();
        for (const [id, dotnet] of subscribers) {
            dotnet.invokeMethodAsync("OnAppearanceChanged", state).catch(() => subscribers.delete(id));
        }
    }

    function update(patch) {
        current = normalize({ ...current, ...patch });
        apply();
        persist();
        return snapshot();
    }

    function reset() {
        return update(defaults);
    }

    // A recovery link still works if a custom rule has hidden the settings controls.
    const url = new URL(window.location.href);
    if (url.searchParams.get("reset-appearance") === "1") {
        reset();
        url.searchParams.delete("reset-appearance");
        window.history.replaceState(window.history.state, "", url);
    } else {
        apply();
    }

    deviceTheme.addEventListener("change", () => {
        if (current.theme !== "system") return;
        apply();
        notify();
    });
    window.addEventListener("storage", event => {
        if (event.key !== storageKey && event.key !== null) return;
        current = read();
        apply();
        notify();
    });

    window.blazorlyAppearance = {
        get: snapshot,
        update,
        reset,
        subscribe(dotnet) {
            const id = ++nextSubscriber;
            subscribers.set(id, dotnet);
            return id;
        },
        unsubscribe(id) { subscribers.delete(id); }
    };
})();