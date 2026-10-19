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
    ? `<button class="who" id="whoBtn" aria-label="Account and settings" data-tip="Account and settings">${faceHtml(me.name)}<span class="nm">${esc(cap(me.name))}<small>${status.gitlab ? "Jira and GitLab" : "Jira"} · settings</small></span>${icon("cog")}</button>`
    : `<button class="btn success" id="whoBtn" style="width:100%">Connect Jira</button>`;
  $("#whoBtn").onclick = openSettings;
  wireFaces($("#whoSlot"));
  $("#checkAll").hidden = !me;
  renderShell();
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
  wireFaces(d);
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

const emptyNote = (title, text, action = "", mood = "empty") => `<div class="rail-empty">${glim(mood)}<b>${title}</b><span>${text}</span>${action}</div>`;

let groupBy = (() => { try { return localStorage.getItem("gl-group") || "story"; } catch { return "story"; } })();

function renderList() {
  renderRailMode();
  if (railMode === "review") return renderReviewList();
  $("#railTitle").textContent = "My tickets";
  if (!status?.account) {
    $("#count").textContent = ""; $("#views").innerHTML = ""; $("#railTools").innerHTML = "";
    $("#list").innerHTML = emptyNote("Not connected", "Connect your Jira account to see the tickets assigned to you.", `<button class="btn success small" id="rc">Connect Jira</button>`, "welcome");
    $("#rc").onclick = openSettings; return;
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

const PRIO_UP = { highest: 2, high: 1 };
function metaChips(t, small = false) {
  const out = [];
  if (t.sprint) out.push(`<span class="chip-m sprint ${t.sprintState === "active" ? "active" : ""} ${small ? "sm" : ""}" title="Sprint (${esc(t.sprintState || "")})">${icon("sprint")}${esc(t.sprint)}</span>`);
  const p = (t.priority || "").toLowerCase();
  if (p && (!small || p !== "medium")) out.push(`<span class="chip-m prio ${esc(p.split(" ")[0])} ${small ? "sm" : ""}" title="Priority">${icon("prio")}${esc(t.priority)}</span>`);
  if (!small) {
    (t.labels || []).forEach(l => out.push(`<span class="chip-m label" title="Label">${icon("tag")}${esc(l)}</span>`));
    (t.components || []).forEach(c => out.push(`<span class="chip-m" title="Component">${esc(c)}</span>`));
    if (t.fixVersions && t.fixVersions !== "none") out.push(`<span class="chip-m" title="Fix version">${icon("flag")}${esc(t.fixVersions)}</span>`);
  }
  if (t.due) { const late = t.due < new Date().toISOString().slice(0, 10) && t.statusCategory !== "done"; out.push(`<span class="chip-m due ${late ? "late" : ""} ${small ? "sm" : ""}" title="Due date">${icon("calendar")}${day(t.due)}</span>`); }
  return out.length ? `<span class="chips">${out.join("")}</span>` : "";
}

function rowHtml(t) {
  const st = statesOf(t.lastBuild);
  const chips = metaChips(t, true);
  return `<button class="row ${t._busy ? "busy" : ""}" role="option" aria-selected="${t.key === selected}" data-key="${esc(t.key)}">
    ${mark(overall(st))}<span class="t">${esc(t.summary)}</span>
    <span class="m">${typeTag(t.type)}<span class="mono">${esc(t.key)}</span><span class="s">${jiraBadge(t.status, t.statusCategory)}</span>${qbar(st)}</span>${chips ? `<span class="tags">${chips}</span>` : ""}</button>`;
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
  document.title = `${key} · greenlight.`;
  crumb(`<a href="#tickets">My tickets</a><span class="sl">/</span><span class="mono">${esc(key)}</span>`);
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
function back() {
  const toReviews = section === "reviews";
  history.pushState(null, "", toReviews ? "#reviews" : "#tickets");
  selected = null; report = null; reviewing = null; reviewerMode = false; diffState = null;
  $("#split").classList.remove("open"); renderList();
  toReviews ? reviewsBlank() : blank();
}

function blank() {
  document.title = "My tickets · greenlight.";
  crumb(`<b>My tickets</b>`);
  const me = status?.account;
  $("#main").innerHTML = `<div class="blank"><div class="blank-in">
    ${glim(me ? "welcome" : "welcome")}
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

  crumb(`<a href="#tickets">My tickets</a><span class="sl">/</span><span class="mono">${esc(r.key)}</span>`);
  document.title = `${r.key} · greenlight.`;
  const mood = nothing ? "empty" : ov === "todo" ? "warning" : ov === "wait" ? "thinking" : "success";
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
            ${me ? `<button role="menuitem" id="moveJira">${icon("send")}Move in Jira…</button>` : ""}
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
    ${i ? `<div class="meta-chips">${metaChips(i)}</div>` : ""}
    ${me ? subControl(r) : ""}

    ${nothing ? voidHtml(r, kids) : `<div class="state ${ov === "done" || ov === "check" ? "ok" : ""}">
      ${glim(mood)}
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
        <div class="ia">${stepActions(s, it)}${it.actionUrl === "post:" ? `<button class="btn solid small" data-post>${icon("send")}${esc(it.actionLabel)}</button>`
          : it.actionUrl ? `<a class="btn line small" href="${url(it.actionUrl)}" target="_blank" rel="noopener">${esc(it.actionLabel || "Open")}${icon("out")}</a>` : ""}</div></li>`).join("")}</ul>`
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
  $("#moveJira")?.addEventListener("click", () => moveDialog(r.key));
  $$("[data-mkmr]").forEach(b => b.onclick = () => createMr(b.dataset.repo, b.dataset.mkmr));
  $$("[data-mktag]").forEach(b => b.onclick = () => createTag(b.dataset.repo, b.dataset.mktag));
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

function logDialog(key, pre = {}) {
  const d = $("#settings"), today = localDay(new Date());
  const left = week?.days.find(x => x.date === week.today);
  const gap = left ? Math.max(0, left.target - left.seconds) : 0;
  d.innerHTML = `<div class="dlg-in">
    <h2>${key ? `Log time on ${esc(key)}` : "Log time"}</h2>
    ${key ? "" : `<label class="fld"><span>Ticket</span><select id="lgK" aria-label="Ticket">${(tickets || []).map(t => `<option value="${esc(t.key)}">${esc(t.key)} · ${esc(t.summary)}</option>`).join("")}</select></label>`}
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

// the sidebar switches between tickets and reviews now; the list no longer carries its own switch
function renderRailMode() { $("#railMode").hidden = true; }

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
    document.title = `!${iid} · ${r.repo} · greenlight.`;
    crumb(`<a href="#reviews">Reviews</a><span class="sl">/</span>${repoTag(r.repo)}<span class="mono">!${iid}</span>`);
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
    <rect x="4" y="22" width="9" height="20" rx="4.5" fill="#EEF1EA"/><rect x="51" y="22" width="9" height="20" rx="4.5" fill="#EEF1EA"/>
    <rect x="8" y="8" width="48" height="45" rx="18" fill="#9ACD3C"/>
    <path d="M14 14q18-7 36 0" fill="none" stroke="#C7EB7C" stroke-width="2.4" stroke-linecap="round" opacity=".8"/>
    <rect x="12.5" y="12.5" width="39" height="36" rx="13.5" fill="#F4F6F0"/>
    <rect x="15.5" y="15.5" width="33" height="30" rx="10.5" fill="#2A1B15"/>
    <path d="M18 19.5q14-4 28 0" fill="none" stroke="#fff" stroke-width="1.4" opacity=".1" stroke-linecap="round"/>
    ${cheeks}${faces[mood] || faces.welcome}
  </svg>`;
}

/* ─────────────────────────── shell: theme, sidebar, crumb, counts ─────────────────────────── */
let section = "overview";
const crumb = html => { $("#crumb").innerHTML = html; };
const localDay = d => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;

function applyTheme(pref) {
  if (pref !== "light") pref = "dark";
  const dark = pref === "dark";
  document.documentElement.dataset.theme = dark ? "dark" : "light";
  document.documentElement.dataset.themePref = pref;
  $$("#themes [data-theme-set]").forEach(b => b.setAttribute("aria-checked", b.dataset.themeSet === pref));
}
$$("#themes [data-theme-set]").forEach(b => b.onclick = () => {
  // in the narrow sidebar only the current theme shows: clicking it switches to the other one
  let pick = b.dataset.themeSet;
  if ($("#app").classList.contains("sb-mini") && b.getAttribute("aria-checked") === "true") pick = pick === "dark" ? "light" : "dark";
  try { localStorage.setItem("gl-theme", pick); } catch { }
  applyTheme(pick);
});
applyTheme(document.documentElement.dataset.themePref || "dark");

$("#brandGlim").innerHTML = glim("welcome");
// collapsed: your choice, or automatically while the window is narrow
let sbChoice = document.documentElement.dataset.sb === "mini";
const narrow = matchMedia("(max-width: 1100px) and (min-width: 901px)");
function syncSidebar() {
  const mini = sbChoice || narrow.matches;
  $("#app").classList.toggle("sb-mini", mini);
  $("#sbToggle").setAttribute("aria-pressed", mini);
  $("#sbToggle").title = $("#sbToggle").ariaLabel = mini ? "Expand the sidebar" : "Collapse the sidebar";
}
syncSidebar();
narrow.addEventListener("change", syncSidebar);
$("#sbToggle").onclick = () => {
  sbChoice = !$("#app").classList.contains("sb-mini");
  if (!sbChoice && narrow.matches) { $("#app").classList.remove("sb-mini"); $("#sbToggle").setAttribute("aria-pressed", false); return; }   // a peek while narrow
  try { localStorage.setItem("gl-sb", sbChoice ? "mini" : "full"); } catch { }
  syncSidebar();
};
$("#menuBtn").onclick = e => { e.stopPropagation(); $("#app").classList.toggle("sb-open"); };
document.addEventListener("click", e => { if ($("#app").classList.contains("sb-open") && !e.target.closest(".sb") && !e.target.closest("#menuBtn")) $("#app").classList.remove("sb-open"); });
$$(".nav-i").forEach(a => a.addEventListener("click", () => $("#app").classList.remove("sb-open")));
$("#brandHome").onclick = e => { e.preventDefault(); history.pushState(null, "", "#overview"); route(); };
$("#cmdBtn").onclick = () => openCmd();

function renderShell() {
  // recent checks, newest first
  const rec = (status?.recent || []).slice(0, 6);
  $("#recentCap").hidden = !rec.length;
  $("#recent").innerHTML = rec.map(r => `<button data-k="${esc(r.key)}" title="${esc(r.summary || r.key)}">${mark(overall(statesOf(r)), false)}<span><span class="t">${esc(r.summary || r.key)}</span><span class="k">${esc(r.key)} · ${esc(ago(r.generated))}</span></span></button>`).join("");
  $$("#recent [data-k]").forEach(b => b.onclick = () => select(b.dataset.k));
  // low disk space: say so, everywhere, because checks start failing
  const free = status?.diskFree ?? -1;
  $("#diskBar").hidden = !(free >= 0 && free < 1024 ** 3);
  if (!$("#diskBar").hidden) $("#diskBar").innerHTML = `${icon("alert", "ico")}<span><b>Drive C: is almost full (${(free / 1024 ** 3).toFixed(2)} GB free).</b> Checks can fail to save. Free some space with Disk Cleanup or Settings › Storage.</span>`;
  navCounts();
}

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
  const listy = s === "tickets" || s === "reviews";
  $("#split").classList.toggle("solo", !listy);
  if (!listy) $("#split").classList.remove("open");
  if (listy) {
    const want = s === "reviews" ? "review" : "mine";
    if (railMode !== want) { railMode = want; if (want === "review" && !reviews && status?.gitlab) loadReviews(); else renderList(); }
  }
  $("#main").scrollTop = 0;
}

/* ─────────────────────────── routing ───────────────────────────
   #overview · #tickets · #MM-1 · #MM-1/diff · #reviews · #review/<project>/<iid> · #worklogs[/<monday>] · #sprint[/<name>] · #team */
function route() {
  const raw = decodeURIComponent(location.hash.slice(1)), low = raw.toLowerCase();
  if (raw.startsWith("review/")) { setSection("reviews"); reviewRoute(); return; }
  if (low === "" || low === "overview") { setSection("overview"); return renderOverview(); }
  if (low === "tickets") { setSection("tickets"); selected = null; report = null; diffState = null; $("#split").classList.remove("open"); renderList(); return blank(); }
  if (low === "reviews") { setSection("reviews"); reviewing = null; $("#split").classList.remove("open"); return reviewsBlank(); }
  if (low.startsWith("worklogs")) { setSection("worklogs"); return renderWorklogs(raw.split("/")[1] || null); }
  if (low.startsWith("sprint")) { setSection("sprint"); return renderSprint(raw.slice(7) || null); }
  if (low === "team") { setSection("team"); return renderTeam(); }
  const [k, sub] = raw.toUpperCase().split("/");
  setSection("tickets");
  if (sub === "DIFF" && diffState && report?.key === k) return renderDiffPage();
  if (k && report?.key === k && !sub) { diffState = null; $("#split").classList.add("open"); renderTicket(); return; }
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
  document.title = "Reviews · greenlight.";
  crumb(`<b>Reviews</b>`);
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

/* ─────────────────────────── overview ─────────────────────────── */
async function renderOverview() {
  document.title = "Overview · greenlight.";
  crumb(`<b>Overview</b>`);
  const me = status?.account;
  if (!me) {
    $("#main").innerHTML = `<div class="page"><div class="hello"><div><h1>Welcome to greenlight.</h1><p>From commit to QA. Greenlight checks what a Jira ticket touched across your repositories, and whether all of it is really ready for QA.</p>
      <div class="acts"><button class="btn primary" id="ovConnect">Connect Jira</button></div></div><div class="portrait"><img src="/assets/glim.jpg" alt="Glim, the Greenlight helper robot"></div></div>
      <ol class="setup card" style="margin-top:18px">
        <li><b>Connect your Jira account</b><div><p>Lists your tickets and posts handovers under your name.</p></div></li>
        <li class="${status?.repoCount ? "ok" : ""}"><b>Repositories folder</b><div><p>${status?.repoCount ? `${pl(status.repoCount, "repository", "repositories")} found in <code>${esc(status.reposRoot)}</code>` : "Point to the folder with your local clones."}</p></div></li>
        <li><b>GitLab (optional)</b><div><p>For reviews, deployments, and creating merge requests and tags.</p></div></li>
      </ol></div>`;
    $("#ovConnect").onclick = openSettings; return;
  }
  const all = tickets || [];
  const by = s => all.filter(t => overall(statesOf(t.lastBuild)) === s);
  const need = [...by("todo"), ...by("wait")], ready = by("check"), unchecked = by("none");
  const waiting = (reviews || []).filter(r => !r.approvedByMe);
  const today = week?.days.find(d => d.date === week.today);
  const left = today?.workday ? Math.max(0, today.target - today.seconds) : 0;
  const hour = new Date().getHours(), first = cap((me.name || "").split(" ")[0]);
  const greet = hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
  const bits = [];
  if (need.length) bits.push(`${pl(need.length, "ticket needs", "tickets need")} you`);
  if (ready.length) bits.push(`${pl(ready.length, "is", "are")} ready to hand over`.replace(/^1 is/, "1 is").replace(/^(\d+) are/, "$1 are"));
  if (waiting.length) bits.push(`${pl(waiting.length, "review is", "reviews are")} waiting`);
  if (left) bits.push(`${hm(left)} left to log today`);
  const say = bits.length ? bits.join(", ").replace(/, ([^,]*)$/, " and $1") + "." : unchecked.length ? `${pl(unchecked.length, "ticket hasn't", "tickets haven't")} been checked yet.` : "Nothing is blocked. Nice and calm.";
  const reason = t => { const s = t.lastBuild?.stations?.find(x => x.state === "todo" || x.state === "wait"); return s ? `${STEPS.find(x => x.id === s.id)?.name}: ${s.headline || WORD[s.state]}` : WORD[overall(statesOf(t.lastBuild))]; };
  const miniRow = t => `<button class="mini" data-k="${esc(t.key)}">${mark(overall(statesOf(t.lastBuild)))}<span class="t">${esc(t.summary)}</span><span class="r mono">${esc(t.key)}</span><span class="s">${typeTag(t.type)}${jiraBadge(t.status, t.statusCategory)}<span class="why">${esc(reason(t))}</span></span></button>`;

  $("#main").innerHTML = `<div class="page wide-page">
    <div class="hello">
      <div><h1>${greet}, ${esc(first)}.</h1><p>${esc(say)}</p>
        <div class="acts">
          <button class="btn primary" id="ovCheck">${icon("checkall")}Check all my tickets</button>
          ${status.gitlab ? `<a class="btn line" href="#reviews">${icon("review")}Open reviews</a>` : ""}
          <button class="btn line" id="ovLog">${icon("clock")}Log time</button>
        </div></div>
      <div class="portrait"><img src="/assets/glim.jpg" alt="Glim, the Greenlight helper robot"></div>
    </div>
    <div class="kpis">
      <button class="kpi" data-goto="tickets" data-v="all"><span class="v">${all.length}</span><span class="l">Open tickets</span></button>
      <button class="kpi" data-goto="tickets" data-v="todo"><span class="v">${need.length ? mark("todo") : ""}${need.length}</span><span class="l">Need you</span></button>
      <button class="kpi" data-goto="tickets" data-v="check"><span class="v">${ready.length ? mark("check") : ""}${ready.length}</span><span class="l">Ready to hand over</span></button>
      ${status.gitlab ? `<button class="kpi" data-goto="reviews"><span class="v">${waiting.length}</span><span class="l">Reviews waiting</span></button>`
        : `<button class="kpi" data-goto="tickets" data-v="none"><span class="v">${unchecked.length}</span><span class="l">Not checked yet</span></button>`}
    </div>
    <div class="grid2">
      <div class="stack">
        <section class="card"><h2>Needs attention<span class="c">${need.length || ""}</span>${need.length > 6 ? `<a class="more-l" href="#tickets">See all</a>` : ""}</h2>
          ${need.length ? `<div class="mini-list">${need.slice(0, 6).map(miniRow).join("")}</div>`
            : `<div class="pane-empty" style="padding:22px 0">${glim(unchecked.length ? "thinking" : "success")}<b>${unchecked.length ? "Nothing known to be blocked" : "Nothing is blocked"}</b><span>${unchecked.length ? `${pl(unchecked.length, "ticket hasn't", "tickets haven't")} been checked yet.` : "Every checked ticket is clear or with QA."}</span></div>`}
        </section>
        ${ready.length ? `<section class="card"><h2>Ready to hand over<span class="c">${ready.length}</span></h2><div class="mini-list">${ready.slice(0, 5).map(miniRow).join("")}</div></section>` : ""}
        ${unchecked.length ? `<section class="card"><h2>Not checked yet<span class="c">${unchecked.length}</span><button class="more-l" id="ovCheck2">Check them</button></h2><div class="mini-list">${unchecked.slice(0, 5).map(miniRow).join("")}</div></section>` : ""}
      </div>
      <div class="stack">
        <section class="card"><h2>Review inbox<span class="c">${status.gitlab ? waiting.length : ""}</span>${status.gitlab ? `<a class="more-l" href="#reviews">Open</a>` : ""}</h2>
          ${!status.gitlab ? `<p class="ink-3" style="margin:0 0 12px">Connect GitLab to see merge requests waiting for you.</p><button class="btn line small" id="ovGl">Connect GitLab</button>`
            : waiting.length ? `<div class="mini-list">${waiting.slice(0, 5).map(r => `<button class="mini" data-mr="${esc(r.project)}" data-iid="${r.iid}">${mark(r.conflicts || r.draft ? "wait" : "todo")}<span class="t">${esc(r.title)}</span><span class="r mono">!${r.iid}</span><span class="s">${repoTag(r.repo)}<span class="why">${esc(r.author?.name || "")}${r.key ? ` · ${esc(r.key)}` : ""}</span></span></button>`).join("")}</div>`
            : `<p class="ink-3" style="margin:0">Nothing waiting for your review.</p>`}
        </section>
        <section class="card"><h2>Today${today?.workday ? `<span class="c">${hm(today.seconds)} of ${hm(today.target)}</span>` : ""}<a class="more-l" href="#worklogs">Worklogs</a></h2>
          ${today?.workday ? `<div class="timebox"><div class="tbar"><i class="mine" style="width:${Math.min(100, today.seconds / Math.max(1, today.target) * 100)}%"></i></div></div>
            <p class="ink-3" style="margin:10px 0 0">${left ? `${hm(left)} still to log.` : "Today's target is met."} <a href="#worklogs" style="color:var(--gl-accent)">See suggestions from your commits</a></p>` : `<p class="ink-3" style="margin:0">Not a working day.</p>`}
        </section>
        <section class="card"><h2>Standup<span class="c" id="suDay"></span><button class="more-l" id="suCopy" hidden>Copy</button></h2><pre class="standup" id="suText"><span class="s2-msg"><span class="spinner"></span>Gathering yesterday's work</span></pre></section>
        <section class="card"><h2>Quick actions</h2><div class="quick-acts">
          <button class="qa" id="qaFind">${icon("search")}Check a ticket</button>
          <a class="qa" href="#sprint">${icon("sprint")}Sprint release</a>
          <a class="qa" href="#worklogs">${icon("clock")}Worklogs</a>
          <a class="qa" href="#team">${icon("team")}Team</a>
        </div></section>
      </div>
    </div></div>`;
  soften($(".hello")); soften($(".kpis"), 60); soften($(".grid2"), 120);
  $("#ovCheck").onclick = () => { history.pushState(null, "", "#tickets"); route(); checkAll(); };
  $("#ovCheck2")?.addEventListener("click", () => $("#ovCheck").click());
  $("#ovLog").onclick = () => logDialog(null);
  $("#ovGl")?.addEventListener("click", openSettings);
  $("#qaFind").onclick = openCmd;
  $$(".kpi[data-goto]").forEach(b => b.onclick = () => { if (b.dataset.v) view = b.dataset.v; history.pushState(null, "", "#" + b.dataset.goto); route(); });
  $$("#main .mini[data-k]").forEach(b => b.onclick = () => select(b.dataset.k));
  $$("#main .mini[data-mr]").forEach(b => b.onclick = () => { setSection("reviews"); openReview(b.dataset.mr, +b.dataset.iid); });
  loadStandup($("#suText"), null, $("#suDay"), $("#suCopy"));
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
  document.title = "Worklogs · greenlight.";
  crumb(`<b>Worklogs</b>`);
  if (!status?.account) { $("#main").innerHTML = `<div class="page">${emptyNote("Connect Jira", "Worklogs come from Jira.", `<button class="btn success small" id="wlc">Connect Jira</button>`, "welcome")}</div>`; $("#wlc").onclick = openSettings; return; }
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
  if ("Notification" in window && Notification.permission === "default") { $("#wlRemind").hidden = false; $("#wlRemind").onclick = async () => { await Notification.requestPermission(); $("#wlRemind").hidden = true; toast(Notification.permission === "granted" ? `You'll get a reminder at ${esc(status.reminderAt || "17:00")} if today is short.` : "Reminders stay inside Greenlight."); }; }
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

/* ─────────────────────────── sprint release ─────────────────────────── */
let sprints = null, spWho = "me", spData = new Map();
async function renderSprint(name) {
  document.title = "Sprint release · greenlight.";
  crumb(`<b>Sprint release</b>`);
  if (!status?.account) { $("#main").innerHTML = `<div class="page">${emptyNote("Connect Jira", "Sprints come from Jira.", `<button class="btn success small" id="spc">Connect Jira</button>`, "welcome")}</div>`; $("#spc").onclick = openSettings; return; }
  $("#main").innerHTML = `<div class="page wide-page"><div class="ph"><div><h1>Sprint release</h1><p>Everything a sprint needs to go live: tags per service, database releases, settings, access and blockers.</p></div></div>
    <div id="spBody" style="margin-top:20px"><span class="s2-msg"><span class="spinner"></span>Reading sprints from Jira</span></div></div>`;
  try { sprints ??= await api("/api/sprints"); } catch (e) { $("#spBody").innerHTML = errBox("Couldn't read sprints", e.message, () => renderSprint(name)); return; }
  if (section !== "sprint") return;
  if (!sprints.length) { $("#spBody").innerHTML = emptyNote("No sprints", "None of your tickets is in a sprint.", "", "empty"); return; }
  const sp = sprints.find(s => s.name === name) || sprints.find(s => s.state === "active") || sprints[0];
  $(".ph").insertAdjacentHTML("beforeend", `<div class="acts">
      <select id="spSel" aria-label="Sprint">${sprints.map(s => `<option value="${esc(s.name)}" ${s.name === sp.name ? "selected" : ""}>${esc(s.name)} · ${esc(s.state)}</option>`).join("")}</select>
      <div class="seg" role="radiogroup" aria-label="Whose tickets"><button role="radio" aria-checked="${spWho === "me"}" data-who="me">Mine</button><button role="radio" aria-checked="${spWho === "all"}" data-who="all">Everyone's</button></div>
      <button class="btn primary" id="spCheck">${icon("checkall")}Check all</button></div>`);
  jq("#spSel").select2({ width: "240px", minimumResultsForSearch: 10 }).on("change", e => { history.pushState(null, "", `#sprint/${encodeURIComponent(e.target.value)}`); route(); });
  $$(".ph [data-who]").forEach(b => b.onclick = () => { spWho = b.dataset.who; renderSprint(sp.name); });
  $("#spBody").innerHTML = `<span class="s2-msg"><span class="spinner"></span>Reading ${esc(sp.name)}</span>`;
  // every page of the sprint (sprints are small; still read page by page)
  const items = [];
  try {
    let next = "";
    for (let i = 0; i < 10; i++) {
      const r = await api(`/api/sprint?name=${encodeURIComponent(sp.name)}&who=${spWho}${next ? `&next=${encodeURIComponent(next)}` : ""}`);
      items.push(...r.items); next = r.next; if (!next) break;
    }
  } catch (e) { $("#spBody").innerHTML = errBox("Couldn't read the sprint", e.message, () => renderSprint(sp.name)); return; }
  if (section !== "sprint") return;
  const draw = () => {
    const checked = items.filter(t => spData.has(t.key));
    $("#spBody").innerHTML = `
      <section class="card"><h2>Tickets<span class="c">${items.length}${checked.length < items.length ? ` · ${checked.length} checked` : ""}</span></h2>
        <div class="wide"><table class="tb"><thead><tr><th></th><th>Ticket</th><th>Assignee</th><th>Jira</th><th>Readiness</th><th class="r"><span class="sr">Open</span></th></tr></thead><tbody id="spRows"></tbody></table></div></section>
      <div id="spRel"></div>`;
    paged($("#spRows"), items.map(t => { const st = statesOf(spData.get(t.key) ? { stations: spData.get(t.key).stations } : t.lastBuild); return `<tr>
      <td style="width:20px">${mark(overall(st))}</td>
      <td><button class="fpath" data-k="${esc(t.key)}" style="font-family:var(--sans)"><span class="mono">${esc(t.key)}</span> ${esc(t.summary)}</button></td>
      <td class="nw ink-2">${esc(t.assignee)}</td><td class="nw">${jiraBadge(t.status, t.statusCategory)}</td><td class="nw">${qbar(st)}</td>
      <td class="r"><button class="btn small" data-chk="${esc(t.key)}">${spData.has(t.key) ? "Recheck" : "Check"}</button></td></tr>`; }), 40);
    $$("#spRows [data-k]").forEach(b => b.onclick = () => select(b.dataset.k));
    $$("#spRows [data-chk]").forEach(b => b.onclick = async () => { b.innerHTML = `<span class="spinner"></span>`; await checkOne(b.dataset.chk); draw(); });
    if (checked.length) $("#spRel").innerHTML = releaseHtml(sp, checked);
    $("#relCopy")?.addEventListener("click", async () => { try { await navigator.clipboard.writeText($("#relNotes").textContent); toast("Release notes copied."); } catch { fail("Couldn't copy."); } });
    $$("#spRel [data-k]").forEach(b => b.onclick = () => select(b.dataset.k));
  };
  const checkOne = async k => { try { const r = await api(`/api/manifest?key=${encodeURIComponent(k)}&fetch=0&children=1&mine=${spWho === "me" ? 1 : 0}`); spData.set(k, r); } catch (e) { fail(`${esc(k)}: ${esc(e.message)}`); } };
  $("#spCheck").onclick = async () => {
    const b = $("#spCheck"); b.disabled = true;
    for (let i = 0; i < items.length; i++) {
      if (section !== "sprint") break;
      b.innerHTML = `<span class="spinner"></span>Checking ${i + 1} of ${items.length}`;
      await checkOne(items[i].key);
      if (i % 3 === 2 || i === items.length - 1) draw();
    }
    if ($("#spCheck")) { $("#spCheck").disabled = false; $("#spCheck").innerHTML = `${icon("checkall")}Check all`; }
  };
  draw();
}

// what the checked tickets of a sprint need to go live, gathered from their checks
function releaseHtml(sp, checked) {
  const reps = checked.map(t => ({ t, r: spData.get(t.key) }));
  const svc = new Map(), blockers = [], db = new Set(), cfg = new Map(), access = [];
  const vnum = n => (n || "").replace(/^[^\d]*/, "").split(".").map(x => +x || 0);
  const newer = (a, b) => { const x = vnum(a), y = vnum(b); for (let i = 0; i < Math.max(x.length, y.length); i++) { if ((x[i] || 0) !== (y[i] || 0)) return (x[i] || 0) > (y[i] || 0); } return false; };
  for (const { t, r } of reps) {
    for (const x of r.repos) {
      if (!x.commits.length) continue;
      const s = svc.get(x.name) || { tag: null, untagged: [], unmerged: [], next: null, tickets: new Set() };
      s.tickets.add(t.key);
      if (x.commits.some(c => !c.inDefault)) s.unmerged.push(t.key);
      const first = x.tags?.containing?.[0]?.name;
      if (first) { if (!s.tag || newer(first, s.tag)) s.tag = first; } else if (x.tags) { s.untagged.push(t.key); s.next = x.tags.next || s.next; }
      svc.set(x.name, s);
    }
    for (const st of r.stations) for (const it of st.items) {
      if (it.state === "todo") blockers.push({ key: t.key, step: STEPS.find(z => z.id === st.id)?.name, title: it.title, repo: it.repo });
      if (st.id === "db" && /^Run /.test(it.title)) db.add(it.title.replace(/^Run /, "").replace(/ on QA$/, ""));
      if (st.id === "config" && it.state !== "skip") { const k = `${it.repo || ""}`; cfg.set(k, [...(cfg.get(k) || []), `${it.title}${it.detail ? `: ${it.detail}` : ""}`]); }
      if (st.id === "access" && it.state !== "skip" && it.state !== "done") access.push(`${it.repo ? it.repo + ": " : ""}${it.title}`);
    }
  }
  const lines = [`${sp.name} release`, "", "Tickets:", ...reps.map(({ t }) => `- ${t.key} ${t.summary} (${t.status})`), "", "Deploy:"];
  for (const [n, s] of svc) lines.push(`- ${n}: ${s.tag ? s.tag : "no tag yet"}${s.untagged.length ? ` (needs a new tag${s.next ? `, next ${s.next}` : ""} for ${s.untagged.join(", ")})` : ""}${s.unmerged.length ? ` (unmerged work: ${s.unmerged.join(", ")})` : ""}`);
  if (db.size) lines.push("", "Database releases to run on QA:", ...[...db].map(d => `- ${d}`));
  if (cfg.size) lines.push("", "Settings:", ...[...cfg].flatMap(([k, v]) => v.map(x => `- ${k ? k + ": " : ""}${x}`)));
  if (access.length) lines.push("", "Access:", ...access.map(a => `- ${a}`));
  if (blockers.length) lines.push("", "Blockers:", ...blockers.map(b => `- ${b.key} ${b.step}: ${b.title}${b.repo ? ` (${b.repo})` : ""}`));
  return `<div class="grid2">
    <div class="stack">
      <section class="card"><h2>Blockers<span class="c">${blockers.length}</span></h2>${blockers.length ? `<div class="mini-list">${blockers.slice(0, 40).map(b => `<button class="mini" data-k="${esc(b.key)}">${mark("todo")}<span class="t">${esc(b.title)}</span><span class="r mono">${esc(b.key)}</span><span class="s">${esc(b.step || "")}${b.repo ? repoTag(b.repo) : ""}</span></button>`).join("")}</div>` : `<div class="pane-empty" style="padding:16px 0">${glim("success")}<b>Nothing blocks the checked tickets</b></div>`}</section>
      <section class="card"><h2>Services to deploy<span class="c">${svc.size}</span></h2>${[...svc].map(([n, s]) => `<div class="rel-svc">${repoTag(n)}<div class="x">${s.tag ? `<span class="tagpill">${esc(s.tag)}</span>` : ""}${s.untagged.length ? `<span class="tagpill next">needs a tag${s.next ? ` · ${esc(s.next)}` : ""}</span>` : ""}${s.unmerged.length ? `${mark("todo")}<span>unmerged: ${esc(s.unmerged.join(", "))}</span>` : ""}<span class="ink-3">${esc([...s.tickets].join(", "))}</span></div></div>`).join("") || `<p class="ink-3" style="margin:0">No service code in the checked tickets.</p>`}</section>
    </div>
    <div class="stack">
      <section class="card"><h2>Database and settings</h2>
        ${db.size ? `<p style="margin:0 0 6px"><b>Run on QA</b></p>${[...db].map(d => `<div class="tk">${icon("db")} ${esc(d)}</div>`).join("")}` : `<p class="ink-3" style="margin:0">No database releases.</p>`}
        ${cfg.size ? `<p style="margin:14px 0 6px"><b>Settings</b></p>${[...cfg].map(([k, v]) => `<div style="margin-bottom:6px">${k ? repoTag(k) : ""}<div class="ink-2" style="font-size:13px">${v.map(esc).join("<br>")}</div></div>`).join("")}` : ""}
      </section>
      <section class="card"><h2>Release notes<button class="more-l" id="relCopy">Copy</button></h2><pre class="notes" id="relNotes">${esc(lines.join("\n"))}</pre></section>
    </div></div>`;
}

/* ─────────────────────────── team ─────────────────────────── */
async function renderTeam() {
  document.title = "Team · greenlight.";
  crumb(`<b>Team</b>`);
  if (!status?.account) { $("#main").innerHTML = `<div class="page">${emptyNote("Connect Jira", "The team view comes from Jira.", `<button class="btn success small" id="tmc">Connect Jira</button>`, "welcome")}</div>`; $("#tmc").onclick = openSettings; return; }
  $("#main").innerHTML = `<div class="page wide-page"><div class="ph"><div><h1>Team</h1><p>Open work in the current sprint${status.project ? ` of ${esc(status.project)}` : ""}, by person. Set <code>team.jql</code> in greenlight.json to change who is shown.</p></div></div>
    <div id="tmBody"><span class="s2-msg" style="margin-top:20px"><span class="spinner"></span>Reading the sprint from Jira</span></div></div>`;
  const items = [];
  try { let next = ""; for (let i = 0; i < 10; i++) { const r = await api(`/api/team${next ? `?next=${encodeURIComponent(next)}` : ""}`); items.push(...r.items); next = r.next; if (!next) break; } }
  catch (e) { $("#tmBody").innerHTML = errBox("Couldn't read the team's tickets", e.message, renderTeam); return; }
  if (section !== "team") return;
  if (!items.length) { $("#tmBody").innerHTML = emptyNote("No open work", "Nothing open in the current sprint.", "", "success"); return; }
  const people = new Map();
  items.forEach(t => { const k = t.assignee || "Unassigned"; if (!people.has(k)) people.set(k, []); people.get(k).push(t); });
  $("#tmBody").innerHTML = `<div class="team">${[...people].sort((a, b) => b[1].length - a[1].length).map(([who, list]) => `<section class="card person">
    <h2><span class="face">${esc(initials(who))}</span>${esc(cap(who))}<span class="c">${list.length}</span></h2>
    <div class="mini-list">${list.map(t => `<button class="mini" data-k="${esc(t.key)}">${mark(overall(statesOf(t.lastBuild)))}<span class="t">${esc(t.summary)}</span><span class="r mono">${esc(t.key)}</span><span class="s">${typeTag(t.type)}${jiraBadge(t.status, t.statusCategory)}${metaChips(t, true)}</span></button>`).join("")}</div></section>`).join("")}</div>`;
  soften($("#tmBody"));
  $$("#tmBody [data-k]").forEach(b => b.onclick = () => select(b.dataset.k));
}

/* ─────────────────────────── ticket extras: deployments and actions ─────────────────────────── */
async function loadDeployments(key) {
  const box = $("#dep"); if (!box) return;
  box.innerHTML = `<h3 class="h3">Where it's deployed</h3><div class="s2-msg"><span class="spinner"></span>Reading environments from GitLab</div>`;
  try {
    const list = await api(`/api/deployments?key=${encodeURIComponent(key)}`);
    if (report?.key !== key || !$("#dep")) return;
    if (!list.length) { box.innerHTML = `<h3 class="h3">Where it's deployed<span>no GitLab environments for these services</span></h3>`; return; }
    box.innerHTML = `<h3 class="h3">Where it's deployed<span>last deployment to each environment</span></h3>
      ${list.map(r => `<div class="grp">${repoTag(r.repo)}</div><div class="envs">${r.environments.map(e => `<div class="env">
        <div class="en">${mark(e.contains === true ? "done" : e.contains === false ? "todo" : "check", false)}<a class="ln" href="${url(e.web)}" target="_blank" rel="noopener">${esc(e.name)}</a><span class="tier">${esc(e.tier || "")}</span></div>
        <div class="er"><code>${esc(e.ref || "")}</code><span>${esc(e.sha || "")}</span><span>${day(e.date)}</span>${e.status && e.status !== "success" ? `<span>${esc(e.status)}</span>` : ""}</div>
        <div class="has">${e.contains === true ? "Includes this work" : e.contains === false ? "Doesn't include all of it yet" : "Can't tell: fetch first"}</div></div>`).join("")}</div>`).join("")}`;
    soften(box);
  } catch (e) { box.innerHTML = `<h3 class="h3">Where it's deployed</h3><p class="ink-3" style="margin:0">${esc(e.message)}</p>`; }
}

// in-app actions for step items, shown only where Greenlight can really do them
function stepActions(s, it) {
  if (!status?.gitlab || it.state !== "todo" || !it.repo) return "";
  if (s.id === "code") { const m = /^Merge (\S+) into /.exec(it.title || ""); if (m) return `<button class="btn primary small" data-mkmr="${esc(m[1])}" data-repo="${esc(it.repo)}">${icon("branch")}Create merge request</button>`; }
  if (s.id === "tag") { const next = report?.repos.find(x => x.name === it.repo)?.tags?.next; if (next) return `<button class="btn primary small" data-mktag="${esc(next)}" data-repo="${esc(it.repo)}">${icon("tag")}Create tag ${esc(next)}</button>`; }
  return "";
}

async function createMr(repo, branch) {
  if (!await confirmDialog("Create a merge request?", `From <code>${esc(branch)}</code> into the default branch of ${repoTag(repo)}, titled with the ticket key and summary.`, "Create merge request", "primary")) return;
  try { const r = await api("/api/gitlab/create-mr", { method: "POST", body: JSON.stringify({ repo, branch }) }); toast(`Merge request !${esc(r.iid)} created.<a href="${url(r.url)}" target="_blank" rel="noopener">Open in GitLab</a>`, 8000); if (report) loadTicketReviews(report.key); }
  catch (e) { fail(esc(e.message)); }
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
      try { await api("/api/jira/transition", { method: "POST", body: JSON.stringify({ key, id }) }); d.close(); toast(`${esc(key)} moved.`); tickets && loadTickets(); if (report?.key === key) load(key, false, true); }
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
  return flatpickr(el, { dateFormat: "Y-m-d", altInput: !el.classList.contains("sr"), altFormat: "D j M Y", disableMobile: true, locale: { firstDayOfWeek: 1 }, ...opts });
}

/* ─────────────────────────── command palette (Ctrl K) ─────────────────────────── */
function openCmd() {
  const d = $("#cmdk");
  const cmds = [
    ["home", "Overview", () => go("#overview")], ["ticket", "My tickets", () => go("#tickets")], ["review", "Reviews", () => go("#reviews")],
    ["clock", "Worklogs", () => go("#worklogs")], ["sprint", "Sprint release", () => go("#sprint")], ["team", "Team", () => go("#team")],
    ["checkall", "Check all my tickets", () => { go("#tickets"); checkAll(); }], ["plus", "Log time", () => logDialog(null)],
    ...(report ? [["refresh", `Fetch and check ${report.key} again`, () => load(report.key, true)]] : []),
    ["sun", "Light theme", () => $(`[data-theme-set="light"]`).click()], ["moon", "Dark theme", () => $(`[data-theme-set="dark"]`).click()],
    ["cog", "Account and settings", openSettings],
  ];
  d.innerHTML = `<div class="bar">${icon("search", "ico")}<input id="cmdQ" placeholder="Ticket key, words from a title, or a command" aria-label="Search" autocomplete="off"></div>
    <div class="res" id="cmdRes" role="listbox"></div><div class="foot"><span><kbd>↑</kbd> <kbd>↓</kbd> move</span><span><kbd>Enter</kbd> open</span><span><kbd>Esc</kbd> close</span></div>`;
  let items = [], at = 0, jiraT = 0, jiraQ = "";
  const draw = (q, jira = null) => {
    const ql = q.trim().toLowerCase();
    const cm = cmds.filter(c => !ql || c[1].toLowerCase().includes(ql)).slice(0, ql ? 6 : 12).map(c => ({ ico: c[0], t: c[1], k: "", run: c[2] }));
    const tk = (tickets || []).filter(t => ql && (t.key.toLowerCase().includes(ql) || (t.summary || "").toLowerCase().includes(ql))).slice(0, 8).map(t => ({ mark: overall(statesOf(t.lastBuild)), t: t.summary, k: t.key, run: () => select(t.key) }));
    const rc = !ql ? (status?.recent || []).slice(0, 5).map(r => ({ mark: overall(statesOf(r)), t: r.summary || r.key, k: r.key, run: () => select(r.key) })) : [];
    const jr = (jira || []).filter(x => !tk.some(t => t.k === x.key)).map(t => ({ mark: "none", t: t.summary, k: t.key, run: () => select(t.key) }));
    items = [...tk, ...jr, ...rc, ...cm];
    if (at >= items.length) at = 0;
    const sec = (cap, list, off) => list.length ? `<div class="cap">${cap}</div>${list.map((x, i) => `<button role="option" aria-selected="${off + i === at}" data-i="${off + i}">${x.mark ? mark(x.mark, false) : icon(x.ico, "ico")}<span class="t">${esc(x.t)}</span><span class="k mono">${esc(x.k)}</span></button>`).join("")}` : "";
    $("#cmdRes").innerHTML = (sec("Your tickets", tk, 0) + sec("From Jira", jr, tk.length) + sec("Recent", rc, tk.length + jr.length) + sec("Go to", cm, tk.length + jr.length + rc.length))
      || `<div class="none">${ql.length >= 2 ? `<span class="s2-msg"><span class="spinner"></span>Searching Jira</span>` : "Nothing matches."}</div>`;
    $$("#cmdRes [data-i]").forEach(b => { b.onclick = () => run(+b.dataset.i); b.onmousemove = () => { if (at !== +b.dataset.i) { at = +b.dataset.i; mark2(); } }; });
  };
  const mark2 = () => $$("#cmdRes [data-i]").forEach(b => b.setAttribute("aria-selected", +b.dataset.i === at));
  const run = i => { const x = items[i]; if (!x) return; d.close(); x.run(); };
  const go = h => { history.pushState(null, "", h); route(); };
  $("#cmdQ").oninput = e => {
    const q = e.target.value; at = 0; draw(q);
    clearTimeout(jiraT);
    if (q.trim().length >= 2 && status?.account) jiraT = setTimeout(async () => { jiraQ = q; try { const r = await api(`/api/search?q=${encodeURIComponent(q.trim())}`); if (jiraQ === $("#cmdQ")?.value) draw(q, r.items); } catch { } }, 250);
  };
  $("#cmdQ").onkeydown = e => {
    if (e.key === "ArrowDown") { e.preventDefault(); at = Math.min(items.length - 1, at + 1); mark2(); $(`#cmdRes [data-i="${at}"]`)?.scrollIntoView({ block: "nearest" }); }
    else if (e.key === "ArrowUp") { e.preventDefault(); at = Math.max(0, at - 1); mark2(); $(`#cmdRes [data-i="${at}"]`)?.scrollIntoView({ block: "nearest" }); }
    else if (e.key === "Enter") { e.preventDefault(); run(at); }
  };
  d.onclick = e => { if (e.target === d) d.close(); };
  draw("");
  d.showModal(); $("#cmdQ").focus();
}

/* ─────────────────────────── notifications and the end-of-day reminder ─────────────────────────── */
function notify(title, body, onClick) {
  toast(`<b>${esc(title)}</b> ${esc(body)}`, 8000);
  try { if ("Notification" in window && Notification.permission === "granted" && document.hidden) { const n = new Notification(title, { body, icon: $("link[rel=icon]").href }); n.onclick = () => { window.focus(); onClick?.(); n.close(); }; } } catch { }
}
async function pollGitlab() {
  if (!status?.gitlab) return;
  let now;
  try { now = await api("/api/notifications"); } catch { return; }
  let was = null; try { was = JSON.parse(localStorage.getItem("gl-notes") || "null"); } catch { }
  try { localStorage.setItem("gl-notes", JSON.stringify(now)); } catch { }
  const n = now.reviews.length;
  if (reviews && reviews.length !== n) api("/api/reviews").then(r => { reviews = r; navCounts(); if (railMode === "review") renderList(); }).catch(() => {});
  if (!was) return;   // first look: nothing to compare with
  const old = new Map(was.mine.map(m => [m.id, m]));
  for (const r of now.reviews) if (!was.reviews.some(x => x.id === r.id)) notify("New review request", `${r.author}: ${r.title}`, () => { setSection("reviews"); openReview(r.project, r.iid); });
  for (const m of now.mine) {
    const o = old.get(m.id); if (!o) continue;
    if (m.notes > o.notes) notify(`New comments on !${m.iid}`, m.title, () => window.open(m.url, "_blank", "noopener"));
    if (m.pipeline === "failed" && o.pipeline !== "failed") notify(`Pipeline failed on !${m.iid}`, m.title, () => window.open(m.url, "_blank", "noopener"));
    for (const who of m.changesRequested) if (!o.changesRequested.includes(who)) notify(`${who} requested changes`, `!${m.iid} ${m.title}`, () => window.open(m.url, "_blank", "noopener"));
    for (const who of m.approved) if (!o.approved.includes(who)) notify(`${who} approved !${m.iid}`, m.title, () => window.open(m.url, "_blank", "noopener"));
  }
}
function remindTick() {
  if (!week || !status?.account) return;
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
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") { e.preventDefault(); if (!$("#cmdk").open) openCmd(); return; }
  if (/INPUT|SELECT|TEXTAREA/.test(document.activeElement?.tagName) || document.querySelector(".select2-container--open") || $("#settings").open || $("#cmdk").open || e.ctrlKey || e.metaKey || e.altKey) return;
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
    api("/api/reviews").then(r => { reviews = r; navCounts(); if (railMode === "review") renderList(); else renderRailMode(); if (section === "overview") renderOverview(); else if (section === "reviews" && !reviewing) reviewsBlank(); }).catch(() => {});
    pollGitlab(); setInterval(pollGitlab, 3 * 60 * 1000);
  }
  if (section === "overview") renderOverview();
  remindTick(); setInterval(remindTick, 60 * 1000);
}
boot();
