function blazorlyTrackPin(element) {
    if (element.dataset.pinBound) return;
    element.dataset.pinBound = "1";
    element.dataset.pinned = "1";
    // Sticky follow state: unpin only on a genuine upward scroll — scrollTop moved
    // up AND the view is still far from the bottom. A bare upward move also happens
    // when the list shrinks (tail-window eviction drops a tall top node) and the
    // browser clamps scrollTop: that lands at the bottom and must re-pin, not unpin.
    let lastTop = element.scrollTop;
    element.addEventListener("scroll", () => {
        const top = element.scrollTop;
        const distFromBottom = element.scrollHeight - top - element.clientHeight;
        if (top < lastTop - 2 && distFromBottom > 160) {
            element.dataset.pinned = "0";
        } else if (distFromBottom < 160) {
            element.dataset.pinned = "1";
        }
        lastTop = top;
    });
}

window.blazorly = {
    focusById: function (id) { document.getElementById(id)?.focus(); },
    revealWorkspaceTab: function (list) {
        if (!list) return;
        const reveal = () => {
            if (!list.isConnected) { list._tabResize?.disconnect(); return; }
            const active = list.querySelector(".code-tab.active");
            if (!active) return;
            const tabBounds = active.getBoundingClientRect();
            const listBounds = list.getBoundingClientRect();
            if (tabBounds.left < listBounds.left) list.scrollLeft += tabBounds.left - listBounds.left;
            else if (tabBounds.right > listBounds.right) list.scrollLeft += tabBounds.right - listBounds.right;
        };
        if (!list._tabResize) {
            list._tabResize = new ResizeObserver(reveal);
            list._tabResize.observe(list);
        }
        reveal();
    },
    scrollBottom: function (element, force) {
        if (!element) return;
        blazorlyTrackPin(element);
        if (force || element.dataset.pinned !== "0") {
            element.scrollTop = element.scrollHeight;
        }
    },
    // Tail-windowed transcript: only the newest nodes render. Near the top, ask
    // Blazor to prepend the next chunk, then hold the reading position against the
    // growth. The "show earlier" button grows via Blazor directly; this only holds.
    attachTopGrow: function (element, dotNetRef) {
        if (!element || element.dataset.growBound) return;
        element.dataset.growBound = "1";
        let growing = false;
        const holdAfterGrow = async () => {
            if (growing) return;
            growing = true;
            try {
                const oldHeight = element.scrollHeight, oldTop = element.scrollTop;
                for (let i = 0; i < 20; i++) {
                    await new Promise(r => setTimeout(r, 50));
                    if (!element.isConnected) return;
                    const height = element.scrollHeight;
                    if (height > oldHeight + 10) {
                        element.scrollTop = oldTop + (height - oldHeight);
                        return;
                    }
                }
            } finally {
                growing = false;
            }
        };
        element.addEventListener("scroll", async () => {
            if (growing || element.scrollTop > 400) return;
            let grew = false;
            try {
                grew = await dotNetRef.invokeMethodAsync("GrowTranscriptTop");
            } catch { return; }
            if (grew) await holdAfterGrow();
        });
        element.addEventListener("click", (e) => {
            if (!e.target.closest(".transcript-more")) return;
            holdAfterGrow();
        });
    },
    // Refresh-safe scroll: rows still stream in after first paint, so a single jump
    // can land above the final bottom. Re-jump while the height is still settling;
    // abort if the user scrolls up mid-settle or the element leaves the DOM.
    settleBottom: function (element) {
        if (!element) return;
        blazorlyTrackPin(element);
        element.dataset.pinned = "1";
        element.scrollTop = element.scrollHeight;
        let stable = 0, last = element.scrollHeight, ticks = 0;
        const timer = setInterval(() => {
            ticks++;
            if (!element.isConnected || element.dataset.pinned === "0" || ticks > 40) {
                clearInterval(timer);
                return;
            }
            const height = element.scrollHeight;
            if (height !== last) {
                stable = 0;
                last = height;
            } else {
                stable++;
            }
            element.scrollTop = element.scrollHeight;
            if (stable >= 4) clearInterval(timer);
        }, 150);
    },
    // Shift any open chip dropdown left so it stays inside the viewport.
    clampMenu: function () {
        const margin = 12;
        const vw = document.documentElement.clientWidth;
        document.querySelectorAll(".popover.anchored").forEach(el => {
            el.style.transform = "";
            const overflow = el.getBoundingClientRect().right - (vw - margin);
            if (overflow > 0) el.style.transform = "translateX(" + (-overflow) + "px)";
        });
    },
    // In-app editor: the textarea owns the text so keystrokes stay in the page.
    // Blazor only hears dirty/save, and reads the value back when saving or switching tabs.
    editor: {
        bind: function (ta, gutter, dotNetRef, relativePath) {
            if (!ta) return;
            ta.editorState = { dotNetRef, relativePath, dirty: false };
            if (ta.dataset.editorBound) return;
            ta.dataset.editorBound = "1";
            const notify = method => {
                const state = ta.editorState;
                state.dotNetRef?.invokeMethodAsync(method, state.relativePath).catch(() => {});
            };
            const paint = () => {
                if (!gutter) return;
                const n = Math.max(1, ta.value.split("\n").length);
                if (gutter.dataset.n !== String(n)) {
                    gutter.dataset.n = String(n);
                    let text = "";
                    for (let i = 1; i <= n; i++) text += i + "\n";
                    gutter.textContent = text;
                }
                gutter.scrollTop = ta.scrollTop;
            };
            ta.addEventListener("scroll", paint);
            ta.addEventListener("editor-sync", paint);
            ta.addEventListener("input", () => {
                paint();
                if (!ta.editorState.dirty) {
                    ta.editorState.dirty = true;
                    notify("MarkDirty");
                }
            });
            ta.addEventListener("keydown", e => {
                if ((e.ctrlKey || e.metaKey) && (e.key === "s" || e.key === "S")) {
                    e.preventDefault();
                    notify("SaveFromEditor");
                    return;
                }
                if (e.key !== "Tab" || e.ctrlKey || e.metaKey || e.altKey) return;
                e.preventDefault();
                const start = ta.selectionStart, end = ta.selectionEnd;
                ta.setRangeText("  ", start, end, "end");
                ta.dispatchEvent(new Event("input", { bubbles: true }));
            });
            paint();
        },
        setText: function (ta, text, dirty) {
            if (!ta) return;
            if (ta.value !== (text ?? "")) ta.value = text ?? "";
            if (ta.editorState) ta.editorState.dirty = !!dirty;
            ta.dispatchEvent(new Event("editor-sync"));
        },
        saved: function (ta, text) {
            if (!ta) return false;
            const unchanged = ta.value === text;
            if (ta.editorState) ta.editorState.dirty = !unchanged;
            return unchanged;
        },
        getText: function (ta) {
            return ta ? ta.value : "";
        },
        repaint: function (ta) {
            if (ta) ta.dispatchEvent(new Event("editor-sync"));
        }
    },
    sidebar: {
        loadCollapsed: function () {
            try { return JSON.parse(localStorage.getItem("blazorly.sidebar.collapsed") ?? "null"); }
            catch { return null; }
        },
        saveCollapsed: function (ids) {
            try { localStorage.setItem("blazorly.sidebar.collapsed", JSON.stringify(ids ?? [])); } catch { }
        }
    },
    // Composer textarea: grow with its content up to the CSS max-height, and keep
    // the caret end of the text in view. Typing keeps the caret visible on its own,
    // but a large paste (or a programmatic value set) leaves the box scrolled to
    // the top — the tail, where the caret now sits, is what must stay visible.
    composer: {
        grow: function (ta, force) {
            if (!ta) return;
            // Skip re-measuring when neither the content nor the wrap width changed;
            // the per-render calls from Blazor then cost nothing while streaming.
            const sig = ta.value.length + ":" + ta.clientWidth;
            if (!force && ta.dataset.growSig === sig) return;
            ta.dataset.growSig = sig;
            ta.style.height = "auto";
            ta.style.height = ta.scrollHeight + "px"; // CSS max-height caps the growth
        },
        attach: function (ta) {
            if (!ta || ta.dataset.composerBound) return;
            ta.dataset.composerBound = "1";
            this.grow(ta, true);
            ta.addEventListener("input", () => {
                this.grow(ta, true);
                // Follow the bottom only while the caret sits at the end of the text;
                // editing an earlier line must not yank the view down.
                if (ta.selectionEnd === ta.value.length) ta.scrollTop = ta.scrollHeight;
            });
            // Rewrap on viewport changes needs a fresh measure even with same-length text.
            window.addEventListener("resize", () => this.grow(ta, true));
        },
        /// Paste or drop any file (image, PDF, doc, text) anywhere in the app. Files
        /// upload over HTTP to /api/session.uploadFile — NEVER through JS interop, whose
        /// SignalR messages cap at 32KB and would silently drop every real screenshot.
        /// Only the tiny result (id/name/kind strings) crosses the circuit afterward.
        /// Paste is claimed ONLY when the clipboard carries a file item — plain text
        /// pastes fall through untouched.
        attachImagePaste: function (ta, dotnetRef, clipInput, sessionId) {
            if (!ta || !dotnetRef) return;
            // One document listener serves every chat. Read its current destination
            // for each upload, then retain that destination until the upload finishes.
            const holder = (this._holder ??= {});
            holder.ref = dotnetRef;
            holder.ta = ta;
            holder.sessionId = sessionId;
            holder.upload = (file) => {
                if (!file || !holder.ta?.isConnected || !holder.ta.getClientRects().length) return false;
                const destination = holder.sessionId;
                const receiver = holder.ref;
                const form = new FormData();
                form.append("sessionId", destination);
                form.append("file", file, file.name || "pasted-file");
                fetch("/api/session.uploadFile", { method: "POST", body: form })
                    .then(r => r.ok ? r.json() : r.text().then(t => Promise.reject(new Error("upload " + r.status + ": " + t))))
                    .then(j => receiver.invokeMethodAsync("OnFileUploaded", destination, j.id, j.fileName, j.kind)
                        .catch(() => { /* circuit gone mid-upload; nothing to do */ }))
                    .catch(err => console.error("[blazorly] attach failed:", err));
                return true;
            };
            const hasFileItem = (e) => {
                const items = e.clipboardData?.items;
                if (!items) return false;
                for (let i = 0; i < items.length; i++) if (items[i].kind === "file") return true;
                return false;
            };
            if (!this._pasteBound) {
                this._pasteBound = true;
                // Paste bubbles to document from wherever it lands (composer, transcript,
                // anywhere), so one listener covers the focused AND the unfocused case.
                document.addEventListener("paste", (e) => {
                    if (!hasFileItem(e)) return; // text paste: let the browser handle it
                    const items = e.clipboardData.items;
                    let uploaded = false;
                    for (let i = 0; i < items.length; i++) {
                        if (items[i].kind === "file") uploaded = holder.upload(items[i].getAsFile()) || uploaded;
                    }
                    if (!uploaded) return;
                    e.preventDefault();
                    holder.ta.focus(); // the next action is typing a note or hitting Send
                });
            }
            if (!this._dropBound) {
                this._dropBound = true;
                // Dropping a file on the page would otherwise navigate away from the app;
                // claim the gesture and upload every dropped file instead.
                document.addEventListener("dragover", (e) => {
                    if (e.dataTransfer?.types?.includes("Files")) {
                        e.preventDefault();
                        e.dataTransfer.dropEffect = "copy";
                    }
                });
                document.addEventListener("drop", (e) => {
                    if (!e.dataTransfer?.files?.length) return;
                    e.preventDefault();
                    let uploaded = false;
                    for (const file of e.dataTransfer.files) uploaded = holder.upload(file) || uploaded;
                    if (uploaded) holder.ta.focus();
                });
            }
            // File input (paperclip button)
            if (clipInput && !clipInput.dataset.clipBound) {
                clipInput.dataset.clipBound = "1";
                clipInput.addEventListener("change", () => {
                    const file = clipInput.files?.[0];
                    if (holder.upload(file)) clipInput.value = "";
                });
            }
        },
    },
    // Drag handle that resizes the terminal drawer between min and max pixels.
    // upward: the dock sits under the chat, so dragging the handle up grows it.
    attachTerminalResize: function (handle, drawer, min, max, upward) {
        if (!handle || !drawer || handle.dataset.bound) return;
        handle.dataset.bound = "1";
        let dragging = false, startY = 0, startH = 0;
        handle.addEventListener("pointerdown", e => {
            dragging = true;
            startY = e.clientY;
            startH = drawer.getBoundingClientRect().height;
            handle.setPointerCapture(e.pointerId);
            document.body.style.cursor = "row-resize";
            e.preventDefault();
        });
        handle.addEventListener("pointermove", e => {
            if (!dragging) return;
            const delta = upward ? (startY - e.clientY) : (e.clientY - startY);
            const h = Math.min(max, Math.max(min, startH + delta));
            drawer.style.height = h + "px";
        });
        const stop = () => { dragging = false; document.body.style.cursor = ""; };
        handle.addEventListener("pointerup", stop);
        handle.addEventListener("pointercancel", stop);
    },
    trapModalFocus: function (element) {
        if (!element || element.dataset.focusBound) return;
        element.dataset.focusBound = "1";
        element.addEventListener("keydown", event => {
            if (event.key !== "Tab") return;
            const controls = [...element.querySelectorAll('button, input, select, textarea, a[href], [tabindex]')]
                .filter(control => !control.disabled && control.tabIndex >= 0 && control.getClientRects().length > 0);
            const first = controls[0];
            const last = controls[controls.length - 1];
            if (event.shiftKey && document.activeElement === first) {
                event.preventDefault();
                last?.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault();
                first?.focus();
            }
        });
    },
    viewportWidth: function () {
        return window.innerWidth || document.documentElement.clientWidth || 0;
    },
    clickElement: function (el) {
        if (el && typeof el.click === "function") el.click();
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
                if (this._s.sessionId !== sessionId) {
                    this._hide(this._s);
                    this._s.cache.clear();
                }
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
