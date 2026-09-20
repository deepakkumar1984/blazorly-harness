(() => {
  const nav = document.querySelector("[data-nav]");
  const toggle = document.querySelector("[data-nav-toggle]");
  if (toggle && nav) {
    toggle.addEventListener("click", () => {
      const open = nav.classList.toggle("open");
      toggle.setAttribute("aria-expanded", String(open));
    });
  }

  document.querySelectorAll("[data-copy]").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const sel = btn.getAttribute("data-copy");
      const el = sel ? document.querySelector(sel) : btn.previousElementSibling;
      const text = el ? el.textContent.trim() : "";
      try {
        await navigator.clipboard.writeText(text);
        const prev = btn.textContent;
        btn.textContent = "Copied";
        setTimeout(() => { btn.textContent = prev; }, 1400);
      } catch {
        btn.textContent = "Copy failed";
      }
    });
  });

  const tabs = document.querySelectorAll("[data-install-tab]");
  const panes = document.querySelectorAll("[data-install-pane]");
  tabs.forEach((tab) => {
    tab.addEventListener("click", () => {
      const id = tab.getAttribute("data-install-tab");
      tabs.forEach((t) => t.setAttribute("aria-selected", String(t === tab)));
      panes.forEach((p) => p.classList.toggle("active", p.getAttribute("data-install-pane") === id));
    });
  });
})();
