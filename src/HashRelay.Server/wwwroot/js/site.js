// HashRelay dashboard & site behaviours. No framework; progressive enhancement only.
(function () {
  "use strict";

  // ---- copy-to-clipboard buttons ----
  document.addEventListener("click", async (e) => {
    const btn = e.target.closest("[data-copy]");
    if (!btn) return;
    const sel = btn.getAttribute("data-copy");
    const src = sel ? document.querySelector(sel) : btn.parentElement.querySelector("code, pre");
    const text = src ? (src.getAttribute("data-text") || src.innerText) : "";
    try {
      await navigator.clipboard.writeText(text.replace(/^\$\s/gm, "").trim());
      const prev = btn.textContent;
      btn.textContent = "Copied";
      btn.classList.add("done");
      setTimeout(() => { btn.textContent = prev; btn.classList.remove("done"); }, 1600);
    } catch { /* clipboard unavailable */ }
  });

  // ---- modals ----
  document.querySelectorAll("[data-open]").forEach((el) => {
    el.addEventListener("click", () => {
      const dlg = document.querySelector(el.getAttribute("data-open"));
      if (dlg && typeof dlg.showModal === "function") { dlg.showModal(); const f = dlg.querySelector("input, select, textarea"); if (f) f.focus(); }
    });
  });
  document.querySelectorAll("[data-close]").forEach((el) => {
    el.addEventListener("click", () => el.closest("dialog")?.close());
  });
  document.querySelectorAll("dialog.modal").forEach((d) => {
    d.addEventListener("click", (e) => { if (e.target === d) d.close(); });
  });
  const autoOpen = document.querySelector("dialog[data-auto-open]");
  if (autoOpen && typeof autoOpen.showModal === "function") autoOpen.showModal();

  // ---- workspace switcher ----
  const ws = document.getElementById("ws-switch");
  if (ws) ws.addEventListener("change", () => ws.form.requestSubmit());

  // ---- relative times ----
  const rtf = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
  function rel(iso) {
    const d = (new Date(iso).getTime() - Date.now()) / 1000;
    const a = Math.abs(d);
    if (a < 45) return "just now";
    if (a < 3600) return rtf.format(Math.round(d / 60), "minute");
    if (a < 86400) return rtf.format(Math.round(d / 3600), "hour");
    return rtf.format(Math.round(d / 86400), "day");
  }
  function paintTimes(root) {
    (root || document).querySelectorAll("time[data-rel]").forEach((t) => {
      const iso = t.getAttribute("datetime");
      if (iso) { t.textContent = rel(iso); t.title = new Date(iso).toLocaleString(); }
    });
  }
  paintTimes();
  setInterval(() => paintTimes(), 30000);

  // ---- live dashboard refresh ----
  const live = document.querySelector("[data-live]");
  if (live) {
    const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
    // Mirrors PublisherTypes.Labels; the type is whatever was registered for the credential, never a guess.
    const publisherLabel = (t) => ({ agent: "AI agent", application: "Application", service: "Service / automation", human: "Person", unknown: "Not specified" }[t] ?? "Not specified");
    let lastStamp = null;
    async function tick() {
      try {
        const res = await fetch("/dashboard/live", { headers: { Accept: "application/json" }, credentials: "same-origin" });
        if (!res.ok) return;
        const data = await res.json();
        document.querySelectorAll("[data-stat]").forEach((el) => {
          const k = el.getAttribute("data-stat");
          if (k in data.stats) el.textContent = data.stats[k];
        });
        document.querySelectorAll("[data-endpoint-status]").forEach((el) => {
          const id = el.getAttribute("data-endpoint-status");
          const ep = data.endpoints.find((x) => x.endpointId === id);
          if (!ep) return;
          el.className = "badge " + (ep.online ? "badge-success" : "");
          el.innerHTML = `<span class="dot${ep.online ? " pulse" : ""}"></span>${ep.online ? "Online" : "Offline"}`;
          const seen = document.querySelector(`[data-endpoint-seen="${id}"]`);
          if (seen) { seen.setAttribute("datetime", ep.lastSeenAt); }
        });
        const feed = document.querySelector("[data-feed]");
        if (feed && data.recent) {
          const stamp = data.recent.length ? data.recent[0].notificationId + ":" + data.recent.map((n) => n.delivered).join(",") : "empty";
          if (stamp !== lastStamp) {
            lastStamp = stamp;
            if (data.recent.length === 0) {
              feed.innerHTML = `<li class="empty">No notifications yet.</li>`;
            } else {
              feed.innerHTML = data.recent.map((n) => `
                <li class="fade-in">
                  <div>
                    <div class="t"><a class="plain" href="/n/${esc(n.notificationId)}">${esc(n.title)}</a>${n.publisher && n.publisher.name ? `<span class="small muted"> — ${esc(n.publisher.name)}</span>` : ""}</div>
                    ${n.body ? `<div class="b">${esc(n.body)}</div>` : ""}
                    <div class="tags mt-1">${n.tags.map((t) => `<span class="tag">#${esc(t)}</span>`).join("")}${n.hasMarkdown ? `<span class="badge">markdown</span>` : ""}${n.hasDiagram ? `<span class="badge">diagram</span>` : (n.imageUrl ? `<span class="badge">image</span>` : "")}${n.publisher ? `<span class="badge">${esc(publisherLabel(n.publisher.type))}</span>${n.publisher.channel ? `<span class="badge">${esc(n.publisher.channel)}</span>` : ""}` : ""}</div>
                  </div>
                  <div class="text-right">
                    <time data-rel datetime="${esc(n.publishedAtUtc)}"></time>
                    <div class="small muted">${n.delivered}/${n.matchedEndpoints} delivered</div>
                  </div>
                </li>`).join("");
            }
          }
        }
        paintTimes();
      } catch { /* offline; try again next tick */ }
    }
    tick();
    setInterval(tick, 4000);
  }

  // ---- docs: highlight the section in view ----
  const docsNav = document.querySelector(".docs-nav");
  if (docsNav && "IntersectionObserver" in window) {
    const links = [...docsNav.querySelectorAll("a[href^=\"#\"]")];
    const byId = new Map(links.map((a) => [a.getAttribute("href").slice(1), a]));
    const headings = [...byId.keys()].map((id) => document.getElementById(id)).filter(Boolean);
    let current = null;
    const setActive = (id) => { if (id === current) return; current = id; links.forEach((a) => a.classList.toggle("active", a.getAttribute("href") === "#" + id)); };
    const pick = () => {
      const line = 120; let best = headings[0];
      for (const h of headings) { if (h.getBoundingClientRect().top <= line) best = h; else break; }
      if (best) setActive(best.id);
    };
    window.addEventListener("scroll", pick, { passive: true });
    pick();
  }

  // ---- pairing page: poll for completion message ----
  const pairDone = document.querySelector("[data-pair-done]");
  if (pairDone) setTimeout(() => { pairDone.classList.add("fade-in"); }, 50);
})();
