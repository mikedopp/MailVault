"use strict";

// ---------- bridge ----------
let rpcId = 0;
const pending = new Map();
const listeners = {};

function rpc(cmd, args = {}) {
  return new Promise((resolve, reject) => {
    const id = ++rpcId;
    pending.set(id, { resolve, reject });
    host.postMessage({ id, cmd, args });
  });
}
function on(ev, fn) { (listeners[ev] ??= []).push(fn); }

const isHosted = !!window.chrome?.webview;
const host = isHosted
  ? {
      postMessage: (m) => window.chrome.webview.postMessage(m),
    }
  : mockHost(); // browser preview outside the app

if (isHosted) {
  window.chrome.webview.addEventListener("message", (e) => handleIncoming(e.data));
}

function handleIncoming(m) {
  if (m.ev) { (listeners[m.ev] || []).forEach((fn) => fn(m.data)); return; }
  const p = pending.get(m.id);
  if (!p) return;
  pending.delete(m.id);
  m.ok ? p.resolve(m.data) : p.reject(new Error(m.data));
}

// ---------- state ----------
const state = {
  view: "all",          // all | trash
  sort: "dateDesc",
  query: "",
  offset: 0,
  limit: 200,
  total: 0,
  rows: [],
  selected: new Set(),
  openId: null,
  folderOpen: false,
};

const $ = (id) => document.getElementById(id);
const fmtBytes = (n) => n < 1024 ? n + " B" : n < 1048576 ? (n / 1024).toFixed(1) + " KB"
  : n < 1073741824 ? (n / 1048576).toFixed(1) + " MB" : (n / 1073741824).toFixed(2) + " GB";
const fmtDate = (unix) => {
  if (!unix) return "";
  const d = new Date(unix * 1000), now = new Date();
  return d.getFullYear() === now.getFullYear()
    ? d.toLocaleDateString(undefined, { month: "short", day: "numeric" })
    : d.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
};
const esc = (s) => (s || "").replace(/[&<>"']/g, (c) =>
  ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

// ---------- search / list ----------
function filters() {
  const df = $("fDateFrom").value ? Date.parse($("fDateFrom").value) / 1000 : null;
  const dt = $("fDateTo").value ? Date.parse($("fDateTo").value) / 1000 + 86399 : null;
  return {
    query: state.query || null,
    from: $("fFrom").value || null,
    dateFrom: df, dateTo: dt,
    hasAttach: $("fHasAttach").checked ? true : null,
    deletedView: state.view === "trash",
    sort: state.sort,
    offset: state.offset,
    limit: state.limit,
  };
}

async function refresh(reset = true) {
  if (!state.folderOpen) return;
  if (reset) { state.offset = 0; state.selected.clear(); updateSelUi(); }
  const res = await rpc("search", filters());
  state.total = res.total;
  state.rows = reset ? res.rows : state.rows.concat(res.rows);
  renderList();
  $("resultmeta").textContent =
    `${state.total.toLocaleString()} message(s)` + (state.query ? ` matching “${state.query}”` : "");
  $("btnMore").classList.toggle("hidden", state.rows.length >= state.total);
}

function renderList() {
  const list = $("list");
  list.innerHTML = "";
  for (const r of state.rows) {
    const div = document.createElement("div");
    div.className = "msg" + (state.selected.has(r.id) ? " sel" : "") + (state.openId === r.id ? " open" : "");
    div.dataset.id = r.id;
    div.innerHTML = `
      <input type="checkbox" class="cb" ${state.selected.has(r.id) ? "checked" : ""}>
      <div class="from" title="${esc(r.sender)}">${esc(r.sender) || "(unknown sender)"}</div>
      <div class="date">${fmtDate(r.dateUtc)}</div>
      <div class="subj">${r.attachCount ? '<span class="clip">📎</span> ' : ""}${esc(r.subject) || "(no subject)"}</div>`;
    div.querySelector(".cb").addEventListener("click", (e) => {
      e.stopPropagation();
      e.target.checked ? state.selected.add(r.id) : state.selected.delete(r.id);
      div.classList.toggle("sel", e.target.checked);
      updateSelUi();
    });
    div.addEventListener("click", () => openMessage(r.id));
    list.appendChild(div);
  }
}

function updateSelUi() {
  const n = state.selected.size;
  $("selActions").classList.toggle("hidden", n === 0);
  $("selCount").textContent = n ? `${n} selected` : "";
  $("btnRestore").classList.toggle("hidden", state.view !== "trash");
  $("btnDelete").textContent = state.view === "trash" ? "Delete (already in trash)" : "Delete";
  $("btnDelete").disabled = state.view === "trash";
}

// ---------- reader ----------
async function openMessage(id) {
  state.openId = id;
  renderList();
  const m = await rpc("getMessage", { msgId: id });
  $("readerEmpty").classList.add("hidden");
  $("readerContent").classList.remove("hidden");
  $("rSubject").textContent = m.subject;
  $("rFrom").textContent = m.from;
  $("rTo").textContent = m.to;
  $("rCc").textContent = m.cc;
  $("rCcRow").style.display = m.cc ? "" : "none";
  $("rDate").textContent = m.date;

  const attDiv = $("rAttachments");
  attDiv.innerHTML = "";
  for (const a of m.attachments) {
    const chip = document.createElement("span");
    chip.className = "att";
    chip.innerHTML = `📎 ${esc(a.name)} <i>(${fmtBytes(a.size)})</i>
      <button title="Open">↗</button><button title="Save as…">💾</button>`;
    const [bOpen, bSave] = chip.querySelectorAll("button");
    bOpen.addEventListener("click", () => rpc("openAttachment", { msgId: id, index: a.index }));
    bSave.addEventListener("click", () => rpc("saveAttachment", { msgId: id, index: a.index }));
    attDiv.appendChild(chip);
  }

  const frame = $("rBody");
  if (m.html) {
    frame.classList.remove("textmode");
    frame.srcdoc = m.html; // sandbox="" on the iframe: no scripts, no navigation
  } else {
    frame.classList.add("textmode");
    frame.srcdoc = `<pre style="white-space:pre-wrap;font:13px/1.5 Consolas,monospace;color:#d8dade;background:#16171a;margin:12px">${esc(m.text || "(empty message)")}</pre>`;
  }
  $("btnOpenEml").onclick = () => rpc("openEml", { msgId: id });
}

// ---------- wire up ----------
$("btnOpen").addEventListener("click", async () => {
  const path = await rpc("pickFolder");
  if (!path) return;
  const stats = await rpc("openFolder", { path });
  state.folderOpen = true;
  showStats(stats);
  refresh();
});

$("btnReindex").addEventListener("click", () => state.folderOpen && rpc("reindex"));

// Called by the host when a folder was passed on the command line.
window.openFolderFromHost = async (path) => {
  const stats = await rpc("openFolder", { path });
  state.folderOpen = true;
  showStats(stats);
  refresh();
};

$("search").addEventListener("keydown", (e) => {
  if (e.key === "Enter") { state.query = e.target.value.trim(); refresh(); }
});
$("sort").addEventListener("change", (e) => { state.sort = e.target.value; refresh(); });
["fFrom", "fDateFrom", "fDateTo", "fHasAttach"].forEach((id) =>
  $(id).addEventListener("change", () => refresh()));
$("btnClearFilters").addEventListener("click", () => {
  $("fFrom").value = ""; $("fDateFrom").value = ""; $("fDateTo").value = ""; $("fHasAttach").checked = false;
  $("search").value = ""; state.query = "";
  refresh();
});

document.querySelectorAll(".navbtn").forEach((b) =>
  b.addEventListener("click", () => {
    document.querySelectorAll(".navbtn").forEach((x) => x.classList.remove("active"));
    b.classList.add("active");
    state.view = b.dataset.view;
    state.openId = null;
    $("readerContent").classList.add("hidden");
    $("readerEmpty").classList.remove("hidden");
    refresh();
  }));

$("btnDelete").addEventListener("click", async () => {
  if (!state.selected.size) return;
  await rpc("delete", { ids: [...state.selected] });
  await refreshStats();
  refresh();
});
$("btnRestore").addEventListener("click", async () => {
  if (!state.selected.size) return;
  await rpc("restore", { ids: [...state.selected] });
  await refreshStats();
  refresh();
});
$("btnPurge").addEventListener("click", async () => {
  const n = await rpc("purgeTrash");
  if (n >= 0) { await refreshStats(); refresh(); }
});
$("btnMore").addEventListener("click", () => { state.offset += state.limit; refresh(false); });

async function refreshStats() { if (state.folderOpen) showStats(await rpc("stats")); }
function showStats(s) {
  $("stats").textContent =
    `${s.root}\n${Number(s.count).toLocaleString()} messages · ${fmtBytes(s.bytes)}\n` +
    `${Number(s.withAttachments).toLocaleString()} with attachments`;
  $("trashCount").textContent = s.trashCount ? `(${s.trashCount})` : "";
}

on("indexProgress", ({ done, total }) => {
  const bar = $("indexbar");
  if (total === 0) { bar.classList.add("hidden"); return; }
  bar.classList.remove("hidden");
  $("indexfill").style.width = total ? (done / total) * 100 + "%" : "0";
  $("indextext").textContent = `Indexing ${done.toLocaleString()} / ${total.toLocaleString()}…`;
});
on("indexDone", async ({ indexed, removed }) => {
  $("indexbar").classList.add("hidden");
  await refreshStats();
  if (indexed || removed) refresh();
});
on("indexError", ({ message }) => {
  $("indextext").textContent = "Index error: " + message;
});

// ---------- mock host for browser preview (not the real app) ----------
function mockHost() {
  const now = Math.floor(Date.now() / 1000);
  const rows = [
    { id: 1, sender: "Google <no-reply@accounts.google.com>", subject: "Security alert", dateUtc: now - 3600, attachCount: 0, deleted: false },
    { id: 2, sender: "Alex <alex@example.com>", subject: "Photos from the weekend", dateUtc: now - 86400 * 2, attachCount: 3, deleted: false },
    { id: 3, sender: "Billing <billing@example.net>", subject: "Your March statement is ready", dateUtc: now - 86400 * 30, attachCount: 1, deleted: false },
  ];
  return {
    postMessage(m) {
      const reply = (data) => setTimeout(() => handleIncoming({ id: m.id, ok: true, data }), 30);
      switch (m.cmd) {
        case "pickFolder": return reply("D:\\GmailBackup\\you@example.com\\Gmail");
        case "openFolder": case "stats":
          return reply({ root: "D:\\GmailBackup\\you@example.com\\Gmail", count: 50922, bytes: 5530000000, trashCount: 0, trashBytes: 0, withAttachments: 8120 });
        case "search": return reply({ rows: rows.filter((r) => r.deleted === !!m.args.deletedView), total: 3 });
        case "getMessage": return reply({
          subject: rows.find((r) => r.id === m.args.msgId)?.subject, from: "Preview <preview@mailvault.local>",
          to: "you@example.com", cc: "", date: "Sat, Jul 18 2026 9:00 AM", html: null,
          text: "This is the browser preview with mock data.\nRun the real MailVault.exe to open an actual backup.",
          attachments: m.args.msgId === 2 ? [{ index: 0, name: "IMG_0001.jpg", contentType: "image/jpeg", size: 2400000 }] : [],
        });
        default: return reply(true);
      }
    },
  };
}
if (!isHosted) {
  state.folderOpen = true;
  showStats({ root: "(browser preview — mock data)", count: 50922, bytes: 5530000000, trashCount: 0, withAttachments: 8120 });
  refresh();
}
