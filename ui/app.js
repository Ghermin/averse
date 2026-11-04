// Averse : l'interface commune au PC et au téléphone.
// Elle parle au moteur torrent de l'appli hôte par messages JSON :
//   { id, cmd, args }  →  { id, result } ou { id, error }
// et l'hôte peut envoyer des événements { event: "toast", message, error }.
// Windows : WebView2 (window.chrome.webview). Android : window.AverseAndroid.post().
// Ouverte dans un simple navigateur, elle tourne en démo avec de faux torrents.
"use strict";

const host = (() => {
  const pending = new Map();
  let seq = 0;

  function receive(msg) {
    if (typeof msg === "string") msg = JSON.parse(msg);
    if (msg.id && pending.has(msg.id)) {
      const p = pending.get(msg.id);
      pending.delete(msg.id);
      if (msg.error) p.reject(new Error(msg.error));
      else p.resolve(msg.result);
    } else if (msg.event) {
      onEvent(msg);
    }
  }
  window.__averseReceive = receive;

  function make(platform, post) {
    return {
      platform,
      call(cmd, args = {}) {
        return new Promise((resolve, reject) => {
          const id = ++seq;
          pending.set(id, { resolve, reject });
          post(JSON.stringify({ id, cmd, args }));
        });
      },
    };
  }

  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.addEventListener("message", (e) => receive(e.data));
    return make("windows", (s) => window.chrome.webview.postMessage(s));
  }
  if (window.AverseAndroid) {
    return make("android", (s) => window.AverseAndroid.post(s));
  }
  return demoHost();
})();

document.documentElement.dataset.platform = host.platform;

// ---------- Format ----------

const nf1 = new Intl.NumberFormat("fr-FR", { maximumFractionDigits: 1 });
const nf0 = new Intl.NumberFormat("fr-FR", { maximumFractionDigits: 0 });

function size(n) {
  if (!(n > 0)) return "0 o";
  const units = ["o", "Ko", "Mo", "Go", "To"];
  let i = 0;
  while (n >= 1000 && i < units.length - 1) { n /= 1024; i++; }
  return (i === 0 || n >= 100 ? nf0 : nf1).format(n) + " " + units[i];
}
const rate = (n) => size(n) + "/s";

function eta(s) {
  if (!(s > 0) || s === Infinity) return "";
  if (s < 60) return Math.ceil(s) + " s";
  if (s < 3600) return Math.round(s / 60) + " min";
  if (s < 86400) {
    const h = Math.floor(s / 3600), m = Math.round((s % 3600) / 60);
    return h + " h " + String(m).padStart(2, "0");
  }
  return Math.round(s / 86400) + " j";
}

const STATE = {
  metadata: "Recherche des infos…",
  checking: "Vérification…",
  queued: "En attente",
  downloading: "Téléchargement",
  stalled: "En attente de pairs",
  seeding: "Terminé · partage",
  done: "Terminé",
  paused: "En pause",
  error: "Erreur",
};

const ICON_PAUSE = '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M7 5h3.5v14H7zm6.5 0H17v14h-3.5z"/></svg>';
const ICON_PLAY = '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M8 5.5v13a1 1 0 0 0 1.5.86l10.5-6.5a1 1 0 0 0 0-1.72L9.5 4.64A1 1 0 0 0 8 5.5"/></svg>';

// ---------- Rendu ----------

const $ = (id) => document.getElementById(id);
const list = $("list");
const tpl = $("item");
const rows = new Map();

function render(state) {
  $("dir").textContent = state.downloadDir || "…";
  $("change-dir").hidden = !state.canChangeDir;

  let down = 0, up = 0;
  const seen = new Set();
  for (const t of state.torrents) {
    seen.add(t.id);
    down += t.down || 0;
    up += t.up || 0;
    let li = rows.get(t.id);
    if (!li) {
      li = tpl.content.firstElementChild.cloneNode(true);
      li.dataset.id = t.id;
      rows.set(t.id, li);
      list.prepend(li);
    }
    update(li, t);
  }
  for (const [id, li] of rows) {
    if (!seen.has(id)) { li.remove(); rows.delete(id); }
  }
  $("empty").hidden = state.torrents.length > 0;
  $("total-down").textContent = rate(down);
  $("total-up").textContent = rate(up);
  askNext(state.pending || []);
}

// Un torrent venu d'une autre appli (navigateur, double-clic…) attend un oui avant de démarrer.
let asking = null;
const answered = new Set();

function askNext(pending) {
  const keys = new Set(pending.map((p) => p.key));
  for (const k of answered) if (!keys.has(k)) answered.delete(k);
  const dlg = $("ask");
  if (asking) {
    if (!keys.has(asking)) { asking = null; dlg.close("gone"); }
    return;
  }
  const p = pending.find((x) => !answered.has(x.key));
  if (!p || $("confirm").open) return;
  asking = p.key;
  $("ask-name").textContent = p.size > 0 ? p.name + " · " + size(p.size) : p.name;
  dlg.returnValue = "";
  dlg.showModal();
}

$("ask").addEventListener("close", () => {
  const key = asking;
  asking = null;
  const v = $("ask").returnValue;
  if (!key || v === "gone") return;
  answered.add(key);
  if (v === "ok") run("acceptPending", { key }, "Torrent ajouté.");
  else run("rejectPending", { key });
});

function update(li, t) {
  let st = t.state;
  if (st === "downloading" && !(t.down > 0) && t.peers === 0) st = "stalled";
  li.dataset.state = t.state;
  li.querySelector(".item__name").textContent = t.name;
  li.querySelector(".item__name").title = t.name;
  li.querySelector(".bar-fill").style.width = (Math.max(0, Math.min(1, t.progress)) * 100).toFixed(1) + "%";

  const pct = Math.floor(t.progress * 1000) / 10;
  let label = STATE[st] || st;
  if (st === "downloading" || st === "stalled" || st === "paused") label += " · " + nf1.format(pct) + " %";
  if (st === "error" && t.error) label += " : " + t.error;
  li.querySelector(".item__state").textContent = label;

  const info = [];
  if (t.size > 0) {
    info.push(t.progress >= 1 ? size(t.size) : size(t.done) + " / " + size(t.size));
  }
  if (t.state === "downloading") {
    if (t.down > 0) info.push("↓ " + rate(t.down));
    const e = eta(t.eta);
    if (e) info.push(e);
  }
  if ((t.state === "downloading" || t.state === "seeding") && t.up > 0) info.push("↑ " + rate(t.up));
  if (t.state === "downloading" || t.state === "metadata") {
    info.push(t.peers + (t.peers > 1 ? " pairs" : " pair"));
  }
  const infoEl = li.querySelector(".item__info");
  const html = info.map((s) => "<span>" + escapeHtml(s) + "</span>").join("");
  if (infoEl.innerHTML !== html) infoEl.innerHTML = html;

  const paused = t.state === "paused" || t.state === "error";
  const btn = li.querySelector('[data-act="toggle"]');
  if (btn.dataset.paused !== String(paused)) {
    btn.dataset.paused = String(paused);
    btn.innerHTML = paused ? ICON_PLAY : ICON_PAUSE;
    btn.title = paused ? "Reprendre" : "Mettre en pause";
    btn.setAttribute("aria-label", btn.title);
  }
  li.querySelector('[data-act="open"]').disabled = t.state === "metadata";
}

function escapeHtml(s) {
  return s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c]));
}

// ---------- Messages ----------

function toast(message, error = false) {
  const el = document.createElement("div");
  el.className = "toast" + (error ? " err" : "");
  el.textContent = message;
  $("toasts").append(el);
  setTimeout(() => el.remove(), error ? 5000 : 3000);
}

function onEvent(msg) {
  if (msg.event === "toast") toast(msg.message, !!msg.error);
  if (msg.event === "changed") refresh();
}

async function run(cmd, args, okMessage) {
  try {
    await host.call(cmd, args);
    if (okMessage) toast(okMessage);
    refresh();
    return true;
  } catch (e) {
    toast(e.message || String(e), true);
    return false;
  }
}

// ---------- Actions ----------

let refreshing = false;
async function refresh() {
  if (refreshing) return;
  refreshing = true;
  try { render(await host.call("state")); } catch (e) { console.error(e); }
  finally { refreshing = false; }
}

function looksLikeTorrentLink(s) {
  s = s.trim();
  return /^magnet:\?/i.test(s) || /^[0-9a-f]{40}$/i.test(s) || /^https?:\/\/\S+\.torrent(\?\S*)?$/i.test(s);
}

$("magnet-form").addEventListener("submit", async (e) => {
  e.preventDefault();
  const input = $("magnet");
  const uri = input.value.trim();
  if (!uri) { input.focus(); return; }
  if (!looksLikeTorrentLink(uri)) { toast("Ce n'est pas un lien magnet.", true); return; }
  const btn = e.submitter || e.target.querySelector("button");
  btn.disabled = true;
  const r = await run("addMagnet", { uri });
  btn.disabled = false;
  if (r) input.value = "";
});

$("pick").addEventListener("click", () => run("pickTorrent"));
$("change-dir").addEventListener("click", () => run("changeDir"));
$("open-dir").addEventListener("click", () => run("openDir"));

// Coller un lien magnet n'importe où dans la fenêtre l'ajoute directement.
document.addEventListener("paste", (e) => {
  if (e.target.closest && e.target.closest("input, textarea")) return;
  const text = (e.clipboardData && e.clipboardData.getData("text")) || "";
  if (looksLikeTorrentLink(text)) { e.preventDefault(); run("addMagnet", { uri: text.trim() }); }
});

list.addEventListener("click", async (e) => {
  const btn = e.target.closest("button[data-act]");
  if (!btn) return;
  const li = btn.closest(".item");
  const id = li.dataset.id;
  const act = btn.dataset.act;
  if (act === "toggle") {
    run(btn.dataset.paused === "true" ? "resume" : "pause", { id });
  } else if (act === "open") {
    run("open", { id });
  } else if (act === "remove") {
    const dlg = $("confirm");
    $("confirm-name").textContent = li.querySelector(".item__name").textContent;
    $("confirm-files").checked = false;
    dlg.returnValue = "";
    dlg.showModal();
    dlg.addEventListener("close", () => {
      if (dlg.returnValue === "ok") run("remove", { id, deleteFiles: $("confirm-files").checked });
    }, { once: true });
  }
});

// Glisser-déposer de fichiers .torrent (PC surtout).
let dragDepth = 0;
const hasFiles = (e) => e.dataTransfer && Array.from(e.dataTransfer.types || []).includes("Files");
window.addEventListener("dragenter", (e) => { if (!hasFiles(e)) return; e.preventDefault(); dragDepth++; $("drop").hidden = false; });
window.addEventListener("dragover", (e) => { if (hasFiles(e)) e.preventDefault(); });
window.addEventListener("dragleave", () => { if (--dragDepth <= 0) { dragDepth = 0; $("drop").hidden = true; } });
window.addEventListener("drop", async (e) => {
  if (!hasFiles(e)) return;
  e.preventDefault();
  dragDepth = 0;
  $("drop").hidden = true;
  const files = Array.from(e.dataTransfer.files).filter((f) => /\.torrent$/i.test(f.name));
  if (!files.length) { toast("Dépose un fichier .torrent.", true); return; }
  for (const f of files) {
    const data = await toBase64(f);
    await run("addTorrentFile", { name: f.name, data });
  }
});

function toBase64(file) {
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(String(r.result).split(",")[1] || "");
    r.onerror = () => reject(r.error);
    r.readAsDataURL(file);
  });
}

if (host.platform === "android") $("drop-hint").hidden = true;

refresh();
setInterval(refresh, 1000);

// ---------- Démo (navigateur sans appli) ----------

function demoHost() {
  const GB = 1024 ** 3, MB = 1024 ** 2;
  const phone = window.innerWidth < 600;
  const torrents = [
    { id: "a", name: "debian-13.1.0-amd64-netinst.iso", size: 754 * MB, done: 512 * MB, state: "downloading", down: 6.2 * MB, up: 140 * 1024, peers: 38 },
    { id: "b", name: "Big Buck Bunny (2008) 1080p", size: 2.1 * GB, done: 2.1 * GB, state: "seeding", down: 0, up: 820 * 1024, peers: 4 },
    { id: "c", name: "ubuntu-24.04.3-desktop-amd64.iso", size: 6.1 * GB, done: 1.4 * GB, state: "paused", down: 0, up: 0, peers: 0 },
  ];
  const tick = () => {
    for (const t of torrents) {
      if (t.state === "downloading") {
        t.down = Math.max(0.4 * MB, t.down + (Math.random() - 0.5) * MB);
        t.done = Math.min(t.size, t.done + t.down);
        if (t.done >= t.size) { t.state = "seeding"; t.down = 0; }
      }
      if (t.state === "metadata" && Math.random() < 0.3) Object.assign(t, { state: "downloading", size: 1.3 * GB, name: "linuxmint-22.2-cinnamon-64bit.iso", peers: 12, down: 2 * MB });
    }
  };
  setInterval(tick, 1000);
  const view = (t) => ({ ...t, progress: t.size ? t.done / t.size : 0, eta: t.down ? (t.size - t.done) / t.down : -1 });
  const find = (id) => torrents.find((t) => t.id === id);
  const cmds = {
    state: () => ({
      torrents: torrents.map(view),
      pending: [],
      downloadDir: phone ? "Stockage interne/Download/Averse" : "C:\\Users\\toi\\Downloads",
      canChangeDir: !phone,
    }),
    addMagnet: ({ uri }) => {
      const m = /xt=urn:btih:([0-9a-z]+)/i.exec(uri);
      const name = (/dn=([^&]+)/.exec(uri) || [])[1];
      torrents.push({ id: m ? m[1] : String(Date.now()), name: name ? decodeURIComponent(name.replace(/\+/g, " ")) : "Lien magnet", size: 0, done: 0, state: "metadata", down: 0, up: 0, peers: 3 });
    },
    pickTorrent: () => { throw new Error("Démo : ouvre l'appli Averse pour ajouter un fichier."); },
    addTorrentFile: ({ name }) => { torrents.push({ id: name, name: name.replace(/\.torrent$/i, ""), size: 900 * MB, done: 0, state: "downloading", down: 3 * MB, up: 0, peers: 9 }); },
    pause: ({ id }) => Object.assign(find(id), { state: "paused", down: 0, up: 0, peers: 0 }),
    resume: ({ id }) => { const t = find(id); Object.assign(t, t.done >= t.size ? { state: "seeding", up: 300 * 1024 } : { state: "downloading", down: 2 * MB, peers: 20 }); },
    remove: ({ id }) => { torrents.splice(torrents.indexOf(find(id)), 1); },
    open: () => { throw new Error("Démo : rien à ouvrir ici."); },
    openDir: () => { throw new Error("Démo : rien à ouvrir ici."); },
    changeDir: () => { throw new Error("Démo : rien à changer ici."); },
  };
  return {
    platform: "demo",
    async call(cmd, args = {}) { return cmds[cmd](args); },
  };
}
