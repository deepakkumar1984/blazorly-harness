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
    },
    // @-file mention autocomplete: detects a trailing "@token" before the caret and
    // queries /api/session.files (debounced + cached). Picking rewrites the token in
    // place and dispatches an input event so the Blazor binder stays in sync.
    mention: {
        _s: null,
        attach: function (ta, menu, sessionId) {
            if (!ta || !menu) return;
            if (this._s && this._s.ta === ta) {
                this._s.sessionId = sessionId;
                return;
            }
            const s = { ta, menu, sessionId, open: false, matches: [], highlight: 0, at: -1, token: "", seq: 0, timer: null, cache: new Map() };
            this._s = s;
            ta.addEventListener("input", () => this._sync(s));
            ta.addEventListener("keydown", (e) => this._key(s, e));
            ta.addEventListener("blur", () => setTimeout(() => this._hide(s), 150));
        },
        _match: function (v, caret) {
            const before = v.slice(0, caret);
            const m = before.match(/(^|\s)@([A-Za-z0-9._~/+-]*)$/);
            if (!m) return null;
            return { at: before.length - m[2].length - 1, token: m[2] };
        },
        _sync: function (s) {
            const caret = s.ta.selectionStart ?? s.ta.value.length;
            const m = this._match(s.ta.value, caret);
            if (!m || m.token.length === 0) { this._hide(s); return; }
            s.at = m.at;
            s.token = m.token;
            if (s.cache.has(m.token)) { this._show(s, s.cache.get(m.token)); return; }
            clearTimeout(s.timer);
            const seq = ++s.seq;
            s.timer = setTimeout(async () => {
                try {
                    const res = await fetch(`/api/session.files?sessionId=${encodeURIComponent(s.sessionId)}&q=${encodeURIComponent(m.token)}`);
                    if (!res.ok || seq !== s.seq) return;
                    const body = await res.json();
                    if (s.cache.size > 200) s.cache.clear();
                    s.cache.set(m.token, body.files || []);
                    if (s.token === m.token) this._show(s, body.files || []);
                } catch { /* network hiccups just close the menu */ }
            }, 120);
        },
        _show: function (s, files) {
            if (files.length === 0) { this._hide(s); return; }
            s.matches = files;
            s.highlight = Math.min(s.highlight, files.length - 1);
            s.open = true;
            s.menu.style.display = "block";
            s.menu.innerHTML = files.map((f, i) =>
                `<button type="button" class="command-option${i === s.highlight ? " hl" : ""}" data-i="${i}">` +
                `<code>${f.path}</code><span class="command-desc">${f.isDir ? "dir" : this._size(f.size)}</span></button>`).join("");
            [...s.menu.querySelectorAll(".command-option")].forEach(btn => {
                btn.addEventListener("mousedown", (e) => {
                    e.preventDefault(); // keep textarea focus
                    this._pick(s, parseInt(btn.dataset.i, 10));
                });
            });
        },
        _size: function (n) {
            if (n < 1024) return n + " B";
            if (n < 1024 * 1024) return (n / 1024).toFixed(1) + " KB";
            return (n / (1024 * 1024)).toFixed(1) + " MB";
        },
        _pick: function (s, i) {
            const f = s.matches[i];
            if (!f) return;
            const caret = s.ta.selectionStart ?? s.ta.value.length;
            const v = s.ta.value;
            s.ta.value = v.slice(0, s.at) + "@" + f.path + " " + v.slice(caret);
            const next = s.at + f.path.length + 2;
            s.ta.setSelectionRange(next, next);
            this._hide(s);
            s.ta.dispatchEvent(new Event("input", { bubbles: true })); // keep the Blazor binder in sync
            s.ta.focus();
        },
        pick: function () {
            const s = this._s;
            if (!s || !s.open) return;
            this._pick(s, s.highlight);
        },
        _key: function (s, e) {
            if (e.key === "Escape" && s.open) { this._hide(s); return; }
            if (!s.open) return;
            if (e.key === "Enter" || e.key === "Tab") {
                // The menu owns Enter/Tab while open (pick instead of send); prevent the
                // newline insert that would otherwise race the server round-trip. Enter is
                // picked from the .NET side after the state poll (slash's contract); Tab
                // has no server round-trip, so it picks here.
                e.preventDefault();
                if (e.key === "Tab") this._pick(s, s.highlight);
            } else if (e.key === "ArrowDown") {
                e.preventDefault();
                s.highlight = Math.min(s.highlight + 1, s.matches.length - 1);
                this._show(s, s.matches);
            } else if (e.key === "ArrowUp") {
                e.preventDefault();
                s.highlight = Math.max(s.highlight - 1, 0);
                this._show(s, s.matches);
            }
        },
        _hide: function (s) {
            clearTimeout(s.timer);
            ++s.seq; // invalidate any in-flight fetch
            s.open = false;
            s.menu.style.display = "none";
            s.menu.innerHTML = "";
            s.matches = [];
            s.highlight = 0;
            s.at = -1;
        },
        state: function () {
            const s = this._s;
            return { open: !!(s && s.open && s.matches.length > 0) };
        }
    }
};
