"use strict";
// Greenlight page script. Served from /assets/app.js so the page can forbid inline scripts (CSP).

const jq = jQuery.noConflict(true);
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
// only http(s) links from data: a javascript: URL from a hostile server must never become a link
const url = u => /^https?:\/\//i.test(String(u ?? "")) ? esc(u) : "#";
const esc = s => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const pl = (k, one, many) => `${k} ${k === 1 ? one : (many || one + "s")}`;
const initials = s => (s || "?").split(/\s+/).filter(Boolean).slice(0, 2).map(p => p[0].toUpperCase()).join("");
const cap = s => (s || "").replace(/\b\w/g, c => c.toUpperCase());
const branch = r => (r || "").replace(/^origin\//, "");
const icon = (id, cls = "ico s") => `<svg class="${cls}" aria-hidden="true"><use href="#i-${id}"/></svg>`;
const MON = ["Jan","Feb","Mar","Apr","May","Jun","Jul","Aug","Sep","Oct","Nov","Dec"];
const day = d => { const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(d || ""); return m ? `${+m[3]} ${MON[+m[2] - 1]}` : esc(d); };
function ago(stamp) {
  const m = /^(\d{4})-(\d{2})-(\d{2}) (\d{2}):(\d{2})/.exec(stamp || "");
  if (!m) return "";
  const min = Math.round((Date.now() - new Date(+m[1], +m[2] - 1, +m[3], +m[4], +m[5])) / 6e4);
  return min < 1 ? "just now" : min < 60 ? `${min} min ago` : min < 1440 ? `${Math.round(min / 60)} h ago` : `${Math.round(min / 1440)} d ago`;
}

const STEPS = [
  { id: "code",   name: "Code",     means: "Every change is merged into the branch the service is built from." },
  { id: "tag",    name: "Tagged",   means: "QA deploys version tags, so the merged code has to be inside one." },
  { id: "db",     name: "Database", means: "Scripts are in a release folder, and that release has run on the QA database." },
  { id: "access", name: "Access",   means: "Role grants, scopes and menus exist on QA, so testers can reach the feature." },
  { id: "config", name: "Config",   means: "New settings are added to the QA configuration, which lives outside git." },
  { id: "qa",     name: "Handover", means: "The ticket is moved in Jira and the checklist is posted for the tester." },
];
const WORD = { done: "Done", todo: "Needs you", check: "To confirm", wait: "Waiting", skip: "Not needed", none: "Not checked" };

function mark(s, label = true) {
  const a = label ? `role="img" aria-label="${WORD[s] || s}"` : `aria-hidden="true"`;
  const w = inner => `<svg class="mark" viewBox="0 0 16 16" ${a}>${inner}</svg>`;
  if (s === "done")  return w(`<circle cx="8" cy="8" r="7" fill="var(--green)"/><path d="m5 8.3 2 2 4-4.4" fill="none" stroke="#0e1a13" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/>`);
  if (s === "todo")  return w(`<circle cx="8" cy="8" r="6.25" fill="none" stroke="var(--amber)" stroke-width="1.5"/><circle cx="8" cy="8" r="3" fill="var(--amber)"/>`);
  if (s === "check") return w(`<circle cx="8" cy="8" r="6.25" fill="none" stroke="var(--ink-3)" stroke-width="1.5"/><path d="M8 4.25a3.75 3.75 0 0 1 0 7.5z" fill="var(--ink-3)"/>`);
  if (s === "wait")  return w(`<circle cx="8" cy="8" r="6.25" fill="none" stroke="var(--ink-3)" stroke-width="1.5" stroke-dasharray="2.4 2.6"/>`);
  if (s === "skip")  return w(`<circle cx="8" cy="8" r="6.25" fill="none" stroke="var(--hair-2)" stroke-width="1.5"/><path d="M5.5 8h5" stroke="var(--ink-4)" stroke-width="1.5" stroke-linecap="round"/>`);
  return w(`<circle cx="8" cy="8" r="6.25" fill="none" stroke="var(--hair-2)" stroke-width="1.5"/>`);
}
const qbar = st => `<span class="qbar" aria-hidden="true">${(st || Array(6).fill("none")).map(s => `<i class="${s}"></i>`).join("")}</span>`;
const statesOf = b => b?.stations?.length === 6 ? STEPS.map(s => b.stations.find(x => x.id === s.id)?.state || "none") : null;
function overall(st) {
  if (!st) return "none";
  if (st.includes("todo")) return "todo";
  if (st.includes("wait")) return "wait";
  return st[5] === "done" ? "done" : "check";
}
const GROUP = { todo: "Needs you", wait: "Waiting", check: "Ready to hand over", none: "Not checked yet", done: "With QA" };

let viewsScroll = 0, evTicket = null, evRepos = [], evMine = true, fileQuery = "", groupOpen = {};
let status = null, tickets = null, selected = null, report = null, view = "all", openStep = null, tab = "repos", commitFilter = "", includeKids = true, checking = false, seq = 0;

// messages always have a close button; errors are marked and stay until closed
function toast(html, ms = 4200, error = false) {
  const t = $("#toast");
  t.classList.toggle("err", error);
  t.setAttribute("role", error ? "alert" : "status");
  t.innerHTML = `${error ? icon("alert") : ""}<span class="tmsg">${html}</span><button class="tx" aria-label="Close" title="Close">${icon("x")}</button>`;
  t.querySelector(".tx").onclick = () => { clearTimeout(toast.t); t.classList.remove("show"); };
  t.classList.add("show");
  clearTimeout(toast.t);
  if (!error && ms > 0) toast.t = setTimeout(() => t.classList.remove("show"), ms);
}
const fail = msg => toast(msg, 0, true);
async function api(path, opts = {}) {
  // the server only accepts changes that carry this header (another site can't add it without CORS)
  opts.headers = { "X-Ledger": "1", ...(opts.method && opts.method !== "GET" ? { "Content-Type": "application/json" } : {}) };
  let res;
  try { res = await fetch(path, opts); }
  catch { throw new Error("Can't reach Greenlight. It may be restarting; try again in a moment."); }
  const body = res.headers.get("content-type")?.includes("json") ? await res.json() : await res.text();
  if (!res.ok) throw new Error(body?.error || res.statusText);
  return body;
}

async function loadStatus() {
  try { status = await api("/api/status"); }
  catch { $("#whoSlot").innerHTML = `<span class="ink-3" style="padding:0 8px">Server isn't running</span>`; return; }
  const me = status.account;
  $("#whoSlot").innerHTML = me
    ? `<button class="who" id="whoBtn" aria-label="Account and settings"><span class="face">${esc(initials(me.name))}</span><span class="nm">${esc(cap(me.name.split(" ")[0]))}</span></button>`
    : `<button class="btn success" id="whoBtn">Connect Jira</button>`;
  $("#whoBtn").onclick = openSettings;
  $("#checkAll").hidden = !me;
}

function openSettings() {
  const me = status?.account, d = $("#settings");
  d.innerHTML = `<div class="dlg-in">
      <h2 id="dlgTitle">${me ? "Account" : "Connect your Jira account"}</h2>
      ${me ? `
        <div class="acct"><span class="face">${esc(initials(me.name))}</span><div style="flex:1;min-width:0">${esc(cap(me.name))}<small>${esc(me.email)} · ${esc(status.jiraBaseUrl.replace("https://", ""))}</small></div><button class="btn danger small" id="disc">Disconnect</button></div>
        <p class="intro" style="margin:0">Your API token is kept in ${esc(me.source)} on this computer. Handovers are posted under your name.</p>` : `
        <p class="intro">Greenlight lists the tickets assigned to you and posts handovers as you. The token is stored in Windows Credential Manager, never in the project.</p>
        ${status?.credentialsRejected ? `<p class="intro" style="color:var(--red);margin:0">The saved token was rejected. Enter a new one.</p>` : ""}
        <label class="fld"><span>Jira site</span><input id="fSite" value="${esc(status?.jiraBaseUrl || "")}" spellcheck="false"><span class="err" id="eSite"></span></label>
        <label class="fld"><span>Atlassian sign-in email</span><input id="fEmail" type="email" autocomplete="username" spellcheck="false"><small>Can differ from the email on your Jira profile.</small><span class="err" id="eEmail"></span></label>
        <label class="fld"><span>API token</span><input id="fToken" type="password" autocomplete="off"><small><a href="https://id.atlassian.com/manage-profile/security/api-tokens" target="_blank" rel="noopener">Create a token</a> while signed in as yourself.</small><span class="err" id="eToken"></span></label>`}
      <div class="rule"></div>
      ${gitlabSection()}
      <div class="rule"></div>
      <label class="fld"><span>Repositories folder</span><input id="fRoot" value="${esc(status?.reposRoot || "")}" spellcheck="false"><small>${status ? `${pl(status.repoCount, "repository", "repositories")} found. ` : ""}One git repository per subfolder.</small><span class="err" id="eRoot"></span></label>
    </div>
    <div class="dlg-f"><span class="msg" id="msg"></span>
      <button class="btn" id="cancel">Close</button>
      <button class="btn line" id="saveRoot">Save folder</button>
      ${me ? "" : `<button class="btn success" id="connect">Connect</button>`}
    </div>`;
  const msg = (h, bad) => { const m = $("#msg"); m.className = "msg" + (bad ? " bad" : ""); m.innerHTML = h; };
  $("#cancel").onclick = () => d.close();
  $("#saveRoot").onclick = async () => {
    $("#eRoot").textContent = "";
    if (!$("#fRoot").value.trim()) { $("#eRoot").textContent = "Enter a folder."; return; }
    try { const r = await api("/api/settings", { method: "POST", body: JSON.stringify({ reposRoot: $("#fRoot").value.trim() }) }); msg(`Saved. ${pl(r.repoCount, "repository", "repositories")} found.`); loadStatus(); }
    catch (e) { $("#eRoot").textContent = e.message; }
  };
  if (me) $("#disc").onclick = async () => { await api("/api/account/disconnect", { method: "POST" }); tickets = null; selected = null; report = null; d.close(); toast("Disconnected. The token was removed from this computer."); boot(); };
  else $("#connect").onclick = async () => {
    const site = $("#fSite").value.trim(), email = $("#fEmail").value.trim(), token = $("#fToken").value.trim();
    const errs = { Site: /^https:\/\/[^/]+/.test(site) ? "" : "Use the full address, starting with https://",
                   Email: /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email) ? "" : "Enter a valid email address.",
                   Token: !token ? "Paste your API token." : token.length < 40 ? "Too short for an API token. Make sure it isn't your password." : "" };
    for (const [k, v] of Object.entries(errs)) { $("#e" + k).textContent = v; $("#f" + k).classList.toggle("bad", !!v); }
    if (Object.values(errs).some(Boolean)) return;
    const b = $("#connect"); b.disabled = true; msg(`<span class="spinner"></span>Checking with Jira`);
    try { const who = await api("/api/account", { method: "POST", body: JSON.stringify({ baseUrl: site, email, token }) }); d.close(); toast(`Connected as ${esc(cap(who.name))}.`); tickets = null; boot(); }
    catch (e) { msg(esc(e.message), true); }
    finally { b.disabled = false; }
  };
  wireGitlab(msg);
  if (!d.open) d.showModal();
  setTimeout(() => ($("#fEmail") || $("#cancel")).focus(), 30);
}

// GitLab, for code review: a personal access token with the api scope (approving needs it)
function gitlabSection() {
  const g = status?.gitlab, base = status?.gitlabBaseUrl || "https://gitlab.com";
  const create = `${base}/-/user_settings/personal_access_tokens?name=Greenlight&scopes=api`;
  return g ? `<div class="fld"><span>GitLab · code review</span>
      <div class="acct"><span class="face">${esc(initials(g.name))}</span><div style="flex:1;min-width:0">${esc(g.name)}<small>@${esc(g.username)} · ${esc(base.replace("https://", ""))}</small></div><button class="btn danger small" id="glDisc">Disconnect</button></div></div>`
    : `<div class="fld"><span>GitLab · code review <span class="ink-3">(optional)</span></span>
      <small style="margin-top:-2px">Connect to see the merge requests you're asked to review, and the reviews on yours.</small>
      ${status?.gitlabRejected ? `<small style="color:var(--red)">The saved GitLab token stopped working. Enter a new one.</small>` : ""}
      <div class="glrow"><input id="glBase" value="${esc(base)}" spellcheck="false" aria-label="GitLab address"><input id="glToken" type="password" placeholder="Personal access token" autocomplete="off" aria-label="GitLab personal access token"><button class="btn success small" id="glConn">Connect</button></div>
      <small><a href="${url(create)}" target="_blank" rel="noopener">Create a token</a> with the <b>api</b> scope (needed to approve).</small><span class="err" id="glErr"></span></div>`;
}

function wireGitlab(msg) {
  $("#glDisc")?.addEventListener("click", async () => { await api("/api/gitlab/disconnect", { method: "POST" }); reviews = null; railMode = "mine"; await loadStatus(); openSettings(); renderList(); toast("GitLab disconnected. The token was removed from this computer."); });
  $("#glConn")?.addEventListener("click", async () => {
    const base = $("#glBase").value.trim(), token = $("#glToken").value.trim();
    $("#glErr").textContent = !/^https:\/\/[^/]+/.test(base) ? "Use the full address, starting with https://" : token.length < 20 ? "Paste your personal access token." : "";
    if ($("#glErr").textContent) return;
    $("#glConn").disabled = true; msg(`<span class="spinner"></span>Checking with GitLab`);
    try {
      const who = await api("/api/gitlab/account", { method: "POST", body: JSON.stringify({ baseUrl: base, token }) });
      await loadStatus(); openSettings(); msg(`Connected to GitLab as @${esc(who.username)}.`); renderList();
      api("/api/reviews").then(r => { reviews = r; renderRailMode(); }).catch(() => {});
    } catch (e) { msg(esc(e.message), true); $("#glConn").disabled = false; }
  });
}

async function loadTickets() {
  $("#list").innerHTML = Array(8).fill(`<div class="ghost-row"></div>`).join("");
  try { tickets = await api("/api/my-tickets"); } catch (e) { tickets = []; fail(esc(e.message)); }
  renderList();
}

/* shows a long list a page at a time: the next page arrives as the end scrolls into view */
function paged(box, items, size = 40, onAdd) {
  if (!box) return;
  box._pager?.disconnect();
  box.innerHTML = "";
  if (!items.length) return;
  let i = 0;
  const more = document.createElement("div");
  more.className = "more";
  const add = () => {
    const next = items.slice(i, i + size); i += next.length;
    more.insertAdjacentHTML("beforebegin", next.join(""));
    onAdd?.();
    if (i >= items.length) { box._pager?.disconnect(); more.remove(); }
    else more.innerHTML = `<span class="spinner"></span>${items.length - i} more`;
  };
  box.appendChild(more);
  add();
  if (i < items.length) {
    box._pager = new IntersectionObserver(es => { if (es.some(e => e.isIntersecting)) add(); }, { rootMargin: "600px 0px" });
    box._pager.observe(more);
  }
}

/* a gentle fade and lift for content that just changed */
function soften(el, delay = 0) {
  if (!el || matchMedia("(prefers-reduced-motion: reduce)").matches) return;
  el.animate([{ opacity: 0, transform: "translateY(5px)" }, { opacity: 1, transform: "none" }], { duration: 300, delay, easing: "cubic-bezier(.33, .8, .25, 1)", fill: "backwards" });
}

const emptyNote = (title, text, action = "") => `<div class="rail-empty">${icon("list", "ico big")}<b>${title}</b><span>${text}</span>${action}</div>`;

function renderList() {
  renderRailMode();
  if (railMode === "review") return renderReviewList();
  $("#railTitle").textContent = "My tickets";
  if (!status?.account) {
    $("#count").textContent = ""; $("#views").innerHTML = "";
    $("#list").innerHTML = emptyNote("Not connected", "Connect your Jira account to see the tickets assigned to you.", `<button class="btn success small" id="rc">Connect Jira</button>`);
    $("#rc").onclick = openSettings; return;
  }
  const all = tickets || [];
  const g = { todo: [], wait: [], check: [], none: [], done: [] };
  all.forEach(t => g[overall(statesOf(t.lastBuild))].push(t));
  const V = [["all", "All", all.length], ["todo", "Needs you", g.todo.length + g.wait.length], ["check", "Ready", g.check.length], ["done", "With QA", g.done.length], ["none", "New", g.none.length]];
  $("#count").textContent = all.length || "";
  viewsScroll = $("#views").scrollLeft;
  $("#views").innerHTML = V.map(([id, l, n]) => `<button aria-pressed="${view === id}" data-v="${id}">${l}<span class="c">${n}</span></button>`).join("");
  $$("#views [data-v]").forEach(b => {
    b.onclick = () => { if (view === b.dataset.v) return; view = b.dataset.v; renderList(); soften($("#list")); $(`#views [data-v="${view}"]`)?.focus({ preventScroll: true }); };
    b.onkeydown = e => {
      const d = { ArrowRight: 1, ArrowLeft: -1 }[e.key]; if (!d) return;
      e.preventDefault(); const all = $$("#views [data-v]"); all[(all.indexOf(b) + d + all.length) % all.length].click();
    };
  });
  $("#views").scrollLeft = viewsScroll;
  glide($("#views"), $("#views [aria-pressed=true]"));
  const order = view === "all" ? ["todo", "wait", "check", "none", "done"] : view === "todo" ? ["todo", "wait"] : [view];
  const secs = order.map(k => [k, g[k]]).filter(([, l]) => l.length);
  if (!secs.length) {
    const words = { todo: ["Nothing needs you", "No ticket is blocked right now."], check: ["Nothing ready yet", "Tickets with every step clear show up here."], done: ["Nothing with QA", "Tickets you've handed over show up here."], none: ["Everything is checked", "Each of your tickets has been checked at least once."] };
    const [t, x] = !all.length ? ["All clear", "Nothing open is assigned to you in Jira."] : words[view] || ["Nothing here", "No tickets in this view."];
    $("#list").innerHTML = emptyNote(t, x, all.length && view !== "all" ? `<button class="btn line small" id="showAll">Show all ${all.length}</button>` : "");
    $("#showAll")?.addEventListener("click", () => $(`#views [data-v="all"]`).click());
    return;
  }
  // one flat list of section labels and rows, paged
  paged($("#list"), secs.flatMap(([k, l]) => [...(view === "all" ? [`<div class="sec-l">${GROUP[k]}</div>`] : []), ...l.map(rowHtml)]), 30);
}
function rowHtml(t) {
  const st = statesOf(t.lastBuild);
  return `<button class="row ${t._busy ? "busy" : ""}" role="option" aria-selected="${t.key === selected}" data-key="${esc(t.key)}">
    ${mark(overall(st))}<span class="t">${esc(t.summary)}</span>
    <span class="m">${typeTag(t.type)}<span class="mono">${esc(t.key)}</span><span class="s">${jiraBadge(t.status, t.statusCategory)}</span>${qbar(st)}</span></button>`;
}

function move(dir) {
  const keys = $$("#list .row").map(b => b.dataset.key);
  if (!keys.length) return;
  const i = keys.indexOf(selected);
  const k = keys[i === -1 ? 0 : Math.max(0, Math.min(keys.length - 1, i + dir))];
  select(k); document.querySelector(`#list .row[data-key="${CSS.escape(k)}"]`)?.scrollIntoView({ block: "nearest" });
}

function select(key, fetchFirst = false) {
  key = (key || "").trim().toUpperCase();
  if (!key) return;
  if (location.hash.slice(1) !== key) history.pushState(null, "", "#" + key);
  load(key, fetchFirst);
}

// reads the check as it is sent: one JSON object per line
async function stream(url, onEvent) {
  const res = await fetch(url, { headers: { "X-Ledger": "1" } });
  if (!res.ok) { let m = res.statusText; try { m = (await res.json()).error || m; } catch { } throw new Error(m); }
  const reader = res.body.getReader(), dec = new TextDecoder();
  let buf = "";
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    buf += dec.decode(value, { stream: true });
    for (let i; (i = buf.indexOf("\n")) >= 0;) { const line = buf.slice(0, i); buf = buf.slice(i + 1); if (line.trim()) onEvent(JSON.parse(line)); }
  }
}

const headHtml = (key, k, ancestors = []) => `<div class="path">${ancestors.map(a => `<span>${esc(a.type)} <span class="mono">${esc(a.key)}</span></span><span class="sl">/</span>`).join("")}<span class="mono">${esc(key)}</span></div>
  ${k ? `<div class="title-row"><h2>${esc(k.summary)}</h2></div>
    <div class="facts"><span class="tag">${typeTag(k.type, true)}</span><span class="tag">${jiraBadge(k.status, k.statusCategory)}</span>${k.assignee ? `<span><b>${esc(cap(k.assignee))}</b></span>` : ""}</div>`
  : `<div class="line" style="width:64%;height:22px;margin-top:14px"></div><div class="line" style="width:30%"></div>`}`;

async function load(key, fetchFirst, inPlace = false, retrying = false) {
  if (key !== selected) { evTicket = "*"; evRepos = []; fileQuery = ""; groupOpen = {}; }
  selected = key; commitFilter = ""; openStep = null;
  $("#split").classList.add("open");
  $$("#list .row").forEach(b => b.setAttribute("aria-selected", b.dataset.key === key));
  document.title = `${key} · Greenlight`;
  const n = ++seq;
  // switching scope keeps the page and dims what is about to change, instead of blanking it;
  // a retry keeps the error on screen with a spinner on its button, so nothing flashes
  const started = Date.now();
  const settle = ms => new Promise(ok => setTimeout(ok, Math.max(0, ms - (Date.now() - started))));
  if (retrying) { const b = $("#retry"); if (b) { b.disabled = true; b.innerHTML = `<span class="spinner"></span>Trying again`; } }
  else if (inPlace && $(".page")) $(".page").classList.add("dim");
  else {
    // what we already know shows straight away; the rest fills in part by part as the check runs
    const k = tickets?.find(t => t.key === key) || (report?.issue?.key === key ? report.issue : null);
    $("#main").innerHTML = `<div class="loading">
      <button class="btn back" id="back">${icon("back")}My tickets</button>
      <div id="ldHead">${headHtml(key, k)}</div>
      <div class="ldprog"><div class="note" id="ldNote"><span class="spinner"></span>${fetchFirst ? "Fetching the latest from GitLab" : "Reading Jira"}</div><div class="ldbar"><i id="ldBar"></i></div></div>
      <h3 class="h3" style="margin-top:30px">Steps to QA</h3>
      <ol class="steps ldsteps" id="ldSteps">${STEPS.map((s, k2) => `<li class="stp" data-id="${s.id}"><div class="stp-h">${mark("none")}<span class="nm">${s.name}</span><span class="hl"><span class="line" style="width:${[60, 48, 55, 40, 52, 44][k2]}%;margin:0"></span></span><span class="ct"></span><span></span></div></li>`).join("")}</ol></div>`;
  }
  if ($("#back")) $("#back").onclick = back;
  const progress = (text, frac) => { const nt = $("#ldNote"), b = $("#ldBar"); if (nt) nt.innerHTML = `<span class="spinner"></span>${text}`; if (b && frac != null) b.style.width = `${Math.round(frac * 100)}%`; };
  try {
    let r = null;
    await stream(`/api/manifest-stream?key=${encodeURIComponent(key)}&fetch=${fetchFirst ? 1 : 0}&children=${includeKids ? 1 : 0}&mine=${reviewerMode ? 0 : 1}`, ev => {
      if (n !== seq) return;
      if (ev.type === "issue") {
        if ($("#ldHead") && ev.issue) { $("#ldHead").innerHTML = headHtml(key, ev.issue, ev.ancestors); soften($("#ldHead")); }
        progress(fetchFirst ? "Fetching the latest from GitLab" : "Reading your repositories", fetchFirst ? .02 : .1);
      } else if (ev.type === "progress") {
        // fetching is the slow half when it happens; reading local history the rest
        const f = ev.done / Math.max(1, ev.total);
        if (ev.phase === "fetch") progress(`Fetching ${ev.done} of ${ev.total} repositories`, .02 + f * .58);
        else progress(`Reading ${ev.done} of ${ev.total} repositories`, (fetchFirst ? .6 : .1) + f * (fetchFirst ? .25 : .65));
      } else if (ev.type === "station") {
        const s = ev.station, row = $(`#ldSteps [data-id="${s.id}"]`);
        if (row) {
          const k2 = STEPS.findIndex(x => x.id === s.id);
          row.innerHTML = `<div class="stp-h">${mark(s.state)}<span class="nm">${STEPS[k2].name}</span><span class="hl">${esc(s.headline)}</span><span class="ct ${s.state === "todo" ? "need" : ""}">${WORD[s.state]}</span><span></span></div>`;
          soften(row);
          progress("Working out what QA needs", .85 + (k2 + 1) / STEPS.length * .15);
        }
      } else if (ev.type === "done") r = ev.report;
      else if (ev.type === "error") throw new Error(ev.message);
    });
    if (n !== seq) return;
    if (!r) throw new Error("The check stopped before it finished.");
    if (retrying) await settle(500);
    r.fetched = fetchFirst; report = r;
    const t = tickets?.find(x => x.key === key);
    if (t) { t.lastBuild = { generated: r.generated, stations: r.stations }; renderList(); }
    const st = r.stations.map(s => s.state);
    openStep = r.stations[["todo", "wait", "check"].map(x => st.indexOf(x)).find(i => i !== -1) ?? 5].id;
    renderTicket();
    soften($(".page .body"), 40); soften($(".page .ev"), 90);
  } catch (e) {
    if (n !== seq) return;
    if (retrying) await settle(600);
    const disk = /disk was full|not enough space/i.test(e.message);
    $("#main").innerHTML = `<div class="page"><button class="btn back" id="back">${icon("back")}My tickets</button>
      <div class="void fail-c">${icon("alert", "ico big")}
        <h3>Couldn't check ${esc(key)}</h3>
        <p>${disk ? "Your disk is full, so the result couldn't be saved. Free some space on drive C:, then try again." : "Something went wrong while checking this ticket."}</p>
        <div class="void-acts"><button class="btn solid" id="retry">${icon("refresh")}Try again</button></div>
        <details class="why"><summary>Technical details</summary><code>${esc(e.message)}</code></details>
      </div></div>`;
    $("#back").onclick = back; $("#retry").onclick = () => load(key, fetchFirst, false, true);
  }
}
function back() { history.pushState(null, "", location.pathname); selected = null; report = null; reviewing = null; reviewerMode = false; $("#split").classList.remove("open"); renderList(); blank(); }

function blank() {
  document.title = "Greenlight";
  const me = status?.account;
  $("#main").innerHTML = `<div class="blank"><div class="blank-in">
    ${icon("list", "ico")}
    ${me ? `<h2>Pick a ticket</h2><p>Greenlight shows what stands between it and QA: code, tags, database, access, config and the handover.</p>
      <div class="keys"><span><kbd>J</kbd><kbd>K</kbd></span><span>Next and previous ticket</span><span><kbd>Ctrl K</kbd></span><span>Find a ticket</span><span><kbd>R</kbd></span><span>Fetch and check again</span></div>`
    : `<h2>Set up Greenlight</h2><p>Two steps, once on this computer.</p>
      <ol class="setup">
        <li><b>Connect your Jira account</b><div><p>Lists your tickets and posts handovers under your name.</p><button class="btn success small" id="s1">Connect Jira</button></div></li>
        <li class="${status?.repoCount ? "ok" : ""}"><b>Repositories folder</b><div><p>${status?.repoCount ? `${pl(status.repoCount, "repository", "repositories")} found in <code>${esc(status.reposRoot)}</code>` : "Point to the folder with your local clones."}</p><button class="btn line small" id="s2">${status?.repoCount ? "Change" : "Choose folder"}</button></div></li>
      </ol>`}
  </div></div>`;
  $("#s1")?.addEventListener("click", openSettings);
  $("#s2")?.addEventListener("click", openSettings);
}

function renderTicket() {
  const r = report, i = r.issue, me = status?.account;
  const st = r.stations.map(s => s.state), ov = overall(st), nothing = r.repos.length === 0;
  const todo = r.stations.reduce((n, s) => n + s.items.filter(x => x.state === "todo").length, 0);
  const confirm = r.stations.reduce((n, s) => n + s.items.filter(x => x.state === "check").length, 0);
  const at = st.findIndex(s => s === "todo" || s === "wait");
  const kids = r.scope.length - 1;
  const v = nothing ? ["none", "No trace in git yet", `Nothing in your repositories mentions ${esc(r.key)}${kids ? ` or your ${pl(kids, "sub-ticket")}` : ""}.`]
    : ov === "todo" ? ["todo", `Stopped at ${STEPS[at].name.toLowerCase()}`, `${pl(todo, "thing needs", "things need")} doing before QA can test this${confirm ? `, and ${pl(confirm, "item")} to confirm` : ""}.`]
    : ov === "wait" ? ["wait", `Waiting at ${STEPS[at].name.toLowerCase()}`, "An earlier step has to finish first."]
    : ov === "done" ? ["done", "With QA", confirm ? `Handed over. ${pl(confirm, "item")} still worth a look.` : "Handed over, and every step is done."]
    : ["check", "Ready to hand over", `Nothing blocks QA. Confirm ${pl(confirm, "item")}, then post the handover.`];

  $("#main").innerHTML = `<div class="page">
    <button class="btn back" id="back">${icon("back")}My tickets</button>
    <div class="path">${r.ancestors.map(a => `<button class="ln" data-go="${esc(a.key)}">${esc(a.type)} <span class="mono">${esc(a.key)}</span></button><span class="sl">/</span>`).join("")}<a class="mono ln" href="${url(r.jiraUrl)}" target="_blank" rel="noopener" title="Open in Jira">${esc(r.key)}</a></div>
    <div class="title-row">
      <h2>${esc(i?.summary || r.key)}</h2>
      <div class="acts">
        <button class="btn" id="recheck" title="Fetch every repository, then check again">${icon("refresh")}Check again</button>
        <button class="btn solid" id="post" ${me && !nothing ? "" : "disabled"} title="${me ? "Posts the checklist to the Jira ticket, replacing your previous one" : "Connect Jira first"}">Post handover</button>
        <div class="pop-wrap"><button class="btn only" id="more" aria-haspopup="menu" aria-expanded="false" aria-label="More">${icon("more", "ico")}</button>
          <div class="pop" id="pop" role="menu" hidden>
            <button role="menuitem" id="copy">${icon("copy")}Copy checklist</button>
            <a role="menuitem" href="/api/markdown?key=${encodeURIComponent(r.key)}&download=1">${icon("down")}Download as Markdown</a>
            <hr><a role="menuitem" href="${url(r.jiraUrl)}" target="_blank" rel="noopener">${icon("out")}Open in Jira</a>
          </div></div>
      </div>
    </div>
    <div class="facts">
      ${i ? `<span class="tag">${typeTag(i.type, true)}</span><span class="tag">${jiraBadge(i.status, i.statusCategory)}</span><span><b>${esc(cap(i.assignee))}</b></span>${i.fixVersions !== "none" ? `<span>Fix version <b>${esc(i.fixVersions)}</b></span>` : ""}`
          : me ? "" : `<span><button id="hc" style="color:var(--ink-2);border-bottom:1px solid var(--hair-2)">Connect Jira</button> for title, status and sub-tickets</span>`}
    </div>
    ${me ? subControl(r) : ""}

    ${nothing ? voidHtml(r, kids) : `<div class="state">
      <div class="say">${mark(v[0])}${v[1]}</div>
      <div class="sub">${v[2]}</div>
      <div class="fresh">Checked ${esc(ago(r.generated))}<br>${r.fetched ? "with the latest from the server" : `from your local copies · <button id="fetchNow">fetch latest</button>`}</div>
    </div>`}
    ${nothing ? "" : `<div class="meter" aria-hidden="true">${STEPS.map((s, k) => `<div class="${k === at ? "here" : ""}"><i class="${st[k]}"></i><span>${s.name}</span></div>`).join("")}</div>

    <div class="body">
      <section>
        <h3 class="h3">Steps to QA<span>${todo ? pl(todo, "action") : "No actions"}${confirm ? ` · ${confirm} to confirm` : ""}</span></h3>
        <ol class="steps">${r.stations.map((s, k) => stepHtml(s, k)).join("")}</ol>
      </section>
      <aside class="side">
        <div id="timeBox" class="timebox"></div>
        <div><h3 class="h3">Your footprint<span id="figsFor"></span></h3><div class="figs" id="figs"></div></div>
        ${kids ? `<div><h3 class="h3">Your tickets in scope</h3><ul class="scope">${r.scope.map(t => `<li><button data-go="${esc(t.key)}"><span class="k"><span class="kk">${typeTag(t.type)}<span class="mono ln">${esc(t.key)}</span>${jiraBadge(t.status, t.statusCategory)}</span><span>${t.commits ? `${t.merged}/${t.commits} merged` : "no code"}</span></span><span class="s">${esc(t.summary)}</span></button></li>`).join("")}</ul></div>` : ""}
      </aside>
    </div>

    <section class="ev">
      <div class="evbar">
        ${r.scope.length > 1 ? `<select id="evTicket" aria-label="Ticket">
          <option value="*" ${evTicket === "*" ? "selected" : ""}>All ${r.scope.length} of your tickets here</option>
          ${r.scope.map(t => `<option value="${esc(t.key)}" ${t.key === evTicket ? "selected" : ""}>${esc(t.key)}${t.key === r.key ? " (this ticket)" : ""} · ${esc(t.summary)}</option>`).join("")}</select>` : ""}
        <select id="evRepos" multiple aria-label="Repositories"></select>
        <label class="tgl"><input type="checkbox" id="evMine" ${evMine ? "checked" : ""}><span class="knob"></span>Only my commits</label>
      </div>
      <div class="tabs" role="tablist" id="evTabs"></div>
      <div class="pane" id="pane"></div>
    </section>`}
    ${status?.gitlab ? `<section class="crv" id="crv"></section>` : ""}
  </div>`;
  wireTicket();
  if (!nothing) initEvidence();
  if (me) loadTicketTime(r.key);
  if (status?.gitlab) loadTicketReviews(r.key);
}

/* service / repository names: one stable colour per name, so a service looks the same everywhere */
function repoTag(name) {
  let h = 7; for (const c of name || "") h = (h * 31 + c.charCodeAt(0)) >>> 0;
  return `<span class="rt" style="--h:${h % 360}" title="${esc(name)}">${esc(name)}</span>`;
}

/* Jira status as a lozenge, coloured by Jira's own category: to do grey, in progress blue, done green */
const jiraBadge = (name, cat) => `<span class="jb ${cat === "done" ? "done" : cat === "indeterminate" ? "prog" : "todo"}" title="Jira status">${esc(name)}</span>`;

/* issue type, drawn like Jira's type icons */
function typeTag(type, withName = false) {
  const t = (type || "").toLowerCase();
  const k = t.includes("epic") ? "epic" : t.includes("bug") ? "bug" : t.includes("sub") ? "sub" : t.includes("story") ? "story" : "task";
  const glyph = {
    story: `<path d="M5 3.5h6v9l-3-2.2-3 2.2z" fill="#fff"/>`,
    task:  `<path d="m4.8 8.2 2.2 2.2 4.2-4.6" fill="none" stroke="#fff" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"/>`,
    bug:   `<circle cx="8" cy="8" r="3.2" fill="#fff"/>`,
    epic:  `<path d="M8.9 2.8 4.8 9h3l-.7 4.2L11.2 7h-3z" fill="#fff"/>`,
    sub:   `<rect x="4" y="4" width="4.5" height="4.5" rx=".8" fill="none" stroke="#fff" stroke-width="1.3"/><rect x="7.5" y="7.5" width="4.5" height="4.5" rx=".8" fill="#fff"/>`,
  }[k];
  const svg = `<svg class="ty ${k}" viewBox="0 0 16 16" ${withName ? 'aria-hidden="true"' : `role="img" aria-label="${esc(type)}"`}><rect width="16" height="16" rx="3.5"/>${glyph}</svg>`;
  return withName ? `<span class="tyn">${svg}${esc(type)}</span>` : svg;
}

/* the sub-ticket control adapts to what Jira actually has under this ticket */
function subControl(r) {
  const s = r.subTickets;
  if (!s || !s.possible) return "";
  const others = s.total - s.mine;
  if (s.total === 0) return `<div class="subs"><span class="subs-note">${icon("list")}No sub-tickets under this ${esc((r.issue?.type || "ticket").toLowerCase())}</span></div>`;
  if (s.mine === 0) return `<div class="subs"><span class="subs-note">${icon("list")}${pl(s.total, "sub-ticket")}, none assigned to you. Only this ticket is checked.</span></div>`;
  return `<div class="subs">
    <div class="seg" role="radiogroup" aria-label="What to check">
      <button role="radio" aria-checked="${!includeKids}" data-kids="0">This ticket only</button>
      <button role="radio" aria-checked="${includeKids}" data-kids="1">With my ${pl(s.mine, "sub-ticket")}</button>
    </div>
    <span class="subs-note">${includeKids ? `Checking ${pl(s.mine + 1, "ticket")} together.` : `Your ${pl(s.mine, "sub-ticket")} ${s.mine === 1 ? "is" : "are"} left out.`}${others ? ` ${pl(others, "sub-ticket")} by others ${others === 1 ? "is" : "are"} never counted.` : ""}</span>
  </div>`;
}

/* nothing in git yet: say why, and give the one command that fixes it */
function voidHtml(r, kids) {
  const slug = (r.issue?.summary || "").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").split("-").slice(0, 6).join("-");
  const cmd = `git switch -c ${r.key}${slug ? "-" + slug : ""}`;
  return `<div class="void">
    <svg class="void-art" viewBox="0 0 120 64" aria-hidden="true">
      <path d="M8 32h104" stroke="var(--hair-2)" stroke-width="1.5" stroke-dasharray="3 5" stroke-linecap="round"/>
      ${[16, 36, 56, 76, 96].map(x => `<circle cx="${x}" cy="32" r="5.5" fill="var(--canvas)" stroke="var(--hair-2)" stroke-width="1.5"/>`).join("")}
      <circle cx="16" cy="32" r="5.5" fill="var(--canvas)" stroke="var(--ink-3)" stroke-width="1.5" stroke-dasharray="2.4 2.6"/>
    </svg>
    <h3>No work found for ${esc(r.key)} yet</h3>
    <p>None of your ${pl(status?.repoCount ?? 0, "repository", "repositories")} has a commit, branch or merge request that mentions this ticket${kids ? ` or your ${pl(kids, "sub-ticket")}` : ""}${r.fetched ? "" : ", based on your local copies"}.</p>
    <div class="void-acts">
      ${r.fetched ? "" : `<button class="btn solid" id="voidFetch">${icon("refresh")}Fetch latest and check again</button>`}
      ${!includeKids && status?.account ? `<button class="btn line" id="voidKids">Include my sub-tickets</button>` : ""}
    </div>
    <div class="void-start">
      <span>Starting now? Create the branch with the ticket key so Greenlight can follow it:</span>
      <div class="cmd"><code>${esc(cmd)}</code><button class="btn only small" id="copyCmd" title="Copy command" aria-label="Copy command" data-cmd="${esc(cmd)}">${icon("copy")}</button></div>
    </div>
  </div>`;
}

function stepHtml(s, k) {
  const open = s.id === openStep;
  const rank = ["todo", "wait", "check", "done", "skip"];
  const items = [...s.items].sort((a, b) => rank.indexOf(a.state) - rank.indexOf(b.state));
  const nTodo = s.items.filter(x => x.state === "todo").length, nCheck = s.items.filter(x => x.state === "check").length;
  const ct = nTodo ? `<span class="ct need">${pl(nTodo, "action")}</span>` : nCheck ? `<span class="ct">${nCheck} to confirm</span>` : `<span class="ct">${WORD[s.state]}</span>`;
  return `<li class="stp" data-open="${open}" data-id="${s.id}">
    <button class="stp-h" aria-expanded="${open}" aria-controls="sb-${s.id}">${mark(s.state)}<span class="nm">${STEPS[k].name}</span><span class="hl">${esc(s.headline)}</span>${ct}${icon("chev", "ico chev")}</button>
    <div class="stp-b" id="sb-${s.id}" ${open ? "" : "hidden"}>
      <p class="means">${esc(STEPS[k].means)}</p>
      ${items.length ? `<ul class="its">${items.map(it => `<li class="it"><div class="lead"><span class="dot ${it.state}"></span>${it.repo ? `<span class="rp">${repoTag(it.repo)}</span>` : ""}<span class="tx">${esc(it.title)}</span>${it.detail ? `<span class="dt">${esc(it.detail)}</span>` : ""}</div>
        ${it.actionUrl === "post:" ? `<button class="btn solid small" data-post>${icon("send")}${esc(it.actionLabel)}</button>`
          : it.actionUrl ? `<a class="btn line small" href="${url(it.actionUrl)}" target="_blank" rel="noopener">${esc(it.actionLabel || "Open")}${icon("out")}</a>` : ""}</li>`).join("")}</ul>`
        : `<p class="empty-step">${esc(s.headline)}.</p>`}
    </div></li>`;
}

/* ── evidence: what *you* changed for the chosen ticket, by repository ── */
const MINE = () => new Set((report?.myEmails || []).map(e => e.toLowerCase()));
const byDate = (a, b) => (a.date || "").localeCompare(b.date || "");

// commits that pass the ticket and author filters; the repository filter is applied separately
function evScoped() {
  const mine = MINE();
  return report.repos.map(x => ({ ...x, commits: x.commits.filter(c => (evTicket === "*" || c.ticket === evTicket) && (!evMine || mine.has((c.email || "").toLowerCase()))).sort(byDate) }))
    .filter(x => x.commits.length);
}
const evShown = () => evScoped().filter(x => !evRepos.length || evRepos.includes(x.name));
const keyHit = name => evTicket === "*" ? report.scope.some(t => (name || "").toUpperCase().includes(t.key)) : (name || "").toUpperCase().includes(evTicket);

// one entry per path: final status across the commits, and which commits touched it (oldest first)
function evFiles(x) {
  const m = new Map();
  for (const c of x.commits) for (const f of c.files || []) {
    const prev = m.get(f.p);
    const st = prev && prev.s === "A" && f.s !== "D" ? "A" : f.s === "R" ? "A" : f.s;
    m.set(f.p, { p: f.p, s: st, shas: [...(prev?.shas || []), c.sha] });
  }
  return [...m.values()].sort((a, b) => a.p.localeCompare(b.p));
}

function initEvidence() {
  const repos = evScoped();
  evRepos = evRepos.filter(n => repos.some(x => x.name === n));
  const el = jq("#evRepos");
  if (el.data("select2")) el.select2("destroy");
  el.off(".ev").empty();
  // "All repositories" is a real option: chosen by default, and it replaces / is replaced by specific repositories
  const total = repos.reduce((n, x) => n + x.commits.length, 0);
  el.append(new Option("All repositories", "*", false, !evRepos.length));
  repos.forEach(x => el.append(new Option(x.name, x.name, false, evRepos.includes(x.name))));
  el.select2({
    width: "360px", closeOnSelect: false, dropdownParent: jq(".ev"),
    templateResult: o => o.id === "*" ? jq(`<span class="s2-repo"><b class="s2-all">All repositories</b><span class="ink-3">${pl(total, "commit")}</span></span>`)
      : o.id ? jq(`<span class="s2-repo">${repoTag(o.id)}<span class="ink-3">${pl(repos.find(x => x.name === o.id)?.commits.length || 0, "commit")}</span></span>`) : o.text,
    templateSelection: o => o.id === "*" ? jq(`<span class="s2-all">All repositories</span>`) : o.id ? jq(repoTag(o.id)) : o.text,
  });
  const apply = vals => { el.val(vals).trigger("change.select2"); evRepos = vals.filter(v => v !== "*"); renderEvidence(); soften($("#pane")); soften($("#figs")); };
  el.on("select2:select.ev", e => {
    const vals = el.val() || [];
    apply(e.params.data.id === "*" ? ["*"] : vals.filter(v => v !== "*"));
  });
  el.on("select2:unselect.ev", () => { const vals = el.val() || []; apply(vals.length ? vals : ["*"]); });
  if ($("#evTicket")) jq("#evTicket").select2({ width: "300px", minimumResultsForSearch: 8, dropdownParent: jq(".ev") })
    .on("change", e => { evTicket = e.target.value; initEvidence(); soften($("#pane")); soften($("#figs")); });
  $("#evMine").onchange = e => { evMine = e.target.checked; initEvidence(); soften($("#pane")); soften($("#figs")); };
  renderEvidence();
}

function renderEvidence() {
  const all = evScoped(), shown = evShown();
  const files = shown.reduce((n, x) => n + evFiles(x).length, 0), commits = shown.reduce((n, x) => n + x.commits.length, 0);
  // footprint follows the ticket and author filters
  const mrs = all.reduce((n, x) => n + x.merges.filter(m => keyHit(m.branch)).length, 0);
  const cAll = all.flatMap(x => x.commits);
  $("#figsFor").textContent = evTicket === "*" ? "all your tickets here" : evTicket;
  $("#figs").innerHTML = `
    <div class="fig"><div class="v">${all.length}</div><div class="l">Repositories</div></div>
    <div class="fig"><div class="v">${mrs}</div><div class="l">Merge requests</div></div>
    <div class="fig"><div class="v">${cAll.filter(c => c.inDefault).length}<small> / ${cAll.length}</small></div><div class="l">Commits merged</div></div>
    <div class="fig"><div class="v">${all.reduce((n, x) => n + evFiles(x).length, 0)}</div><div class="l">Files changed</div></div>`;
  const tabs = [["repos", "Repositories", shown.length], ["files", "Files", files], ["commits", "Commits", commits], ...(report.foreign.length ? [["foreign", "Other tickets", report.foreign.length]] : [])];
  $("#evTabs").innerHTML = tabs.map(([id, l, c]) => `<button role="tab" aria-selected="${tab === id}" data-tab="${id}">${l}<span class="c">${c}</span></button>`).join("");
  $$("#evTabs [data-tab]").forEach(b => {
    b.onclick = () => { tab = b.dataset.tab; $$("#evTabs [data-tab]").forEach(x => x.setAttribute("aria-selected", x === b)); glide($("#evTabs"), b); renderPane(); soften($("#pane")); };
    b.onkeydown = e => { const k = { ArrowRight: 1, ArrowLeft: -1 }[e.key]; if (!k) return; e.preventDefault(); const t = $$("#evTabs [data-tab]"); const nx = t[(t.indexOf(b) + k + t.length) % t.length]; nx.focus(); nx.click(); };
  });
  renderPane();
}

const openBtns = (repo, path, sln) => `<span class="acts-i">
  <button class="btn only small" data-open="code" data-repo="${esc(repo)}" ${path ? `data-path="${esc(path)}"` : ""} title="Open in VS Code" aria-label="Open in VS Code">${icon("vscode")}</button>
  ${path || sln ? `<button class="btn only small" data-open="vs" data-repo="${esc(repo)}" ${path ? `data-path="${esc(path)}"` : ""} title="Open in Visual Studio${sln && !path ? ` (${esc(sln)})` : ""}" aria-label="Open in Visual Studio">${icon("vs")}</button>` : `<span class="slot" aria-hidden="true"></span>`}
  <button class="btn only small" data-open="folder" data-repo="${esc(repo)}" ${path ? `data-path="${esc(path)}"` : ""} title="Show in folder" aria-label="Show in folder">${icon("folder")}</button></span>`;

function groupHtml(x, count, body, id) {
  const open = groupOpen[id] !== false;
  return `<section class="gbox" data-open="${open}" data-gid="${esc(id)}">
    <div class="gbox-h"><button class="gtoggle" aria-expanded="${open}">${icon("chev", "ico chev")}${repoTag(x.name)}<span class="ink-3">${count}</span></button>${openBtns(x.name, null, x.sln)}</div>
    <div class="gbox-b" ${open ? "" : "hidden"}>${body}</div></section>`;
}

const fileRow = (x, f) => `<li><span class="op ${esc(f.s)}" title="${({ A: "Added", M: "Modified", D: "Deleted", R: "Renamed" })[f.s] || ""}">${esc(f.s)}</span>
  <button class="fpath" data-preview="${esc(x.name)}" data-path="${esc(f.p)}" data-shas="${esc(f.shas.join(","))}" title="Preview the changes">${esc(f.p)}</button>
  <span class="facts-i"><button class="btn only small" data-preview="${esc(x.name)}" data-path="${esc(f.p)}" data-shas="${esc(f.shas.join(","))}" title="Preview the changes" aria-label="Preview the changes">${icon("eye")}</button>${f.s === "D" ? "" : openBtns(x.name, f.p, x.sln)}</span></li>`;

const tagCell = t => !t ? `<span class="ink-3">—</span>`
  : t.containing?.length ? `<a class="tagpill ln" href="${url(t.containing[0].url || "#")}" target="_blank" rel="noopener" title="First tag with this work, ${esc(t.containing[0].date)}">${esc(t.containing[0].name)}</a>${t.containing.length > 1 ? `<span class="ink-3"> +${t.containing.length - 1}</span>` : ""}`
  : `<span class="ink-3">not tagged</span>${t.next ? ` <span class="tagpill next" title="Suggested next version${t.latest ? `; latest is ${esc(t.latest.name)}` : ""}">${esc(t.next)}</span>` : ""}`;

function renderPane() {
  const p = $("#pane"), shown = evShown();
  // offer the one change that would actually show something
  const anyByOthers = evMine && report.repos.some(x => x.commits.some(c => evTicket === "*" || c.ticket === evTicket));
  const subs = report.scope.length > 1 && evTicket !== "*";
  const empty = what => `<div class="pane-empty">${icon("list", "ico")}
    <b>${subs && !anyByOthers ? `${esc(evTicket)} has no commits of its own` : `No ${what} here`}</b>
    <span>${subs && !anyByOthers ? "The work is in its sub-tickets." : evRepos.length ? "Nothing in the selected repositories." : anyByOthers ? "Only your commits are counted, and others made these changes." : "Nothing matches the filters."}</span>
    ${subs && !anyByOthers ? `<button class="btn line small" id="peAll">Show all ${report.scope.length} of your tickets</button>`
      : evRepos.length ? `<button class="btn line small" id="peRepos">Show all repositories</button>`
      : anyByOthers ? `<button class="btn line small" id="peMine">Include everyone's commits</button>` : ""}</div>`;
  const wireEmpty = () => {
    $("#peMine")?.addEventListener("click", () => { evMine = false; $("#evMine").checked = false; initEvidence(); });
    $("#peAll")?.addEventListener("click", () => { evTicket = "*"; jq("#evTicket").val("*").trigger("change.select2"); initEvidence(); });
    $("#peRepos")?.addEventListener("click", () => { evRepos = []; jq("#evRepos").val(["*"]).trigger("change.select2"); renderEvidence(); });
  };
  if (tab === "repos") {
    if (!shown.length) { p.innerHTML = empty("repositories"); return wireEmpty(); }
    p.innerHTML = `<div class="wide"><table class="tb"><thead><tr><th>Repository</th><th>Branch</th><th>Merge requests</th><th>Ticket branches</th><th>Tag</th><th class="r num">Merged</th><th class="r act"><span class="sr">Open</span></th></tr></thead><tbody id="repoRows"></tbody></table></div>`;
    paged($("#repoRows"), shown.map(x => {
      const merges = x.merges.filter(m => keyHit(m.branch)), branches = x.branches.filter(b => keyHit(b.name));
      return `<tr><td>${x.webUrl ? `<a class="ln" href="${url(x.webUrl)}" target="_blank" rel="noopener">${repoTag(x.name)}</a>` : repoTag(x.name)}</td><td><code>${esc(branch(x.defaultRef))}</code></td>
        <td>${merges.map(m => m.url ? `<a class="ln" href="${url(m.url)}" target="_blank" rel="noopener">${esc(m.label)}</a> <span class="ink-3">${day(m.date)}</span>` : `<span class="ink-3">Direct merge, ${day(m.date)}</span>`).join("<br>") || `<span class="ink-3">None</span>`}</td>
        <td>${branches.map(b => `<code>${esc(b.name)}</code>${b.unmerged ? ` <span style="color:var(--amber)">${b.unmerged} unmerged</span>` : ""}`).join("<br>") || `<span class="ink-3">None</span>`}</td>
        <td class="nw">${tagCell(x.tags)}</td>
        <td class="r num"><span class="cnt"><b>${x.commits.filter(c => c.inDefault).length}</b><span class="ink-3"> of ${x.commits.length}</span></span></td><td class="r act">${openBtns(x.name, null, x.sln)}</td></tr>`;
    }), 25);
  } else if (tab === "files") {
    if (!shown.length) { p.innerHTML = empty("files"); return wireEmpty(); }
    p.innerHTML = `<div class="tool"><input class="filter" id="ff" placeholder="Filter by path${evRepos.length === 1 ? ` in ${esc(evRepos[0])}` : ""}" aria-label="Filter files" value="${esc(fileQuery)}"><button class="btn small" id="xAll" hidden>Collapse all</button></div><div id="fl"></div>`;
    const draw = () => {
      const q = fileQuery.toLowerCase();
      const groups = shown.map(x => ({ x, fs: evFiles(x).filter(f => !q || f.p.toLowerCase().includes(q)) })).filter(g => g.fs.length);
      const many = groups.length > 1;
      $("#xAll").hidden = !many;
      if (!groups.length) { $("#fl").innerHTML = `<div class="pane-empty"><b>No files match “${esc(fileQuery)}”</b></div>`; return; }
      $("#fl").innerHTML = groups.map(g => many ? groupHtml(g.x, pl(g.fs.length, "file"), `<ul class="files" data-fill="${esc(g.x.name)}"></ul>`, "f:" + g.x.name) : `<ul class="files" data-fill="${esc(g.x.name)}"></ul>`).join("");
      groups.forEach(g => paged($(`#fl ul[data-fill="${CSS.escape(g.x.name)}"]`), g.fs.map(f => fileRow(g.x, f)), 60));
      syncAll();
    };
    $("#ff").oninput = e => { fileQuery = e.target.value; clearTimeout(draw.t); draw.t = setTimeout(() => { draw(); soften($("#fl")); }, 120); };
    draw();
  } else if (tab === "commits") {
    if (!shown.length) { p.innerHTML = empty("commits"); return wireEmpty(); }
    const many = shown.length > 1, multi = evTicket === "*";
    p.innerHTML = `${many ? `<div class="tool"><button class="btn small" id="xAll">Collapse all</button></div>` : ""}
      ${shown.map(x => { const body = `<div data-fill="${esc(x.name)}"></div>`; return many ? groupHtml(x, pl(x.commits.length, "commit"), body, "c:" + x.name) : body; }).join("")}`;
    shown.forEach(x => paged(p.querySelector(`[data-fill="${CSS.escape(x.name)}"]`), x.commits.map(c => cmHtml(c, x.defaultRef, multi && c.ticket ? `${esc(c.ticket)} · ` : "")), 40));
  } else {
    p.innerHTML = `<p class="ink-3" style="margin:0 0 12px">Commits from tickets outside this scope that sit on these branches. They don't count toward any step.</p><div id="fx"></div>`;
    paged($("#fx"), report.foreign.map(c => cmHtml(c, null, `${esc(c.otherKey)} · ${repoTag(c.repo)} `)), 40);
  }
  wirePane();
}

// collapse / expand all, kept in step with the groups
function syncAll() {
  const xAll = $("#xAll"); if (!xAll) return;
  xAll.textContent = $$("#pane .gbox").some(g => g.dataset.open === "true") ? "Collapse all" : "Expand all";
}
function wirePane() {
  const xAll = $("#xAll");
  if (!xAll) return;
  syncAll();
  xAll.onclick = () => {
    const open = xAll.textContent === "Expand all";
    $$("#pane .gbox").forEach(g => { if ((g.dataset.open === "true") !== open) toggleGroup(g, open); });
    syncAll();
  };
}

function toggleGroup(g, open = g.dataset.open !== "true") {
  g.dataset.open = open;
  g.querySelector(".gtoggle")?.setAttribute("aria-expanded", open);
  if (g.dataset.gid) groupOpen[g.dataset.gid] = open;
  const body = g.querySelector(".gbox-b");
  // review files build their diff the first time they open
  if (open && g.dataset.path && !body.dataset.built && reviewing?.files) { body.innerHTML = reviewFileBody(g.dataset.path); body.dataset.built = "1"; }
  slide(body, open);
}

// one listener for everything clickable in the main area, so rows added later by paging work too
function mainClicks(e) {
  const t = e.target;
  const tog = t.closest(".gtoggle");
  if (tog) { toggleGroup(tog.closest(".gbox")); syncAll(); return; }
  // only the open buttons: steps and groups also carry data-open (their expanded state), which must not count
  const op = t.closest("button[data-open][data-repo]");
  if (op && !op.closest("#pop") && !op.closest(".dpage")) { openLocal(op.dataset.repo, op.dataset.open, op.dataset.path); return; }
  const pv = t.closest("[data-preview]");
  if (pv) { preview(pv.dataset.preview, pv.dataset.path, pv.dataset.shas.split(",")); return; }
  const ol = t.closest("[data-open-line]");
  if (ol) { openLocal(ol.dataset.repo, "code", ol.dataset.openLine, +ol.dataset.line); return; }
  const rw = t.closest(".gbox[data-path] .lnb");
  if (rw && reviewing?.local) openLocal(reviewing.repo, "code", rw.closest(".gbox").dataset.path, +rw.dataset.line);
}
// slides a horizontal pill row so the chosen pill is fully in view, with an ease-in-out glide
function glide(box, el) {
  if (!box || !el) return;
  const pad = 18, from = box.scrollLeft, left = el.getBoundingClientRect().left - box.getBoundingClientRect().left + from, right = left + el.offsetWidth;
  let to = from;
  if (left - pad < from) to = left - pad;
  else if (right + pad > from + box.clientWidth) to = right + pad - box.clientWidth;
  to = Math.max(0, Math.min(to, box.scrollWidth - box.clientWidth));
  if (Math.abs(to - from) < 1) return;
  if (matchMedia("(prefers-reduced-motion: reduce)").matches) { box.scrollLeft = to; return; }
  const t0 = performance.now(), dur = 460, ease = t => t < .5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
  cancelAnimationFrame(box._glide);
  const step = now => { const t = Math.min(1, (now - t0) / dur); box.scrollLeft = from + (to - from) * ease(t); if (t < 1) box._glide = requestAnimationFrame(step); };
  box._glide = requestAnimationFrame(step);
}

// smooth, unhurried expand and collapse, used everywhere something folds
function slide(el, open) {
  if (!el) return;
  if (matchMedia("(prefers-reduced-motion: reduce)").matches) { el.hidden = !open; return; }
  el.getAnimations().forEach(a => a.cancel());
  if (open) {
    el.hidden = false;
    const h = el.scrollHeight;
    el.animate([{ height: "0px", opacity: 0 }, { height: h + "px", opacity: 1 }], { duration: 380, easing: "cubic-bezier(.33, .8, .25, 1)" });
  } else {
    const h = el.scrollHeight;
    const a = el.animate([{ height: h + "px", opacity: 1 }, { height: "0px", opacity: 0 }], { duration: 320, easing: "cubic-bezier(.45, 0, .3, 1)" });
    a.onfinish = () => { el.hidden = true; };
  }
}

const APP = { code: "VS Code", vs: "Visual Studio", folder: "File Explorer" };
async function openLocal(repo, app, path, line) {
  try { await api("/api/open", { method: "POST", body: JSON.stringify({ repo, app, path: path || null, line: line || 1 }) }); toast(`Opening ${esc(path ? path.split("/").pop() : repo)}${line ? ` at line ${line}` : ""} in ${APP[app]}.`); }
  catch (e) { fail(esc(e.message)); }
}

/* change preview: its own page, side by side like VS Code's diff editor */
let diffState = null;   // { repo, path, shas, data, mode }

function preview(repo, path, shas) {
  diffState = { repo, path, shas, data: null, mode: "net", scroll: $("#main").scrollTop };
  history.pushState(null, "", `#${report.key}/diff`);
  renderDiffPage();
}

function leaveDiff() {
  const y = diffState?.scroll || 0;
  diffState = null;
  history.pushState(null, "", `#${report.key}`);
  renderTicket();
  $("#main").scrollTop = y;
}

async function renderDiffPage() {
  const d = diffState; if (!d) return;
  const name = d.path.split("/").pop(), dir = d.path.slice(0, d.path.length - name.length);
  $("#main").innerHTML = `<div class="dpage">
    <div class="dp-h">
      <button class="btn" id="dpBack">${icon("back")}${esc(report.key)}</button>
      <div class="dp-t"><div class="dp-file">${repoTag(d.repo)}<span class="mono"><span class="ink-3">${esc(dir)}</span>${esc(name)}</span></div>
        <div class="dp-sub ink-3">${pl(d.shas.length, "commit")} of yours changed this file</div></div>
      <div class="seg" role="radiogroup" aria-label="View">
        <button role="radio" aria-checked="${d.mode === "net"}" data-mode="net">Combined</button>
        <button role="radio" aria-checked="${d.mode === "each"}" data-mode="each">By commit</button>
      </div>
      <div class="dp-a"><button class="btn line small" id="ddCode"><span class="i-code">${icon("vscode")}</span>Open diff in VS Code</button>${openBtns(d.repo, d.path, null)}</div>
    </div>
    <div class="dp-b" id="dpBody">${d.data ? diffBody(d) : `<div class="s2-msg" style="padding:24px"><span class="spinner"></span>Loading the changes</div>`}</div>
  </div>`;
  document.title = `${name} · ${report.key} · Greenlight`;
  $("#dpBack").onclick = leaveDiff;
  $$(".dpage [data-mode]").forEach(b => b.onclick = () => { d.mode = b.dataset.mode; $$(".dpage [data-mode]").forEach(x => x.setAttribute("aria-checked", x === b)); $("#dpBody").innerHTML = diffBody(d); });
  $("#ddCode").onclick = async () => { try { await api("/api/open-diff", { method: "POST", body: JSON.stringify({ repo: d.repo, path: d.path, shas: d.shas }) }); toast("Opening the comparison in VS Code."); } catch (e) { fail(esc(e.message)); } };
  $$(".dpage button[data-open][data-repo]").forEach(b => b.onclick = () => openLocal(b.dataset.repo, b.dataset.open, b.dataset.path));
  $("#dpBody").onclick = e => { const b = e.target.closest(".lnb"); if (b) openLocal(d.repo, "code", d.path, +b.dataset.line); };
  if (!d.data) {
    try { d.data = await api("/api/diff", { method: "POST", body: JSON.stringify({ repo: d.repo, path: d.path, shas: d.shas }) }); }
    catch (e) {
      if (diffState !== d) return;
      $("#dpBody").innerHTML = `<div class="void fail-c">${icon("alert", "ico big")}<h3>Couldn't load the changes</h3>
        <p>The comparison for ${esc(d.path.split("/").pop())} didn't come back.</p>
        <div class="void-acts"><button class="btn solid" id="dpRetry">${icon("refresh")}Try again</button></div>
        <details class="why"><summary>Technical details</summary><code>${esc(e.message)}</code></details></div>`;
      $("#dpRetry").onclick = async () => {
        const b = $("#dpRetry"); b.disabled = true; b.innerHTML = `<span class="spinner"></span>Trying again`;
        const t0 = Date.now();
        try { d.data = await api("/api/diff", { method: "POST", body: JSON.stringify({ repo: d.repo, path: d.path, shas: d.shas }) }); }
        catch (e2) { await new Promise(ok => setTimeout(ok, Math.max(0, 600 - (Date.now() - t0)))); b.disabled = false; b.innerHTML = `${icon("refresh")}Try again`; $(".dpage .why code").textContent = e2.message; return; }
        if (diffState === d) { $("#dpBody").innerHTML = diffBody(d); soften($("#dpBody")); }
      };
      return;
    }
    // commits that cancel out (e.g. a change and its revert) have nothing to combine: show them one by one
    if (!(d.data.net || "").trim() && d.mode === "net") { d.mode = "each"; $$(".dpage [data-mode]").forEach(x => x.setAttribute("aria-checked", x.dataset.mode === "each")); }
    if (diffState === d) $("#dpBody").innerHTML = diffBody(d);
  }
}

function diffBody(d) {
  const head = `<div class="sbs-cols"><span>Before</span><span>After</span></div>`;
  if (d.mode === "net" && !(d.data.net || "").trim())
    return `<div class="pane-empty">${icon("list", "ico")}<b>No overall change</b><span>Your commits cancel each other out: the file ends exactly as it started. See them under “By commit”.</span></div>`;
  if (d.mode === "net")
    return `<p class="dp-note ink-3">From before your first change to after your last${d.shas.length > 1 ? `, across ${d.shas.length} commits` : ""}.</p>${head}${sideBySide(d.data.net)}`;
  return d.data.commits.map(c => `<section class="dd-c"><div class="dd-ch"><span class="mono ink-2">${esc(c.short)}</span><span class="dd-s">${esc(c.subject)}</span><span class="ink-3">${esc(c.author)} · ${day(c.date)}</span></div>${head}${sideBySide(c.patch)}</section>`).join("");
}

// unified patch -> rows of [oldNo, oldText, kindLeft, newNo, newText, kindRight]; changed lines are paired on one row
function sideBySide(patch) {
  const rows = [];
  let o = 0, n = 0, dels = [], adds = [];
  // a line number opens the file in VS Code at that line
  const no = (k, side) => `<td class="no"><button class="lnb" data-line="${k}" data-side="${side}" title="Open in VS Code at line ${k}">${k}</button></td>`;
  const none = `<td class="no"></td>`;
  const flush = () => {
    for (let k = 0; k < Math.max(dels.length, adds.length); k++) {
      const a = dels[k], b = adds[k];
      rows.push(`<tr>${a ? `${no(a[0], "before")}<td class="tx del">${esc(a[1]) || " "}</td>` : `${none}<td class="tx gap"></td>`}${b ? `${no(b[0], "after")}<td class="tx add">${esc(b[1]) || " "}</td>` : `${none}<td class="tx gap"></td>`}</tr>`);
    }
    dels = []; adds = [];
  };
  for (const line of (patch || "").replace(/\r/g, "").split("\n")) {
    if (/^(diff --git|index |--- |\+\+\+ |new file mode|deleted file mode|similarity index|rename (from|to)|old mode|new mode|Binary files)/.test(line)) continue;
    const h = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@(.*)$/.exec(line);
    if (h) { flush(); o = +h[1]; n = +h[2]; rows.push(`<tr class="hunk"><td colspan="4">${esc(h[3].trim() ? `@@ ${h[3].trim()}` : `Lines ${o} → ${n}`)}</td></tr>`); continue; }
    if (line.startsWith("\\")) continue;
    if (line.startsWith("-")) dels.push([o++, line.slice(1)]);
    else if (line.startsWith("+")) adds.push([n++, line.slice(1)]);
    else if (line.startsWith(" ")) { flush(); rows.push(`<tr>${no(o++, "before")}<td class="tx">${esc(line.slice(1)) || " "}</td>${no(n++, "after")}<td class="tx">${esc(line.slice(1)) || " "}</td></tr>`); }
  }
  flush();
  return rows.length ? `<div class="sbs"><table class="sbs-t"><colgroup><col class="c-no"><col><col class="c-no"><col></colgroup>${rows.join("")}</table></div>`
    : `<div class="pane-empty" style="padding:18px"><span>No text changes (a binary file, or only renamed).</span></div>`;
}const cmHtml = (c, ref, pre = "") => `<div class="cm"><a class="h mono ${c.url ? "ln" : ""}" ${c.url ? `href="${url(c.url)}" target="_blank" rel="noopener"` : ""}>${esc(c.short)}</a><span>${esc(c.subject)}</span>
  <span class="m">${pre}${esc(c.author)} · ${day(c.date)} · ${c.inDefault ? "merged" : `<span style="color:var(--amber)">not in ${esc(branch(ref) || "main")}</span>`}</span>${c.note ? `<span class="nt">${esc(c.note)}</span>` : ""}</div>`;

function wireTicket() {
  const r = report;
  $("#back").onclick = back;
  $("#hc")?.addEventListener("click", openSettings);
  $$(".seg [data-kids]").forEach(b => b.onclick = () => {
    const want = b.dataset.kids === "1";
    if (want === includeKids) return;
    includeKids = want;
    $$(".seg [data-kids]").forEach(x => x.setAttribute("aria-checked", x === b));
    b.insertAdjacentHTML("afterbegin", `<span class="spinner"></span>`);
    load(r.key, false, true);
  });
  $("#recheck").onclick = () => load(r.key, true);
  $("#fetchNow")?.addEventListener("click", () => load(r.key, true));
  $("#voidFetch")?.addEventListener("click", () => load(r.key, true));
  $("#voidKids")?.addEventListener("click", () => { includeKids = true; load(r.key, false); });
  $("#copyCmd")?.addEventListener("click", async e => { try { await navigator.clipboard.writeText(e.currentTarget.dataset.cmd); toast("Command copied."); } catch { fail("Couldn't copy."); } });
  $$("[data-go]").forEach(b => b.onclick = () => select(b.dataset.go));
  $("#post").onclick = post; $$("[data-post]").forEach(b => b.onclick = post);
  const more = $("#more"), pop = $("#pop");
  more.onclick = e => { e.stopPropagation(); pop.hidden = !pop.hidden; more.setAttribute("aria-expanded", !pop.hidden); if (!pop.hidden) document.addEventListener("click", () => { pop.hidden = true; more.setAttribute("aria-expanded", "false"); }, { once: true }); };
  $("#copy").onclick = async () => { try { await navigator.clipboard.writeText(await api(`/api/markdown?key=${encodeURIComponent(r.key)}`)); toast("Checklist copied."); } catch (e) { fail(`Couldn't copy: ${esc(e.message)}`); } };
  $$(".stp-h").forEach(h => h.onclick = () => {
    const s = h.closest(".stp"), open = s.dataset.open !== "true";
    s.dataset.open = open; h.setAttribute("aria-expanded", open); slide(s.querySelector(".stp-b"), open); if (open) openStep = s.dataset.id;
  });
}

async function post() {
  const bs = $$("#post, [data-post]");
  bs.forEach(b => { b.disabled = true; b.dataset.l = b.innerHTML; b.innerHTML = `<span class="spinner"></span>Posting`; });
  try { const res = await api(`/api/post?key=${encodeURIComponent(report.key)}`, { method: "POST" }); toast(`Handover posted to ${esc(report.key)}.<a href="${url(res.url)}" target="_blank" rel="noopener">View in Jira</a>`, 6500); }
  catch (e) { fail(/^Jira /.test(e.message) ? esc(e.message) : `Couldn't post the handover: ${esc(e.message)}`); }
  finally { bs.forEach(b => { b.disabled = false; b.innerHTML = b.dataset.l; }); }
}

async function checkAll() {
  if (checking || !tickets?.length) return;
  checking = true; $("#checkAll").disabled = true;
  for (let k = 0; k < tickets.length; k++) {
    const t = tickets[k]; t._busy = true; renderList();
    $("#progress").innerHTML = `<span class="spinner"></span>${k === 0 ? "Fetching, then checking" : "Checking"} ${k + 1} of ${tickets.length}<span class="pg-t">${typeTag(t.type)}<span class="mono">${esc(t.key)}</span>${jiraBadge(t.status, t.statusCategory)}</span>`;
    try {
      const r = await api(`/api/manifest?key=${encodeURIComponent(t.key)}&fetch=${k === 0 ? 1 : 0}&children=1&mine=1`);
      t.lastBuild = { generated: r.generated, stations: r.stations };
      if (t.key === selected) { r.fetched = true; report = r; renderTicket(); }
    } catch { }
    t._busy = false;
  }
  checking = false; $("#checkAll").disabled = false; $("#progress").textContent = "";
  renderList(); toast(`All ${pl(tickets.length, "ticket")} checked.`);
}

/* ── find: a Select2 async select over your tickets ──
   Empty or one letter: your open tickets, filtered locally. Two letters or more: also asks Jira (done and older tickets too). */
/* ── time: my day against the target, and what's logged on a ticket ── */
let week = null;
const hm = s => { s = Math.round((s || 0) / 60); const h = Math.floor(s / 60), m = s % 60; return h && m ? `${h}h ${m}m` : h ? `${h}h` : `${m}m`; };

async function loadWeek() {
  if (!status?.account) { $("#dayWrap").hidden = true; return; }
  try { week = await api("/api/time/week"); renderDayMeter(); } catch { $("#dayWrap").hidden = true; }
}

function renderDayMeter() {
  const today = week.days.find(d => d.date === week.today);
  if (!today) return;
  const target = today.target || week.target, done = today.seconds, pct = target ? Math.min(1, done / target) : 1, left = Math.max(0, target - done);
  const r = 7, c = 2 * Math.PI * r;
  $("#dayWrap").hidden = false;
  $("#dayBtn").className = `daymeter ${!today.workday ? "off" : pct >= 1 ? "met" : pct >= .6 ? "near" : "low"}`;
  $("#dayBtn").innerHTML = `<svg viewBox="0 0 18 18" aria-hidden="true"><circle cx="9" cy="9" r="${r}" class="trk"/><circle cx="9" cy="9" r="${r}" class="val" style="stroke-dasharray:${c};stroke-dashoffset:${c * (1 - pct)}"/></svg>
    <span><b>${hm(done)}</b><span class="ink-3"> / ${hm(target)}</span></span>`;
  $("#dayBtn").setAttribute("aria-label", `Logged today: ${hm(done)} of ${hm(target)}${left ? `, ${hm(left)} to go` : ""}`);
  $("#dayBtn").onclick = e => {
    e.stopPropagation();
    const pop = $("#weekPop"), open = pop.hidden;
    pop.hidden = !open; $("#dayBtn").setAttribute("aria-expanded", open);
    if (open) { renderWeek(); soften(pop); document.addEventListener("click", function close(ev) { if (!pop.contains(ev.target)) { pop.hidden = true; $("#dayBtn").setAttribute("aria-expanded", "false"); document.removeEventListener("click", close); } }); }
  };
}

function renderWeek() {
  const max = Math.max(week.target, ...week.days.map(d => d.seconds));
  const work = week.days.filter(d => d.workday), done = work.reduce((n, d) => n + d.seconds, 0), goal = work.reduce((n, d) => n + d.target, 0);
  const today = week.days.find(d => d.date === week.today);
  const missing = week.days.filter(d => d.workday && d.date < week.today && d.seconds < d.target);
  $("#weekPop").innerHTML = `<div class="wk-h"><b>This week</b><span class="ink-3">${hm(done)} of ${hm(goal)}</span></div>
    ${today?.workday ? `<p class="wk-note">${today.seconds >= today.target ? "Today's target is met." : `${hm(today.target - today.seconds)} still to log today.`}${missing.length ? ` ${pl(missing.length, "earlier day")} short.` : ""}</p>` : ""}
    <div class="wk-bars">${week.days.map(d => `<div class="wk-d ${d.date === week.today ? "today" : ""} ${d.workday ? "" : "off"}" title="${esc(d.date)}: ${hm(d.seconds)}${d.workday ? ` of ${hm(d.target)}` : ""}">
      <div class="wk-col"><i class="tgt" style="bottom:${(d.target / max) * 100}%"></i><i class="bar ${d.seconds >= d.target && d.workday ? "met" : d.seconds ? "" : "none"}" style="height:${Math.max(2, (d.seconds / max) * 100)}%"></i></div>
      <span>${d.weekday}</span><span class="ink-3">${d.seconds ? hm(d.seconds) : "–"}</span></div>`).join("")}</div>
    <div class="wk-list">${week.days.filter(d => d.tickets.length).slice().reverse().map(d => `<div class="wk-day"><span class="ink-3">${d.weekday} ${day(d.date)}</span>${d.tickets.map(t => `<button class="wk-t" data-k="${esc(t.key)}"><span class="mono">${esc(t.key)}</span><span class="s">${esc(t.summary)}</span><b>${hm(t.seconds)}</b></button>`).join("")}</div>`).join("") || `<p class="ink-3" style="margin:6px 0 0">Nothing logged yet this week.</p>`}</div>`;
  $$("#weekPop [data-k]").forEach(b => b.onclick = () => { $("#weekPop").hidden = true; select(b.dataset.k); });
}

async function loadTicketTime(key) {
  const box = $("#timeBox"); if (!box) return;
  box.innerHTML = `<h3 class="h3">Time</h3><div class="line" style="height:8px;width:100%"></div>`;
  try {
    const t = await api(`/api/time?key=${encodeURIComponent(key)}`);
    if (report?.key !== key || !$("#timeBox")) return;
    renderTimeBox(key, t);
  } catch (e) { box.innerHTML = `<h3 class="h3">Time</h3><p class="ink-3" style="margin:0;font-size:12.5px">${esc(e.message)}</p>`; }
}

function renderTimeBox(key, t) {
  const est = t.estimate || 0, spent = t.spent || 0, rem = t.remaining ?? Math.max(0, est - spent);
  const total = Math.max(est, spent + rem, 1), over = est && spent > est;
  const mine = t.entries.filter(e => e.mine);
  $("#timeBox").innerHTML = `<h3 class="h3">Time<span>${est ? `estimate ${hm(est)}` : "no estimate"}</span></h3>
    <div class="tbar ${over ? "over" : ""}"><i class="mine" style="width:${(t.mine / total) * 100}%"></i><i class="others" style="width:${((spent - t.mine) / total) * 100}%"></i></div>
    <div class="tnums"><span><b>${hm(spent)}</b> logged${spent !== t.mine ? `, ${hm(t.mine)} by you` : ""}</span><span class="ink-3">${over ? `${hm(spent - est)} over` : `${hm(rem)} left`}</span></div>
    <button class="btn line small tlog" id="logBtn">${icon("checkall")}Log time</button>
    ${mine.length ? `<ul class="tlist" id="tList"></ul>` : `<p class="ink-3" style="margin:8px 0 0;font-size:12.5px">You haven't logged time on ${esc(key)} yet.</p>`}`;
  if (mine.length) paged($("#tList"), mine.map(e => `<li><span>${day(e.date)}</span><b>${hm(e.seconds)}</b><span class="c ink-3" title="${esc(e.comment)}">${esc(e.comment || "")}</span></li>`), 8);
  $("#logBtn").onclick = () => logDialog(key);
  soften($("#timeBox"));
}

function logDialog(key) {
  const d = $("#settings"), today = new Date().toISOString().slice(0, 10);
  const left = week?.days.find(x => x.date === week.today);
  const gap = left ? Math.max(0, left.target - left.seconds) : 0;
  d.innerHTML = `<div class="dlg-in">
    <h2>Log time on ${esc(key)}</h2>
    <p class="intro">Added to Jira as a worklog under your name.${gap ? ` You have ${hm(gap)} left to reach today's target.` : ""}</p>
    <div class="fld"><span>How long</span>
      <div class="quick">${[15, 30, 60, 120, 240].map(m => `<button class="chip" data-m="${m}">${hm(m * 60)}</button>`).join("")}${gap ? `<button class="chip" data-m="${Math.round(gap / 60)}">Rest of today (${hm(gap)})</button>` : ""}</div>
      <div class="hm"><input id="lgH" type="number" min="0" max="24" value="1" aria-label="Hours"><span>h</span><input id="lgM" type="number" min="0" max="59" step="5" value="0" aria-label="Minutes"><span>m</span></div>
      <span class="err" id="lgErr"></span></div>
    <label class="fld"><span>Day</span><input id="lgD" type="date" value="${today}" max="${today}"></label>
    <label class="fld"><span>What you did <span class="ink-3">(optional)</span></span><textarea id="lgC" rows="3"></textarea></label>
  </div>
  <div class="dlg-f"><span class="msg" id="lgMsg"></span><button class="btn" id="lgCancel">Cancel</button><button class="btn success" id="lgSave">Log time</button></div>`;
  $$("#settings .quick [data-m]").forEach(b => b.onclick = () => { const m = +b.dataset.m; $("#lgH").value = Math.floor(m / 60); $("#lgM").value = m % 60; $$("#settings .chip").forEach(x => x.classList.toggle("on", x === b)); });
  $("#lgCancel").onclick = () => d.close();
  $("#lgSave").onclick = async () => {
    const minutes = (+$("#lgH").value || 0) * 60 + (+$("#lgM").value || 0);
    if (minutes < 1) { $("#lgErr").textContent = "Enter how long you worked."; return; }
    if (minutes > 24 * 60) { $("#lgErr").textContent = "That's more than a day."; return; }
    $("#lgSave").disabled = true; $("#lgMsg").innerHTML = `<span class="spinner"></span>Saving to Jira`;
    try {
      await api("/api/time/log", { method: "POST", body: JSON.stringify({ key, minutes, date: $("#lgD").value, comment: $("#lgC").value }) });
      d.close(); toast(`Logged ${hm(minutes * 60)} on ${esc(key)}.`);
      loadTicketTime(key); loadWeek();
    } catch (e) { $("#lgMsg").className = "msg bad"; $("#lgMsg").textContent = e.message; $("#lgSave").disabled = false; }
  };
  d.showModal();
}

/* the developer's view of code review: every merge request of the ticket, its reviewers and their comments */
async function loadTicketReviews(key) {
  const box = $("#crv"); if (!box) return;
  box.innerHTML = `<h3 class="h3">Code review</h3><div class="s2-msg"><span class="spinner"></span>Reading merge requests from GitLab</div>`;
  try {
    const list = await api(`/api/ticket-reviews?key=${encodeURIComponent(key)}`);
    if (report?.key !== key || !$("#crv")) return;
    const open = list.reduce((n, m) => n + m.discussions.filter(d => d.resolvable && !d.resolved).length, 0);
    box.innerHTML = `<h3 class="h3">Code review<span>${list.length ? `${pl(list.length, "merge request")}${open ? ` · ${pl(open, "open comment")}` : ""}` : "no merge requests found"}</span></h3>
      <div id="crvList"></div>`;
    if (!list.length) { $("#crvList").innerHTML = `<div class="pane-empty"><b>No merge requests yet</b><span>Reviews and comments on this ticket's merge requests will show up here.</span></div>`; return; }
    paged($("#crvList"), list.map(m => {
      const st = m.state === "merged" ? "done" : m.state === "closed" ? "skip" : m.reviewers.some(r => r.state === "requested_changes") ? "todo" : m.approvals && !m.approvals.left ? "done" : "wait";
      return `<article class="mrc">
        <header>${mark(st, false)}${repoTag(m.repo)}<a class="mono ln" href="${url(m.url)}" target="_blank" rel="noopener">!${m.iid}</a><span class="t">${esc(m.title)}</span><span class="ink-3">${esc(m.state)}</span></header>
        <div class="rvs">${m.reviewers.map(r => { const s = RS[r.state] || ["wait", esc(r.state)]; return `<span class="rv ${s[0]}">${mark(s[0], false)}${esc(r.user.name)}<span class="ink-3"> · ${s[1]}</span></span>`; }).join("") || `<span class="ink-3">No reviewers assigned</span>`}
          ${m.approvals ? `<span class="ink-3">${m.approvals.left ? `${pl(m.approvals.left, "approval")} still needed` : "Approvals complete"}</span>` : ""}</div>
        ${m.discussions.length ? `<details class="talks"><summary>${pl(m.discussions.length, "discussion")}${m.discussions.some(d => d.resolvable && !d.resolved) ? ` · <span class="open-n">${m.discussions.filter(d => d.resolvable && !d.resolved).length} open</span>` : ""}</summary>
          ${m.discussions.map(d => discussionHtml(d, m.repo, !!report.repos.find(x => x.name === m.repo))).join("")}</details>` : ""}
      </article>`;
    }), 10);
    soften(box);
  } catch (e) { box.innerHTML = `<h3 class="h3">Code review</h3><p class="ink-3" style="margin:0">${esc(e.message)}</p>`; }
}

/* ── code review (GitLab) ── */
let railMode = "mine", reviews = null, reviewing = null, reviewView = "all";

function renderRailMode() {
  const box = $("#railMode");
  if (!status?.gitlab) { box.hidden = true; railMode = "mine"; return; }
  box.hidden = false;
  const n = reviews?.filter(r => !r.approvedByMe).length;
  box.innerHTML = `<div class="seg" role="tablist" aria-label="List">
    <button role="tab" aria-selected="${railMode === "mine"}" data-rm="mine">My tickets</button>
    <button role="tab" aria-selected="${railMode === "review"}" data-rm="review">To review${n ? `<span class="c">${n}</span>` : ""}</button></div>`;
  $$("#railMode [data-rm]").forEach(b => b.onclick = () => {
    if (railMode === b.dataset.rm) return;
    railMode = b.dataset.rm;
    if (railMode === "review" && !reviews) loadReviews(); else { renderList(); soften($("#list")); }
  });
}

async function loadReviews() {
  railMode = "review";
  renderRailMode();
  $("#railTitle").textContent = "To review";
  $("#views").innerHTML = "";
  $("#list").innerHTML = Array(6).fill(`<div class="ghost-row"></div>`).join("");
  try { reviews = await api("/api/reviews"); }
  catch (e) { reviews = null; $("#list").innerHTML = emptyNote("Couldn't load your reviews", esc(e.message), `<button class="btn line small" id="rvRetry">Try again</button>`); $("#rvRetry").onclick = loadReviews; return; }
  renderList();
  soften($("#list"));
}

function renderReviewList() {
  $("#railTitle").textContent = "To review";
  const all = reviews || [];
  const waiting = all.filter(r => !r.approvedByMe), done = all.filter(r => r.approvedByMe);
  $("#count").textContent = all.length || "";
  const V = [["all", "All", all.length], ["waiting", "Waiting for you", waiting.length], ["approved", "Approved", done.length]];
  $("#views").innerHTML = V.map(([id, l, c]) => `<button aria-pressed="${reviewView === id}" data-rv="${id}">${l}<span class="c">${c}</span></button>`).join("");
  $$("#views [data-rv]").forEach(b => b.onclick = () => { reviewView = b.dataset.rv; renderList(); soften($("#list")); });
  glide($("#views"), $("#views [aria-pressed=true]"));
  const list = reviewView === "waiting" ? waiting : reviewView === "approved" ? done : [...waiting, ...done];
  if (!list.length) {
    $("#list").innerHTML = emptyNote(all.length ? "Nothing here" : "No reviews waiting", all.length ? "No merge requests in this view." : "Nobody has asked you to review a merge request.", `<button class="btn line small" id="rvReload">Check again</button>`);
    $("#rvReload").onclick = loadReviews; return;
  }
  paged($("#list"), list.map(reviewRowHtml), 30);
}

function reviewRowHtml(r) {
  const state = r.approvedByMe ? "done" : r.conflicts || r.draft ? "wait" : "todo";
  const on = reviewing && reviewing.project === r.project && reviewing.iid === r.iid;
  return `<button class="row" role="option" aria-selected="${!!on}" data-project="${esc(r.project)}" data-iid="${r.iid}">
    ${mark(state)}<span class="t">${esc(r.title)}</span>
    <span class="m">${repoTag(r.repo)}<span class="mono">!${r.iid}</span><span class="s">${esc(r.author?.name || "")}${r.draft ? " · draft" : ""}${r.conflicts ? " · conflicts" : ""}</span>${r.key ? `<span class="mono">${esc(r.key)}</span>` : ""}</span></button>`;
}

function openReview(project, iid) {
  history.pushState(null, "", `#review/${encodeURIComponent(project)}/${iid}`);
  loadReview(project, +iid);
}

async function loadReview(project, iid, retrying = false) {
  const started = Date.now();
  const settle = ms => new Promise(ok => setTimeout(ok, Math.max(0, ms - (Date.now() - started))));
  if (retrying) { const b = $("#retry"); if (b) { b.disabled = true; b.innerHTML = `<span class="spinner"></span>Trying again`; } }
  reviewing = { project, iid };
  selected = null; report = null; diffState = null;
  $("#split").classList.add("open");
  $$("#list .row").forEach(b => b.setAttribute("aria-selected", b.dataset.project === project && +b.dataset.iid === iid));
  const known = reviews?.find(r => r.project === project && r.iid === iid);
  if (!retrying) $("#main").innerHTML = `<div class="loading">
    <button class="btn back" id="back">${icon("back")}To review</button>
    ${known ? `<div class="path">${repoTag(known.repo)}<span class="mono">!${iid}</span></div><div class="title-row"><h2>${esc(known.title)}</h2></div><div style="height:28px"></div>` : ""}
    <div class="note"><span class="spinner"></span>Reading the merge request, its changes and its discussions from GitLab</div>
    ${[72, 58, 66, 49, 61, 54].map(w => `<div class="line" style="width:${w}%"></div>`).join("")}</div>`;
  if ($("#back")) $("#back").onclick = back;
  try {
    const r = await api(`/api/review?project=${encodeURIComponent(project)}&iid=${iid}`);
    if (retrying) await settle(500);
    if (!reviewing || reviewing.iid !== iid || reviewing.project !== project) return;
    reviewing = { ...r, project, iid, fileQuery: "" };
    document.title = `!${iid} · ${r.repo} · Greenlight`;
    renderReview();
  } catch (e) {
    if (retrying) await settle(600);
    $("#main").innerHTML = `<div class="page"><button class="btn back" id="back">${icon("back")}To review</button>
      <div class="void fail-c">${icon("alert", "ico big")}<h3>Couldn't open !${iid}</h3><p>${esc(e.message)}</p>
      <div class="void-acts"><button class="btn solid" id="retry">${icon("refresh")}Try again</button></div></div></div>`;
    $("#back").onclick = back; $("#retry").onclick = () => loadReview(project, iid, true);
  }
}

const RS = { approved: ["done", "Approved"], requested_changes: ["todo", "Changes requested"], reviewed: ["check", "Reviewed"], unreviewed: ["wait", "Not reviewed yet"], unapproved: ["wait", "Approval revoked"] };
const PIPE = { success: ["done", "Pipeline passed"], failed: ["todo", "Pipeline failed"], running: ["wait", "Pipeline running"], pending: ["wait", "Pipeline pending"], canceled: ["check", "Pipeline canceled"] };

function renderReview() {
  const r = reviewing, unresolved = r.discussions.filter(d => d.resolvable && !d.resolved).length;
  const todo = r.checklist.filter(c => c.state === "todo").length;
  const pipe = r.pipeline ? PIPE[r.pipeline.status] || ["check", `Pipeline ${esc(r.pipeline.status)}`] : null;
  const t = r.tags;
  $("#main").innerHTML = `<div class="page review">
    <button class="btn back" id="back">${icon("back")}To review</button>
    <div class="path">${repoTag(r.repo)}<a class="mono ln" href="${url(r.url)}" target="_blank" rel="noopener" title="Open in GitLab">!${r.iid}</a>${r.key ? `<span class="sl">/</span><button class="mono ln" id="rvTicket" title="Open the ticket page with everyone's work">${esc(r.key)}</button>` : ""}</div>
    <div class="title-row"><h2>${esc(r.title)}</h2>
      <div class="acts">
        ${r.approvedByMe ? `<button class="btn danger" id="rvUnapprove">Revoke approval</button>` : `<button class="btn success" id="rvApprove">${icon("checkall")}Approve</button>`}
        <button class="btn danger" id="rvChanges">Request changes</button>
        <div class="pop-wrap"><button class="btn only" id="more" aria-haspopup="menu" aria-expanded="false" aria-label="More">${icon("more", "ico")}</button>
          <div class="pop" id="pop" role="menu" hidden>
            <button role="menuitem" id="rvComment">${icon("copy")}Add a comment</button>
            <a role="menuitem" href="${url(r.url)}" target="_blank" rel="noopener">${icon("out")}Open in GitLab</a>
            ${r.local ? `<hr><button role="menuitem" data-open="code" data-repo="${esc(r.repo)}">${icon("vscode")}Open repository in VS Code</button>${r.sln ? `<button role="menuitem" data-open="vs" data-repo="${esc(r.repo)}">${icon("vs")}Open in Visual Studio</button>` : ""}` : ""}
          </div></div>
      </div></div>
    <div class="facts">
      <span><b>${esc(r.author?.name || "")}</b> wants to merge</span>
      <span><code>${esc(r.source)}</code> → <code>${esc(r.target)}</code></span>
      ${r.issue ? `<span class="tag">${typeTag(r.issue.type)}&nbsp;${jiraBadge(r.issue.status, r.issue.statusCategory)}</span>` : ""}
      ${pipe ? `<span class="tag"><a class="pipe-l ${pipe[0]}" href="${url(r.pipeline.url)}" target="_blank" rel="noopener">${mark(pipe[0], false)}${pipe[1]}</a></span>` : ""}
    </div>

    <div class="state">
      <div class="say">${mark(todo ? "todo" : unresolved ? "check" : "done")}${todo ? `${pl(todo, "thing")} to sort out` : unresolved ? `${pl(unresolved, "open discussion")}` : "Nothing blocks approval"}</div>
      <div class="sub">${r.approvals ? `${r.approvals.by.length ? `Approved by ${r.approvals.by.map(esc).join(", ")}. ` : ""}${r.approvals.left ? `${pl(r.approvals.left, "more approval")} needed.` : "No more approvals needed."}` : ""}</div>
      <div class="fresh">${r.files.length} files · +${r.files.reduce((n, f) => n + f.adds, 0)} −${r.files.reduce((n, f) => n + f.dels, 0)}<br>${pl(r.commits.length, "commit")}</div>
    </div>

    <div class="body">
      <section>
        <h3 class="h3">Review checklist<span>${todo ? pl(todo, "action") : "No actions"}</span></h3>
        <ul class="its review-list">${r.checklist.map(c => `<li class="it"><div class="lead"><span class="dot ${c.state}"></span><span class="tx">${esc(c.title)}</span>${c.detail ? `<span class="dt">${esc(c.detail)}</span>` : ""}</div>${c.url ? `<a class="btn line small" href="${url(c.url)}" target="_blank" rel="noopener">Open${icon("out")}</a>` : ""}</li>`).join("")}</ul>
      </section>
      <aside class="side">
        <div><h3 class="h3">Reviewers</h3><ul class="people">${r.reviewers.map(x => { const s = RS[x.state] || ["wait", esc(x.state)]; return `<li>${mark(s[0], false)}<span>${esc(x.user.name)}${x.user.username === r.me ? " (you)" : ""}</span><span class="ink-3">${s[1]}</span></li>`; }).join("") || `<li class="ink-3">No reviewers assigned</li>`}</ul></div>
        ${t ? `<div><h3 class="h3">Tags</h3>${tagBlock(t, r.repo)}</div>` : ""}
      </aside>
    </div>

    <section class="ev">
      <div class="tabs" role="tablist" id="rvTabs">
        <button role="tab" aria-selected="${(r.tab || "files") === "files"}" data-rt="files">Changes<span class="c">${r.files.length}</span></button>
        <button role="tab" aria-selected="${r.tab === "talk"}" data-rt="talk">Discussions<span class="c">${r.discussions.length}</span></button>
        <button role="tab" aria-selected="${r.tab === "commits"}" data-rt="commits">Commits<span class="c">${r.commits.length}</span></button>
      </div>
      <div class="pane" id="rvPane"></div>
    </section>
  </div>`;
  $("#back").onclick = back;
  $("#rvTicket")?.addEventListener("click", () => openAsReviewer(r.key));
  $("#rvApprove")?.addEventListener("click", () => reviewAct("approve"));
  $("#rvUnapprove")?.addEventListener("click", () => reviewAct("unapprove"));
  $("#rvChanges").onclick = () => reviewDialog(true);
  $("#rvComment").onclick = () => reviewDialog(false);
  const more = $("#more"), pop = $("#pop");
  more.onclick = e => { e.stopPropagation(); pop.hidden = !pop.hidden; more.setAttribute("aria-expanded", !pop.hidden); if (!pop.hidden) document.addEventListener("click", () => { pop.hidden = true; more.setAttribute("aria-expanded", "false"); }, { once: true }); };
  $$("#pop button[data-open][data-repo]").forEach(b => b.onclick = () => openLocal(b.dataset.repo, b.dataset.open));
  $$("#rvTabs [data-rt]").forEach(b => b.onclick = () => { r.tab = b.dataset.rt; $$("#rvTabs [data-rt]").forEach(x => x.setAttribute("aria-selected", x === b)); glide($("#rvTabs"), b); renderReviewPane(); soften($("#rvPane")); });
  renderReviewPane();
}

function renderReviewPane() {
  const r = reviewing, p = $("#rvPane"), tab = r.tab || "files";
  if (tab === "files") {
    p.innerHTML = `<div class="tool"><input class="filter" id="rvf" placeholder="Filter by path" aria-label="Filter files" value="${esc(r.fileQuery)}"><button class="btn small" id="rvAll">Expand all</button></div><div id="rvFiles"></div>`;
    const draw = () => {
      const q = r.fileQuery.toLowerCase();
      const list = r.files.filter(f => !q || f.path.toLowerCase().includes(q));
      const notes = path => r.discussions.filter(d => d.notes.some(n => n.path === path)).length;
      if (!list.length) { $("#rvFiles").innerHTML = `<div class="pane-empty"><b>No files match</b></div>`; return; }
      paged($("#rvFiles"), list.map(f => `<section class="gbox" data-open="false" data-path="${esc(f.path)}">
        <div class="gbox-h"><button class="gtoggle" aria-expanded="false">${icon("chev", "ico chev")}<span class="op-${esc(f.status)} fst">${esc(f.status)}</span><span class="mono fp">${esc(f.path)}</span><span class="dstat"><span class="a">+${f.adds}</span><span class="d">−${f.dels}</span></span>${notes(f.path) ? `<span class="ccount" title="Discussions on this file">${icon("eye")}${notes(f.path)}</span>` : ""}</button>
        ${r.local && f.status !== "D" ? openBtns(r.repo, f.path, r.sln) : ""}</div><div class="gbox-b" hidden></div></section>`), 25);
    };
    draw();
    $("#rvf").oninput = e => { r.fileQuery = e.target.value; draw(); };
    $("#rvAll").onclick = () => $$("#rvFiles .gbox[data-open=false] .gtoggle").slice(0, 25).forEach(b => b.click());
  } else if (tab === "talk") {
    if (!r.discussions.length) { p.innerHTML = `<div class="pane-empty">${icon("list", "ico")}<b>No discussions yet</b><span>Comments from GitLab show up here.</span></div>`; return; }
    p.innerHTML = `<div id="rvTalk"></div>`;
    paged($("#rvTalk"), r.discussions.map(d => discussionHtml(d, r.repo, r.local)), 15);
  } else {
    p.innerHTML = `<div id="rvCommits"></div>`;
    paged($("#rvCommits"), r.commits.map(c => `<div class="cm"><a class="h mono ln" href="${url(c.url)}" target="_blank" rel="noopener">${esc(c.short)}</a><span>${esc(c.subject)}${c.hasKey ? "" : ` <span class="nokey" title="No ticket key in this commit">no key</span>`}</span><span class="m">${esc(c.author)} · ${day(c.date)}</span></div>`), 40);
  }
}

// a review diff, built when the file is first opened
function reviewFileBody(path) {
  const f = reviewing.files.find(x => x.path === path);
  const here = reviewing.discussions.filter(d => d.notes.some(n => n.path === path));
  return `<div class="sbs-cols"><span>Before</span><span>After</span></div>${sideBySide(f?.diff || "")}${here.length ? `<div class="file-talk">${here.map(d => discussionHtml(d, reviewing.repo, reviewing.local)).join("")}</div>` : ""}`;
}

function discussionHtml(d, repo, local) {
  const first = d.notes[0];
  const where = first.path ? `<span class="where">${local ? `<button class="mono ln" data-open-line="${esc(first.path)}" data-line="${first.newLine || first.oldLine || 1}" data-repo="${esc(repo)}" title="Open in VS Code at this line">${esc(first.path.split("/").pop())}:${first.newLine || first.oldLine || ""}</button>` : `<span class="mono">${esc(first.path)}:${first.newLine || first.oldLine || ""}</span>`}</span>` : "";
  return `<article class="talk ${d.resolvable ? (d.resolved ? "resolved" : "open") : ""}">
    <header>${where}${d.resolvable ? `<span class="rstate">${d.resolved ? "Resolved" : "Open"}</span>` : ""}</header>
    ${d.notes.map(n => `<div class="note"><div class="who"><b>${esc(n.author?.name || "")}</b><span class="ink-3">${esc(ago((n.created || "").replace("T", " ").slice(0, 16)) || "")}</span></div><div class="body">${esc(n.body || "").replace(/\n/g, "<br>")}</div></div>`).join("")}
  </article>`;
}

function tagBlock(t, repo) {
  const first = t.containing?.[0];
  return `<div class="tagbox">
    ${first ? `<div class="tg"><span class="ink-3">First in</span><a class="tagpill ln" href="${url(first.url || "#")}" target="_blank" rel="noopener">${esc(first.name)}</a><span class="ink-3">${esc(first.date)}</span></div>
      ${t.containing.length > 1 ? `<div class="tg"><span class="ink-3">Also in</span><span class="tags-more">${t.containing.slice(1, 6).map(x => `<a class="tagpill sm" href="${url(x.url || "#")}" target="_blank" rel="noopener">${esc(x.name)}</a>`).join("")}${t.containing.length > 6 ? `<span class="ink-3">+${t.containing.length - 6}</span>` : ""}</span></div>` : ""}`
    : `<div class="tg"><span class="ink-3">Not tagged yet</span>${t.next ? `<span class="tagpill next" title="Suggested next version">next ${esc(t.next)}</span>` : ""}</div>`}
    ${t.latest ? `<div class="tg"><span class="ink-3">Latest</span><a class="tagpill sm ln" href="${url(t.latest.url || "#")}" target="_blank" rel="noopener">${esc(t.latest.name)}</a><span class="ink-3">${esc(t.latest.date)}</span></div>` : ""}
  </div>`;
}

async function reviewAct(what, body = "", requestChanges = false, jira = true) {
  const r = reviewing;
  try {
    const res = await api(`/api/review/${what}`, { method: "POST", body: JSON.stringify({ project: r.project, iid: r.iid, key: r.key, body, requestChanges, jira }) });
    toast({ approve: "Approved.", unapprove: "Approval revoked.", comment: requestChanges ? "Changes requested." : "Comment posted." }[what] + (res.jiraUrl ? ` <a href="${url(res.jiraUrl)}" target="_blank" rel="noopener">See it on ${esc(r.key)}</a>` : ""), 6000);
    reviews = null;
    await loadReview(r.project, r.iid);
    loadReviews().then(() => { railMode = "review"; renderList(); });
  } catch (e) { fail(esc(e.message)); }
}

function reviewDialog(requestChanges) {
  const d = $("#settings"), r = reviewing;
  d.innerHTML = `<div class="dlg-in">
    <h2>${requestChanges ? "Request changes" : "Add a comment"}</h2>
    <p class="intro">Posted on !${r.iid} in GitLab${r.key ? `, and on ${esc(r.key)} in Jira if you keep the box ticked` : ""}.</p>
    <label class="fld"><span>${requestChanges ? "What needs to change" : "Comment"}</span><textarea id="rvText" rows="6" placeholder="${requestChanges ? "Be specific: what, where, and why." : ""}"></textarea><span class="err" id="rvErr"></span></label>
    ${r.key ? `<label class="tgl"><input type="checkbox" id="rvJira" checked><span class="knob"></span>Also post it on ${esc(r.key)}</label>` : ""}
  </div>
  <div class="dlg-f"><span class="msg" id="rvMsg"></span><button class="btn" id="rvCancel">Cancel</button><button class="btn ${requestChanges ? "danger" : "solid"}" id="rvSend">${requestChanges ? "Request changes" : "Post comment"}</button></div>`;
  $("#rvCancel").onclick = () => d.close();
  $("#rvSend").onclick = async () => {
    const text = $("#rvText").value.trim();
    if (!text) { $("#rvErr").textContent = "Write something first."; $("#rvText").focus(); return; }
    $("#rvSend").disabled = true; $("#rvMsg").innerHTML = `<span class="spinner"></span>Posting`;
    d.close();
    await reviewAct("comment", text, requestChanges, $("#rvJira")?.checked ?? false);
  };
  d.showModal(); setTimeout(() => $("#rvText").focus(), 30);
}

// the reviewer can open the developer's ticket page with everyone's work and every sub-ticket
let reviewerMode = false;
function openAsReviewer(key) { reviewerMode = true; evMine = false; reviewing = null; select(key); }

const finderNext = {}, finderSeen = {};
function localHits(q, max = 12) {
  const ql = q.toLowerCase();
  return (tickets || []).filter(t => !q || t.key.toLowerCase().includes(ql) || (t.summary || "").toLowerCase().includes(ql)).slice(0, max);
}
const hitHtml = t => `<span class="s2-hit">${mark(overall(statesOf((tickets || []).find(x => x.key === t.key)?.lastBuild)), false)}${typeTag(t.type)}<span class="mono">${esc(t.key)}</span><span class="t">${esc(t.summary)}</span>${jiraBadge(t.status, t.statusCategory)}</span>`;
function initFinder() {
  const el = jq("#finderSel");
  if (el.data("select2")) el.select2("destroy");
  el.select2({
    width: "100%",
    dropdownParent: jq(".finder-wrap"),
    placeholder: "Find a ticket",
    minimumInputLength: 0,
    ajax: {
      delay: 220,
      // page 1: your open tickets that match, then Jira's first page; scrolling to the end asks Jira for the next page
      transport(params, ok, fail) {
        const q = (params.data.q || "").trim(), page = params.data.page || 1;
        if (q.length < 2) { const all = localHits(q, 999), from = (page - 1) * 20; ok({ items: all.slice(from, from + 20), more: from + 20 < all.length }); return { abort() {} }; }
        const local = page === 1 ? localHits(q) : [];
        const token = page === 1 ? "" : finderNext[q] || "";
        if (page > 1 && !token) { ok({ items: [], more: false }); return { abort() {} }; }
        const ctrl = new AbortController();
        fetch(`/api/search?q=${encodeURIComponent(q)}${token ? `&next=${encodeURIComponent(token)}` : ""}`, { signal: ctrl.signal, headers: { "X-Ledger": "1" } })
          .then(r => r.ok ? r.json() : Promise.reject(new Error(r.statusText)))
          .then(r => {
            finderNext[q] = r.next || "";
            if (page === 1) finderSeen[q] = new Set(local.map(t => t.key));
            const seen = finderSeen[q] ||= new Set();
            const fresh = r.items.filter(x => !seen.has(x.key)); fresh.forEach(x => seen.add(x.key));
            ok({ items: [...local, ...fresh], more: !!r.next });
          })
          .catch(e => { if (e.name !== "AbortError") fail(e); });
        return { abort: () => ctrl.abort() };
      },
      processResults: data => ({ results: data.items.map(t => ({ id: t.key, text: `${t.key} ${t.summary}`, t })), pagination: { more: data.more } }),
    },
    templateResult: o => o.loading ? jq(`<span class="s2-msg"><span class="spinner"></span>Searching your tickets</span>`) : o.t ? jq(hitHtml(o.t)) : o.text,
    templateSelection: () => jq(`<span class="s2-ph">Find a ticket</span>`),
    language: {
      noResults: () => "None of your tickets match.",
      searching: () => "Searching your tickets",
      errorLoading: () => "Couldn't reach Jira.",
    },
  });
  el.on("select2:opening", e => { if (!status?.account) { e.preventDefault(); openSettings(); } });
  el.on("select2:open", () => { const f = document.querySelector(".finder-wrap .select2-search__field"); if (f) { f.placeholder = "Key or words from the title"; f.focus(); } });
  el.on("select2:select", e => { const k = e.params.data.id; el.val(null).trigger("change.select2"); select(k); });
}
const openFind = () => jq("#finderSel").select2("open");
$("#settings").addEventListener("click", e => { if (e.target === $("#settings")) $("#settings").close(); });
document.addEventListener("keydown", e => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); openFind(); return; }
  if (/INPUT|SELECT|TEXTAREA/.test(document.activeElement?.tagName) || document.querySelector(".select2-container--open") || $("#settings").open || e.ctrlKey || e.metaKey || e.altKey) return;
  if (e.key === "/") { e.preventDefault(); openFind(); }
  else if (e.key === "j") move(1);
  else if (e.key === "k") move(-1);
  else if (e.key === "r" && report) load(report.key, true);
  else if (e.key === "Escape" && selected && innerWidth <= 900) back();
});

$("#checkAll").onclick = checkAll;
$("#reload").onclick = () => status?.account && loadTickets();
// #review/<group/project>/<iid> opens a merge request; project paths are case-sensitive, so they are not upper-cased
function reviewRoute() {
  const raw = decodeURIComponent(location.hash.slice(1));
  if (!raw.startsWith("review/")) return false;
  const rest = raw.slice(7), cut = rest.lastIndexOf("/");
  if (cut > 0 && +rest.slice(cut + 1) > 0) { railMode = status?.gitlab ? "review" : railMode; loadReview(rest.slice(0, cut), +rest.slice(cut + 1)); return true; }
  return false;
}

window.addEventListener("popstate", () => {
  if (reviewRoute()) return;
  const h = decodeURIComponent(location.hash.slice(1)).toUpperCase(), [k, sub] = h.split("/");
  if (sub === "DIFF" && diffState && report?.key === k) return renderDiffPage();
  if (k && report?.key === k && !sub) { diffState = null; renderTicket(); return; }   // back from a diff: no re-check
  if (k) load(k, false); else back();
});

// the app frame never scrolls as a whole; only its panes do. If focus ever nudges the document, put it back.
addEventListener("scroll", () => { if (scrollX || scrollY) scrollTo(0, 0); }, { passive: true });

$("#main").addEventListener("click", mainClicks);
// opened comment threads ease in like everything else that unfolds
$("#main").addEventListener("toggle", e => { if (e.target.matches?.("details.talks, details.why") && e.target.open) [...e.target.children].slice(1).forEach((c, i) => soften(c, i * 30)); }, true);
$("#list").addEventListener("click", e => {
  const r = e.target.closest(".row"); if (!r) return;
  if (r.dataset.project) openReview(r.dataset.project, +r.dataset.iid); else { reviewing = null; if (reviewerMode) { reviewerMode = false; evMine = true; } select(r.dataset.key); }
});

async function boot() {
  await loadStatus();
  initFinder();
  if (status?.account) await loadTickets(); else renderList();
  // reviews load quietly, so the "To review" count is ready when the switch appears
  if (status?.gitlab) api("/api/reviews").then(r => { reviews = r; if (railMode === "review") renderList(); else renderRailMode(); }).catch(() => {});
  loadWeek();
  if (reviewRoute()) return;
  const k = decodeURIComponent(location.hash.slice(1)).toUpperCase().split("/")[0];
  if (k) load(k, false); else blank();
}
boot();
