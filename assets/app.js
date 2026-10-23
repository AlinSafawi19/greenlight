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
  { id: "qa",     name: "Handover", means: "The ticket is moved in Jira and the tester has the checklist." },
];
const WORD = { done: "Done", todo: "To do", check: "To check", wait: "Waiting", skip: "Not needed", none: "Not checked" };

function mark(s, label = true) {
  const a = label ? `role="img" aria-label="${WORD[s] || s}"` : `aria-hidden="true"`;
  const w = inner => `<svg class="mark" viewBox="0 0 16 16" ${a}>${inner}</svg>`;
  if (s === "done")  return w(`<circle cx="8" cy="8" r="7" fill="var(--st-done)"/><path d="m5 8.3 2 2 4-4.4" fill="none" stroke="var(--gl-bg)" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/>`);
  if (s === "todo")  return w(`<circle cx="8" cy="8" r="6.25" fill="none" stroke="var(--st-todo)" stroke-width="1.6"/><circle cx="8" cy="8" r="3" fill="var(--st-todo)"/>`);
  if (s === "check") return w(`<circle cx="8" cy="8" r="6.25" fill="none" stroke="var(--st-check)" stroke-width="1.6"/><path d="M8 4.25a3.75 3.75 0 0 1 0 7.5z" fill="var(--st-check)"/>`);
  if (s === "wait")  return w(`<circle cx="8" cy="8" r="6.25" fill="none" stroke="var(--st-wait)" stroke-width="1.6" stroke-dasharray="2.4 2.6"/>`);
  if (s === "skip")  return w(`<circle cx="8" cy="8" r="6.25" fill="none" stroke="var(--st-skip)" stroke-width="1.5"/><path d="M5.5 8h5" stroke="var(--st-skip)" stroke-width="1.5" stroke-linecap="round"/>`);
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

/* ── motion for things that appear and disappear: menus, popovers, dialogs, dropdowns ──
   Everything eases in from where it comes from and eases out instead of blinking; nothing moves for people who ask for less motion. */
const calm = () => matchMedia("(prefers-reduced-motion: reduce)").matches;
const EASE_IN = "cubic-bezier(.2, .8, .2, 1)", EASE_OUT = "cubic-bezier(.4, 0, 1, 1)";

function showPop(pop, open) {
  if (!pop) return;
  pop.getAnimations().forEach(a => a.cancel());
  if (open) {
    pop._closing = false; pop.hidden = false;
    if (!calm()) pop.animate([{ opacity: 0, transform: "translateY(-6px) scale(.96)" }, { opacity: 1, transform: "none" }], { duration: 180, easing: EASE_IN });
  } else if (!pop.hidden) {
    if (calm()) { pop.hidden = true; return; }
    pop._closing = true;
    const a = pop.animate([{ opacity: 1, transform: "none" }, { opacity: 0, transform: "translateY(-4px) scale(.97)" }], { duration: 130, easing: EASE_OUT });
    a.onfinish = () => { if (pop._closing) { pop.hidden = true; pop._closing = false; } };
  }
}
// a button that opens a popover; any click elsewhere (or on one of its items) and Escape close it
function wirePop(btn, pop, onOpen) {
  if (!btn || !pop) return;
  btn.onclick = e => {
    e.stopPropagation();
    const open = pop.hidden || pop._closing;
    if (open) onOpen?.();
    showPop(pop, open); btn.setAttribute("aria-expanded", open);
    if (!open) return;
    const close = ev => {
      if (ev.type === "keydown" && ev.key !== "Escape") return;
      if (ev.type === "click" && pop.contains(ev.target) && !ev.target.closest("a, button")) return;
      showPop(pop, false); btn.setAttribute("aria-expanded", "false");
      document.removeEventListener("click", close); document.removeEventListener("keydown", close);
    };
    setTimeout(() => { document.addEventListener("click", close); document.addEventListener("keydown", close); });
  };
}

// dialogs close with the same care they open with, whether by a button, Escape or a click outside
function smoothDialog(d) {
  const close = HTMLDialogElement.prototype.close, show = HTMLDialogElement.prototype.showModal;
  d.close = function (v) {
    if (!this.open || this._closing) return;
    if (calm()) return close.call(this, v);
    this._closing = true;
    try { this.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 160, easing: EASE_OUT, pseudoElement: "::backdrop" }); } catch { }
    const a = this.animate([{ opacity: 1, transform: "none" }, { opacity: 0, transform: "translateY(8px) scale(.98)" }], { duration: 160, easing: EASE_OUT });
    a.onfinish = () => { this._closing = false; close.call(this, v); };
  };
  d.showModal = function () {
    if (this._closing) { this.getAnimations().forEach(a => a.cancel()); this._closing = false; return; }   // reopened while closing: just stay
    show.call(this);
  };
  d.addEventListener("cancel", e => { e.preventDefault(); d.close(); });
}
smoothDialog($("#settings"));

// every searchable dropdown says what you can type (a select can set its own with data-search)
jq(document).on("select2:open", "select", function () {
  const f = document.querySelector(".select2-container--open .select2-search--dropdown .select2-search__field");
  if (f && !f.placeholder) f.placeholder = this.dataset.search || "Type to search";
});

// Select2 dropdowns fold away instead of vanishing (they already ease in through CSS)
jq(document).on("select2:closing", "select", function (e) {
  const inst = jq(this).data("select2");
  const dd = inst?.$dropdown?.[0]?.querySelector(".select2-dropdown");
  if (!dd || this._s2closing || calm()) return;
  e.preventDefault();
  this._s2closing = true;
  const a = dd.animate([{ opacity: 1, transform: "none" }, { opacity: 0, transform: "translateY(-4px) scale(.98)" }], { duration: 120, easing: EASE_OUT });
  a.onfinish = () => { jq(this).select2("close"); this._s2closing = false; };
});

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

// your Jira (or GitLab) picture when there is one, otherwise your initials
const faceHtml = name => status?.avatar
  ? `<img class="face" src="/me/avatar?u=${encodeURIComponent(status.account?.accountId || status.gitlab?.username || "")}" alt="" data-initials="${esc(initials(name))}">`
  : `<span class="face">${esc(initials(name))}</span>`;
function wireFaces(root = document) {
  root.querySelectorAll("img.face").forEach(i => i.onerror = () => { const s = document.createElement("span"); s.className = i.className; s.textContent = i.dataset.initials; i.replaceWith(s); });
}

async function loadStatus() {
  try { status = await api("/api/status"); }
  catch { $("#whoSlot").innerHTML = `<span class="ink-3" style="padding:0 8px">Server isn't running</span>`; return; }
  const me = status.account;
  $("#whoSlot").innerHTML = me
    ? `<button class="who" id="whoBtn" aria-label="Account and settings" title="${esc(cap(me.name))} · account and settings">${faceHtml(me.name)}<span class="nm">${esc(cap(me.name))}<small>${status.gitlab ? "Jira and GitLab" : "Jira"} · settings</small></span>${icon("cog")}</button>`
    : `<button class="btn success" id="whoBtn">Connect Jira</button>`;
  $("#whoBtn").onclick = openSettings;
  wireFaces($("#whoSlot"));
  $("#checkAll").hidden = !me;
  $(".finder-wrap").hidden = !me;   // searching your tickets needs Jira
  navCounts();
}

function openSettings() {
  const me = status?.account, d = $("#settings");
  d.innerHTML = `<div class="dlg-in">
      <h2 id="dlgTitle">${me ? "Account" : "Connect your Jira account"}</h2>
      ${me ? `
        <div class="acct">${faceHtml(me.name)}<div style="flex:1;min-width:0">${esc(cap(me.name))}<small>${esc(me.email)} · ${esc(status.jiraBaseUrl.replace("https://", ""))}</small></div><button class="btn danger small" id="disc">Disconnect</button></div>
        <p class="intro" style="margin:0">Your API token is kept in ${esc(me.source)} on this computer. Handovers are posted under your name.</p>` : `
        <p class="intro">Greenlight lists the tickets assigned to you and posts handovers as you. The token is stored in Windows Credential Manager, never in the project.</p>
        ${status?.credentialsRejected ? `<p class="intro" style="color:var(--red);margin:0">The saved token was rejected. Enter a new one.</p>` : ""}
        <label class="fld"><span>Jira site</span><input id="fSite" value="${esc(status?.jiraBaseUrl || "")}" spellcheck="false"><span class="err" id="eSite"></span></label>
        <label class="fld"><span>Atlassian sign-in email</span><input id="fEmail" type="email" autocomplete="username" spellcheck="false"><small>Can differ from the email on your Jira profile.</small><span class="err" id="eEmail"></span></label>
        <label class="fld"><span>API token</span><input id="fToken" type="password" autocomplete="off"><small><a href="https://id.atlassian.com/manage-profile/security/api-tokens" target="_blank" rel="noopener">Create a token</a> while signed in as yourself.</small><span class="err" id="eToken"></span></label>`}
      ${diskNote()}
      <div class="rule"></div>
      ${gitlabSection()}
      <div class="rule"></div>
      <label class="fld"><span>Repositories folder</span><div class="pathrow"><input id="fRoot" value="${esc(status?.reposRoot || "")}" spellcheck="false"><button class="btn line" id="browseRoot">${icon("folder")}Browse…</button></div><small>${status ? `${pl(status.repoCount, "repository", "repositories")} found. ` : ""}Type or paste the folder that holds your clones, one repository per subfolder.</small><span class="err" id="eRoot"></span></label>
    </div>
    <div class="dlg-f"><span class="msg" id="msg"></span>
      <button class="btn" id="cancel">Close</button>
      <button class="btn line" id="saveRoot">Save folder</button>
      ${me ? "" : `<button class="btn success" id="connect">Connect</button>`}
    </div>`;
  const msg = (h, bad) => { const m = $("#msg"); m.className = "msg" + (bad ? " bad" : ""); m.innerHTML = h; };
  $("#cancel").onclick = () => d.close();
  $("#saveRoot").onclick = () => saveReposRoot($("#fRoot").value, $("#fRoot"), $("#eRoot"), msg);
  $("#browseRoot").onclick = async () => { if (await browseFolder($("#fRoot"), $("#eRoot"), msg)) saveReposRoot($("#fRoot").value, $("#fRoot"), $("#eRoot"), msg); };
  if (me) $("#disc").onclick = async () => {
    if (!await confirmDialog("Disconnect Jira?", "Removes your Jira token from this computer. Tickets, worklogs and handovers stop until you connect again.", "Disconnect", "danger")) return openSettings();
    await api("/api/account/disconnect", { method: "POST" }); tickets = null; selected = null; report = null; d.close(); toast("Disconnected. The token was removed from this computer."); boot(); };
  else $("#connect").onclick = async () => {
    const site = $("#fSite").value.trim(), email = $("#fEmail").value.trim(), token = $("#fToken").value.trim();
    const errs = jiraErrors(site, email, token);
    for (const [k, v] of Object.entries(errs)) { $("#e" + k).textContent = v; $("#f" + k).classList.toggle("bad", !!v); }
    if (Object.values(errs).some(Boolean)) return;
    const b = $("#connect"); b.disabled = true; msg(`<span class="spinner"></span>Checking with Jira`);
    try { const who = await api("/api/account", { method: "POST", body: JSON.stringify({ baseUrl: site, email, token }) }); d.close(); toast(`Connected as ${esc(cap(who.name))}.`); tickets = null; boot(); }
    catch (e) { msg(esc(e.message), true); }
    finally { b.disabled = false; }
  };
  wireGitlab(msg);
  wireFaces(d);
  if (!d.open) d.showModal();
  setTimeout(() => ($("#fEmail") || $("#cancel")).focus(), 30);
}

const jiraErrors = (site, email, token) => ({
  Site: /^https:\/\/[^/]+/.test(site) ? "" : "Use the full address, starting with https://",
  Email: /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email) ? "" : "Enter a valid email address.",
  Token: !token ? "Paste your API token." : token.length < 40 ? "Too short for an API token. Make sure it isn't your password." : "" });

// the repositories folder, typed or chosen: the server checks it and keeps the current one if it's wrong
async function saveReposRoot(value, inp, err, msg) {
  err.textContent = ""; inp.classList.remove("bad");
  const v = value.trim().replace(/^["']|["']$/g, "");
  if (!v) { err.textContent = "Enter a folder."; inp.classList.add("bad"); return null; }
  msg(`<span class="spinner"></span>Looking for repositories`);
  try {
    const r = await api("/api/settings", { method: "POST", body: JSON.stringify({ reposRoot: v }) });
    inp.value = r.reposRoot;   // the folder actually used (one level up when a single repository was chosen)
    msg(`Saved. ${pl(r.repoCount, "repository", "repositories")} found.`);
    await loadStatus();
    return r;
  } catch (e) { msg(""); err.textContent = e.message; inp.classList.add("bad"); return null; }
}

// the Windows folder dialog, opened by the server on this computer; fills the field with the chosen path
async function browseFolder(inp, err, msg) {
  err.textContent = "";
  msg(`<span class="spinner"></span>Choose a folder in the dialog`);
  try {
    const r = await api("/api/pick-folder", { method: "POST" });
    msg("");
    if (!r.path) return false;   // closed without choosing
    inp.value = r.path; return true;
  } catch (e) { msg(""); err.textContent = e.message; return false; }
}

// low disk space: checks start failing to save, so the account dialog says so while it lasts
function diskNote() {
  const free = status?.diskFree ?? -1;
  if (!(free >= 0 && free < 1024 ** 3)) return "";
  return `<div class="note-warn">${icon("alert")}<span><b>Drive C: is almost full (${(free / 1024 ** 3).toFixed(2)} GB free).</b> Checks can fail to save. Free some space with Disk Cleanup or Settings › Storage.</span></div>`;
}

// GitLab, for code review: a personal access token with the api scope (approving needs it)
function gitlabSection() {
  const g = status?.gitlab, base = status?.gitlabBaseUrl || "https://gitlab.com";
  const create = `${base}/-/user_settings/personal_access_tokens?name=Greenlight&scopes=api`;
  return g ? `<div class="fld"><span>GitLab · code review</span>
      <div class="acct">${g.avatar ? `<img class="face" src="/me/avatar?of=gitlab&u=${encodeURIComponent(g.username)}" alt="" data-initials="${esc(initials(g.name))}">` : `<span class="face">${esc(initials(g.name))}</span>`}<div style="flex:1;min-width:0">${esc(g.name)}<small>@${esc(g.username)} · ${esc(base.replace("https://", ""))}</small></div><button class="btn danger small" id="glDisc">Disconnect</button></div></div>`
    : `<div class="fld"><span>GitLab · code review <span class="ink-3">(optional)</span></span>
      <small style="margin-top:-2px">Connect to see the merge requests you're asked to review, and the reviews on yours.</small>
      ${status?.gitlabRejected ? `<small style="color:var(--red)">The saved GitLab token stopped working. Enter a new one.</small>` : ""}
      <div class="glrow"><input id="glBase" value="${esc(base)}" spellcheck="false" aria-label="GitLab address"><input id="glToken" type="password" placeholder="Personal access token" autocomplete="off" aria-label="GitLab personal access token"><button class="btn success small" id="glConn">Connect</button></div>
      <small><a href="${url(create)}" target="_blank" rel="noopener">Create a token</a> with the <b>api</b> scope (needed to approve).</small><span class="err" id="glErr"></span></div>`;
}

function wireGitlab(msg) {
  $("#glDisc")?.addEventListener("click", async () => {
    if (!await confirmDialog("Disconnect GitLab?", "Removes your GitLab token from this computer. Reviews, deployments and creating merge requests or tags stop until you connect again.", "Disconnect", "danger")) return openSettings();
    await api("/api/gitlab/disconnect", { method: "POST" }); reviews = null; railMode = "mine"; await loadStatus(); openSettings(); renderList(); toast("GitLab disconnected. The token was removed from this computer."); });
  $("#glConn")?.addEventListener("click", async () => {
    const base = $("#glBase").value.trim(), token = $("#glToken").value.trim();
    $("#glErr").textContent = !/^https:\/\/[^/]+/.test(base) ? "Use the full address, starting with https://" : token.length < 20 ? "Paste your personal access token." : "";
    if ($("#glErr").textContent) return;
    $("#glConn").disabled = true; msg(`<span class="spinner"></span>Checking with GitLab`);
    try {
      const who = await api("/api/gitlab/account", { method: "POST", body: JSON.stringify({ baseUrl: base, token }) });
      await loadStatus(); openSettings(); msg(`Connected to GitLab as @${esc(who.username)}.`); renderList();
      api("/api/reviews").then(r => { reviews = r; }).catch(() => {});
    } catch (e) { msg(esc(e.message), true); $("#glConn").disabled = false; }
  });
}

// tickets moved to a Done status from here. Jira's search lags a transition by a few seconds,
// so these are dropped from the list until a reload stops returning them on its own.
const closedKeys = new Set();

async function loadTickets() {
  $("#list").innerHTML = Array(8).fill(`<div class="ghost-row"></div>`).join("");
  try {
    const all = await api("/api/my-tickets");
    const back = new Set(all.map(t => t.key));
    closedKeys.forEach(k => { if (!back.has(k)) closedKeys.delete(k); });
    tickets = all.filter(t => !closedKeys.has(t.key));
  } catch (e) { tickets = []; fail(esc(e.message)); }
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

const emptyNote = (title, text, action = "", mood = "empty") => `<div class="rail-empty">${glim(mood)}<b>${title}</b><span>${text}</span>${action}</div>`;

let groupBy = (() => { try { return localStorage.getItem("gl-group") || "story"; } catch { return "story"; } })();

function renderList() {
  if (railMode === "review") return renderReviewList();
  $("#railTitle").textContent = "My tickets";
  if (!status?.account) {
    $("#count").textContent = ""; $("#views").innerHTML = ""; $("#railTools").innerHTML = "";
    $("#list").innerHTML = emptyNote("Not connected", "Connect your Jira account to see the tickets assigned to you.", `<button class="btn success small" id="rc">Connect Jira</button>`, "welcome");
    // on the home page the stepper is already showing: go to its Jira step instead of opening the dialog
    $("#rc").onclick = () => { if (onboard && section === "tickets") { onboard.step = 0; renderOnboarding(); } else openSettings(); }; return;
  }
  const all = tickets || [];
  const g = { todo: [], wait: [], check: [], none: [], done: [] };
  all.forEach(t => g[overall(statesOf(t.lastBuild))].push(t));
  const V = [["all", "All", all.length], ["todo", "Needs you", g.todo.length + g.wait.length], ["check", "Ready", g.check.length], ["done", "With QA", g.done.length], ["none", "New", g.none.length]];
  $("#count").textContent = all.length || "";
  navCounts();
  $("#railTools").innerHTML = `<div class="seg" role="radiogroup" aria-label="Group tickets by">
      <button role="radio" aria-checked="${groupBy === "story"}" data-gb="story">By story</button>
      <button role="radio" aria-checked="${groupBy === "state"}" data-gb="state">By readiness</button></div>`;
  $$("#railTools [data-gb]").forEach(b => b.onclick = () => { if (groupBy === b.dataset.gb) return; groupBy = b.dataset.gb; try { localStorage.setItem("gl-group", groupBy); } catch { } renderList(); soften($("#list")); });
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
    const words = { todo: ["Nothing needs you", "No ticket is blocked right now.", "success"], check: ["Nothing ready yet", "Tickets with every step clear show up here."], done: ["Nothing with QA", "Tickets you've handed over show up here."], none: ["Everything is checked", "Each of your tickets has been checked at least once.", "success"] };
    const [t, x, mood] = !all.length ? ["All clear", "Nothing open is assigned to you in Jira.", "success"] : words[view] || ["Nothing here", "No tickets in this view."];
    $("#list").innerHTML = emptyNote(t, x, all.length && view !== "all" ? `<button class="btn line small" id="showAll">Show all ${all.length}</button>` : "", mood);
    $("#showAll")?.addEventListener("click", () => $(`#views [data-v="all"]`).click());
    return;
  }
  if (groupBy === "story") {
    // under their parent story or epic; the header checks the whole story with its sub-tickets
    const shown = order.flatMap(k => g[k]);
    const parents = new Map();
    for (const t of shown) {
      const k = t.parentKey || "";
      if (!parents.has(k)) parents.set(k, { key: k, summary: t.parentSummary, type: t.parentType, items: [] });
      parents.get(k).items.push(t);
    }
    const groups = [...parents.values()].sort((a, b) => (a.key === "") - (b.key === "") || b.items.length - a.items.length);
    paged($("#list"), groups.flatMap(gr => [
      gr.key ? `<button class="grp-h" data-story="${esc(gr.key)}" title="Check the whole ${esc((gr.type || "story").toLowerCase())} with its sub-tickets">${typeTag(gr.type || "Story")}<span class="t">${esc(gr.summary || gr.key)} <span class="k">${esc(gr.key)}</span></span><span class="go">Check all${icon("chev")}</span></button>`
             : `<div class="sec-l">No parent</div>`,
      ...gr.items.map(rowHtml)]), 30);
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
  if (!status?.account) { openSettings(); return; }
  if (section !== "tickets") setSection("tickets");
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
  document.title = `${key} · greenlight`;
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
      <div class="ldprog">${glim("scanning")}<div class="note" id="ldNote"><span class="spinner"></span>${fetchFirst ? "Fetching the latest from GitLab" : "Reading Jira"}</div><div class="ldbar"><i id="ldBar"></i></div></div>
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
          row.innerHTML = `<div class="stp-h">${mark(s.state)}<span class="nm">${STEPS[k2].name}</span><span class="hl">${esc(s.headline)}</span><span class="ct ${s.state}">${WORD[s.state]}</span><span></span></div>`;
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
function back() {
  const toReviews = section === "reviews";
  history.pushState(null, "", toReviews ? "#reviews" : "#tickets");
  selected = null; report = null; reviewing = null; reviewerMode = false; diffState = null; filesState = null; setupState = null;
  $("#split").classList.remove("open"); renderList();
  toReviews ? reviewsBlank() : blank();
}

function blank() {
  document.title = "My tickets · greenlight";
  const me = status?.account;
  // first run: the setup stepper, until it's finished (it stays open after Jira connects, for the remaining steps)
  if (!me && !onboard) onboard = { step: OB.findIndex(s => !s.done()) };
  if (onboard) return renderOnboarding();
  $("#main").innerHTML = `<div class="blank"><div class="blank-in">
    ${glim("welcome")}
    <h2>Pick a ticket</h2><p>Greenlight shows what stands between it and QA: code, tags, database, access, config and the handover.</p>
      <div class="keys"><span><kbd>J</kbd><kbd>K</kbd></span><span>Next and previous ticket</span><span><kbd>Ctrl K</kbd></span><span>Find a ticket</span><span><kbd>R</kbd></span><span>Fetch and check again</span></div>
  </div></div>`;
}

/* ─────────────────────────── first-run setup stepper ─────────────────────────── */
let onboard = null;   // { step } while the stepper is open
const OB = [
  { title: "Jira account", note: "Required", done: () => !!status?.account },
  { title: "Repositories", note: "Your local clones", done: () => status?.repoCount > 0 },
  { title: "GitLab", note: "Optional", done: () => !!status?.gitlab },
];

function renderOnboarding() {
  document.title = "Set up · greenlight";
  if (onboard.step < 0) onboard.step = OB.length;   // everything done already
  const at = onboard.step, last = at >= OB.length;
  const rail = OB.map((s, i) => {
    const st = s.done() ? "ok" : i === at ? "now" : "";
    return `<li class="${st}${i === at ? " cur" : ""}"><button data-ob="${i}" ${i === at ? `aria-current="step"` : ""}>
      <span class="dot">${s.done() ? "✓" : i + 1}</span><span class="tx"><b>${s.title}</b><small>${s.done() ? "Done" : s.note}</small></span></button></li>`;
  }).join("");
  $("#main").innerHTML = `<div class="blank"><div class="blank-in ob">
    ${glim(last ? "success" : "welcome")}
    <h2>${last ? "You're set" : "Set up Greenlight"}</h2>
    <p>${last ? "Everything Greenlight needs is in place on this computer." : "A few steps, once on this computer. Tokens stay in Windows Credential Manager."}</p>
    <ol class="ob-rail" aria-label="Setup steps">${rail}</ol>
    <section class="ob-card" aria-live="polite">${last ? obDoneHtml() : [obJiraHtml, obReposHtml, obGitlabHtml][at]()}</section>
  </div></div>`;
  $$("[data-ob]").forEach(b => b.onclick = () => { onboard.step = +b.dataset.ob; renderOnboarding(); });
  last ? wireObDone() : [wireObJira, wireObRepos, wireObGitlab][at]();
  soften($(".ob-card"));
}

// Back / Continue under each step; Continue only once the step is done, except where it can be skipped
function obNav(canGo, skip = "") {
  const at = onboard.step;
  return `<div class="ob-nav">${at > 0 ? `<button class="btn" id="obBack">${icon("back")}Back</button>` : "<span></span>"}
    <span class="msg" id="obMsg"></span>
    ${skip && !canGo ? `<button class="btn" id="obSkip">${skip}</button>` : ""}
    <button class="btn primary" id="obNext" ${canGo ? "" : "disabled"}>Continue</button></div>`;
}
function wireObNav() {
  $("#obBack")?.addEventListener("click", () => { onboard.step--; renderOnboarding(); });
  $("#obNext")?.addEventListener("click", () => { onboard.step++; renderOnboarding(); });
  $("#obSkip")?.addEventListener("click", () => { onboard.step++; renderOnboarding(); });
}
const obMsg = (h, bad) => { const m = $("#obMsg"); if (m) { m.className = "msg" + (bad ? " bad" : ""); m.innerHTML = h; } };

// 1 · Jira
function obJiraHtml() {
  const me = status?.account;
  if (me) return `<h3>Jira account</h3>
    <div class="acct">${faceHtml(me.name)}<div style="flex:1;min-width:0">${esc(cap(me.name))}<small>${esc(me.email)} · ${esc(status.jiraBaseUrl.replace("https://", ""))}</small></div></div>${obNav(true)}`;
  return `<h3>Connect your Jira account</h3>
    <p class="ob-why">Lists the tickets assigned to you, logs time and moves tickets, all as you.</p>
    ${status?.credentialsRejected ? `<p class="ob-why" style="color:var(--red)">The saved token was rejected. Enter a new one.</p>` : ""}
    <label class="fld"><span>Jira site</span><input id="obSite" value="${esc(status?.jiraBaseUrl || "")}" spellcheck="false"><span class="err" id="obeSite"></span></label>
    <label class="fld"><span>Atlassian sign-in email</span><input id="obEmail" type="email" autocomplete="username" spellcheck="false"><small>Can differ from the email on your Jira profile.</small><span class="err" id="obeEmail"></span></label>
    <label class="fld"><span>API token</span><input id="obToken" type="password" autocomplete="off"><small><a href="https://id.atlassian.com/manage-profile/security/api-tokens" target="_blank" rel="noopener">Create a token</a> while signed in as yourself, then paste it here.</small><span class="err" id="obeToken"></span></label>
    <div class="ob-nav"><span></span><span class="msg" id="obMsg"></span><button class="btn success" id="obConnect">Connect Jira</button></div>`;
}
function wireObJira() {
  wireObNav(); wireFaces($(".ob-card"));
  const b = $("#obConnect"); if (!b) return;
  setTimeout(() => $("#obEmail")?.focus(), 30);
  $$(".ob-card input").forEach(i => i.addEventListener("keydown", e => { if (e.key === "Enter") b.click(); }));
  b.onclick = async () => {
    const site = $("#obSite").value.trim(), email = $("#obEmail").value.trim(), token = $("#obToken").value.trim();
    const errs = jiraErrors(site, email, token);
    for (const [k, v] of Object.entries(errs)) { $("#obe" + k).textContent = v; $("#ob" + k).classList.toggle("bad", !!v); }
    if (Object.values(errs).some(Boolean)) return;
    b.disabled = true; obMsg(`<span class="spinner"></span>Checking with Jira`);
    try {
      const who = await api("/api/account", { method: "POST", body: JSON.stringify({ baseUrl: site, email, token }) });
      toast(`Connected as ${esc(cap(who.name))}.`);
      onboard.step = 1; tickets = null; boot();
    } catch (e) { obMsg(esc(e.message), true); b.disabled = false; }
  };
}

// 2 · repositories folder: typed or pasted, or chosen in the Windows folder dialog
function obReposHtml() {
  const n = status?.repoCount || 0;
  return `<h3>Where are your repositories?</h3>
    <p class="ob-why">The folder that holds your local clones, one repository per subfolder. Greenlight reads their git history.</p>
    <label class="fld"><span>Repositories folder</span>
      <div class="pathrow"><input id="obRoot" value="${esc(status?.reposRoot || "")}" spellcheck="false" placeholder="C:\\Users\\you\\source\\repos"><button class="btn line" id="obBrowse">${icon("folder")}Browse…</button></div>
      <small id="obFound">${n ? `${icon("checkall")}${pl(n, "repository", "repositories")} found.` : "Type or paste the path, or browse to it."}</small><span class="err" id="obeRoot"></span></label>
    ${obNav(n > 0)}`;
}
function wireObRepos() {
  wireObNav();
  const inp = $("#obRoot");
  let tried = inp.value.trim();
  const save = async () => {
    if (!inp.value.trim() || inp.value.trim() === tried) return;   // Enter and leaving the field both land here
    tried = inp.value.trim();
    if (await saveReposRoot(inp.value, inp, $("#obeRoot"), obMsg)) renderOnboarding();
  };
  inp.addEventListener("keydown", e => { if (e.key === "Enter") save(); });
  inp.addEventListener("change", save);   // typed or pasted, saved as soon as you leave the field
  inp.addEventListener("input", () => { tried = ""; });
  $("#obBrowse").onclick = async () => { if (await browseFolder(inp, $("#obeRoot"), obMsg)) save(); };
}

// 3 · GitLab
function obGitlabHtml() {
  const g = status?.gitlab, base = status?.gitlabBaseUrl || "https://gitlab.com";
  if (g) return `<h3>GitLab</h3>
    <div class="acct"><span class="face">${esc(initials(g.name))}</span><div style="flex:1;min-width:0">${esc(g.name)}<small>@${esc(g.username)} · ${esc(base.replace("https://", ""))}</small></div></div>${obNav(true)}`;
  const create = `${base}/-/user_settings/personal_access_tokens?name=Greenlight&scopes=api`;
  return `<h3>Connect GitLab <span class="ink-3">(optional)</span></h3>
    <p class="ob-why">For merge requests: the ones waiting for your review, the reviews on yours, and creating merge requests and tags from Greenlight.</p>
    <label class="fld"><span>GitLab address</span><input id="obGlBase" value="${esc(base)}" spellcheck="false"></label>
    <label class="fld"><span>Personal access token</span><input id="obGlToken" type="password" autocomplete="off"><small><a href="${url(create)}" target="_blank" rel="noopener">Create a token</a> with the <b>api</b> scope (needed to approve).</small><span class="err" id="obeGl"></span></label>
    <div class="ob-nav"><button class="btn" id="obBack">${icon("back")}Back</button><span class="msg" id="obMsg"></span>
      <button class="btn" id="obSkip">Skip for now</button><button class="btn success" id="obGlConn">Connect GitLab</button></div>`;
}
function wireObGitlab() {
  wireObNav();
  const b = $("#obGlConn"); if (!b) return;
  b.onclick = async () => {
    const base = $("#obGlBase").value.trim(), token = $("#obGlToken").value.trim();
    $("#obeGl").textContent = !/^https:\/\/[^/]+/.test(base) ? "Use the full address, starting with https://" : token.length < 20 ? "Paste your personal access token." : "";
    if ($("#obeGl").textContent) return;
    b.disabled = true; obMsg(`<span class="spinner"></span>Checking with GitLab`);
    try {
      const who = await api("/api/gitlab/account", { method: "POST", body: JSON.stringify({ baseUrl: base, token }) });
      toast(`Connected to GitLab as @${esc(who.username)}.`);
      await loadStatus(); onboard.step++; renderOnboarding();
      api("/api/reviews").then(r => { reviews = r; navCounts(); }).catch(() => {});
    } catch (e) { obMsg(esc(e.message), true); b.disabled = false; }
  };
}

// done
function obDoneHtml() {
  const n = (tickets || []).length;
  return `<ul class="ob-sum">${OB.map(s => `<li class="${s.done() ? "ok" : ""}"><span class="dot">${s.done() ? "✓" : "–"}</span>${s.title}<small>${s.done() ? "" : s === OB[0] ? "needed: go back and connect it" : "skipped, add it later from settings"}</small></li>`).join("")}</ul>
    <div class="ob-nav"><button class="btn" id="obBack">${icon("back")}Back</button><span></span>
      <button class="btn primary" id="obFinish" ${status?.account ? "" : "disabled"}>${icon("ticket")}${tickets ? `Open my tickets${n ? ` (${n})` : ""}` : "Open my tickets"}</button></div>`;
}
function wireObDone() {
  wireObNav();
  $("#obFinish").onclick = () => { onboard = null; blank(); };
}

// git only sees a merge request once it is merged; GitLab knows the open ones, so they're laid over the code step
// when the ticket's merge requests arrive: an open MR turns "merge this branch" from an action into a wait on review
let ticketMrs = { key: null, list: null };
// "approved" only when someone did approve: GitLab reports 0 approvals left on projects that need none, even with no review at all
const approvers = m => { const r = (m.reviewers || []).filter(x => x.state === "approved").map(x => x.user?.name).filter(Boolean); return r.length ? r : m.approvals?.by || []; };
const isApproved = m => approvers(m).length > 0 && !m.approvals?.left;
function reviewSay(m) {
  const who = approvers(m), left = m.approvals?.left || 0;
  const pending = (m.reviewers || []).filter(x => x.state !== "approved").map(x => x.user?.name).filter(Boolean);
  if (left) return `${pl(left, "approval")} still needed${who.length ? `, approved so far by ${who.join(", ")}` : ""}.`;
  if (who.length) return `Approved by ${who.join(", ")}, waiting to be merged.`;
  return pending.length ? `Waiting for ${pending.join(", ")} to review.` : "Open. No reviewer assigned and nobody has approved it yet.";
}
// the ticket's merge request from this branch into this target: an open one first, else one that's already merged.
// A merged one only counts when it can hold the branch as it is now: commits written after it was merged
// (fix-ups pushed to the same branch) still need a merge request of their own.
const mrFor = (repo, branch, into) => {
  if (ticketMrs.key !== report?.key) return null;
  const src = branch.replace(/^origin\//, "");
  const hits = (ticketMrs.list || []).filter(m => (m.repo || "").toLowerCase() === (repo || "").toLowerCase()
    && (m.source || "") === src && (m.target || "") === into);
  const tipAt = report.repos.find(x => x.name.toLowerCase() === (repo || "").toLowerCase())?.branches
    .find(b => b.name.replace(/ \(local only.*$/, "").replace(/^origin\//, "") === src)?.tipAt || 0;
  const holdsTip = m => !m.mergedAt || !tipAt || tipAt * 1000 <= Date.parse(m.mergedAt) + 60000;
  return hits.find(m => m.state === "opened") || hits.find(m => m.state === "merged" && holdsTip(m)) || null;
};

// Each "Merge <branch> into <target>" item is one merge request. A service with a dev branch has two of them, dev first:
// GitLab says whether each one is still to create, open (in review, approved, changes asked) or already merged.
function applyOpenMrs(r) {
  const s = r.stations.find(x => x.id === "code"); if (!s) return false;
  s.items.forEach(codeInfo);   // read the server's titles before they're renamed below
  let changed = false;
  s.items.forEach(it => {
    if (it.mr || (it.state !== "todo" && it.state !== "wait")) return;
    const b = /^Merge (\S+) into (\S+)$/.exec(it.title || ""); if (!b) return;
    const m = mrFor(it.repo, b[1], b[2]); if (!m) return;
    const step = /^Step \d of \d[.,]/.exec(it.detail || "")?.[0].replace(/,$/, ".") || "";
    it.mr = m; changed = true; it.actionUrl = m.url; it.actionLabel = `Open !${m.iid}`;
    if (m.state === "merged") {
      // merged on GitLab, but the local copy hasn't fetched it yet
      it.state = "done"; it.title = `Merged ${b[1]} into ${b[2]}`;
      it.detail = `${step} Merge request !${m.iid}. Fetch to bring it into your copy.`.trim();
      return;
    }
    const asked = (m.reviewers || []).some(x => x.state === "requested_changes");
    it.state = asked ? "todo" : "wait";
    it.title = `Merge request !${m.iid} into ${b[2]}`;
    it.detail = `${step} ${m.draft ? "Draft. " : ""}${asked ? "Changes requested." : reviewSay(m)}`.trim();
  });
  // a dev merge request that's merged unblocks step 2 of the same service and branch
  s.items.forEach(it => {
    const b = /^Merge (\S+) into (\S+)$/.exec(it.title || "");
    if (!b || it.state !== "wait" || it.mr || !/^Step 2 of 2/.test(it.detail || "")) return;
    const first = s.items.find(x => x !== it && x.repo === it.repo && x.state === "done" && /^Step 1 of 2/.test(x.detail || "") && x.title.startsWith(`Merged ${b[1]} into `));
    if (first) { it.state = "todo"; it.detail = it.detail.replace(/^Step 2 of 2, after the \S+ merge request is merged\./, "Step 2 of 2."); changed = true; }
  });
  if (!changed) return false;
  const n = f => s.items.filter(f).length;
  const create = n(x => x.state === "todo" && !x.mr), asked = n(x => x.state === "todo" && x.mr);
  const review = n(x => x.state === "wait" && x.mr), later = n(x => x.state === "wait" && !x.mr);
  s.state = create + asked ? "todo" : review + later ? "wait" : "done";
  s.headline = create + asked + review + later === 0 ? "Every merge request is merged"
    : [create ? `${pl(create, "merge request")} to create` : "", asked ? `${asked} with changes requested` : "",
       review ? `${review} in review` : "", later ? `${later} after an earlier step` : ""].filter(Boolean).join(" · ");
  return true;
}

function progressHtml(r) {
  const st = r.stations.map(s => s.state), ov = overall(st), nothing = r.repos.length === 0;
  const todo = r.stations.reduce((n, s) => n + s.items.filter(x => x.state === "todo").length, 0);
  const confirm = r.stations.reduce((n, s) => n + s.items.filter(x => x.state === "check").length, 0);
  const at = st.findIndex(s => s === "todo" || s === "wait");
  const kids = r.scope.length - 1;
  const v = nothing ? ["none", "No trace in git yet", `Nothing in your repositories mentions ${esc(r.key)}${kids ? ` or your ${pl(kids, "sub-ticket")}` : ""}.`]
    : ov === "todo" ? ["todo", `Stopped at ${STEPS[at].name.toLowerCase()}`, `${pl(todo, "thing needs", "things need")} doing before QA can test this${confirm ? `, and ${pl(confirm, "item")} to check` : ""}.`]
    : ov === "wait" ? ["wait", `Waiting at ${STEPS[at].name.toLowerCase()}`, at === 0 ? "Merge requests are open and waiting for review." : "An earlier step has to finish first."]
    : ov === "done" ? ["done", "With QA", confirm ? `Handed over. ${pl(confirm, "item")} still worth a look.` : "Handed over, and every step is done."]
    : ["check", "Ready to hand over", `Nothing blocks QA. Check ${pl(confirm, "item")}, then copy the checklist for the tester.`];
  const mood = nothing ? "empty" : ov === "todo" ? "warning" : ov === "wait" ? "thinking" : "success";
  return {
    state: `<div class="state ${ov === "done" || ov === "check" ? "ok" : ""}" id="tkState">
      ${glim(mood)}
      <div class="say">${mark(v[0])}${v[1]}</div>
      <div class="sub">${v[2]}</div>
      <div class="fresh">Checked ${esc(ago(r.generated))}<br>${r.fetched ? "with the latest from the server" : `from your local copies · <button id="fetchNow">fetch latest</button>`}</div>
    </div>`,
    // each step says its state in a word next to its colour; hovering gives the step's one-line summary
    meter: `<div id="tkMeter"><div class="meter">${STEPS.map((s, k) => `<div class="${k === at ? "here" : ""} ${st[k]}" title="${esc(`${s.name}: ${r.stations[k]?.headline || WORD[st[k]] || ""}`)}"><i class="${st[k]}"></i>
        <span class="mn">${mark(st[k], false)}${s.name}</span><span class="mw">${k === at ? "You are here · " : ""}${WORD[st[k]] || ""}</span></div>`).join("")}</div></div>`,
    steps: `<section id="tkSteps">
        <h3 class="h3">Steps to QA<span>${todo ? pl(todo, "action") : "No actions"}${confirm ? ` · ${confirm} to check` : ""}</span></h3>
        <ol class="steps">${r.stations.map((s, k) => stepHtml(s, k)).join("")}</ol>
      </section>`,
  };
}

// the open merge requests just arrived: repaint the verdict, the meter and the steps in place
function repaintProgress() {
  const r = report; if (!r || !$("#tkSteps")) return;
  const p = progressHtml(r);
  $("#tkState")?.replaceWith(frag(p.state)); $("#tkMeter")?.replaceWith(frag(p.meter)); $("#tkSteps").replaceWith(frag(p.steps));
  $("#fetchNow")?.addEventListener("click", () => load(r.key, true));
  wireSteps();
}
const frag = html => { const t = document.createElement("template"); t.innerHTML = html.trim(); return t.content.firstChild; };

function renderTicket() {
  const r = report, i = r.issue, me = status?.account;
  const nothing = r.repos.length === 0;
  const kids = r.scope.length - 1;
  applyOpenMrs(r);
  const p = progressHtml(r);

  document.title = `${r.key} · greenlight`;
  $("#main").innerHTML = `<div class="page">
    <button class="btn back" id="back">${icon("back")}My tickets</button>
    <div class="path">${r.ancestors.map(a => `<button class="ln" data-go="${esc(a.key)}">${esc(a.type)} <span class="mono">${esc(a.key)}</span></button><span class="sl">/</span>`).join("")}<a class="mono ln" href="${url(r.jiraUrl)}" target="_blank" rel="noopener" title="Open in Jira">${esc(r.key)}</a></div>
    <div class="title-row">
      <h2>${esc(i?.summary || r.key)}</h2>
      <div class="acts">
        <button class="btn" id="recheck" title="Fetch every repository, then check again">${icon("refresh")}Check again</button>
        <button class="btn solid" data-copychk ${nothing ? "disabled" : ""} title="The QA checklist as Markdown, to paste where the tester will read it">${icon("copy")}Copy checklist</button>
        <div class="pop-wrap"><button class="btn only" id="more" aria-haspopup="menu" aria-expanded="false" aria-label="More">${icon("more", "ico")}</button>
          <div class="pop" id="pop" role="menu" hidden>
            ${me ? `<button role="menuitem" id="moveJira">${icon("send")}Move in Jira…</button>` : ""}
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

    ${nothing ? voidHtml(r, kids) : p.state}
    ${nothing ? "" : `${p.meter}

    <div class="body">
      ${p.steps}
      <aside class="side">
        <div id="timeBox" class="timebox"></div>
        <div><h3 class="h3">Your footprint<span id="figsFor"></span></h3><div class="figs" id="figs"></div></div>
        ${kids ? `<div><h3 class="h3">Your tickets in scope</h3><ul class="scope">${r.scope.map(t => `<li><button data-go="${esc(t.key)}"><span class="k"><span class="kk">${typeTag(t.type)}<span class="mono ln">${esc(t.key)}</span>${jiraBadge(t.status, t.statusCategory)}</span><span>${t.commits ? `${t.merged}/${t.commits} merged` : "no code"}</span></span><span class="s">${esc(t.summary)}</span></button></li>`).join("")}</ul></div>` : ""}
      </aside>
    </div>

    ${setupHtml(r)}
    <section class="ev">
      <div class="evbar">
        ${r.scope.length > 1 ? `<select id="evTicket" aria-label="Ticket" data-search="Search your tickets in this check">
          <option value="*" ${evTicket === "*" ? "selected" : ""}>All ${r.scope.length} of your tickets here</option>
          ${r.scope.map(t => `<option value="${esc(t.key)}" ${t.key === evTicket ? "selected" : ""}>${esc(t.key)}${t.key === r.key ? " (this ticket)" : ""} · ${esc(t.summary)}</option>`).join("")}</select>` : ""}
        <select id="evRepos" multiple aria-label="Repositories"></select>
        <label class="tgl"><input type="checkbox" id="evMine" ${evMine ? "checked" : ""}><span class="knob"></span>Only my commits</label>
      </div>
      <div class="tabs" role="tablist" id="evTabs"></div>
      <div class="pane" id="pane"></div>
    </section>`}
    ${status?.gitlab && !nothing ? `<section class="dep" id="dep"></section>` : ""}
    ${status?.gitlab ? `<section class="crv" id="crv"></section>` : ""}
  </div>`;
  wireTicket();
  if (status?.gitlab && !nothing) loadDeployments(r.key);
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
    ${glim("empty")}
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

/* one service's files for the ticket, on a page of their own (#MM-1/files/<repo>): opened from a step item about files,
   or from "See all" on a Files group, which only shows its first FILES_PEEK */
let filesState = null;   // { repo, scroll, query }
const FILES_PEEK = 8;
function showFiles(repo) {
  filesState = { repo, scroll: $("#main").scrollTop, query: "" };
  history.pushState(null, "", `#${report.key}/files/${encodeURIComponent(repo)}`);
  renderFilesPage();
}
function leaveFiles() {
  const y = filesState?.scroll || 0;
  filesState = null;
  history.pushState(null, "", `#${report.key}`);
  renderTicket();
  $("#main").scrollTop = y;
}
function renderFilesPage() {
  const f = filesState; if (!f || !report) return;
  // every commit of the ticket in that repository, whoever made it: this page is about the service, not the footprint filters
  const x = report.repos.find(r => r.name.toLowerCase() === f.repo.toLowerCase());
  const all = x ? evFiles(x) : [];
  $("#main").innerHTML = `<div class="dpage fpage">
    <div class="dp-h">
      <button class="btn dp-back" id="fpBack">${icon("back")}${esc(report.key)}</button>
      <div class="dp-t"><div class="dp-file">${repoTag(f.repo)}${x ? openBtns(x.name, null, x.sln) : ""}</div>
        <div class="dp-sub ink-3">${pl(all.length, "file")} changed for ${esc(report.key)}</div></div>
      ${all.length > 8 ? `<input class="filter" id="fpq" placeholder="Filter by path" aria-label="Filter files" value="${esc(f.query)}">` : ""}
    </div>
    <div class="dp-b"><ul class="files" id="fpList"></ul></div>
  </div>`;
  document.title = `${f.repo} files · ${report.key} · Greenlight`;
  $("#fpBack").onclick = leaveFiles;
  const draw = () => {
    const q = f.query.toLowerCase(), list = all.filter(y => !q || y.p.toLowerCase().includes(q));
    if (!list.length) { $("#fpList").innerHTML = `<li class="pane-empty"><b>${all.length ? `No files match “${esc(f.query)}”` : "No files in this repository for the ticket"}</b></li>`; return; }
    paged($("#fpList"), list.map(y => fileRow(x, y)), 80);
  };
  $("#fpq")?.addEventListener("input", e => { f.query = e.target.value; clearTimeout(draw.t); draw.t = setTimeout(draw, 120); });
  draw();
  $("#main").scrollTop = 0;
}

function stepHtml(s, k) {
  const open = s.id === openStep;
  const rank = ["todo", "wait", "check", "done", "skip"];
  const items = [...s.items].sort((a, b) => rank.indexOf(a.state) - rank.indexOf(b.state));
  const nTodo = s.items.filter(x => x.state === "todo").length, nCheck = s.items.filter(x => x.state === "check").length;
  // a pill in the step's own colour: how many actions, how many to check, or the state in a word
  const ct = nTodo ? `<span class="ct todo">${pl(nTodo, "action")}</span>` : nCheck ? `<span class="ct check">${nCheck} to check</span>` : `<span class="ct ${s.state}">${WORD[s.state]}</span>`;
  return `<li class="stp" data-open="${open}" data-id="${s.id}">
    <button class="stp-h" aria-expanded="${open}" aria-controls="sb-${s.id}">${mark(s.state)}<span class="nm">${STEPS[k].name}</span><span class="hl">${esc(s.headline)}</span>${ct}${icon("chev", "ico chev")}</button>
    <div class="stp-b" id="sb-${s.id}" ${open ? "" : "hidden"}>
      <p class="means">${esc(STEPS[k].means)}</p>
      ${items.length ? `<ul class="its">${s.id === "code" ? codeItemsHtml(s, items) : items.map(it => itemHtml(s, it)).join("")}</ul>`
        : `<p class="empty-step">${esc(s.headline)}.</p>`}
    </div></li>`;
}

const itemLink = it => it.actionUrl ?`<a class="btn line small" href="${url(it.actionUrl)}" target="_blank" rel="noopener">${esc(it.actionLabel || "Open")}${icon("out")}</a>` : "";
function itemHtml(s, it) {
  return `<li class="it"><div class="lead"><span class="dot ${it.state}"></span>${it.repo ? `<span class="rp">${repoTag(it.repo)}</span>` : ""}<span class="tx">${esc(it.title)}</span>${it.detail ? `<span class="dt">${esc(it.detail)}</span>` : ""}${s.id === "qa" ? openStepsHtml(it) : ""}</div>
    <div class="ia">${it.files?.length ? `<button class="btn line small" data-showfiles="${esc(it.repo)}">${icon("list")}Show files</button>` : ""}${detailLinks(s, it)}${stepActions(s, it)}${itemLink(it)}</div></li>`;
}

/* where an item's details are: the file it's about (preview the change, open it), or its cards in the System Manager setup */
function detailLinks(s, it) {
  if (s.id === "qa") return handoverLinks(it);
  const x = report?.repos.find(r => r.name === it.repo); if (!x) return "";
  // a file named by its path in the detail ("Changed: …", pipelines, SQL grants), or by its name in the title ("3 new settings in appsettings.json")
  const fs = evFiles(x), inTitle = / in (\S+\.\w+)$/.exec(it.title || "")?.[1];
  const f = fs.find(y => y.p === it.detail) || (inTitle && fs.find(y => y.p.split("/").pop() === inTitle));
  if (f) return `<button class="btn line small" data-preview="${esc(x.name)}" data-path="${esc(f.p)}" data-shas="${esc(f.shas.join(","))}">${icon("eye")}Preview change</button>${f.s === "D" ? "" : openBtns(x.name, f.p, x.sln)}`;
  if (s.id !== "access") return "";
  const st = report.setup || {};
  if (/scope/.test(it.title) && (st.services || []).some(v => v.repo === it.repo))
    return `<button class="btn line small" data-setup="${esc("smx:" + it.repo)}">${icon("list")}Show scopes</button>`;
  const menus = (st.menus || []).filter(m => m.repo === it.repo);
  if (/menu/.test(it.title) && menus.length)
    return `<button class="btn line small" data-setup="${esc(menus.map(m => "smx:" + m.uri).join("|"))}">${icon("list")}Show ${menus.length === 1 ? "menu" : "menus"}</button>`;
  return "";
}

/* handover items do the handover: log time, move the ticket in Jira, jump to the steps still open */
function handoverLinks(it) {
  const t = it.title || "", jira = status?.account;
  if (/^Jira isn't connected/.test(t)) return `<button class="btn line small" data-connect>Connect Jira</button>`;
  if (!jira) return "";
  const move = `<button class="btn line small" data-movejira>${icon("send")}Move in Jira…</button>`;
  if (/logged by you$|^No time logged/.test(t)) return `<button class="btn ${it.state === "done" ? "line" : "primary"} small" data-logtime>${icon("checkall")}Log time</button>`;
  if (/^Move the ticket/.test(t)) return `<button class="btn primary small" data-copychk>${icon("copy")}Copy checklist</button>${move}`;
  return /still open$|can't have it yet$|^Jira status is/.test(t) ? move : "";
}
// the steps still blocking the handover, named on the item; each one opens that step
function openStepsHtml(it) {
  if (!/still open$|can't have it yet$/.test(it.title || "")) return "";
  const open = (report?.stations || []).map((s, k) => ({ s, k })).filter(({ s }) => s.id !== "qa" && (s.state === "todo" || s.state === "wait"));
  return open.length ? `<span class="opn">${open.map(({ s, k }) => `<button class="opn-s ${s.state}" data-openstep="${esc(s.id)}" title="${esc(s.headline)}">${mark(s.state, false)}${STEPS[k].name}<span>· ${WORD[s.state]}</span></button>`).join("")}</span>` : "";
}
function goToStep(id) {
  const li = $(`.stp[data-id="${CSS.escape(id)}"]`); if (!li) return;
  if (li.dataset.open !== "true") li.querySelector(".stp-h").click();
  li.scrollIntoView({ behavior: calm() ? "auto" : "smooth", block: "start" });
}

/* the System Manager setup on a page of its own (#MM-1/setup), with the cards an Access item is about already open */
let setupState = null;   // { scroll }
function showSetup(ids) {
  ids.filter(Boolean).forEach(id => { groupOpen[id] = true; });
  setupState = { scroll: $("#main").scrollTop };
  history.pushState(null, "", `#${report.key}/setup`);
  renderSetupPage();
}
function leaveSetup() {
  const y = setupState?.scroll || 0;
  setupState = null;
  history.pushState(null, "", `#${report.key}`);
  renderTicket();
  $("#main").scrollTop = y;
}
function renderSetupPage() {
  if (!report) return;
  const body = setupHtml(report, true);
  $("#main").innerHTML = `<div class="dpage fpage">
    <div class="dp-h">
      <button class="btn dp-back" id="suBack">${icon("back")}${esc(report.key)}</button>
      <div class="dp-t"><div class="dp-file"><b>Access on QA</b></div><div class="dp-sub ink-3">Scopes, menus and data testers need to reach the feature</div></div>
    </div>
    <div class="dp-b su-b">${body || `<div class="pane-empty"><b>Nothing to set up</b><span>This ticket adds no scopes, menus or data.</span></div>`}</div>
  </div>`;
  document.title = `Setup · ${report.key} · Greenlight`;
  $("#suBack").onclick = leaveSetup;
  syncSmx();
  $("#main").scrollTop = 0;
}

/* code items are merge requests. The server names them "Merge <branch> into <target>" (or "Merged …", "Push and merge …"),
   with "Step n of m." in front of the detail when a service merges into dev first; read once, before GitLab renames them */
function codeInfo(it) {
  if (it._c !== undefined) return it._c;
  const t = it.title || "";
  const m = /^Merged? (\S+) into (\S+)$/.exec(t), p = /^Push and merge (\S+)$/.exec(t);
  const c = m ? { branch: m[1], into: m[2] } : p ? { branch: p[1], into: null, local: true } : null;
  if (c) { const st = /^Step (\d) of (\d)/.exec(it.detail || ""); c.step = st ? +st[1] : 0; c.of = st ? +st[2] : 1; }
  return it._c = c;
}

// one card per service and branch, one row per merge request: where it goes, where it stands, what to do
function codeItemsHtml(s, items) {
  const cards = new Map(), rest = [];
  items.forEach(it => { const c = codeInfo(it); if (!c) return rest.push(it); const k = `${it.repo}|${c.branch}`; if (!cards.has(k)) cards.set(k, []); cards.get(k).push(it); });
  const rank = ["todo", "wait", "check", "done", "skip"];
  const worst = list => list.map(x => x.state).sort((a, b) => rank.indexOf(a) - rank.indexOf(b))[0];
  const status = it => {
    const c = codeInfo(it), m = it.mr;
    const text = (it.detail || "").replace(/^Step \d of \d[.,]\s*/, "");
    const say = text ? text[0].toUpperCase() + text.slice(1) : "";
    if (it.state === "done") return ["done", "Merged", say];
    if (m && it.state === "todo") return ["todo", "Changes requested", `!${m.iid} · ${say}`];
    if (m) return ["wait", m.draft ? "Draft" : isApproved(m) ? "Approved" : m.approvals?.left || (m.reviewers || []).length ? "In review" : "Open", `!${m.iid} · ${say}`];
    if (it.state === "wait") return ["wait", "Not yet", say];
    return ["todo", c.local ? "Not pushed" : "To create", say];
  };
  const row = it => {
    const c = codeInfo(it), [st, word, say] = status(it);
    return `<li class="ms">
      ${c.of > 1 ? `<span class="ms-n ${st}">${c.step}</span>` : `<span class="ms-n ${st}">${mark(st, false)}</span>`}
      <div class="ms-t"><div class="ms-into">${c.into ? `Into <code>${esc(c.into)}</code>` : "Push the branch"}<span class="ms-w ${st}">${esc(word)}</span></div>${say ? `<div class="ms-s">${esc(say)}</div>` : ""}</div>
      <div class="ms-a">${stepActions(s, it)}${itemLink(it)}</div></li>`;
  };
  return [...cards.values()].map(list => {
    list.sort((a, b) => (codeInfo(a).step || 0) - (codeInfo(b).step || 0));
    const c = codeInfo(list[0]);
    return `<li class="it mrg"><div class="mrg-h"><span class="dot ${worst(list)}"></span>${repoTag(list[0].repo)}<code class="mrg-br" title="${esc(c.branch)}">${esc(c.branch)}</code></div>
      <ol class="mss">${list.map(row).join("")}</ol></li>`;
  }).join("") + rest.map(it => itemHtml(s, it)).join("");
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
  // merged ones come from git; open ones only GitLab knows, so they're added once the ticket's merge requests arrive
  const merged = all.flatMap(x => x.merges.filter(m => keyHit(m.branch)).map(m => `${x.name}|${m.label}`.toLowerCase()));
  const names = new Set(all.map(x => x.name.toLowerCase()));
  const live = ticketMrs.key === report.key ? (ticketMrs.list || []).filter(m => m.state !== "closed" && names.has((m.repo || "").toLowerCase()) && keyHit(m.source || m.title))
    .map(m => `${m.repo}|!${m.iid}`.toLowerCase()) : [];
  const mrs = new Set([...merged, ...live]).size;
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

function groupHtml(x, count, body, id, tools = "") {
  // collapsed until opened, like the setup cards
  const open = groupOpen[id] === true;
  return `<section class="gbox" data-open="${open}" data-gid="${esc(id)}">
    <div class="gbox-h"><button class="gtoggle" aria-expanded="${open}">${icon("chev", "ico chev")}${repoTag(x.name)}<span class="ink-3">${count}</span></button>${tools}${openBtns(x.name, null, x.sln)}</div>
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
        <td>${branches.map(branchHtml).join("") || `<span class="ink-3">None</span>`}</td>
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
      // a group shows its first few files; the rest are on the repository's own files page. A path filter shows every match.
      const cut = g => !q && g.fs.length > FILES_PEEK;
      const seeAll = g => `<button class="btn line small see-all" data-showfiles="${esc(g.x.name)}">See all ${g.fs.length}</button>`;
      // under the cut list, a row that says how many more there are; it opens the same page as "See all"
      const more = g => cut(g) ? `<button class="more-files" data-showfiles="${esc(g.x.name)}">${icon("more")}<span>${g.fs.length - FILES_PEEK} more ${g.fs.length - FILES_PEEK === 1 ? "file" : "files"}</span><span class="ink-3">· See all ${g.fs.length}</span></button>` : "";
      $("#fl").innerHTML = groups.map(g => many ? groupHtml(g.x, pl(g.fs.length, "file"), `<ul class="files" data-fill="${esc(g.x.name)}"></ul>${more(g)}`, "f:" + g.x.name, cut(g) ? seeAll(g) : "")
        : `<ul class="files" data-fill="${esc(g.x.name)}"></ul>${more(g)}`).join("");
      groups.forEach(g => paged($(`#fl ul[data-fill="${CSS.escape(g.x.name)}"]`), (cut(g) ? g.fs.slice(0, FILES_PEEK) : g.fs).map(f => fileRow(g.x, f)), 60));
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
// a <details class="smooth"> glides open and shut like the steps do, instead of the browser's instant jump
function toggleSmooth(d) {
  const body = d.querySelector(":scope > summary + *");
  if (!body || calm()) { d.open = !d.open; return; }
  body.getAnimations().forEach(a => a.cancel());
  // tall bodies (a long review) get a little longer, so they don't fly open
  const ms = base => base + Math.min(260, body.scrollHeight / 12);
  if (!d.open) {
    d.open = true;
    body.animate([{ height: "0px", opacity: 0 }, { height: body.scrollHeight + "px", opacity: 1 }], { duration: ms(380), easing: "cubic-bezier(.33, .8, .25, 1)" });
  } else {
    d.classList.add("closing");
    const a = body.animate([{ height: body.scrollHeight + "px", opacity: 1 }, { height: "0px", opacity: 0 }], { duration: ms(320), easing: "cubic-bezier(.45, 0, .3, 1)" });
    a.onfinish = () => { d.open = false; d.classList.remove("closing"); };
    a.oncancel = () => d.classList.remove("closing");
  }
}

function mainClicks(e) {
  const t = e.target;
  const sm = t.closest("details.smooth > summary");
  if (sm) { e.preventDefault(); toggleSmooth(sm.parentElement); return; }
  const sh = t.closest("#smx .smx-h");
  if (sh && !t.closest("input, label, [data-copy], [data-copy-all]")) { toggleGroup(sh.closest(".gbox")); syncSmx(); return; }
  const sf = t.closest("[data-showfiles]");
  if (sf) { showFiles(sf.dataset.showfiles); return; }
  const su = t.closest("[data-setup]");
  if (su) { showSetup(su.dataset.setup.split("|")); return; }
  if (t.closest("[data-logtime]")) { logDialog(report.key); return; }
  if (t.closest("[data-copychk]")) { copyChecklist(); return; }
  if (t.closest("[data-movejira]")) { moveDialog(report.key); return; }
  const os = t.closest("[data-openstep]");
  if (os) { goToStep(os.dataset.openstep); return; }
  if (t.closest("[data-connect]")) { openSettings(); return; }
  const tog = t.closest(".gtoggle");
  if (tog) { toggleGroup(tog.closest(".gbox")); syncAll(); syncSmx(); return; }
  if (t.closest("#smxAll")) {
    const open = $("#smxAll").textContent === "Expand all";
    $$("#smx .gbox").forEach(g => { if ((g.dataset.open === "true") !== open) toggleGroup(g, open); });
    syncSmx(); return;
  }
  // only the open buttons: steps and groups also carry data-open (their expanded state), which must not count
  const op = t.closest("button[data-open][data-repo]");
  if (op && !op.closest("#pop") && !op.closest(".dpage:not(.fpage)")) { openLocal(op.dataset.repo, op.dataset.open, op.dataset.path); return; }
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
  // from a service's files page, Back returns there instead of to the ticket
  diffState = { repo, path, shas, data: null, mode: "net", scroll: $("#main").scrollTop, from: filesState };
  history.pushState(null, "", `#${report.key}/diff`);
  renderDiffPage();
}

function leaveDiff() {
  const y = diffState?.scroll || 0, from = diffState?.from;
  diffState = null;
  if (from) { filesState = from; history.pushState(null, "", `#${report.key}/files/${encodeURIComponent(from.repo)}`); renderFilesPage(); $("#main").scrollTop = y; return; }
  history.pushState(null, "", `#${report.key}`);
  renderTicket();
  $("#main").scrollTop = y;
}

async function renderDiffPage() {
  const d = diffState; if (!d) return;
  const name = d.path.split("/").pop(), dir = d.path.slice(0, d.path.length - name.length);
  $("#main").innerHTML = `<div class="dpage">
    <div class="dp-h">
      <button class="btn dp-back" id="dpBack">${icon("back")}${esc(report.key)}</button>
      <div class="dp-t"><div class="dp-file">${repoTag(d.repo)}<span class="mono"><span class="ink-3">${esc(dir)}</span>${esc(name)}</span><button class="btn only small cp" data-copy="${esc(d.path)}" title="Copy the path" aria-label="Copy the path">${icon("copy")}</button></div>
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
    // one spinner, on the choice being loaded: switching again while it loads moves it instead of adding another
    $$(".seg [data-kids]").forEach(x => { x.setAttribute("aria-checked", x === b); x.querySelectorAll(".spinner").forEach(sp => sp.remove()); });
    b.insertAdjacentHTML("afterbegin", `<span class="spinner"></span>`);
    load(r.key, false, true);
  });
  $("#recheck").onclick = () => load(r.key, true);
  $("#fetchNow")?.addEventListener("click", () => load(r.key, true));
  $("#voidFetch")?.addEventListener("click", () => load(r.key, true));
  $("#voidKids")?.addEventListener("click", () => { includeKids = true; load(r.key, false); });
  $("#copyCmd")?.addEventListener("click", async e => { try { await navigator.clipboard.writeText(e.currentTarget.dataset.cmd); toast("Command copied."); } catch { fail("Couldn't copy."); } });
  $$("[data-go]").forEach(b => b.onclick = () => select(b.dataset.go));
  wirePop($("#more"), $("#pop"));
  $("#moveJira")?.addEventListener("click", () => moveDialog(r.key));
  wireSteps();
}

// the handover is the checklist, copied as Markdown and pasted where the tester reads it
async function copyChecklist() {
  try { await navigator.clipboard.writeText(await api(`/api/markdown?key=${encodeURIComponent(report.key)}`)); toast("Checklist copied."); }
  catch (e) { fail(`Couldn't copy: ${esc(e.message)}`); }
}

// the steps list is repainted on its own when the open merge requests arrive, so its wiring stands alone
function wireSteps() {
  $$("[data-mktag]").forEach(b => b.onclick = () => createTag(b.dataset.repo, b.dataset.mktag));
  $$(".stp-h").forEach(h => h.onclick = () => {
    const s = h.closest(".stp"), open = s.dataset.open !== "true";
    s.dataset.open = open; h.setAttribute("aria-expanded", open); slide(s.querySelector(".stp-b"), open); if (open) openStep = s.dataset.id;
  });
}


// checks every one of your tickets in turn; while it runs the button becomes Stop (the current ticket finishes, the rest are skipped)
let stopAll = false;
async function checkAll() {
  const btn = $("#checkAll");
  if (checking) { stopAll = true; btn.disabled = true; btn.innerHTML = `<span class="spinner"></span>Stopping`; return; }
  if (!tickets?.length) return;
  checking = true; stopAll = false;
  btn.classList.add("danger"); btn.innerHTML = `${icon("x")}Stop`; btn.title = "Stop after the ticket being checked";
  let done = 0;
  for (let k = 0; k < tickets.length && !stopAll; k++) {
    const t = tickets[k]; t._busy = true; renderList();
    $("#progress").innerHTML = `<span class="spinner"></span><span class="pt" title="${k === 0 ? "Fetching every repository, then checking" : "Checking"} ${esc(t.key)}">${k === 0 ? "Fetching, then checking" : "Checking"} ${k + 1} of ${tickets.length} · <span class="mono">${esc(t.key)}</span></span>`;
    try {
      const r = await api(`/api/manifest?key=${encodeURIComponent(t.key)}&fetch=${k === 0 ? 1 : 0}&children=1&mine=1`);
      t.lastBuild = { generated: r.generated, stations: r.stations };
      if (t.key === selected) { r.fetched = true; report = r; renderTicket(); }
      done++;
    } catch { }
    t._busy = false;
  }
  const stopped = stopAll;
  checking = false; stopAll = false;
  btn.disabled = false; btn.classList.remove("danger"); btn.innerHTML = `${icon("checkall")}Check all`; btn.title = "Fetch every repository, then check all your tickets";
  $("#progress").textContent = "";
  renderList();
  toast(stopped ? `Stopped. ${pl(done, "ticket")} of ${tickets.length} checked.` : `All ${pl(tickets.length, "ticket")} checked.`);
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
  wirePop($("#dayBtn"), $("#weekPop"), renderWeek);
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
  $$("#weekPop [data-k]").forEach(b => b.onclick = () => { showPop($("#weekPop"), false); select(b.dataset.k); });
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

function logDialog(key, pre = {}) {
  const d = $("#settings"), today = localDay(new Date());
  const left = week?.days.find(x => x.date === week.today);
  const gap = left ? Math.max(0, left.target - left.seconds) : 0;
  d.innerHTML = `<div class="dlg-in">
    <h2>${key ? `Log time on ${esc(key)}` : "Log time"}</h2>
    ${key ? "" : `<label class="fld"><span>Ticket</span><select id="lgK" aria-label="Ticket" data-search="Search by key or title">${(tickets || []).map(t => `<option value="${esc(t.key)}">${esc(t.key)} · ${esc(t.summary)}</option>`).join("")}</select></label>`}
    <p class="intro">Added to Jira as a worklog under your name.${gap ? ` You have ${hm(gap)} left to reach today's target.` : ""}</p>
    <div class="fld"><span>How long</span>
      <div class="quick">${[15, 30, 60, 120, 240].map(m => `<button class="chip" data-m="${m}">${hm(m * 60)}</button>`).join("")}${gap ? `<button class="chip" data-m="${Math.round(gap / 60)}">Rest of today (${hm(gap)})</button>` : ""}</div>
      <div class="hm"><input id="lgH" type="number" min="0" max="24" value="1" aria-label="Hours"><span>h</span><input id="lgM" type="number" min="0" max="59" step="5" value="0" aria-label="Minutes"><span>m</span></div>
      <span class="err" id="lgErr"></span></div>
    <label class="fld"><span>Day</span><input id="lgD" class="date" value="${esc(pre.date || today)}" readonly></label>
    <label class="fld"><span>What you did <span class="ink-3">(optional)</span></span><textarea id="lgC" rows="3">${esc(pre.comment || "")}</textarea></label>
  </div>
  <div class="dlg-f"><span class="msg" id="lgMsg"></span><button class="btn" id="lgCancel">Cancel</button><button class="btn success" id="lgSave">Log time</button></div>`;
  $$("#settings .quick [data-m]").forEach(b => b.onclick = () => { const m = +b.dataset.m; $("#lgH").value = Math.floor(m / 60); $("#lgM").value = m % 60; $$("#settings .chip").forEach(x => x.classList.toggle("on", x === b)); });
  if (pre.minutes) { $("#lgH").value = Math.floor(pre.minutes / 60); $("#lgM").value = pre.minutes % 60; }
  datePicker($("#lgD"), { maxDate: today });
  if ($("#lgK")) { if (pre.key) $("#lgK").value = pre.key; jq("#lgK").select2({ width: "100%", dropdownParent: jq("#settings") }); }
  $("#lgCancel").onclick = () => d.close();
  $("#lgSave").onclick = async () => {
    key = key || $("#lgK")?.value;
    if (!key) { $("#lgErr").textContent = "Pick a ticket."; return; }
    const minutes = (+$("#lgH").value || 0) * 60 + (+$("#lgM").value || 0);
    if (minutes < 1) { $("#lgErr").textContent = "Enter how long you worked."; return; }
    if (minutes > 24 * 60) { $("#lgErr").textContent = "That's more than a day."; return; }
    $("#lgSave").disabled = true; $("#lgMsg").innerHTML = `<span class="spinner"></span>Saving to Jira`;
    try {
      await api("/api/time/log", { method: "POST", body: JSON.stringify({ key, minutes, date: $("#lgD").value, comment: $("#lgC").value }) });
      d.close(); toast(`Logged ${hm(minutes * 60)} on ${esc(key)}.`);
      if (report?.key === key) loadTicketTime(key);
      loadWeek().then(() => { if (section === "worklogs") renderWorklogs(wlWeek, wlDay); });
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
    ticketMrs = { key, list };
    if (applyOpenMrs(report)) repaintProgress();
    if ($("#figs")) renderEvidence();
    const open = list.reduce((n, m) => n + m.discussions.filter(d => d.resolvable && !d.resolved).length, 0);
    // filter by GitLab state; the counts show what each filter holds, and states with none aren't offered
    const F = [["all", "All", () => true], ["opened", "Open", m => m.state === "opened"], ["merged", "Merged", m => m.state === "merged"], ["closed", "Closed", m => m.state === "closed"]]
      .map(([id, l, f]) => [id, l, f, list.filter(f).length]).filter(([id, , , c]) => id === "all" || c);
    // the filter the developer chose is kept as is; a ticket that has none of that state just shows All this time
    let crvFilter = F.some(([id]) => id === crvWant) ? crvWant : "all";
    box.innerHTML = `<h3 class="h3">Code review<span>${list.length ? `${pl(list.length, "merge request")}${open ? ` · ${pl(open, "open comment")}` : ""}` : "no merge requests found"}</span></h3>
      ${F.length > 2 ? `<div class="tabs crv-f" role="tablist" aria-label="Merge request status">${F.map(([id, l, , c]) => `<button role="tab" aria-selected="${crvFilter === id}" data-crvf="${id}">${l}<span class="c">${c}</span></button>`).join("")}</div>` : ""}
      <div id="crvList"></div>`;
    if (!list.length) { $("#crvList").innerHTML = `<div class="pane-empty"><b>No merge requests yet</b><span>Reviews and comments on this ticket's merge requests will show up here.</span></div>`; return; }
    const draw = () => paged($("#crvList"), list.filter(F.find(([id]) => id === crvFilter)[2]).map(m => {
      const st = m.state === "merged" ? "done" : m.state === "closed" ? "skip" : m.reviewers.some(r => r.state === "requested_changes") ? "todo" : isApproved(m) ? "done" : "wait";
      return `<article class="mrc">
        <header>${mark(st, false)}${repoTag(m.repo)}<a class="mono ln" href="${url(m.url)}" target="_blank" rel="noopener">!${m.iid}</a><span class="t">${esc(m.title)}</span><span class="ink-3">${esc(m.state)}</span></header>
        <div class="rvs">${m.reviewers.map(r => { const s = RS[r.state] || ["wait", esc(r.state)]; return `<span class="rv ${s[0]}">${mark(s[0], false)}${esc(r.user.name)}<span class="ink-3"> · ${s[1]}</span></span>`; }).join("") || `<span class="ink-3">No reviewers assigned</span>`}
          ${m.approvals && m.state === "opened" ? `<span class="ink-3">${m.approvals.left ? `${pl(m.approvals.left, "approval")} still needed` : isApproved(m) ? "Approved" : "Nobody has approved it yet"}</span>` : ""}</div>
        ${m.discussions.length ? `<details class="talks smooth"><summary>${icon("chev", "ico chev")}${pl(m.discussions.length, "discussion")}${m.discussions.some(d => d.resolvable && !d.resolved) ? ` · <span class="open-n">${m.discussions.filter(d => d.resolvable && !d.resolved).length} open</span>` : ""}</summary>
          <div class="sm-b">${m.discussions.map(d => discussionHtml(d, m.repo, !!report.repos.find(x => x.name === m.repo))).join("")}</div></details>` : ""}
      </article>`;
    }), 10);
    draw();
    $$("#crv [data-crvf]").forEach(b => b.onclick = () => {
      crvFilter = crvWant = b.dataset.crvf;
      $$("#crv [data-crvf]").forEach(x => x.setAttribute("aria-selected", x === b));
      draw(); soften($("#crvList"));
    });
    soften(box);
  } catch (e) { box.innerHTML = `<h3 class="h3">Code review</h3><p class="ink-3" style="margin:0">${esc(e.message)}</p>`; }
}
// open merge requests first, until the developer picks another filter
let crvWant = "opened";

/* ── code review (GitLab) ── */
let railMode = "mine", reviews = null, reviewing = null, reviewView = "all";

async function loadReviews() {
  railMode = "review";
  $("#railTitle").textContent = "To review";
  $("#railTools").innerHTML = "";
  $("#views").innerHTML = "";
  $("#list").innerHTML = Array(6).fill(`<div class="ghost-row"></div>`).join("");
  try { reviews = await api("/api/reviews"); }
  catch (e) { reviews = null; $("#list").innerHTML = emptyNote("Couldn't load your reviews", esc(e.message), `<button class="btn line small" id="rvRetry">Try again</button>`); $("#rvRetry").onclick = loadReviews; return; }
  renderList();
  soften($("#list"));
}

function renderReviewList() {
  $("#railTitle").textContent = "To review";
  $("#railTools").innerHTML = "";   // story / readiness grouping belongs to My tickets
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
  selected = null; report = null; diffState = null; filesState = null; setupState = null;
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
    document.title = `!${iid} · ${r.repo} · greenlight`;
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
        ${checkGroups(r.checklist).map(([name, list]) => `<div class="ck-grp"><h4>${esc(name)}${list.some(c => c.state === "todo") ? `<span class="n">${pl(list.filter(c => c.state === "todo").length, "action")}</span>` : ""}</h4>
          <ul class="its review-list">${list.map(c => `<li class="it"><div class="lead"><span class="dot ${c.state}"></span><span class="tx">${esc(c.title)}</span>${c.detail ? `<span class="dt">${esc(c.detail)}</span>` : ""}</div>${c.url ? `<a class="btn line small" href="${url(c.url)}" target="_blank" rel="noopener">Open${icon("out")}</a>` : ""}</li>`).join("")}</ul></div>`).join("")}
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
  $("#rvApprove")?.addEventListener("click", async () => { if (await confirmDialog(`Approve !${r.iid}?`, `Adds your approval in GitLab${r.key ? ` and notes it on ${esc(r.key)} in Jira` : ""}.${todoCount(r) ? ` ${pl(todoCount(r), "checklist item")} still says it needs attention.` : ""}`, "Approve", "success")) reviewAct("approve"); });
  $("#rvUnapprove")?.addEventListener("click", async () => { if (await confirmDialog(`Revoke your approval of !${r.iid}?`, "Removes your approval in GitLab.", "Revoke approval", "danger")) reviewAct("unapprove"); });
  $("#rvChanges").onclick = () => reviewDialog(true);
  $("#rvComment").onclick = () => reviewDialog(false);
  wirePop($("#more"), $("#pop"));
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
    ${d.notes.map(n => `<div class="note"><div class="by"><b>${esc(n.author?.name || "")}</b><span class="ink-3">${esc(ago((n.created || "").replace("T", " ").slice(0, 16)) || "")}</span>${n.body ? copyBtn(n.body, "Copy the comment") : ""}</div><div class="body">${esc(n.body || "").replace(/\n/g, "<br>")}</div></div>`).join("")}
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
const openFind = () => { if (status?.account) jq("#finderSel").select2("open"); };

/* ─────────────────────────── Glim ───────────────────────────
   The mascot, drawn as a small vector so it stays crisp and themed at every size. Same character as the
   reference: lime helmet, white visor rim, dark screen face, ear pods and antennae. Moods change only the face. */
function glim(mood = "welcome", label = "") {
  const a = label ? `role="img" aria-label="${esc(label)}"` : `aria-hidden="true"`;
  const eye = (x, y) => `<circle cx="${x}" cy="${y}" r="3.7" fill="#0E0E0E" stroke="#B9C0BA" stroke-width="1.2"/><circle cx="${x - 1.1}" cy="${y - 1.1}" r="1" fill="#fff" opacity=".85"/>`;
  const faces = {
    welcome:  `<g class="blink">${eye(25, 29)}${eye(39, 29)}</g><path d="M28.5 36.5q3.5 2.6 7 0" fill="none" stroke="#EDE6DA" stroke-width="2" stroke-linecap="round"/>`,
    success:  `<path d="M21.5 30.5q3.5-4.2 7 0M35.5 30.5q3.5-4.2 7 0" fill="none" stroke="#EDE6DA" stroke-width="2.2" stroke-linecap="round"/><path d="M27 35.5q5 5 10 0z" fill="#EDE6DA"/>`,
    thinking: `${eye(27, 27)}${eye(41, 27)}<circle cx="33" cy="37" r="1.7" fill="none" stroke="#EDE6DA" stroke-width="1.6"/>`,
    scanning: `<g class="scan">${eye(25, 29)}${eye(39, 29)}</g><path d="M29.5 37h5" stroke="#EDE6DA" stroke-width="2" stroke-linecap="round"/>`,
    warning:  `${eye(25, 30)}${eye(39, 30)}<path d="M21.5 24.5l6 1.6M42.5 24.5l-6 1.6" stroke="#EDE6DA" stroke-width="1.8" stroke-linecap="round"/><path d="M28 38q2-1.6 4 0t4 0" fill="none" stroke="#EDE6DA" stroke-width="1.8" stroke-linecap="round"/>`,
    empty:    `<path d="M22 30h6.5M35.5 30h6.5" stroke="#EDE6DA" stroke-width="2.2" stroke-linecap="round"/><path d="M29.5 37h5" stroke="#EDE6DA" stroke-width="2" stroke-linecap="round"/>`,
  };
  const cheeks = mood === "welcome" || mood === "success" ? `<ellipse cx="20.5" cy="35" rx="2.6" ry="1.6" fill="#E2553E" opacity=".45"/><ellipse cx="43.5" cy="35" rx="2.6" ry="1.6" fill="#E2553E" opacity=".45"/>` : "";
  return `<svg class="glim" viewBox="0 0 64 64" ${a}>
    <path d="M16 12 12.5 4.5M48 12l3.5-7.5" stroke="#DCE1D8" stroke-width="2" stroke-linecap="round"/>
    <rect x="8" y="8" width="48" height="45" rx="18" fill="#9ACD3C"/>
    <path d="M14 14q18-7 36 0" fill="none" stroke="#C7EB7C" stroke-width="2.4" stroke-linecap="round" opacity=".8"/>
    <rect x="12.5" y="12.5" width="39" height="36" rx="13.5" fill="#F4F6F0"/>
    <rect x="15.5" y="15.5" width="33" height="30" rx="10.5" fill="#2A1B15"/>
    <path d="M18 19.5q14-4 28 0" fill="none" stroke="#fff" stroke-width="1.4" opacity=".1" stroke-linecap="round"/>
    ${cheeks}${faces[mood] || faces.welcome}
  </svg>`;
}

/* ─────────────────────────── shell: theme and counts ─────────────────────────── */
let section = "tickets";
const localDay = d => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;

// one button: shows where it takes you (a sun in dark mode, a moon in light mode)
function applyTheme(pref) {
  if (pref !== "light") pref = "dark";
  document.documentElement.dataset.theme = pref;
  document.documentElement.dataset.themePref = pref;
  const b = $("#themeBtn"), to = pref === "dark" ? "light" : "dark";
  b.innerHTML = icon(to === "light" ? "sun" : "moon", "ico");
  b.title = b.ariaLabel = `Switch to ${to} mode`;
}
// switching theme floods the page from the toggle like liquid, with a soft moving edge (where the browser can)
$("#themeBtn").onclick = () => {
  const to = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
  try { localStorage.setItem("gl-theme", to); } catch { }
  if (!document.startViewTransition || calm()) return applyTheme(to);
  const b = $("#themeBtn").getBoundingClientRect(), x = b.left + b.width / 2, y = b.top + b.height / 2;
  const root = document.documentElement.style;
  root.setProperty("--gl-x", `${x}px`); root.setProperty("--gl-y", `${y}px`);
  root.setProperty("--gl-end", `${Math.hypot(Math.max(x, innerWidth - x), Math.max(y, innerHeight - y)) + 160}px`);
  document.startViewTransition(() => applyTheme(to));
};
applyTheme(document.documentElement.dataset.themePref || "dark");

$("#brandGlim").innerHTML = glim("welcome");$("#brandHome").onclick = e => { e.preventDefault(); history.pushState(null, "", "#tickets"); route(); };


function navCounts() {
  const need = (tickets || []).filter(t => ["todo", "wait"].includes(overall(statesOf(t.lastBuild)))).length;
  const nt = $("#nTickets"); nt.textContent = need || (tickets?.length ?? ""); nt.className = `cnt ${need ? "hot" : ""}`; nt.title = need ? `${need} need you` : "";
  const waiting = (reviews || []).filter(r => !r.approvedByMe).length;
  const nr = $("#nReviews"); nr.textContent = waiting || ""; nr.className = `cnt ${waiting ? "hot" : ""}`; nr.hidden = !waiting;
  const today = week?.days.find(d => d.date === week.today);
  const left = today?.workday ? Math.max(0, today.target - today.seconds) : 0;
  const ntm = $("#nTime"); ntm.textContent = left ? hm(left) : ""; ntm.hidden = !left; ntm.className = "cnt"; ntm.title = left ? `${hm(left)} left to log today` : "";
}

function setSection(s) {
  section = s;
  $$(".nav-i").forEach(a => { if (a.dataset.nav === s) a.setAttribute("aria-current", "page"); else a.removeAttribute("aria-current"); });
  // the review list only exists once GitLab is connected; before that Reviews is a single "connect" page
  // a list only exists once its account is connected: before that the page is a single "connect" screen
  const listy = (s === "tickets" && !!status?.account) || (s === "reviews" && !!status?.gitlab);
  $("#split").classList.toggle("solo", !listy);
  if (!listy) $("#split").classList.remove("open");
  if (listy) {
    const want = s === "reviews" ? "review" : "mine";
    if (railMode !== want) { railMode = want; if (want === "review" && !reviews && status?.gitlab) loadReviews(); else renderList(); }
  }
  $("#main").scrollTop = 0;
}

/* ─────────────────────────── routing ───────────────────────────
   #tickets (home) · #MM-1 · #MM-1/diff · #MM-1/files/<repo> · #MM-1/setup · #reviews · #review/<project>/<iid> · #worklogs[/<monday>] */
function route() {
  const raw = decodeURIComponent(location.hash.slice(1)), low = raw.toLowerCase();
  if (raw.startsWith("review/")) { setSection("reviews"); if (!status?.gitlab) return reviewsBlank(); reviewRoute(); return; }
  if (low === "" || low === "tickets" || low === "overview") { setSection("tickets"); selected = null; report = null; diffState = null; filesState = null; setupState = null; $("#split").classList.remove("open"); renderList(); return blank(); }
  if (low === "reviews") { setSection("reviews"); reviewing = null; $("#split").classList.remove("open"); return reviewsBlank(); }
  if (low.startsWith("worklogs")) { setSection("worklogs"); return renderWorklogs(raw.split("/")[1] || null); }
  const [k, sub] = raw.toUpperCase().split("/");
  setSection("tickets");
  if (!status?.account) return blank();   // tickets come from Jira: nothing to show until it's connected
  if (sub === "DIFF" && diffState && report?.key === k) return renderDiffPage();
  // repository names are case-sensitive, so it's read from the raw hash
  if (sub === "SETUP" && report?.key === k) { diffState = null; filesState = null; setupState = setupState || { scroll: 0 }; return renderSetupPage(); }
  if (sub === "FILES" && report?.key === k && raw.split("/")[2]) { diffState = null; filesState = { repo: raw.split("/")[2], scroll: filesState?.scroll || 0, query: filesState?.query || "" }; return renderFilesPage(); }
  if (k && report?.key === k && !sub) { diffState = null; filesState = null; setupState = null; $("#split").classList.add("open"); renderTicket(); return; }
  if (k) load(k, false); else blank();
}
// #review/<group/project>/<iid> opens a merge request; project paths are case-sensitive, so they are not upper-cased
function reviewRoute() {
  const raw = decodeURIComponent(location.hash.slice(1));
  if (!raw.startsWith("review/")) return false;
  const rest = raw.slice(7), cut = rest.lastIndexOf("/");
  if (cut > 0 && +rest.slice(cut + 1) > 0) { loadReview(rest.slice(0, cut), +rest.slice(cut + 1)); return true; }
  return false;
}
window.addEventListener("popstate", route);

function reviewsBlank() {
  document.title = "Reviews · greenlight";
  if (!status?.gitlab) {
    $("#main").innerHTML = `<div class="blank"><div class="blank-in">${glim("thinking")}<h2>Connect GitLab for reviews</h2>
      <p>Merge requests waiting for your review, and the reviews on yours, appear here once GitLab is connected.</p>
      <div class="void-acts"><button class="btn primary" id="glGo">${icon("review")}Connect GitLab</button></div></div></div>`;
    $("#glGo").onclick = openSettings; return;
  }
  const waiting = (reviews || []).filter(r => !r.approvedByMe).length;
  $("#main").innerHTML = `<div class="blank"><div class="blank-in">${glim(waiting ? "welcome" : "success")}<h2>${waiting ? `${pl(waiting, "merge request")} waiting for you` : "Nothing waiting for your review"}</h2>
    <p>${waiting ? "Pick one from the list. Each review shows a checklist, the changes side by side, discussions and the tags." : "When someone asks you to review a merge request, it shows up in the list."}</p></div></div>`;
}

/* standup text for a day: what I worked on, merged, reviewed and logged */
async function loadStandup(box, date, dayEl, copyBtn) {
  try {
    const s = await api(`/api/standup${date ? `?date=${date}` : ""}`);
    if (!box.isConnected) return;
    if (dayEl) dayEl.textContent = day(s.date);
    const lines = [];
    for (const w of s.work) {
      const parts = [];
      if (w.commits.length) parts.push(`${pl(w.commits.length, "commit")}${[...new Set(w.commits.map(c => c.repo))].length ? ` in ${[...new Set(w.commits.map(c => c.repo))].join(", ")}` : ""}`);
      if (w.merged.length) parts.push(`merged ${w.merged.length > 1 ? pl(w.merged.length, "branch", "branches") : "the branch"}`);
      if (w.logged) parts.push(`${hm(w.logged)} logged`);
      lines.push(`• ${w.key} ${w.summary || ""}${w.status ? ` (${w.status})` : ""}\n    ${parts.join(", ") || "worked on it"}`);
    }
    if (s.reviews.length) lines.push(`• Code review: ${s.reviews.map(r => `${r.action} "${r.title}"`).slice(0, 4).join("; ")}`);
    if (s.otherCommits.length) lines.push(`• ${pl(s.otherCommits.length, "commit")} without a ticket key`);
    const text = lines.length ? `${day(s.date)}:\n${lines.join("\n")}` : `Nothing recorded on ${day(s.date)}: no commits, worklogs or reviews.`;
    box.textContent = text;
    if (copyBtn) { copyBtn.hidden = !lines.length; copyBtn.onclick = async () => { try { await navigator.clipboard.writeText(text); toast("Standup copied."); } catch { fail("Couldn't copy."); } }; }
  } catch (e) { if (box.isConnected) box.textContent = `Couldn't gather the standup: ${e.message}`; }
}

/* ─────────────────────────── worklogs ─────────────────────────── */
let wlWeek = null, wlDay = null;
const mondayOf = d => { const x = new Date(d); x.setDate(x.getDate() - ((x.getDay() + 6) % 7)); return localDay(x); };
const addDays = (s, n) => { const d = new Date(s + "T12:00:00"); d.setDate(d.getDate() + n); return localDay(d); };

async function renderWorklogs(from, pickDay) {
  document.title = "Worklogs · greenlight";
  if (!status?.account) {
    $("#main").innerHTML = `<div class="blank"><div class="blank-in">${glim("thinking")}<h2>Connect Jira for worklogs</h2>
      <p>Your logged time, the daily target and suggestions from your commits come from Jira, so they appear once it's connected.</p>
      <div class="void-acts"><button class="btn success" id="wlc">Connect Jira</button></div></div></div>`;
    $("#wlc").onclick = openSettings; return;
  }
  const today = localDay(new Date());
  wlWeek = from && /^\d{4}-\d{2}-\d{2}$/.test(from) ? mondayOf(from + "T12:00:00") : mondayOf(new Date());
  wlDay = pickDay || (wlWeek === mondayOf(new Date()) ? today : wlWeek);
  $("#main").innerHTML = `<div class="page wide-page">
    <div class="ph"><div><h1>Worklogs</h1><p>Your time in Jira against the ${hm((status.dailyTargetHours || 5) * 3600)} daily target.</p></div>
      <div class="acts">
        <div class="seg"><button id="wlPrev" aria-label="Previous week">${icon("back")}</button><button id="wlPick" aria-label="Pick a week">${icon("calendar")}<span id="wlLabel"></span></button><button id="wlNext" aria-label="Next week" style="transform:scaleX(-1)">${icon("back")}</button></div>
        <input id="wlDate" class="sr" aria-hidden="true" tabindex="-1">
        <button class="btn line" id="wlRemind" hidden>${icon("bell")}Desktop reminders</button>
        <button class="btn primary" id="wlLog">${icon("plus")}Log time</button>
      </div></div>
    <div id="wlBody" style="margin-top:20px"><div class="wl-week">${Array(7).fill(`<div class="wl-day"><div class="line" style="height:14px;width:60%"></div></div>`).join("")}</div></div></div>`;
  $("#wlLabel").textContent = `${day(wlWeek)} – ${day(addDays(wlWeek, 6))}`;
  $("#wlPrev").onclick = () => { history.pushState(null, "", `#worklogs/${addDays(wlWeek, -7)}`); route(); };
  $("#wlNext").onclick = () => { history.pushState(null, "", `#worklogs/${addDays(wlWeek, 7)}`); route(); };
  const fp = datePicker($("#wlDate"), { maxDate: today, onChange: d => { if (d[0]) { history.pushState(null, "", `#worklogs/${mondayOf(d[0])}`); route(); } }, positionElement: $("#wlPick") });
  $("#wlPick").onclick = () => fp?.open();
  $("#wlLog").onclick = () => logDialog(null, { date: wlDay });
  $("#wlRemind").outerHTML = `<label class="tgl" title="At ${esc(status.reminderAt || "17:00")} on working days, if today is short"><input type="checkbox" id="wlRemindOn" ${reminderOn() ? "checked" : ""}><span class="knob"></span>End-of-day reminder</label>`;
  $("#wlRemindOn").onchange = async e => {
    const on = e.target.checked;
    try { localStorage.setItem("gl-remind", on ? "on" : "off"); } catch { }
    if (on && "Notification" in window && Notification.permission === "default") await Notification.requestPermission();
    toast(!on ? "Reminders are off." : "Notification" in window && Notification.permission === "granted"
      ? `You'll get a reminder at ${esc(status.reminderAt || "17:00")} if today is short.`
      : `You'll get a reminder inside Greenlight at ${esc(status.reminderAt || "17:00")}. Desktop notifications are blocked in this browser's site settings.`);
  };
  let wk;
  try { wk = await api(`/api/time/week?from=${wlWeek}`); } catch (e) { $("#wlBody").innerHTML = errBox("Couldn't read your worklogs", e.message, () => renderWorklogs(from)); return; }
  if (section !== "worklogs") return;
  const goal = wk.days.filter(d => d.workday).reduce((n, d) => n + d.target, 0), done = wk.days.reduce((n, d) => n + d.seconds, 0);
  $("#wlBody").innerHTML = `
    <div class="wl-week" role="listbox" aria-label="Days">${wk.days.map(d => `<button class="wl-day ${d.workday ? "" : "off"} ${d.date === today ? "today" : ""}" aria-pressed="${d.date === wlDay}" data-day="${d.date}" ${d.date > today ? "disabled" : ""}>
      <span class="d">${d.weekday}<span>${day(d.date)}</span></span>
      <span class="tot">${d.seconds ? hm(d.seconds) : "–"}${d.workday ? `<small> / ${hm(d.target)}</small>` : ""}</span>
      ${d.workday ? `<span class="bar"><i class="${d.seconds >= d.target ? "met" : ""}" style="width:${Math.min(100, d.seconds / Math.max(1, d.target) * 100)}%"></i></span>` : ""}
      ${d.tickets.slice(0, 3).map(t => `<span class="tk"><span class="mono">${esc(t.key)}</span><span>${hm(t.seconds)}</span></span>`).join("")}${d.tickets.length > 3 ? `<span class="tk ink-3">+${d.tickets.length - 3} more</span>` : ""}
    </button>`).join("")}</div>
    <p class="ink-3" style="margin:12px 0 0">This week: <b style="color:var(--gl-text)">${hm(done)}</b> of ${hm(goal)}.${wk.days.filter(d => d.workday && d.date < today && d.seconds < d.target).length ? ` ${pl(wk.days.filter(d => d.workday && d.date < today && d.seconds < d.target).length, "day is", "days are")} short.` : ""}</p>
    <div class="grid2">
      <section class="card"><h2>Suggested for ${esc(day(wlDay))}<span class="c" id="sgSub"></span></h2><div id="sgList"><span class="s2-msg"><span class="spinner"></span>Reading your commits</span></div></section>
      <div class="stack">
        <section class="card"><h2>Logged on ${esc(day(wlDay))}</h2>${(() => { const d = wk.days.find(x => x.date === wlDay); return d?.tickets.length ? `<div class="mini-list">${d.tickets.map(t => `<button class="mini" data-k="${esc(t.key)}"><span></span><span class="t">${esc(t.summary || t.key)}</span><span class="r"><b>${hm(t.seconds)}</b></span><span class="s"><span class="mono">${esc(t.key)}</span></span></button>`).join("")}</div>` : `<p class="ink-3" style="margin:0">Nothing logged that day.</p>`; })()}</section>
        <section class="card"><h2>Standup<button class="more-l" id="wlSuCopy" hidden>Copy</button></h2><pre class="standup" id="wlSu"><span class="s2-msg"><span class="spinner"></span>Gathering</span></pre></section>
      </div>
    </div>`;
  soften($("#wlBody"));
  $$("#wlBody [data-day]").forEach(b => b.onclick = () => renderWorklogs(wlWeek, b.dataset.day));
  $$("#wlBody .mini[data-k]").forEach(b => b.onclick = () => select(b.dataset.k));
  loadStandup($("#wlSu"), wlDay, null, $("#wlSuCopy"));
  try {
    const sg = await api(`/api/time/suggest?date=${wlDay}`);
    if (!$("#sgList")) return;
    $("#sgSub").textContent = sg.target ? `${hm(sg.logged)} of ${hm(sg.target)} logged` : "";
    $("#sgList").innerHTML = !sg.tickets.length
      ? `<div class="pane-empty" style="padding:18px 0">${glim("empty")}<b>No commits that day</b><span>Suggestions come from the commits you made on each ticket.</span></div>`
      : `${sg.left ? `<p class="ink-3" style="margin:0 0 6px">${hm(sg.left)} still to log, shared by where you committed.</p>` : `<p class="ink-3" style="margin:0 0 6px">The day's target is met. These are the tickets you committed on.</p>`}
        ${sg.tickets.map(t => `<div class="sug"><span class="t">${esc(t.key)} · ${esc(t.summary || "")}</span><span class="h">${t.suggested ? hm(t.suggested) : ""}</span>
          <button class="btn ${t.suggested ? "primary" : "line"} small" data-log="${esc(t.key)}" data-min="${Math.max(15, Math.round(t.suggested / 60))}" data-c="${esc(t.subjects.join("; "))}">${icon("plus")}Log</button>
          <span class="s">${pl(t.commits, "commit")} ${esc(t.from)}–${esc(t.to)} in ${esc(t.repos.join(", "))}${t.logged ? ` · ${hm(t.logged)} already logged` : ""}</span></div>`).join("")}`;
    $$("#sgList [data-log]").forEach(b => b.onclick = () => logDialog(b.dataset.log, { minutes: +b.dataset.min, date: wlDay, comment: b.dataset.c }));
  } catch (e) { if ($("#sgList")) $("#sgList").innerHTML = `<p class="ink-3" style="margin:0">${esc(e.message)}</p>`; }
}

/* ─────────────────────────── System Manager setup on QA ───────────────────────────
   Every scope the ticket adds (one per row, ready to copy, with the service's AppName in front once you type it),
   and every new menu with its actions and the scopes to link to each. */
const appName = repo => { try { return localStorage.getItem("gl-app:" + repo) || ""; } catch { return ""; } };
const fullScope = (scope, repo) => (appName(repo) ? appName(repo).toLowerCase() + ":" : "") + scope;
const copyBtn = (text, label = "Copy") => `<button class="btn only small cp" data-copy="${esc(text)}" title="${label}" aria-label="${label}">${icon("copy")}</button>`;
const scopeCode = (e, cls = "") => `<code class="sc ${cls}" data-scope="${esc(e.scope)}" data-repo="${esc(e.repo)}">${esc(fullScope(e.scope, e.repo))}</code>`;
const verbTag = v => `<span class="vb vb-${esc((v || "any").toLowerCase())}">${esc(v || "ANY")}</span>`;
// one folding card, closed until opened: its whole header row toggles it, the controls on it still do their own thing
function smxCard(id, head, tools, body) {
  const open = groupOpen[id] === true;
  return `<div class="smx-card gbox" data-open="${open}" data-gid="${esc(id)}">
    <div class="smx-h"><button class="gtoggle" aria-expanded="${open}">${icon("chev", "ico chev")}${head}</button>${tools}</div>
    <div class="gbox-b" ${open ? "" : "hidden"}>${body}</div></div>`;
}
// collapse / expand every setup card at once
function syncSmx() {
  const b = $("#smxAll"); if (!b) return;
  b.textContent = $$("#smx .gbox").some(g => g.dataset.open === "true") ? "Collapse all" : "Expand all";
}

// lookup rows the code names by tag (categories, types, features, criteria...): one card per table
const dataState = d => d.seeded ? "seeded" : d.older ? "older" : "create";
function dataCards(data) {
  const groups = new Map();
  (data.rows || []).forEach(d => { const k = `${d.schema}.${d.table}`; if (!groups.has(k)) groups.set(k, []); groups.get(k).push(d); });
  const order = { create: 0, seeded: 1, older: 2 };
  const rank = rows => Math.min(...rows.map(d => order[dataState(d)]));
  return [...groups.entries()].sort((a, b) => rank(a[1]) - rank(b[1]) || a[0].localeCompare(b[0])).map(([k, rows]) => {
    rows.sort((a, b) => order[dataState(a)] - order[dataState(b)] || a.tag.localeCompare(b.tag));
    const n = rows.filter(d => dataState(d) === "create").length;
    const status = d => ({
      create: `<span class="dst">${mark("todo", false)}Create on QA</span>`,
      seeded: `<span class="dst" title="${esc(d.seeded || "")}">${mark("done", false)}A script adds it: ${esc((d.seeded || "").split("/").pop())}</span>`,
      older: `<span class="dst ink-3" title="It was on dev before this ticket's first commit, so QA most likely has it already.">Existed before this ticket</span>`,
    })[dataState(d)];
    return smxCard("smx:data:" + k, `<span class="smx-k">${esc(rows[0].kind)}</span><span class="ink-3">${esc(rows[0].schema)}</span>`,
      `<span class="ink-3">${n ? `${n} to create` : "nothing to create"}${rows.length > n ? ` · ${rows.length - n} handled` : ""}</span>`,
      `<div class="wide"><table class="tb smx-t"><thead><tr><th>Tag</th><th>Name on dev</th><th>On dev since</th><th>Used in</th><th>To do</th></tr></thead><tbody>
        ${rows.map(d => `<tr class="${dataState(d) === "create" ? "" : "soft"}"><td><div class="sc-cell"><code class="sc">${esc(d.tag)}</code>${copyBtn(d.tag, "Copy the tag")}</div></td>
          <td>${d.name ? esc(d.name) : `<span class="ink-3">-</span>`}</td><td class="nw ink-3">${esc(d.created || "-")}${d.tenants > 1 ? ` · ${d.tenants} tenants` : ""}</td>
          <td class="nw" title="${esc(d.repo)}: ${esc(d.file)}">${repoTag(d.repo)} <span class="ink-3 mono">${esc(d.file.split("/").pop())}</span></td><td class="nw">${status(d)}</td></tr>`).join("")}
      </tbody></table></div>`);
  }).join("");
}

// on the ticket page the section links to its own page (#MM-1/setup); on that page it doesn't
function setupHtml(r, onPage = false) {
  const s = r.setup || { services: [], menus: [] };
  const data = s.data || { rows: [] }, dataRows = data.rows || [];
  const nData = dataRows.filter(d => dataState(d) === "create").length;
  // the code names tags but they couldn't be looked up: say why, once
  const dataNote = data.status === "no-connection" ? `<p class="smx-note warn">${icon("alert")}<span>The code names ${pl(data.candidates, "tag")} that may be System Manager data (categories, types, features, criteria...). Set the <code>${esc(data.env || "")}</code> environment variable to the dev database's connection string to look them up.</span></p>`
    : data.status === "error" ? `<p class="smx-note warn">${icon("alert")}<span>Couldn't read the dev database to look up the tags the code names: ${esc(data.error)}</span></p>` : "";
  if (!s.services.length && !s.menus.length && !dataRows.length && !dataNote) return "";
  // the cards this ticket shows, by the ids smxCard gives them: only they decide Expand all / Collapse all
  const cardIds = [...s.services.map(sv => "smx:" + sv.repo), ...s.menus.map(m => "smx:" + m.uri), ...new Set(dataRows.map(d => `smx:data:${d.schema}.${d.table}`))];
  const nCards = cardIds.length;
  const nScopes = s.services.reduce((n, x) => n + x.added.length, 0);
  const row = (e, removed) => `<tr class="${removed ? "gone" : ""}"><td><div class="sc-cell">${scopeCode(e)}${removed ? `<span class="sc-tag">removed</span>` : copyBtn(fullScope(e.scope, e.repo), "Copy scope")}</div></td>
    <td class="nw">${verbTag(e.verb)} <code class="rt-path">${esc(e.route)}</code></td><td class="ink-3 nw">${esc(e.action)}</td></tr>`;
  return `<section class="smx" id="smx">
    <h3 class="h3">System Manager setup on QA<span>${[nScopes ? pl(nScopes, "scope") : "", s.menus.length ? pl(s.menus.length, "menu") : "", nData ? `${pl(nData, "data row")} to create` : ""].filter(Boolean).join(" · ")}</span>
      ${nCards > 1 ? `<button class="btn small" id="smxAll">${cardIds.some(id => groupOpen[id] === true) ? "Collapse all" : "Expand all"}</button>` : ""}
      ${onPage ? "" : `<button class="btn line small smx-page" data-setup="">${icon("out")}Open as a page</button>`}</h3>
    <p class="smx-note">Scopes register themselves when each service starts on QA. In the System Manager there: grant the scopes to the QA roles, create each menu with its actions, and link every action to the scopes listed with it. Create the data the code names (categories, types, features, criteria...) with the same tags it has on dev. Each copy button gives the exact text.</p>
    ${dataNote}
    ${s.services.map(sv => smxCard("smx:" + sv.repo, `${repoTag(sv.repo)}<span class="ink-3">${pl(sv.added.length, "new scope")}${sv.removed.length ? ` · ${sv.removed.length} removed` : ""}</span>`,
        `<label class="smx-pre" title="Scopes start with the service's Common:AppName, which isn't stored in git. Type it once; every copy includes it."><span>AppName</span><input data-pre="${esc(sv.repo)}" value="${esc(appName(sv.repo))}" placeholder="${esc((sv.tag || "e.g. omnicore").toLowerCase())}" spellcheck="false" autocomplete="off"></label>
        ${sv.added.length ? `<button class="btn line small" data-copy-all="${esc(sv.repo)}">${icon("copy")}Copy all ${sv.added.length}</button>` : ""}`,
      `<div class="wide"><table class="tb smx-t"><thead><tr><th>Scope</th><th>Endpoint</th><th>Controller action</th></tr></thead>
        <tbody>${sv.added.map(e => row(e, false)).join("")}${sv.removed.map(e => row(e, true)).join("")}</tbody></table></div>`)).join("")}
    ${s.menus.map(m => smxCard("smx:" + m.uri, `<span class="smx-k">Menu</span><code class="smx-uri">${esc(m.uri)}</code>`,
      `${copyBtn(m.uri, "Copy the menu URI")}<span class="ink-3">${m.page ? `page ${esc(m.page)} · ` : ""}${repoTag(m.repo)}</span>`,
      `<div class="wide"><table class="tb smx-t"><thead><tr><th>Action</th><th>Scopes to link</th></tr></thead><tbody>
        ${m.actions.map(a => `<tr><td class="nw"><div class="sc-cell"><code class="act">${esc(a.name)}</code>${copyBtn(a.name, "Copy the action name")}</div>${a.name.toUpperCase() === "VIEW" ? `<div class="ink-3 smx-hint">Needed to open the page</div>` : ""}</td>
          <td>${a.scopes.length ? `<div class="sc-list">${a.scopes.map(e => `<div class="sc-cell">${verbTag(e.verb)}${scopeCode(e)}${copyBtn(fullScope(e.scope, e.repo), "Copy scope")}</div>`).join("")}</div>` : `<span class="ink-3">None found: the page checks this action but calls no endpoint for it</span>`}</td></tr>`).join("")}
        ${m.unlinked.length ? `<tr class="warn"><td><span class="ink-2">Not matched to an action</span></td><td><div class="sc-list">${m.unlinked.map(e => `<div class="sc-cell">${verbTag(e.verb)}${scopeCode(e)}${copyBtn(fullScope(e.scope, e.repo), "Copy scope")}</div>`).join("")}</div></td></tr>` : ""}
      </tbody></table></div>
      ${m.unmatched.length ? `<p class="smx-note warn">${icon("alert")}<span>The page also calls ${m.unmatched.map(x => `<code>${esc(x)}</code>`).join(", ")}, which matches no protected endpoint in your repositories. Check whether it needs a scope.</span></p>` : ""}`)).join("")}
    ${dataCards(data)}
  </section>`;
}

// the branch against main and dev: still to merge, and how far behind
function branchHtml(b) {
  return `<div class="br"><code>${esc(b.name)}</code><div class="br-vs">${(b.vs || []).map(v => `<span class="bvs ${v.ahead ? "need" : "ok"}" title="Against ${esc(v.target)}: ${pl(v.ahead, "commit")} not merged into it yet; it has ${pl(v.behind, "commit")} this branch doesn't.">
    ${mark(v.ahead ? "todo" : "done", false)}<b>${esc(v.target)}</b> ${v.ahead ? `${v.ahead} to merge` : "merged"}${v.behind ? `<span class="bh"> · ${v.behind} behind</span>` : `<span class="bh"> · up to date</span>`}</span>`).join("") || (b.unmerged ? `<span class="bvs need">${b.unmerged} unmerged</span>` : "")}</div></div>`;
}

async function copyText(t) { try { await navigator.clipboard.writeText(t); toast(`Copied <code>${esc(t.length > 60 ? t.slice(0, 57) + "…" : t)}</code>`, 2200); } catch { fail("Couldn't copy."); } }
document.addEventListener("click", e => {
  const b = e.target.closest("[data-copy]"); if (b) { copyText(b.dataset.copy); return; }
  const all = e.target.closest("[data-copy-all]");
  if (all) { const sv = report?.setup?.services.find(x => x.repo === all.dataset.copyAll); if (sv) copyText(sv.added.map(x => fullScope(x.scope, x.repo)).join("\n")); }
});
// typing the AppName updates every scope of that service, and what its copy buttons copy
document.addEventListener("input", e => {
  const inp = e.target.closest("[data-pre]"); if (!inp) return;
  try { localStorage.setItem("gl-app:" + inp.dataset.pre, inp.value.trim()); } catch { }
  $$(`code.sc[data-repo="${CSS.escape(inp.dataset.pre)}"]`).forEach(c => {
    const full = fullScope(c.dataset.scope, c.dataset.repo);
    c.textContent = full;
    const btn = c.parentElement.querySelector("[data-copy]"); if (btn) btn.dataset.copy = full;
  });
});

/* ─────────────────────────── ticket extras: deployments and actions ─────────────────────────── */
/* Where the ticket's code runs. Nothing can be deployed before it's merged, so the section waits for the first merge;
   then it's one line per service (where it is, where it isn't yet), with the environment cards behind it. */
async function loadDeployments(key) {
  const box = $("#dep"); if (!box) return;
  const merged = new Set(report.repos.filter(x => x.commits.some(c => c.inDefault)).map(x => x.name));
  if (!merged.size) { box.remove(); return; }
  box.innerHTML = `<h3 class="h3">Where it's deployed</h3><div class="s2-msg"><span class="spinner"></span>Reading environments from GitLab</div>`;
  try {
    const list = await api(`/api/deployments?key=${encodeURIComponent(key)}`);
    if (report?.key !== key || !$("#dep")) return;
    if (!list.length) { box.innerHTML = `<h3 class="h3">Where it's deployed<span>no GitLab environments for these services</span></h3>`; return; }
    const isQa = e => /\b(qa|test|testing|uat)\b/i.test(`${e.name} ${e.tier || ""}`);
    const env = e => `${esc(e.name)}${e.ref ? ` <span class="ink-3">(${esc(e.ref)})</span>` : ""}`;
    const line = r => {
      const on = r.environments.filter(e => e.contains === true), off = r.environments.filter(e => e.contains === false), unsure = r.environments.filter(e => e.contains == null);
      const st = !merged.has(r.repo) ? "wait" : off.length || unsure.length ? (on.length ? "check" : "todo") : "done";
      const say = !merged.has(r.repo) ? `Not merged into the default branch yet, so not deployed`
        : [on.length ? `On ${on.map(env).join(", ")}` : "", off.length ? `not on ${off.map(env).join(", ")} yet` : "", unsure.length ? `can't tell for ${unsure.map(env).join(", ")}: fetch first` : ""].filter(Boolean).join(" · ");
      const noQa = !r.environments.some(isQa);
      return `<details class="dep-r smooth"><summary>${mark(st, false)}${repoTag(r.repo)}<span class="dep-say">${say.charAt(0).toUpperCase() + say.slice(1)}</span>
          ${noQa ? `<span class="dep-noqa" title="GitLab has no QA environment for this service, so Greenlight can't see what QA runs. Check the QA deployment separately.">No QA environment in GitLab</span>` : ""}${icon("chev", "ico chev")}</summary>
        <div class="envs">${r.environments.map(e => `<div class="env">
          <div class="en">${mark(e.contains === true ? "done" : e.contains === false ? "todo" : "check", false)}<a class="ln" href="${url(e.web)}" target="_blank" rel="noopener">${esc(e.name)}</a><span class="tier">${esc(e.tier || "")}</span></div>
          <div class="er"><code>${esc(e.ref || "")}</code><span>${esc(e.sha || "")}</span><span>${day(e.date)}</span>${e.status && e.status !== "success" ? `<span>${esc(e.status)}</span>` : ""}</div>
          <div class="has">${e.contains === true ? "Includes this work" : e.contains === false ? "Doesn't include all of it yet" : "Can't tell: fetch first"}</div></div>`).join("")}</div></details>`;
    };
    box.innerHTML = `<h3 class="h3">Where it's deployed<span>last deployment to each GitLab environment</span></h3><div class="dep-list">${list.map(line).join("")}</div>`;
    soften(box);
  } catch (e) { box.innerHTML = `<h3 class="h3">Where it's deployed</h3><p class="ink-3" style="margin:0">${esc(e.message)}</p>`; }
}

// in-app actions for step items, shown only where Greenlight can really do them
function stepActions(s, it) {
  if (!status?.gitlab || it.state !== "todo" || !it.repo || it.mr) return "";
  if (s.id === "tag") { const next = report?.repos.find(x => x.name === it.repo)?.tags?.next; if (next) return `<button class="btn primary small" data-mktag="${esc(next)}" data-repo="${esc(it.repo)}">${icon("tag")}Create tag ${esc(next)}</button>`; }
  return "";
}


async function createTag(repo, tag) {
  if (!await confirmDialog(`Create tag ${esc(tag)}?`, `Tags the default branch of ${repoTag(repo)} in GitLab. QA deploys tags, so this starts a release.`, `Create ${esc(tag)}`, "primary")) return;
  try { const r = await api("/api/gitlab/create-tag", { method: "POST", body: JSON.stringify({ repo, tag }) }); toast(`Tag ${esc(tag)} created.<a href="${url(r.url)}" target="_blank" rel="noopener">Open in GitLab</a>`, 8000); if (report) setTimeout(() => load(report.key, false, true), 4000); }
  catch (e) { fail(esc(e.message)); }
}

async function moveDialog(key) {
  const d = $("#settings");
  d.innerHTML = `<div class="dlg-in"><h2>Move ${esc(key)} in Jira</h2><div id="mvList"><span class="s2-msg"><span class="spinner"></span>Reading what's possible</span></div></div>
    <div class="dlg-f"><span class="msg" id="mvMsg"></span><button class="btn" id="mvCancel">Cancel</button><button class="btn primary" id="mvGo" disabled>Move</button></div>`;
  d.showModal();
  $("#mvCancel").onclick = () => d.close();
  try {
    const ts = await api(`/api/jira/transitions?key=${encodeURIComponent(key)}`);
    $("#mvList").innerHTML = ts.length ? `<div class="radio-list">${ts.map(t => `<label><input type="radio" name="mv" value="${esc(t.id)}">${jiraBadge(t.to, t.category)}<span class="ink-2">${esc(t.name)}</span></label>`).join("")}</div>` : `<p class="ink-3">This ticket can't be moved from its current status.</p>`;
    $$("#mvList input").forEach(i => i.onchange = () => { $("#mvGo").disabled = false; });
    $("#mvGo").onclick = async () => {
      const id = $("#mvList input:checked")?.value; if (!id) return;
      $("#mvGo").disabled = true; $("#mvMsg").innerHTML = `<span class="spinner"></span>Moving`;
      try {
        await api("/api/jira/transition", { method: "POST", body: JSON.stringify({ key, id }) }); d.close();
        const done = ts.find(t => t.id === id)?.category === "done";
        if (done) closedKeys.add(key);
        toast(done ? `${esc(key)} moved to Done and hidden from your tickets.` : `${esc(key)} moved.`);
        tickets && loadTickets(); if (report?.key === key) load(key, false, true);
      }
      catch (e) { $("#mvMsg").className = "msg bad"; $("#mvMsg").textContent = e.message; $("#mvGo").disabled = false; }
    };
  } catch (e) { $("#mvList").innerHTML = `<p class="ink-3">${esc(e.message)}</p>`; }
}

// a yes/no question before anything that changes Jira or GitLab
function confirmDialog(title, html, yes, kind = "primary") {
  return new Promise(ok => {
    const d = $("#settings");
    d.innerHTML = `<div class="dlg-in"><h2>${title}</h2><p class="intro" style="margin:0">${html}</p></div>
      <div class="dlg-f"><span class="msg"></span><button class="btn" id="cfNo">Cancel</button><button class="btn ${kind}" id="cfYes">${yes}</button></div>`;
    const done = v => { d.removeEventListener("close", onClose); d.close(); ok(v); };
    const onClose = () => ok(false);
    d.addEventListener("close", onClose, { once: true });
    $("#cfNo").onclick = () => done(false); $("#cfYes").onclick = () => done(true);
    d.showModal(); setTimeout(() => $("#cfYes").focus(), 30);
  });
}

// review checklist, sorted into what a reviewer thinks about
function checkGroups(list) {
  const cat = c => { const t = (c.title + " " + (c.detail || "")).toLowerCase();
    if (/pipeline|test/.test(t)) return "CI and testing";
    if (/database|script|sql|pipeline files/.test(t)) return "Database and deployment";
    if (/setting|endpoint|grant|scope|role|menu/.test(t)) return "Configuration and access";
    if (/discussion/.test(t)) return "Review discussions";
    if (/large|ticket key|no test/.test(t)) return "Change size and quality";
    return "Merge readiness"; };
  const order = ["Merge readiness", "CI and testing", "Database and deployment", "Configuration and access", "Review discussions", "Change size and quality"];
  const m = new Map(order.map(o => [o, []]));
  list.forEach(c => m.get(cat(c)).push(c));
  return [...m].filter(([, v]) => v.length);
}
const todoCount = r => r.checklist.filter(c => c.state === "todo").length;

function errBox(title, msg, retry) {
  setTimeout(() => { const b = $("#errRetry"); if (b) b.onclick = () => { b.disabled = true; b.innerHTML = `<span class="spinner"></span>Trying again`; retry(); }; });
  return `<div class="void fail-c">${icon("alert", "ico big")}<h3>${esc(title)}</h3><p>Greenlight couldn't finish this. The rest of the app still works.</p>
    <div class="void-acts"><button class="btn solid" id="errRetry">${icon("refresh")}Try again</button></div>
    <details class="why"><summary>Technical details</summary><code>${esc(msg)}</code></details></div>`;
}

// date picker (flatpickr), themed; the field keeps yyyy-mm-dd while showing a friendly date
function datePicker(el, opts = {}) {
  if (!el || !window.flatpickr) return null;
  // inside a dialog the calendar must live in the dialog: dialogs sit on the top layer, above anything attached to the page
  const inDialog = !!el.closest("dialog");
  return flatpickr(el, { dateFormat: "Y-m-d", altInput: !el.classList.contains("sr"), altFormat: "D j M Y", disableMobile: true, monthSelectorType: "static", locale: { firstDayOfWeek: 1 }, ...(inDialog ? { static: true } : {}), ...opts });
}

/* ─────────────────────────── notifications and the end-of-day reminder ─────────────────────────── */
function notify(title, body, onClick) {
  toast(`<b>${esc(title)}</b> ${esc(body)}`, 8000);
  try { if ("Notification" in window && Notification.permission === "granted" && document.hidden) { const n = new Notification(title, { body, icon: $("link[rel=icon]").href }); n.onclick = () => { window.focus(); onClick?.(); n.close(); }; } } catch { }
}
const reminderOn = () => { try { return localStorage.getItem("gl-remind") !== "off"; } catch { return true; } };
function remindTick() {
  if (!week || !status?.account || !reminderOn()) return;
  const t = week.days.find(d => d.date === week.today);
  if (!t?.workday || t.seconds >= t.target) return;
  const [h, m] = (status.reminderAt || "17:00").split(":").map(Number), now = new Date();
  if (now.getHours() * 60 + now.getMinutes() < h * 60 + m) return;
  let last = ""; try { last = localStorage.getItem("gl-reminded") || ""; } catch { }
  if (last === week.today) return;
  try { localStorage.setItem("gl-reminded", week.today); } catch { }
  notify("Time to log your day", `${hm(t.target - t.seconds)} still to log today.`, () => go2("#worklogs"));
}
const go2 = h => { history.pushState(null, "", h); route(); };

/* ─────────────────────────── keys and start ─────────────────────────── */
$("#settings").addEventListener("click", e => { if (e.target === $("#settings")) $("#settings").close(); });
document.addEventListener("keydown", e => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); openFind(); return; }
  if (/INPUT|SELECT|TEXTAREA/.test(document.activeElement?.tagName) || document.querySelector(".select2-container--open") || $("#settings").open || e.ctrlKey || e.metaKey || e.altKey) return;
  if (e.key === "/") { e.preventDefault(); openFind(); }
  else if (e.key === "j" && section === "tickets") move(1);
  else if (e.key === "k" && section === "tickets") move(-1);
  else if (e.key === "r" && report && section === "tickets") load(report.key, true);
  else if (e.key === "Escape" && selected && innerWidth <= 900) back();
});

$("#checkAll").onclick = checkAll;
$("#reload").onclick = () => railMode === "review" ? loadReviews() : status?.account && loadTickets();

// the app frame never scrolls as a whole; only its panes do. If focus ever nudges the document, put it back.
addEventListener("scroll", () => { if (scrollX || scrollY) scrollTo(0, 0); }, { passive: true });

// the list rail resizes by dragging its edge; dragged past the snap point it hides, and dragging the edge back out shows it again
(() => {
  const split = $("#split"), grip = $("#grip"), MIN = 260, SNAP = 150, STEP = 24;
  const max = () => Math.max(MIN, Math.min(640, split.clientWidth * .6));
  let w = null, shut = false;
  try { const s = JSON.parse(localStorage.getItem("gl-rail") || "null"); if (s) { w = s.w || null; shut = !!s.shut; } } catch { }
  const width = () => shut ? 0 : Math.round($(".rail").getBoundingClientRect().width);
  const apply = () => {
    if (w) split.style.setProperty("--rail-w", Math.min(w, max()) + "px"); else split.style.removeProperty("--rail-w");
    split.classList.toggle("shut", shut);
    grip.setAttribute("aria-valuenow", shut ? 0 : (w || 340));
    grip.title = shut ? "Drag right to show the list" : "Drag to resize; drag to the edge to hide";
  };
  const save = () => { try { localStorage.setItem("gl-rail", JSON.stringify({ w, shut })); } catch { } };
  const to = px => { if (px < SNAP) shut = true; else { shut = false; w = Math.round(Math.min(Math.max(px, MIN), max())); } apply(); };
  grip.setAttribute("aria-valuemin", 0);
  grip.setAttribute("aria-valuemax", 640);
  apply();

  grip.addEventListener("pointerdown", e => {
    if (e.button !== 0 || innerWidth <= 900) return;
    e.preventDefault();
    grip.setPointerCapture(e.pointerId);
    const x0 = e.clientX, w0 = width();
    split.classList.add("dragging"); document.body.classList.add("resizing");
    const move = ev => to(w0 + ev.clientX - x0);
    const end = () => {
      grip.removeEventListener("pointermove", move); grip.removeEventListener("pointerup", end); grip.removeEventListener("pointercancel", end);
      split.classList.remove("dragging"); document.body.classList.remove("resizing");
      save();
    };
    grip.addEventListener("pointermove", move); grip.addEventListener("pointerup", end); grip.addEventListener("pointercancel", end);
  });
  grip.addEventListener("keydown", e => {
    const d = { ArrowLeft: -STEP, ArrowRight: STEP }[e.key]; if (!d) return;
    e.preventDefault();
    // from the narrowest width one more step left hides it; from hidden one step right brings it back at the narrowest
    to(shut ? (d > 0 ? MIN : 0) : width() <= MIN && d < 0 ? 0 : width() + d);
    save();
  });
  addEventListener("resize", apply);
})();

$("#main").addEventListener("click", mainClicks);
// opened comment threads ease in like everything else that unfolds
$("#main").addEventListener("toggle", e => { if (e.target.matches?.("details.talks, details.why") && e.target.open) [...e.target.children].slice(1).forEach((c, i) => soften(c, i * 30)); }, true);
$("#list").addEventListener("click", e => {
  const g = e.target.closest(".grp-h");
  if (g) { includeKids = true; reviewing = null; select(g.dataset.story); return; }
  const r = e.target.closest(".row"); if (!r) return;
  if (r.dataset.project) openReview(r.dataset.project, +r.dataset.iid); else { reviewing = null; if (reviewerMode) { reviewerMode = false; evMine = true; } select(r.dataset.key); }
});

async function boot() {
  await loadStatus();
  initFinder();
  route();   // the page appears at once; data fills in as it arrives
  if (status?.account) await loadTickets(); else renderList();
  await loadWeek();
  navCounts();
  // reviews load quietly, so the counts are ready when you look
  if (status?.gitlab) {
    api("/api/reviews").then(r => { reviews = r; navCounts(); if (railMode === "review") renderList(); if (section === "reviews" && !reviewing) reviewsBlank(); }).catch(() => {});
  }
  remindTick(); setInterval(remindTick, 60 * 1000);
}
boot();
