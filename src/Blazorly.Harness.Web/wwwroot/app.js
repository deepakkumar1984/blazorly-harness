window.blazorly = {
    scrollBottom: function (element, force) {
        if (!element) return;
        // Pin when the user is near the bottom already, or when force (fresh session load).
        const nearBottom = element.scrollHeight - element.scrollTop - element.clientHeight < 160;
        if (force || nearBottom) {
            element.scrollTop = element.scrollHeight;
        }
    },
    getTheme: function () {
        return localStorage.getItem("blazorly.theme") || "dark";
    },
    setTheme: function (theme) {
        localStorage.setItem("blazorly.theme", theme);
        document.documentElement.dataset.theme = theme;
        return theme;
    },
    // Slash-command autocomplete: runs entirely in the browser (no round-trip per
    // keystroke). The server is contacted only when a command is picked.
    slash: {
        _s: null,
        attach: function (ta, menu, commands, dotnetRef) {
            if (!ta || !menu) return;
            if (this._s && this._s.ta === ta) {
                this._s.commands = commands;
                this._s.dotnet = dotnetRef;
                return;
            }
            const s = { ta, menu, commands, dotnet: dotnetRef, open: false, matches: [], highlight: 0 };
            this._s = s;
            ta.addEventListener("input", () => this._sync(s));
            ta.addEventListener("keydown", (e) => this._key(s, e));
            ta.addEventListener("blur", () => setTimeout(() => this._hide(s), 150));
        },
        _sync: function (s) {
            const v = s.ta.value;
            const bare = v.startsWith("/") && !v.includes(" ");
            if (!bare) { this._hide(s); return; }
            s.matches = s.commands.filter(c => c.name.toLowerCase().startsWith(v.toLowerCase()));
            if (s.matches.length === 0) { this._hide(s); return; }
            s.highlight = Math.min(s.highlight, s.matches.length - 1);
            this._render(s);
        },
        _render: function (s) {
            s.open = true;
            s.menu.style.display = "block";
            s.menu.innerHTML = s.matches.map((c, i) =>
                `<button type="button" class="command-option${i === s.highlight ? " hl" : ""}" data-i="${i}">` +
                `<code>${c.name}</code>${c.args ? `<span class="command-args">${c.args}</span>` : ""}` +
                `<span class="command-desc">${c.description}</span></button>`).join("");
            [...s.menu.querySelectorAll(".command-option")].forEach(btn => {
                btn.addEventListener("mousedown", (e) => {
                    e.preventDefault(); // keep textarea focus
                    this._pick(s, parseInt(btn.dataset.i, 10));
                });
            });
        },
        _pick: function (s, i) {
            const c = s.matches[i];
            if (!c) return;
            if (c.args) {
                s.ta.value = c.name + " ";
                this._hide(s);
                s.dotnet.invokeMethodAsync("CompleteSlash", c.name + " ");
            } else {
                s.ta.value = "";
                this._hide(s);
                s.dotnet.invokeMethodAsync("SubmitSlash", c.name);
            }
        },
        _key: function (s, e) {
            if (e.key === "Escape" && s.open) { this._hide(s); return; }
            if (e.key === "Enter" && !e.shiftKey && s.ta.value.trim().length > 0) {
                // Sending: suppress the default newline insert — it fires an input event
                // during the server round-trip and races the send with a mutated draft.
                e.preventDefault();
            }
            if (!s.open) this._sync(s);
            if (!s.open) return;
            if (e.key === "ArrowDown") {
                e.preventDefault();
                s.highlight = Math.min(s.highlight + 1, s.matches.length - 1);
                this._render(s);
            } else if (e.key === "ArrowUp") {
                e.preventDefault();
                s.highlight = Math.max(s.highlight - 1, 0);
                this._render(s);
            } else if (e.key === "Tab") {
                e.preventDefault();
                this._pick(s, s.highlight);
            }
        },
        _hide: function (s) {
            s.open = false;
            s.menu.style.display = "none";
            s.menu.innerHTML = "";
            s.matches = [];
            s.highlight = 0;
        },
        state: function () {
            const s = this._s;
            if (!s || !s.open || s.matches.length === 0) return { open: false };
            const c = s.matches[s.highlight];
            return { open: true, name: c.name, argsEmpty: !c.args };
        }
    }
};
