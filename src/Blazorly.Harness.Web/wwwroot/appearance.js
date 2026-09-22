// Loaded in <head> so the saved appearance is in place before the first paint.
(() => {
    "use strict";

    const storageKey = "blazorly.appearance";
    const legacyKey = "blazorly.theme";
    const defaults = Object.freeze({
        theme: "dark", interfaceFont: "inter", codeFont: "jetbrains",
        accent: "", customCss: "", customCssEnabled: true
    });
    const themes = [
        { id: "dark", name: "Midnight", description: "Cool blue, quiet focus", accent: "#91b0ff" },
        { id: "light", name: "Daylight", description: "Clean and bright", accent: "#365ec9" },
        { id: "graphite", name: "Graphite", description: "Warmth in the dark", accent: "#e5b887" },
        { id: "forest", name: "Evergreen", description: "A calmer shade of green", accent: "#82d4a8" },
        { id: "dusk", name: "Dusk", description: "Soft violet after hours", accent: "#baa5f7" },
        { id: "sandstone", name: "Sandstone", description: "Paper with a warm touch", accent: "#876039" }
    ];
    const interfaceFonts = [
        { id: "inter", name: "Inter", description: "Crisp & familiar" },
        { id: "manrope", name: "Manrope", description: "Soft & geometric" },
        { id: "system", name: "System sans", description: "Your device's default" },
        { id: "serif", name: "Georgia", description: "A classic reading face" }
    ];
    const codeFonts = [
        { id: "jetbrains", name: "JetBrains Mono", description: "Made for code" },
        { id: "system", name: "System mono", description: "Your device's default" },
        { id: "consolas", name: "Consolas / Menlo", description: "Familiar editor type" }
    ];
    const deviceTheme = window.matchMedia("(prefers-color-scheme: light)");
    const subscribers = new Map();
    let nextSubscriber = 0;
    let persisted = true;

    function normalize(value) {
        const source = value && typeof value === "object" ? value : {};
        return {
            theme: source.theme === "system" || themes.some(t => t.id === source.theme) ? source.theme : defaults.theme,
            interfaceFont: interfaceFonts.some(f => f.id === source.interfaceFont) ? source.interfaceFont : defaults.interfaceFont,
            codeFont: codeFonts.some(f => f.id === source.codeFont) ? source.codeFont : defaults.codeFont,
            accent: typeof source.accent === "string" && /^#[0-9a-f]{6}$/i.test(source.accent) ? source.accent.toLowerCase() : "",
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
            return normalize({ theme: localStorage.getItem(legacyKey) });
        } catch {
            persisted = false;
            return { ...defaults };
        }
    }

    let current = read();

    function resolvedTheme() {
        return current.theme === "system" ? (deviceTheme.matches ? "light" : "dark") : current.theme;
    }

    function foreground(hex) {
        const rgb = hex.slice(1).match(/../g).map(channel => {
            const value = parseInt(channel, 16) / 255;
            return value <= 0.04045 ? value / 12.92 : Math.pow((value + 0.055) / 1.055, 2.4);
        });
        const luminance = rgb[0] * 0.2126 + rgb[1] * 0.7152 + rgb[2] * 0.0722;
        return luminance > 0.179 ? "#000000" : "#ffffff";
    }

    function apply() {
        const root = document.documentElement;
        root.dataset.theme = resolvedTheme();
        root.dataset.font = current.interfaceFont;
        root.dataset.codeFont = current.codeFont;
        // A stylesheet (rather than inline root properties) lets custom CSS override tokens.
        let style = document.getElementById("blazorly-appearance-overrides");
        if (!style) {
            style = document.createElement("style");
            style.id = "blazorly-appearance-overrides";
            style.setAttribute("data-permanent", "");
            document.head.appendChild(style);
        }
        const accent = current.accent
            ? `:root { --accent: ${current.accent}; --accent-contrast: ${foreground(current.accent)}; }\n`
            : "";
        // textContent deliberately treats CSS as text, including any HTML-like content.
        style.textContent = accent + (current.customCssEnabled ? current.customCss : "");
    }

    function snapshot() {
        return { preferences: { ...current }, resolvedTheme: resolvedTheme(), persisted, themes, interfaceFonts, codeFonts };
    }

    function persist() {
        try {
            localStorage.setItem(storageKey, JSON.stringify(current));
            localStorage.removeItem(legacyKey);
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
