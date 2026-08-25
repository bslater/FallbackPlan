// FallbackPlan web console (ADR-0036).
//
// The page is a relay's client: every panel renders what the service answered,
// never a state derived here (ADR-0028 §8). The one thing the page adds is
// honesty about its own connection — when the service stops answering, panels
// are marked stale with the age of last contact, never left green and never
// painted failed (NFR-OPS-006).
//
// No framework, no build step, no inline script (the CSP forbids it): plain
// DOM, template strings escaped at every interpolation, and one delegated
// click handler.

/* ---------------------------------------------------------------- token */

const params = new URLSearchParams(location.search);
if (params.get("token")) {
  sessionStorage.setItem("fbp-token", params.get("token"));
  params.delete("token");
  const rest = params.toString();
  history.replaceState(null, "", location.pathname + (rest ? "?" + rest : "") + location.hash);
}
const token = sessionStorage.getItem("fbp-token");

// The SERVICE session, which is a different thing from the console's bearer
// token above. The bearer token says this browser may talk to this console;
// this says which person is acting on the service behind it. Held per browser
// rather than by the console process, so one console relaying for several
// people does not make every action attributable to whoever signed in first
// (ADR-0045 §5).
let session = sessionStorage.getItem("fbp-session");

// The sign-in screen's own state, kept out of S because it holds a password
// field for as long as somebody is typing in it and nothing else should be
// able to reach it.
let SI = null;

function rememberSession(value) {
  session = value;
  if (value) sessionStorage.setItem("fbp-session", value);
  else sessionStorage.removeItem("fbp-session");
}

const appEl = document.getElementById("app");
const gateEl = document.getElementById("gate");

/* ---------------------------------------------------------------- state */

const S = {
  connected: null,          // null until first answer; then true/false
  signedInUser: null,       // whose session this browser is presenting, per describe_service
  signInRequired: false,    // whether the sign-in screen stands in place of the views
  everRefused: false,       // whether a command has been refused for want of a session
  diagnostics: null,        // DiagnosticsResult, or null before the first read
  logRecords: [],           // LogRecordDescriptor[] newest last, capped for the DOM's sake
  logCursor: 0,             // the sequence to ask from next
  logDropped: false,        // whether this reader has fallen behind the ring
  logLevelFilter: "",       // the minimum level asked for, "" for everything
  lastContact: null,        // ms epoch of last successful exchange
  desc: null,               // ServiceDescriptionResult
  status: null,             // StatusResult
  sets: [],                 // BackupSetDescriptor[]
  snapshots: [],            // SnapshotDescriptor[]
  jobs: [],                 // JobDescriptor[]
  progress: new Map(),      // jobId -> JobProgress (live, via SSE)
  view: "overview",
  snapshotFilter: "",
  destinations: [],         // DestinationDescriptor[]
  pairings: [],             // PairingDescriptor[]
  invites: [],              // PairingInviteDescriptor[]
  notices: null,            // NoticeDescriptor[]; null until first list_notices
  noticesHistory: false,    // whether the view includes acknowledged history
  setupRequired: false,     // describe_service said the ceremony is unfinished (ADR-0044)
  setupState: null,         // setup_required | kit_required | ready — which step it resumes at
};

const SETTLED = new Set([
  "Complete", "CompletedWithFailures", "Cancelled", "Paused", "FailedRecoverable", "FailedPermanent",
]);

const PROTECTION = {
  NeverBackedUp: { cls: "", icon: "○", label: "Never backed up", blurb: "No committed snapshot exists for this set yet." },
  Captured: { cls: "warn", icon: "◐", label: "Captured", blurb: "Committed, but only within this machine's own failure domain — no defence against losing the machine." },
  Protected: { cls: "ok", icon: "●", label: "Protected", blurb: "Durable at a replica outside this machine's failure domain." },
  Replicated: { cls: "ok", icon: "●", label: "Replicated", blurb: "Durable at a named destination." },
  Verified: { cls: "ok", icon: "✔", label: "Verified", blurb: "Independently confirmed at a destination." },
  PolicyCompliant: { cls: "ok", icon: "✔", label: "Policy compliant", blurb: "This set's durability policy is satisfied." },
  Degraded: { cls: "serious", icon: "▲", label: "Degraded", blurb: "Recoverable, but below policy — act soon." },
  Unrecoverable: { cls: "bad", icon: "✖", label: "Unrecoverable", blurb: "Required objects are missing or damaged with no replica able to heal them." },
};

const JOBSTATE = {
  Pending: { cls: "", label: "Pending" },
  Scanning: { cls: "accent", label: "Scanning" },
  Reading: { cls: "accent", label: "Reading" },
  Segmenting: { cls: "accent", label: "Segmenting" },
  Packing: { cls: "accent", label: "Packing" },
  Uploading: { cls: "accent", label: "Uploading" },
  Publishing: { cls: "accent", label: "Publishing" },
  Verifying: { cls: "accent", label: "Verifying" },
  Complete: { cls: "ok", label: "Complete" },
  Paused: { cls: "warn", label: "Paused" },
  Retrying: { cls: "warn", label: "Retrying" },
  Cancelled: { cls: "", label: "Cancelled" },
  FailedRecoverable: { cls: "serious", label: "Failed — will retry" },
  FailedPermanent: { cls: "bad", label: "Failed — needs attention" },
  CompletedWithFailures: { cls: "warn", label: "Partial" },
};

const DEST_STATE = {
  "in-sync": { cls: "ok", icon: "●" },
  "behind": { cls: "warn", icon: "◐" },
  "unavailable": { cls: "serious", icon: "◌" },
  "failed": { cls: "bad", icon: "✖" },
  "not-supported": { cls: "", icon: "–" },
};

/* ---------------------------------------------------------------- utils */

function esc(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;").replaceAll("'", "&#39;");
}

function fmtBytes(n) {
  if (n == null) return "—";
  const units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
  let v = Number(n), u = 0;
  while (v >= 1024 && u < units.length - 1) { v /= 1024; u++; }
  return `${v >= 100 || u === 0 ? Math.round(v) : v.toFixed(1)} ${units[u]}`;
}

function fmtCount(n) { return Number(n ?? 0).toLocaleString(); }

function fmtWhen(ms) {
  if (!ms) return "—";
  const d = new Date(Number(ms));
  return d.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
}

function rel(ms) {
  if (!ms) return "never";
  const s = Math.max(0, (Date.now() - Number(ms)) / 1000);
  if (s < 50) return "just now";
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 172800) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86400)} d ago`;
}

function setName(backupSetId) {
  const set = S.sets.find(s => s.id === backupSetId);
  return set ? set.name : (backupSetId ? backupSetId.slice(0, 12) + "…" : "—");
}

// A descriptor's capture roots: `roots` is the whole truth from a 1.10
// service; `root` is the pre-multi-root single (ADR-0040).
function rootsOf(set) {
  return set.roots?.length ? set.roots.map(root => root.path) : set.root ? [set.root] : [];
}

function rootsSummary(set) {
  const roots = rootsOf(set);
  return roots.length === 1 ? roots[0] : `${roots.length} folders`;
}

function badge(meta, label) {
  return `<span class="badge ${meta.cls}">${meta.icon ?? ""} ${esc(label)}</span>`;
}

/* ---------------------------------------------------------------- api */

async function api(command, { signal } = {}) {
  let response;
  try {
    response = await fetch("/api/command", {
      method: "POST",
      headers: session
        ? {
            "Content-Type": "application/json",
            "Authorization": "Bearer " + token,
            "X-FallbackPlan-Session": session,
          }
        : { "Content-Type": "application/json", "Authorization": "Bearer " + token },
      body: JSON.stringify(command),
      signal,
    });
  } catch (error) {
    // An abort is this page changing its mind, not the console failing —
    // and it does real work: the console drops its service connection for
    // the aborted request, which the service takes as a hang-up and cancels
    // the command's work.
    if (error.name === "AbortError") throw new ConsoleError("aborted", "This request was superseded.");
    setConnected(false);
    throw new ConsoleError("unreachable", "The console process stopped answering.");
  }

  if (response.status === 401) { gateEl.hidden = false; appEl.hidden = true; throw new ConsoleError("token", "Token refused."); }
  if (response.status === 503) {
    setConnected(false);
    const body = await safeJson(response);
    throw new ConsoleError("unreachable", body?.message ?? "The service is not listening.");
  }
  if (!response.ok) {
    const body = await safeJson(response);
    throw new ConsoleError("transport", body?.message ?? `The console answered ${response.status}.`);
  }

  setConnected(true);
  const body = await response.json();

  // A refusal naming the sign-in is what tells this browser it is anonymous.
  // Read from the answer rather than guessed from the command, because the
  // service is the one that decides whether an installation has accounts.
  if (body?.result === "error" && typeof body.message === "string") {
    // The session this browser held has lapsed — it idled out, was revoked,
    // or did not survive a service restart. Forget it: a dead token
    // re-presented on every request is a doomed resume per poll, for ever.
    // (The 2026-08-25 service log: 581 of them in sixteen minutes.)
    if (session && body.message.includes("not current")) {
      sessionExpired();
    } else if (body.message.includes("has not signed in")) {
      S.everRefused = true;
      S.signInRequired = true;
      renderSignIn();
    }
  }

  return body;
}

// The one transition out of "acting as somebody": forget the dead token so
// requests stop presenting it, stop the event stream so it stops redialling
// with it, and stand the sign-in screen up. The pollers check
// S.signInRequired themselves, so this also quiets them until sign-in.
function sessionExpired() {
  rememberSession(null);
  S.signedInUser = null;
  S.everRefused = true;
  S.signInRequired = true;
  disconnectEvents();
  renderSignIn();
}

async function safeJson(response) { try { return await response.json(); } catch { return null; } }

class ConsoleError extends Error {
  constructor(kind, message) { super(message); this.kind = kind; }
}

// A command whose ServiceError should surface as a toast rather than a throw.
async function run(command, { okToast, errToast, signal } = {}) {
  try {
    const result = await api(command, { signal });
    if (result.result === "error") {
      toast("bad", `${errToast ?? "The service refused"}: ${result.message}`);
      return null;
    }
    if (okToast) toast("ok", okToast);
    return result;
  } catch (error) {
    if (error.kind === "aborted") return null; // superseded on purpose; nothing to tell anyone
    if (error.kind === "unreachable") toast("warn", `Service unreachable — ${error.message}`);
    else if (error.kind !== "token") toast("bad", error.message);
    return null;
  }
}

/* ---------------------------------------------------------------- connection */

function setConnected(up) {
  const was = S.connected;
  S.connected = up;
  if (up) S.lastContact = Date.now();
  if (was !== up) { renderConn(); if (up && was === false) refreshAll(); }
}

function renderConn() {
  const conn = document.getElementById("conn");
  const text = document.getElementById("conn-text");
  const banner = document.getElementById("stale-banner");
  conn.classList.toggle("ok", S.connected === true);
  conn.classList.toggle("bad", S.connected === false);
  if (S.connected) {
    text.textContent = "service reachable";
    banner.hidden = true;
    document.body.classList.remove("stale");
  } else {
    text.textContent = "service unreachable";
    banner.hidden = false;
    banner.textContent = S.lastContact
      ? `Service unreachable — everything below is as of last contact, ${rel(S.lastContact)}. The console keeps retrying.`
      : "No service has answered yet. Start it with `fallbackplan-agent run`, and this page will attach on its own.";
    document.body.classList.add("stale");
  }
}

/* ---------------------------------------------------------------- refresh */

async function refreshStatus() {
  const result = await api({ command: "get_status" }).catch(() => null);
  if (!result || result.result !== "status") return;
  S.status = result;
  document.getElementById("machine-name").textContent = "· " + result.machineName;
  const count = document.getElementById("notices-count");
  count.hidden = !result.notices?.length;
  count.textContent = result.notices?.length ?? 0;
  if (S.view === "overview") renderOverview();
  if (S.view === "notices") refreshNotices();
}

async function refreshSets() {
  const result = await api({ command: "list_backup_sets" }).catch(() => null);
  if (result?.result === "backup_sets") S.sets = result.sets;
}

async function refreshJobs() {
  const result = await api({ command: "list_jobs", activeOnly: false }).catch(() => null);
  if (!result || result.result !== "jobs") return;
  S.jobs = result.jobs;
  const live = S.jobs.filter(j => !SETTLED.has(j.state)).length;
  const count = document.getElementById("jobs-count");
  count.hidden = live === 0;
  count.textContent = live;
  if (S.view === "jobs") renderJobs();
}

async function refreshSnapshots() {
  const result = await api({ command: "list_snapshots" }).catch(() => null);
  if (result?.result === "snapshots") {
    S.snapshots = result.snapshots;
    if (S.view === "snapshots") renderSnapshots();
  }
}

async function refreshDesc() {
  const result = await api({ command: "describe_service" }).catch(() => null);
  if (result?.result === "service_description") {
    S.desc = result;
    // A service older than contract 1.13 answers null here, which reads as
    // "cannot tell" and so as no reason to interrupt anybody.
    const wants = result.setupState === "setup_required" || result.setupState === "kit_required";
    const changed = wants !== S.setupRequired || result.setupState !== S.setupState;
    S.setupState = result.setupState ?? null;
    S.signedInUser = result.signedInUser ?? null;

    // Signing in is asked for when the installation has accounts and this
    // browser is not acting as one of them. users_required is the other side
    // of the same question — the installation finished setup and nobody has an
    // account yet — and both are answered by the same screen.
    S.signInRequired = !wants
      && (result.setupState === "users_required"
        || (result.signedInUser === null && result.contractVersion >= "1.16" && S.everRefused));

    if (changed) { S.setupRequired = wants; renderSetupGate(); }
    renderSignIn();
    if (S.view === "maintenance") renderMaintenance();
    if (S.view === "diagnostics") renderDiagnostics();
  }
}

function refreshAll() {
  refreshDesc();
  refreshSets().then(() => { refreshStatus(); refreshSnapshots(); });
  refreshJobs();
}

/* ---------------------------------------------------------------- SSE */

let eventSource = null;

function connectEvents() {
  disconnectEvents();

  // The session rides the query the same way the console token does, because
  // EventSource cannot set a header. Without it, an installation with
  // accounts answers every watch with an empty stream that ends at once, and
  // EventSource redials every two seconds for ever — progress never arrives.
  let url = "/api/events?token=" + encodeURIComponent(token);
  if (session) url += "&session=" + encodeURIComponent(session);

  eventSource = new EventSource(url);
  eventSource.onmessage = event => {
    const { progress } = JSON.parse(event.data);
    if (!progress) return;
    S.progress.set(progress.jobId, progress);
    if (S.view === "jobs") scheduleJobsRender();
  };
  // The console's answer when the session it was handed is dead: stop
  // streaming, show sign-in, and do not redial with the same dead token.
  eventSource.addEventListener("session", () => sessionExpired());
  // EventSource redials on its own; nothing to do on error — the poller is
  // what decides reachability, from actual answers.
}

function disconnectEvents() {
  eventSource?.close();
  eventSource = null;
}

let jobsRenderQueued = false;
function scheduleJobsRender() {
  if (jobsRenderQueued) return;
  jobsRenderQueued = true;
  requestAnimationFrame(() => { jobsRenderQueued = false; renderJobs(); });
}

/* ---------------------------------------------------------------- views */

const VIEWS = ["overview", "snapshots", "jobs", "notices", "config", "maintenance", "diagnostics"];

function route() {
  const view = location.hash.replace("#", "") || "overview";
  S.view = VIEWS.includes(view) ? view : "overview";
  for (const name of VIEWS) {
    document.getElementById("view-" + name).hidden = name !== S.view;
  }
  for (const link of document.querySelectorAll(".nav a")) {
    link.classList.toggle("active", link.dataset.view === S.view);
  }
  ({ overview: renderOverview, snapshots: renderSnapshots, jobs: renderJobs,
     notices: renderNotices, config: renderConfig, maintenance: renderMaintenance,
     diagnostics: renderDiagnostics })[S.view]();
  if (S.view === "config") refreshConfigData();
  if (S.view === "notices") refreshNotices();
  if (S.view === "diagnostics") refreshDiagnostics();
}

/* ----- overview ----- */

function renderOverview() {
  const el = document.getElementById("view-overview");
  const sets = S.status?.sets ?? [];

  if (!S.status) {
    el.innerHTML = `<h2>Overview</h2><div class="card empty"><span class="big">⏳</span>Waiting for the service's first answer…</div>`;
    return;
  }

  if (sets.length === 0) {
    el.innerHTML = `
      <h2>Overview</h2>
      <p class="view-sub">Observed ${esc(rel(S.status.observedAt))} on ${esc(S.status.machineName)}</p>
      <div class="card empty"><span class="big">🗂</span>
        No backup sets are configured yet.<br>
        Create one under <a href="#config">Configuration</a> — add a destination first; every set needs at least one.
      </div>`;
    return;
  }

  el.innerHTML = `
    <h2>Overview</h2>
    <p class="view-sub">Per set, per destination — as the service derives it. Observed ${esc(rel(S.status.observedAt))}.</p>
    <div class="grid cols-2">${sets.map(renderSetCard).join("")}</div>`;
}

function renderSetCard(set) {
  const meta = PROTECTION[set.status.state] ?? { cls: "", icon: "?", label: set.status.state, blurb: "" };
  const config = S.sets.find(s => s.name === set.setName);
  const verification = set.status.verification
    ? `<span class="chip" title="Verification coverage and age — never a bare tick">
         ${Math.round(set.status.verification.coverage * 100)}% verified ${esc(rel(set.status.verification.verifiedAtUnixMilliseconds))}
       </span>`
    : "";

  const destinations = set.destinations?.length ? `
    <div class="table-wrap"><table class="data">
      <thead><tr><th>Destination</th><th>State</th><th>Failure domain</th><th>Possession</th><th>Last sync</th></tr></thead>
      <tbody>${set.destinations.map(d => {
        const ds = DEST_STATE[d.state] ?? { cls: "", icon: "?" };
        return `<tr>
          <td><b>${esc(d.name)}</b> <span class="detail">${esc(d.kind)}</span>${d.detail ? `<div class="detail">${esc(d.detail)}</div>` : ""}</td>
          <td>${badge(ds, d.state)}</td>
          <td>${esc(d.failureDomain)}</td>
          <td class="detail">${esc(d.verification)}</td>
          <td class="detail">${esc(rel(d.lastSuccessAt))}</td>
        </tr>`;
      }).join("")}</tbody>
    </table></div>` : `<p class="sub">No destinations declared for this set.</p>`;

  return `<div class="card">
    <h3>${esc(set.setName)} ${badge(meta, meta.label)} ${verification}</h3>
    <p class="sub">${config ? `<span title="${esc(rootsOf(config).join("\n"))}">${esc(rootsSummary(config))}</span> · ` : ""}${esc(meta.blurb)}
       ${set.nextRun ? `· next run ${esc(fmtWhen(Date.parse(set.nextRun)))}` : "· manual only"}</p>
    ${destinations}
    ${set.status.warnings?.length ? `<ul class="warnings">${set.status.warnings.map(w => `<li>${esc(w)}</li>`).join("")}</ul>` : ""}
    <div class="actions-row">
      <button type="button" class="btn primary small" data-action="backup" data-set="${esc(set.setName)}">⛊ Back up now</button>
      <button type="button" class="btn small" data-action="sync" data-set="${esc(set.setName)}">⇄ Sync destinations</button>
      <button type="button" class="btn small" data-action="what-changed" data-set="${esc(set.setName)}">Δ What changed?</button>
      <button type="button" class="btn small" data-action="backup-full" data-set="${esc(set.setName)}">Full…</button>
    </div>
  </div>`;
}

/* ----- snapshots ----- */

function renderSnapshots() {
  const el = document.getElementById("view-snapshots");
  const snapshots = [...S.snapshots].reverse()
    .filter(s => !S.snapshotFilter || s.backupSetId === S.snapshotFilter);

  const filter = S.sets.length > 1 ? `
    <label class="field" for="snapshot-filter">Backup set</label>
    <select id="snapshot-filter" data-action-change="filter-snapshots">
      <option value="">All sets</option>
      ${S.sets.map(s => `<option value="${esc(s.id)}" ${s.id === S.snapshotFilter ? "selected" : ""}>${esc(s.name)}</option>`).join("")}
    </select>` : "";

  el.innerHTML = `
    <h2>Snapshots</h2>
    <p class="view-sub">Every committed point in time. Restores write on the service's machine — content never streams to this page.</p>
    ${filter}
    ${snapshots.length === 0
      ? `<div class="card empty mt"><span class="big">📸</span>No snapshots yet. The first backup creates one.</div>`
      : `<div class="card mt"><div class="table-wrap"><table class="data">
          <thead><tr><th>Captured</th><th>Set</th><th class="num">Files</th><th>Capture</th><th>Destinations</th><th></th></tr></thead>
          <tbody>${snapshots.map(s => `
            <tr>
              <td><b>${esc(fmtWhen(s.capturedAt))}</b><div class="detail mono">${esc(s.snapshotId.slice(0, 16))}…</div></td>
              <td>${esc(setName(s.backupSetId))}</td>
              <td class="num">${fmtCount(s.files)}</td>
              <td>${s.captureStatus === 1 ? badge({ cls: "ok", icon: "✔" }, "complete") : badge({ cls: "warn", icon: "◐" }, "partial")}</td>
              <td>${(s.destinations ?? []).map(d => `<span class="chip">${esc(d)}</span>`).join(" ") || "<span class='detail'>—</span>"}</td>
              <td>
                <button type="button" class="btn small" data-action="browse" data-snapshot="${esc(s.snapshotId)}">Browse</button>
                <button type="button" class="btn small" data-action="restore" data-snapshot="${esc(s.snapshotId)}" data-path="">Restore…</button>
              </td>
            </tr>`).join("")}</tbody>
        </table></div></div>`}`;
}

/* ----- jobs ----- */

function renderJobs() {
  const el = document.getElementById("view-jobs");
  const live = S.jobs.filter(j => !SETTLED.has(j.state));
  const settled = [...S.jobs.filter(j => SETTLED.has(j.state))].reverse().slice(0, 50);

  el.innerHTML = `
    <h2>Jobs</h2>
    <p class="view-sub">Live progress streams from the service; the list below is its authoritative job record.</p>
    ${live.length ? `<div class="grid cols-2">${live.map(renderLiveJob).join("")}</div>` : ""}
    ${settled.length === 0 && live.length === 0
      ? `<div class="card empty"><span class="big">🕘</span>No jobs yet. Scheduled backups appear here as they run.</div>`
      : settled.length ? `<div class="card ${live.length ? "mt" : ""}"><h3>History</h3>
        <div class="table-wrap"><table class="data">
          <thead><tr><th>Started</th><th>Set</th><th>Outcome</th><th>Snapshot</th><th>Detail</th></tr></thead>
          <tbody>${settled.map(j => `
            <tr>
              <td>${esc(fmtWhen(j.startedAt))}</td>
              <td>${esc(setName(j.backupSetId))}</td>
              <td>${badge(JOBSTATE[j.state] ?? { cls: "", label: j.state }, (JOBSTATE[j.state] ?? { label: j.state }).label)}</td>
              <td class="mono detail">${j.snapshotId ? esc(j.snapshotId.slice(0, 16)) + "…" : "—"}</td>
              <td class="detail">${esc(j.detail ?? "")}</td>
            </tr>`).join("")}</tbody>
        </table></div></div>` : ""}`;

  // The CSP forbids inline style attributes, so mark widths are set from
  // script — the one place a computed style is applied.
  for (const bar of el.querySelectorAll(".meter > i")) {
    bar.style.width = (bar.dataset.w ?? 0) + "%";
  }
}

function renderLiveJob(job) {
  const progress = S.progress.get(job.id);
  const meta = JOBSTATE[progress?.state ?? job.state] ?? { cls: "accent", label: job.state };
  const seen = progress?.filesSeen ?? 0;
  const handled = (progress?.filesDone ?? 0) + (progress?.filesReused ?? 0) + (progress?.filesFailed ?? 0);
  const ratio = seen > 0 ? Math.min(100, Math.round(handled / seen * 100)) : 0;
  const scanning = !progress || progress.state === "Scanning" || seen === 0;

  return `<div class="card job-live">
    <h3>${esc(setName(job.backupSetId))} ${badge(meta, meta.label)}</h3>
    <p class="sub">Job <span class="mono">${esc(job.id)}</span> · started ${esc(rel(job.startedAt))}</p>
    <div class="meter ${scanning ? "indeterminate" : ""}"><i data-w="${ratio}"></i></div>
    ${progress ? `<div class="job-stats">
        <span><b>${fmtCount(handled)}</b> of <b>${fmtCount(seen)}</b> files seen</span>
        <span><b>${fmtCount(progress.filesReused)}</b> unchanged</span>
        ${progress.filesFailed ? `<span><b>${fmtCount(progress.filesFailed)}</b> failed</span>` : ""}
        <span><b>${fmtBytes(progress.bytesStored)}</b> written of <b>${fmtBytes(progress.bytesSeen)}</b> read</span>
      </div>` : `<div class="job-stats"><span>Waiting for the first progress event…</span></div>`}
    <div class="actions-row">
      <button type="button" class="btn small" data-action="cancel-job" data-job="${esc(job.id)}">✕ Cancel</button>
    </div>
  </div>`;
}

/* ----- notices ----- */

async function refreshNotices() {
  const result = await api({ command: "list_notices", includeAcknowledged: S.noticesHistory }).catch(() => null);
  if (result?.result === "notices") { S.notices = result.notices; renderNotices(); }
}

function renderNotices() {
  const el = document.getElementById("view-notices");
  const notices = S.notices ?? [];
  const pending = notices.filter(notice => !notice.acknowledgedAt);
  el.innerHTML = `
    <h2>Notices</h2>
    <p class="view-sub">Durable events awaiting a person — a peering ended, terms narrowed, a set reconfigured, a hold
    outstayed its deferral. Acknowledging says you have seen one; it stays on record.</p>
    <div class="actions-row">
      ${pending.length > 1 ? `<button type="button" class="btn small" data-action="notice-ack-all">Acknowledge all</button>` : ""}
      <button type="button" class="btn small" data-action="notice-history">${S.noticesHistory ? "Hide" : "Show"} acknowledged history</button>
    </div>
    ${notices.length === 0
      ? `<div class="card empty"><span class="big">✅</span>${S.noticesHistory ? "No notices have ever been raised." : "Nothing awaits you."}</div>`
      : `<div class="card notice-list">${notices.map(notice => `
          <div class="notice${notice.acknowledgedAt ? " done" : ""}">
            <span class="text">${esc(notice.message)}
              <span class="subtle" title="${esc(fmtWhen(notice.raisedAt))}">· raised ${esc(rel(notice.raisedAt))}
              ${notice.acknowledgedAt ? `· acknowledged ${esc(rel(notice.acknowledgedAt))}` : ""}</span></span>
            ${notice.acknowledgedAt ? "" : `<button type="button" class="btn small" data-action="notice-ack" data-id="${esc(notice.id)}">Acknowledge</button>`}
          </div>`).join("")}</div>`}`;
}

/* ----- maintenance ----- */

/* ----- diagnostics ----- */

// The seventh view (ADR-0043 §6, FR-SVC-010). It reads the service's in-memory
// ring through the contract like every other view, on the existing poll rather
// than a second SSE stream: a log a person is watching is being read at human
// speed, and a stream would be a second push channel to keep honest for no gain.
//
// The level control is here rather than withheld. The console runs on loopback,
// so it is a local caller and set_log_level is permitted to it (ADR-0028 §6);
// the change does not persist, so a level somebody raised and forgot goes back
// on the next restart rather than becoming a machine that has been at trace for
// eight months.

const LOG_LEVELS = ["trace", "debug", "information", "warning", "error", "critical", "none"];

// What the DOM will hold. The ring is the service's memory; this is the
// browser's, and an unbounded one would turn a busy service into a tab that
// eventually stops responding.
const LOG_RECORDS_KEPT = 500;

async function refreshDiagnostics() {
  const diagnostics = await run({ command: "get_diagnostics" });
  if (diagnostics?.result === "diagnostics") S.diagnostics = diagnostics;

  const page = await run({
    command: "read_log",
    sinceSequence: S.logCursor,
    maxRecords: 200,
    minimumLevel: S.logLevelFilter || null,
  });

  if (page?.result === "log_records") {
    if (page.dropped) S.logDropped = true;
    S.logRecords = [...S.logRecords, ...page.records].slice(-LOG_RECORDS_KEPT);
    S.logCursor = page.nextSequence;
  }

  if (S.view === "diagnostics") renderDiagnostics();
}

function renderDiagnostics() {
  const el = document.getElementById("view-diagnostics");
  const d = S.diagnostics;
  const categories = Object.entries(d?.categoryLevels ?? {});

  el.innerHTML = `
    <h2>Diagnostics</h2>
    <p class="view-sub">The engineer's view, not the operator's — anything you must act on is a notice, and a log line is never the fix for a missing one.</p>
    <div class="grid cols-2">

      <div class="card">
        <h3>🎚 Level</h3>
        <p class="sub">Takes effect immediately and lasts until the service stops; <code>config.json</code>'s level applies again after a restart.</p>
        <label class="field" for="diag-level">Default level</label>
        <select id="diag-level">
          ${LOG_LEVELS.map(name => `<option${name === (d?.defaultLevel ?? "") ? " selected" : ""}>${esc(name)}</option>`).join("")}
        </select>
        <label class="field" for="diag-category">Category override <span class="sub">(optional, longest prefix wins)</span></label>
        <input id="diag-category" type="text" placeholder="FallbackPlan.Repository">
        <div class="actions-row"><button type="button" class="btn" data-action="set-log-level">Apply</button></div>
        ${categories.length === 0 ? "" : `
          <p class="sub">In force now: ${categories.map(([k, v]) => `<code>${esc(k)}</code> → ${esc(v)}`).join(", ")}</p>`}
      </div>

      <div class="card">
        <h3>📦 Where the records go</h3>
        ${d ? `
          <div class="table-wrap"><table class="data"><tbody>
            <tr><td>Durable file</td><td>${d.durableSink ? `yes — ${esc(String(d.retainFiles))} kept, ${fmtBytes(d.maximumFileBytes)} each` : "no — the ring is all there is"}</td></tr>
            <tr><td>Ring</td><td>${esc(String(d.ringCapacity))} records, sequences ${esc(String(d.oldestSequence))}–${esc(String(d.nextSequence))}</td></tr>
          </tbody></table></div>
          <p class="sub">The service does not say where the file is: it exposes no filesystem access to clients, and a log reader is not where to make an exception.</p>`
        : `<p class="sub">Waiting for the service.</p>`}
      </div>

      <div class="card records">
        <h3>📜 Records</h3>
        <div class="actions-row">
          <label class="field" for="diag-filter">Show</label>
          <select id="diag-filter">
            <option value=""${S.logLevelFilter === "" ? " selected" : ""}>everything the ring holds</option>
            ${LOG_LEVELS.slice(0, -1).map(name =>
              `<option value="${esc(name)}"${S.logLevelFilter === name ? " selected" : ""}>${esc(name)} and above</option>`).join("")}
          </select>
          <button type="button" class="btn" data-action="clear-log">Clear this view</button>
        </div>
        ${S.logDropped ? `<p class="sub dropped">Records were dropped before what you can see — the service logged faster than this page was reading. Raise <code>ring_capacity</code>, or leave this view open.</p>` : ""}
        ${S.logRecords.length === 0
          ? `<p class="sub">Nothing at this level yet.</p>`
          : `<div class="table-wrap"><table class="data"><thead><tr><th>#</th><th>When</th><th>Level</th><th>Event</th><th>Category</th><th>Message</th></tr></thead><tbody>
              ${S.logRecords.slice().reverse().map(r => `
                <tr class="lvl-${esc(r.level)}">
                  <td>${esc(String(r.sequence))}</td>
                  <td>${esc(new Date(r.timestampUnixMilliseconds).toLocaleTimeString())}</td>
                  <td>${esc(r.level)}</td>
                  <td>${esc(String(r.eventId))}</td>
                  <td><code>${esc(r.category)}</code></td>
                  <td>${esc(r.message)}${r.exceptionType ? `<br><span class="sub">${esc(r.exceptionType)}: ${esc(r.exceptionMessage ?? "")}</span>` : ""}</td>
                </tr>`).join("")}
             </tbody></table></div>`}
      </div>

    </div>`;

  document.getElementById("diag-filter").addEventListener("change", event => {
    // A different filter is a different question, so the answer starts over
    // rather than mixing two level sets in one table.
    S.logLevelFilter = event.target.value;
    S.logRecords = [];
    S.logCursor = 0;
    S.logDropped = false;
    refreshDiagnostics();
  });
}

function renderMaintenance() {
  const el = document.getElementById("view-maintenance");
  const destinationOptions = [...new Set(S.sets.flatMap(s => s.destinations ?? []))];

  el.innerHTML = `
    <h2>Maintenance</h2>
    <p class="view-sub">Verification reads and reports; retention's apply is the one destructive act here, and it asks twice.</p>
    <div class="grid cols-2">

      <div class="card">
        <h3>🔍 Verify the staging archives</h3>
        <p class="sub">Re-reads the hub's own stored blobs against their seals.</p>
        <label class="field" for="verify-level">Depth</label>
        <select id="verify-level">
          <option value="locator">locator — headers and footers</option>
          <option value="digest" selected>digest — whole-blob digests</option>
          <option value="records">records — every record decrypted</option>
        </select>
        <div class="actions-row">
          <button type="button" class="btn" data-action="verify">Verify</button>
          <button type="button" class="btn" data-action="check">Full health check</button>
        </div>
      </div>

      <div class="card">
        <h3>🎯 Verify a destination</h3>
        <p class="sub">Asks what a destination can still be trusted for (FR-VER-002).</p>
        <label class="field" for="vd-set">Set</label>
        <select id="vd-set"><option value="">Every set</option>${S.sets.map(s => `<option>${esc(s.name)}</option>`).join("")}</select>
        <label class="field" for="vd-dest">Destination</label>
        <select id="vd-dest"><option value="">Every destination</option>${destinationOptions.map(d => `<option>${esc(d)}</option>`).join("")}</select>
        <label class="field">Depth</label>
        <div class="radio-row">
          <label><input type="radio" name="vd-depth" value="probe"> probe — reachable and writable, reads nothing</label>
          <label><input type="radio" name="vd-depth" value="sample" checked> sample — one bounded segment</label>
          <label><input type="radio" name="vd-depth" value="full"> full — every stored object</label>
        </div>
        <div class="actions-row"><button type="button" class="btn" data-action="verify-destination">Run</button></div>
      </div>

      <div class="card">
        <h3>⇄ Sync destinations</h3>
        <p class="sub">Converges declared destinations now, outside the schedule.</p>
        <label class="field" for="sync-set">Set</label>
        <select id="sync-set"><option value="">Every set</option>${S.sets.map(s => `<option>${esc(s.name)}</option>`).join("")}</select>
        <label class="field" for="sync-dest">Destination</label>
        <select id="sync-dest"><option value="">Every destination</option>${destinationOptions.map(d => `<option>${esc(d)}</option>`).join("")}</select>
        <div class="actions-row"><button type="button" class="btn" data-action="sync-explicit">Sync now</button></div>
      </div>

      <div class="card">
        <h3>🗓 Retention</h3>
        <p class="sub">The dry run is always produced; apply tombstones the condemned and sweeps the grace-expired (FR-GC-005).</p>
        <div class="actions-row">
          <button type="button" class="btn" data-action="retention-dry">Dry run</button>
          <button type="button" class="btn danger" data-action="retention-apply">Apply…</button>
        </div>
      </div>

      <div class="card">
        <h3>ℹ️ This service</h3>
        ${S.desc ? `
          <div class="table-wrap"><table class="data"><tbody>
            <tr><td>Machine</td><td><b>${esc(S.desc.machineName)}</b></td></tr>
            <tr><td>Service version</td><td>${esc(S.desc.serviceVersion)}</td></tr>
            <tr><td>Contract</td><td>${esc(S.desc.contractVersion)}</td></tr>
            <tr><td>State directory</td><td class="mono">${esc(S.desc.stateDirectory)}</td></tr>
            <tr><td>Remote binding</td><td>${S.desc.remoteBindingEnabled ? badge({ cls: "accent", icon: "◉" }, "enabled") : badge({ cls: "", icon: "○" }, "off (default)")}</td></tr>
            <tr><td>Active jobs</td><td>${fmtCount(S.desc.activeJobs)}</td></tr>
          </tbody></table></div>` : `<p class="sub">Waiting for the service to describe itself…</p>`}
        <div class="actions-row"><button type="button" class="btn small" data-action="show-config">View configuration</button></div>
      </div>

      <div class="card">
        <h3>🗝 Recovery kit</h3>
        <p class="sub">One of the two things a recovery needs. The kit is useless to a thief without the passphrase — and useless to you without it either.</p>
        ${renderKitStatus()}
      </div>

    </div>`;
}

// FR-KIT-005 asks for kit status "surfaced continuously", and continuously
// means here — on a card an operator passes every time they open Maintenance —
// rather than only inside the setup ceremony they saw once and closed.
//
// Two states, not three. An installation kit carries no destinations, so the
// requirement's staleness trigger cannot fire, and its salt, Argon2id
// parameters and sealing public key are fixed for the life of the installation,
// so nothing else can make it stale either (ADR-0013 as amended).
function renderKitStatus() {
  const status = S.desc?.kitStatus ?? null;

  if (status === null) {
    // A service older than contract 1.15 says nothing here, which reads as
    // "cannot tell" — not as a missing kit, which would be a false alarm.
    return `<p class="sub">This service does not report kit status.</p>`;
  }

  if (status !== "saved") {
    return `
      <p class="dropped"><b>Never saved.</b> Nothing on this machine can rebuild your keys without it.
      Losing the passphrase or the kit makes every backup unrecoverable — there is no reset and no support path.</p>`;
  }

  const when = S.desc.kitConfirmedAt
    ? new Date(Number(S.desc.kitConfirmedAt)).toLocaleString()
    : null;

  return `
    <p>${badge({ cls: "ok", icon: "✓" }, "saved")}${when ? ` <span class="sub">confirmed ${esc(when)}</span>` : ""}</p>
    <p class="sub">Keep it somewhere separate from the passphrase. It holds neither the passphrase nor any key.</p>`;
}

/* ---------------------------------------------------------------- dialogs */

const dialog = document.getElementById("dialog");

function openDialog(html) {
  dialog.innerHTML = html;
  if (!dialog.open) dialog.showModal();
}

function closeDialog() {
  dialog.close();
  dialog.innerHTML = "";
  dialog.classList.remove("wide");
  E = null;
  endSourceScans(); // a walk for an editor that no longer exists stops now, not in ten minutes
  if (W) {
    // The wizard's server-side source handle is released best-effort; the
    // idle sweep reclaims it anyway if this never lands. The passphrase
    // lives only in W and dies here.
    if (W.source) run({ command: "close_restore_source", sourceId: W.source.sourceId });
    W = null;
  }
}

dialog.addEventListener("click", event => {
  if (event.target === dialog) closeDialog(); // backdrop click
});

function reportDialog(title, lines, sub) {
  openDialog(`
    <h3>${esc(title)}</h3>
    ${sub ? `<p class="dlg-sub">${esc(sub)}</p>` : ""}
    <pre class="report">${esc((lines ?? []).join("\n") || "(nothing to report)")}</pre>
    <div class="dlg-actions"><button type="button" class="btn primary" data-action="close-dialog">Close</button></div>`);
}

/* ----- the set-change comparison, shared by the preview and the confirm step ----- */

function comparisonReport(result) {
  const buckets = [
    ["new", result.new], ["updated", result.updated], ["metadata-only", result.metadataOnly],
    ["moved", result.moved], ["deleted", result.deleted],
    ["no longer included by these rules", result.noLongerIncluded],
  ].filter(([, bucket]) => bucket.count > 0);

  const baseline = result.baselineSnapshotId
    ? `vs the last backup: ${result.unchanged} unchanged`
    : "never backed up — everything is new";
  const detail = buckets.map(([label, bucket]) => {
    const more = bucket.count > bucket.sample.length
      ? `\n  … and ${bucket.count - bucket.sample.length} more` : "";
    return `${bucket.count} ${label}\n` + bucket.sample.map(path => `  ${path}`).join("\n") + more;
  }).join("\n");
  const failures = result.failures > 0 ? ` · ${result.failures} unreadable` : "";

  return {
    summary: `${baseline}${buckets.length === 0 ? " — nothing else changed" : ""}${failures}`,
    detail,
  };
}

// Step two of the material-edit save (ADR-0038): the editor stays in the
// dialog, hidden, so Back loses nothing; only the Apply here performs the
// upsert. Rendered even when the comparison failed — the operator may be
// pointing at a drive that is not mounted yet — but then it says so
// instead of pretending to know the consequences. And rendered before the
// comparison arrives ({ comparing: true }): the walk fills the panel in
// from the background, because Apply must never wait on it — the service
// runs its own rescan job after a material save regardless.
function renderSetSaveConfirm(saved, preview, previewError, { comparing = false } = {}) {
  document.getElementById("set-confirm")?.remove();
  const editor = document.getElementById("set-editor");
  if (editor) editor.hidden = true;

  const savedRoots = rootsOf(saved);
  const draftRoots = (E.pendingSave?.roots ?? []).map(root => root.path);
  const sortedEqual = (left, right) =>
    JSON.stringify([...left].sort()) === JSON.stringify([...right].sort());
  const rootsChanged = !sortedEqual(savedRoots, draftRoots);
  const draftIncludes = E.pendingSave?.includeRules ?? [];
  const draftExcludes = E.pendingSave?.excludeRules ?? [];

  const edits = [];
  if (rootsChanged) {
    for (const path of draftRoots.filter(root => !savedRoots.includes(root))) edits.push(`folder added: '${path}'`);
    for (const path of savedRoots.filter(root => !draftRoots.includes(root))) edits.push(`folder removed: '${path}'`);
  }
  if (!sortedEqual(saved.includeRules ?? [], draftIncludes)) {
    edits.push(`include rules: ${(saved.includeRules ?? []).length} → ${draftIncludes.length}`);
  }
  if (!sortedEqual(saved.excludeRules ?? [], draftExcludes)) {
    edits.push(`exclude rules: ${(saved.excludeRules ?? []).length} → ${draftExcludes.length}`);
  }

  const warnings = [];
  if (rootsChanged) {
    warnings.push("The folders changed: version history does not follow files across folders, and "
      + "everything under a newly added folder is captured as new. Growing past one folder (or back "
      + "to one) also moves paths in the snapshot — the service re-anchors the saved rules to match.");
  }
  if (preview?.noLongerIncluded.count > 0) {
    warnings.push(`${preview.noLongerIncluded.count} file(s) the last backup holds will no longer be `
      + "included — future backups skip them, and retention will eventually age their old versions out.");
  }
  if (previewError) {
    warnings.push(`The source could not be compared (${previewError}), so the consequences below are unknown. `
      + "Apply only if you expected that — for example, a drive that is not mounted yet.");
  }

  const report = preview ? comparisonReport(preview) : null;
  const dropping = (preview?.noLongerIncluded.count ?? 0) > 0 || rootsChanged;

  const panel = document.createElement("div");
  panel.id = "set-confirm";
  panel.innerHTML = `
    <h3>Confirm changes to '${esc(saved.name)}'</h3>
    <p class="dlg-sub">Step two of two — nothing is saved yet. This is what the edit means, compared with
    the last backup.</p>
    <pre class="report">${esc(edits.join("\n"))}</pre>
    ${warnings.length ? `<ul class="warnings">${warnings.map(text => `<li>${esc(text)}</li>`).join("")}</ul>` : ""}
    ${report ? `
      <p class="subtle">${esc(report.summary)}</p>
      ${report.detail ? `<pre class="report">${esc(report.detail)}</pre>` : ""}` : comparing ? `
      <p class="subtle">Comparing with the last backup in the background — there is no need to wait.
      Applying runs the same comparison as a service job, and its findings land under Notices.</p>` : ""}
    <div class="dlg-actions">
      <button type="button" class="btn" data-action="set-save-back">Back to editing</button>
      <button type="button" class="btn ${dropping ? "danger" : "primary"}" data-action="set-save-apply">Apply these changes</button>
    </div>`;
  dialog.append(panel);
}

// The actual upsert — reached directly for non-material edits, and only
// through the confirm step's Apply for material ones.
async function applySetUpsert(payload) {
  const result = await run(
    { command: "upsert_backup_set", set: payload }, { errToast: "The service refused the set" });
  if (result?.result === "configuration_change") {
    // A material edit: the service says what it means and has queued a
    // rescan whose finding lands under Notices (ADR-0038).
    const savedName = payload.name;
    closeDialog();
    reportDialog(`Backup set '${savedName}' saved`, result.lines,
      "The next backup captures under the new settings.");
    refreshConfigData(); refreshStatus();
  } else if (result) {
    toast("ok", E.isNew ? `Backup set '${payload.name}' created.` : `Backup set '${payload.name}' saved.`);
    closeDialog();
    refreshConfigData(); refreshStatus();
  }
}

/* ---------------------------------------------------------------- actions */

async function withBusy(button, work) {
  if (button) { button.disabled = true; button.classList.add("busy"); }
  try { await work(); }
  finally { if (button) { button.disabled = false; button.classList.remove("busy"); } }
}

/* -------------------------------------------------- first-run setup (ADR-0044) */

// The one ceremony that runs before anything else works. Three steps —
// what you are about to commit to, the passphrase, and confirming it —
// shown INSTEAD of the console rather than in a dialog over it, because
// there is nothing behind it that functions until an installation has a
// passphrase.
let U = null;

const SETUP_STEPS = ["What this is", "Passphrase", "Confirm", "Recovery kit"];

/* ------------------------------------------------------------- sign-in */

function renderSignIn() {
  const host = document.getElementById("signin");
  if (!host) return;

  // The top bar says who is acting, always, not only after signing in. An
  // action nobody can attribute is the thing this arc exists to end, so the
  // name is on screen rather than a click away.
  const who = document.getElementById("signed-in");
  const out = document.getElementById("sign-out");
  if (who) {
    who.hidden = !S.signedInUser;
    who.textContent = S.signedInUser ?? "";
  }

  if (out) {
    out.hidden = !S.signedInUser;
    out.onclick = signOut;
  }

  if (!S.signInRequired) {
    host.hidden = true;
    if (!S.setupRequired) appEl.hidden = false;
    SI = null;
    return;
  }

  if (!SI) SI = { user: "", busy: false, message: null, first: S.setupState === "users_required" };
  appEl.hidden = true;
  host.hidden = false;

  const heading = SI.first ? "Create the first account" : "Sign in";
  const blurb = SI.first
    ? `This installation is set up and has no accounts yet. The first account is
       its <b>owner</b>: it cannot be deleted, and for now only it may add or
       remove other accounts. Until one exists, anyone who can reach this
       service is indistinguishable from anyone else.`
    : `This service knows who is acting. Signing in is what makes a restore
       attributable to a person and lets one person be revoked without
       affecting anybody else.`;

  host.innerHTML = `<div class="gate-card">
    <h2>${esc(heading)}</h2>
    <p class="muted">${blurb}</p>
    <label>Account name<input id="signin-user" autocomplete="username" value="${esc(SI.user)}"></label>
    <label>Password<input id="signin-pass" type="password"
      autocomplete="${SI.first ? "new-password" : "current-password"}"></label>
    ${SI.message ? `<p class="warn">${esc(SI.message)}</p>` : ""}
    <button id="signin-go" ${SI.busy ? "disabled" : ""}>${SI.busy ? "Working…" : esc(heading)}</button>
  </div>`;

  document.getElementById("signin-user")?.focus();
  document.getElementById("signin-go")?.addEventListener("click", signInSubmit);
}

async function signInSubmit() {
  const user = document.getElementById("signin-user")?.value ?? "";
  const password = document.getElementById("signin-pass")?.value ?? "";

  SI.user = user;
  SI.busy = true;
  SI.message = null;
  renderSignIn();

  try {
    if (SI.first) {
      const created = await api({ command: "create_user", name: user, password });
      if (created.result === "error") { SI.message = created.message; return; }
    }

    const answered = await api({ command: "login", user, password });
    if (answered.result !== "session") {
      SI.message = answered.message ?? "That did not work.";
      return;
    }

    rememberSession(answered.token);
    S.signInRequired = false;
    S.everRefused = false;
    S.signedInUser = answered.user;
    SI = null;
    renderSignIn();
    connectEvents(); // redial the progress stream with the new session
    refreshAll();
    await refreshDesc();
  } catch (failure) {
    SI.message = failure?.message ?? "The console could not reach the service.";
  } finally {
    if (SI) { SI.busy = false; renderSignIn(); }
  }
}

async function signOut() {
  try {
    await api({ command: "logout" });
  } catch {
    // A service that cannot be reached cannot revoke, and the session dies
    // with its process anyway. Forgetting it here is the half that matters.
  }

  rememberSession(null);
  S.signedInUser = null;
  S.signInRequired = true;
  disconnectEvents();
  renderSignIn();
}

function renderSetupGate() {
  const host = document.getElementById("setup");
  if (!S.setupRequired) {
    host.hidden = true;
    appEl.hidden = false;
    U = null;
    return;
  }

  // A service in kit_required has a passphrase already: the ceremony
  // resumes at the kit step rather than asking for one again.
  if (!U) U = S.setupState === "kit_required"
    ? { step: 4, passphrase: "", confirmation: "", acknowledged: true, strength: null, busy: false,
        kit: null, taken: false, resumed: true }
    : { step: 1, passphrase: "", confirmation: "", acknowledged: false, strength: null, busy: false,
        kit: null, taken: false, resumed: false };
  appEl.hidden = true;
  host.hidden = false;
  setupRender();
}

function setupRender() {
  const host = document.getElementById("setup");
  const body = [setupStep1, setupStep2, setupStep3, setupStep4][U.step - 1]();
  host.innerHTML = `<div class="gate-card setup-card">
    <div class="rst-steps">${SETUP_STEPS.map((label, index) =>
      `<span class="${index + 1 === U.step ? "now" : index + 1 < U.step ? "done" : ""}">${esc(label)}</span>`).join("")}</div>
    ${body}
  </div>`;
  if (U.step === 2) {
    const field = document.getElementById("setup-pass");
    field?.focus();
  }
}

function setupStep1() {
  // The wording IS the feature. Everything here is stated before a
  // passphrase field exists, so nobody types one having skipped it.
  return `
    <h1>Set up this installation</h1>
    <p>FallbackPlan is not protecting anything yet. It needs one passphrase, and
    everything else follows from it.</p>
    <ul class="setup-facts">
      <li><b>The passphrase is the master key for this installation.</b> Every
      key protecting your backups is derived from it.</li>
      <li><b>It is never stored</b> — not on this machine, not in the backup, not
      anywhere. This service will be able to add to your backup history and see
      its structure, but it will never be able to read your files back.</li>
      <li><b>It can never be changed.</b> There is no reset and no export.</li>
      <li class="setup-danger"><b>If you lose it, every backup this installation
      ever makes is permanently unrecoverable.</b> Nobody can recover it for you
      — not us, not support, not with the machine in front of them.</li>
    </ul>
    <p class="gate-hint">Write it down and put it somewhere safe before you
    continue. A password manager, or paper somewhere only you can reach.</p>
    <label class="check-row"><input type="checkbox" id="setup-ack" ${U.acknowledged ? "checked" : ""}
      data-action-change="setup-ack">
      I understand that losing this passphrase loses the backup, permanently.</label>
    <div class="dlg-actions">
      <button type="button" class="btn danger" data-action="setup-begin"
        ${U.acknowledged ? "" : "disabled"}>Choose the passphrase</button>
    </div>`;
}

function setupStep2() {
  const meter = U.strength;
  return `
    <h1>Choose the passphrase</h1>
    <p>Longer is better than complicated. Several unrelated words you will
    remember beat a short string of symbols you will not.</p>
    <input type="password" id="setup-pass" class="setup-field" autocomplete="new-password"
      spellcheck="false" placeholder="the passphrase for this installation"
      value="${esc(U.passphrase)}" data-action-input="setup-pass">
    ${meter ? `
      <div class="meter meter-${esc(meter.band)}"><i style="width:${Math.max(4, meter.score)}%"></i></div>
      <ul class="setup-findings">${meter.findings.map(line => `<li>${esc(line)}</li>`).join("")}</ul>` : ""}
    <div class="dlg-actions">
      <button type="button" class="btn" data-action="setup-back">‹ Back</button>
      <button type="button" class="btn primary" data-action="setup-to-confirm"
        ${meter?.acceptable ? "" : "disabled"}>Continue</button>
    </div>`;
}

function setupStep3() {
  const mismatch = U.confirmation.length > 0 && U.confirmation !== U.passphrase;
  return `
    <h1>Type it again</h1>
    <p>The one thing that cannot be fixed later is a typo in a passphrase you
    only ever entered once.</p>
    <input type="password" id="setup-confirm" class="setup-field" autocomplete="new-password"
      spellcheck="false" placeholder="the same passphrase" value="${esc(U.confirmation)}"
      data-action-input="setup-confirm">
    ${mismatch ? `<p class="setup-danger">These do not match.</p>` : ""}
    <div class="dlg-actions">
      <button type="button" class="btn" data-action="setup-back">‹ Back</button>
      <button type="button" class="btn danger" data-action="setup-finish"
        ${U.busy || mismatch || U.confirmation.length === 0 ? "disabled" : ""}>
        ${U.busy ? "Setting up…" : "Set up this installation"}</button>
    </div>`;
}

function setupStep4() {
  // The kit is the second of the two things a recovery needs, and the only
  // one this ceremony can hand over. It holds no passphrase and no keys, so
  // it is safe to print — and useless to anyone who does not also have the
  // passphrase, which is exactly why it must not live beside it.
  if (U.resumed && !U.kit) {
    return `
      <h1>Your recovery kit is still unsaved</h1>
      <p>This installation has its passphrase, but setup is not finished: the
      recovery kit was never confirmed saved.</p>
      <p>A kit can only be built from the passphrase, so to produce one now,
      re-enter it.</p>
      <input type="password" id="setup-pass" class="setup-field" autocomplete="current-password"
        spellcheck="false" placeholder="the passphrase for this installation"
        value="${esc(U.passphrase)}" data-action-input="setup-pass">
      <div class="dlg-actions">
        <button type="button" class="btn danger" data-action="setup-rebuild-kit"
          ${U.busy || U.passphrase.length === 0 ? "disabled" : ""}>
          ${U.busy ? "Building…" : "Build the recovery kit"}</button>
      </div>`;
  }

  return `
    <h1>Save your recovery kit</h1>
    <p>This is the <b>second</b> of the two things a recovery needs. Your
    passphrase is the first, and it is <b>not</b> in this file.</p>
    <ul class="setup-facts">
      <li><b>It holds no passphrase and no keys.</b> It is safe to print, and
      it opens nothing on its own.</li>
      <li><b>Keep it apart from your passphrase.</b> Together in one place they
      are one thing to lose, not two.</li>
      <li><b>It opens every archive this installation makes</b> — including
      backup sets you have not created yet.</li>
    </ul>
    <div class="dlg-actions setup-kit-actions">
      <button type="button" class="btn primary" data-action="setup-kit-file">Download the file</button>
      <button type="button" class="btn" data-action="setup-kit-print">Open the printable page</button>
    </div>
    <label class="check-row"><input type="checkbox" id="setup-kit-ack" ${U.taken ? "" : "disabled"}
      ${U.saved ? "checked" : ""} data-action-change="setup-kit-ack">
      I have saved this somewhere separate from my passphrase.</label>
    ${U.taken ? "" : `<p class="gate-hint">Take one of the two forms above first.</p>`}
    <div class="dlg-actions">
      <button type="button" class="btn danger" data-action="setup-kit-done"
        ${U.busy || !U.saved ? "disabled" : ""}>
        ${U.busy ? "Finishing…" : "Finish setup"}</button>
    </div>`;
}

// The kit is handed over as a download the page builds itself: the console
// host returned it inline and keeps no copy, so there is nothing to fetch
// back and nothing left behind if this tab closes.
function setupTakeKit(kind) {
  if (!U.kit) return;
  const [data, name, type] = kind === "file"
    ? [Uint8Array.from(atob(U.kit.machine), c => c.charCodeAt(0)),
       "fallbackplan-recovery-kit.fbpkrkit", "application/octet-stream"]
    : [U.kit.text, "fallbackplan-recovery-kit.txt", "text/plain;charset=utf-8"];

  const url = URL.createObjectURL(new Blob([data], { type }));
  const link = document.createElement("a");
  link.href = url;
  link.download = name;
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 10000);

  U.taken = true;
  setupRender();
}

// Debounced so a fast typist does not queue a request per keystroke. The
// scoring is the server's so there is exactly one implementation of the
// policy — see WebConsoleHost.AssessPassphraseAsync for why that is worth a
// round trip.
let setupStrengthTimer = null;
function setupScheduleStrength() {
  clearTimeout(setupStrengthTimer);
  setupStrengthTimer = setTimeout(async () => {
    const candidate = U?.passphrase ?? "";
    if (!candidate) { if (U) { U.strength = null; setupRender(); } return; }
    try {
      const response = await fetch("/api/passphrase-strength", {
        method: "POST",
        headers: { "Content-Type": "application/json", "Authorization": "Bearer " + token },
        body: JSON.stringify({ candidate }),
      });
      if (!response.ok || !U) return;
      U.strength = await response.json();
      setupRender();
    } catch {
      // The meter is a courtesy; the submit is where the policy is enforced.
    }
  }, 250);
}

const setupActions = {
  "setup-ack"(el) { U.acknowledged = el.checked; setupRender(); },

  "setup-begin"() { if (U.acknowledged) { U.step = 2; setupRender(); } },

  "setup-back"() { U.step = Math.max(1, U.step - 1); setupRender(); },

  "setup-pass"(el) {
    U.passphrase = el.value;
    // No re-render here: it would replace the field the person is typing in.
    setupScheduleStrength();
    const go = document.querySelector('[data-action="setup-to-confirm"]');
    if (go) go.disabled = !U.strength?.acceptable;
  },

  "setup-to-confirm"() { if (U.strength?.acceptable) { U.step = 3; setupRender(); } },

  "setup-confirm"(el) {
    U.confirmation = el.value;
    const go = document.querySelector('[data-action="setup-finish"]');
    if (go) go.disabled = U.busy || U.confirmation.length === 0 || U.confirmation !== U.passphrase;
  },

  async "setup-finish"() {
    U.busy = true;
    setupRender();
    let body;
    try {
      const response = await fetch("/api/setup", {
        method: "POST",
        headers: { "Content-Type": "application/json", "Authorization": "Bearer " + token },
        body: JSON.stringify({
          passphrase: U.passphrase, confirmation: U.confirmation, acknowledged: U.acknowledged,
        }),
      });
      body = await response.json();
    } catch {
      U.busy = false;
      toast("bad", "The console process stopped answering.");
      setupRender();
      return;
    }

    U.busy = false;
    if (body?.outcome !== "provisioned") {
      toast("warn", body?.detail ?? "Setup did not complete.");
      if (body?.outcome === "weak") { U.step = 2; U.strength = null; setupScheduleStrength(); }
      setupRender();
      return;
    }

    // The passphrase is done with; the kit is not. Setup is not finished
    // until the operator says they saved it (FR-KIT-004).
    U.passphrase = "";
    U.confirmation = "";
    U.strength = null;
    U.kit = body.kit ?? null;
    U.step = 4;
    reportDialog("Passphrase accepted", body.lines ?? []);
    setupRender();
  },

  "setup-kit-file"() { setupTakeKit("file"); },

  "setup-kit-print"() { setupTakeKit("print"); },

  "setup-kit-ack"(el) { U.saved = el.checked; setupRender(); },

  async "setup-rebuild-kit"() {
    // Resuming an installation whose kit was never confirmed. The kit can
    // only come from the passphrase, so it has to be entered again — the
    // one place this ceremony asks twice, and only because the first
    // attempt did not finish.
    U.busy = true;
    setupRender();
    let body;
    try {
      const response = await fetch("/api/recovery-kit", {
        method: "POST",
        headers: { "Content-Type": "application/json", "Authorization": "Bearer " + token },
        body: JSON.stringify({ passphrase: U.passphrase }),
      });
      body = await response.json();
    } catch {
      U.busy = false;
      toast("bad", "The console process stopped answering.");
      setupRender();
      return;
    }

    U.busy = false;
    if (body?.outcome !== "built") {
      toast("warn", body?.detail ?? "The kit could not be built.");
      setupRender();
      return;
    }

    U.passphrase = "";
    U.kit = body.kit;
    U.resumed = false;
    setupRender();
  },

  async "setup-kit-done"() {
    U.busy = true;
    setupRender();
    let result;
    try {
      result = await api({ command: "confirm_recovery_kit", kitChecksum: U.kit.checksum });
    } catch (error) {
      U.busy = false;
      toast("bad", error?.message ?? "Could not record the confirmation.");
      setupRender();
      return;
    }

    U.busy = false;
    if (result?.result === "error") {
      toast("warn", result.message ?? "Could not record the confirmation.");
      setupRender();
      return;
    }

    // Held only as long as the ceremony took.
    U = null;
    S.setupRequired = false;
    renderSetupGate();
    reportDialog("Setup complete", result?.lines ?? []);
    refreshAll();
  },
};

const actions = {
  async "set-log-level"(el) {
    await withBusy(el, async () => {
      const category = document.getElementById("diag-category").value.trim();
      const result = await run(
        { command: "set_log_level", category: category || null, level: document.getElementById("diag-level").value },
        { errToast: "The level was not changed" });
      if (result?.result === "configuration_change") {
        toast("ok", result.lines[0]);
        refreshDiagnostics();
      }
    });
  },

  async "clear-log"() {
    // Clears this page, never the service's ring: two consoles may be watching
    // and neither owns the history.
    S.logRecords = [];
    S.logDropped = false;
    renderDiagnostics();
  },

  async "backup"(el) {
    await withBusy(el, async () => {
      const result = await run(
        { command: "run_backup", setName: el.dataset.set, full: false },
        { errToast: "Backup refused" });
      if (result?.result === "job_accepted") {
        toast("ok", `Backup of '${el.dataset.set}' queued as job ${result.jobId}.`);
        refreshJobs();
        location.hash = "#jobs";
      }
    });
  },

  async "sync"(el) {
    await withBusy(el, async () => {
      const result = await run(
        { command: "sync", backupSetName: el.dataset.set ?? null, destinationName: null },
        { errToast: "Sync refused" });
      if (result?.result === "sync") reportDialog("Sync", result.lines, "One line per (set, destination) pair, from the refreshed ledger.");
      refreshStatus();
    });
  },

  async "sync-explicit"(el) {
    const set = document.getElementById("sync-set").value || null;
    const dest = document.getElementById("sync-dest").value || null;
    await withBusy(el, async () => {
      const result = await run({ command: "sync", backupSetName: set, destinationName: dest }, { errToast: "Sync refused" });
      if (result?.result === "sync") reportDialog("Sync", result.lines, "One line per (set, destination) pair, from the refreshed ledger.");
      refreshStatus();
    });
  },

  async "cancel-job"(el) {
    await withBusy(el, async () => {
      const result = await run({ command: "cancel_job", jobId: el.dataset.job }, { errToast: "Cancel refused" });
      if (result) toast("ok", "Cancellation commanded — the job will record Cancelled.");
      refreshJobs();
    });
  },

  async "verify"(el) {
    const level = document.getElementById("verify-level").value;
    await withBusy(el, async () => {
      const result = await run({ command: "verify", level }, { errToast: "Verify refused" });
      if (result?.result === "verification") {
        const clean = Number(result.failures) === 0;
        // Sealed records on a write-only set are a stated incapacity, not
        // damage and not a clean content check (ADR-0042).
        const sealed = Number(result.sealedRecords ?? 0);
        toast(clean ? "ok" : "bad",
          `Verified ${fmtCount(result.objectsChecked)} blob(s) at ${result.level} — ${fmtCount(result.failures)} failure(s).`
          + (sealed ? ` ${fmtCount(sealed)} sealed record(s) need a restore grant for content checks.` : ""));
      }
    });
  },

  async "check"(el) {
    const level = document.getElementById("verify-level").value;
    await withBusy(el, async () => {
      const result = await run({ command: "check", level }, { errToast: "Check refused" });
      if (result?.result === "check") {
        if (result.findings.length === 0) toast("ok", "Check: OK — no findings.");
        else reportDialog("Health check findings", result.findings);
      }
    });
  },

  async "verify-destination"(el) {
    const set = document.getElementById("vd-set").value || null;
    const dest = document.getElementById("vd-dest").value || null;
    const depth = document.querySelector("input[name=vd-depth]:checked").value;
    await withBusy(el, async () => {
      const result = await run(
        { command: "verify_destination", backupSetName: set, destinationName: dest, full: depth === "full", probe: depth === "probe" },
        { errToast: "Destination verification refused" });
      if (result?.result === "verify_destination") {
        reportDialog(
          Number(result.damaged) === 0 ? "Destination verification — clean" : `Destination verification — ${fmtCount(result.damaged)} damaged`,
          result.lines);
      }
    });
  },

  async "retention-dry"(el) {
    await withBusy(el, async () => {
      const result = await run({ command: "retention", apply: false }, { errToast: "Retention refused" });
      if (result?.result === "retention") reportDialog("Retention — dry run", result.lines, "Nothing was deleted. Apply is a separate, confirmed act.");
    });
  },

  "retention-apply"() {
    openDialog(`
      <h3>Apply retention</h3>
      <p class="dlg-sub">This tombstones every snapshot the policy condemns and deletes what an earlier
      pass condemned and the grace period has released. The dry-run report is shown after the pass.
      Destinations converge under their own policies.</p>
      <label class="field" for="confirm-word">Type <b>apply</b> to confirm</label>
      <input type="text" id="confirm-word" class="confirm-word" autocomplete="off" spellcheck="false"
             data-action-input="confirm-word" data-word="apply" data-enables="retention-apply-go">
      <div class="dlg-actions">
        <button type="button" class="btn" data-action="close-dialog">Cancel</button>
        <button type="button" class="btn danger" id="retention-apply-go" data-action="retention-apply-go" disabled>Apply retention</button>
      </div>`);
    document.getElementById("confirm-word").focus();
  },

  async "retention-apply-go"(el) {
    await withBusy(el, async () => {
      const result = await run({ command: "retention", apply: true }, { errToast: "Retention refused" });
      if (result?.result === "retention") {
        reportDialog("Retention — applied", result.lines);
        refreshStatus(); refreshSnapshots();
      } else closeDialog();
    });
  },

  async "show-config"() {
    const result = await run({ command: "export_configuration" }, { errToast: "Export refused" });
    if (result?.result === "configuration") {
      let pretty = result.json;
      try { pretty = JSON.stringify(JSON.parse(result.json), null, 2); } catch { /* shown raw */ }
      reportDialog("Configuration", [pretty],
        "Exported without secrets — though with destinations in it, it names who stores your backups and where.");
    }
  },

  async "browse"(el) { await openBrowser(el.dataset.snapshot, ""); },

  async "browse-to"(el) { await openBrowser(el.dataset.snapshot, el.dataset.path); },

  "restore"(el) {
    openRestoreWizard({ snapshotId: el.dataset.snapshot, path: el.dataset.path ?? "" });
  },

  async "what-changed"(el) {
    await withBusy(el, async () => {
      const result = await run(
        { command: "preview_set_changes", setName: el.dataset.set },
        { errToast: "The service could not compare" });
      if (result?.result !== "set_change_preview") return;
      const report = comparisonReport(result);
      reportDialog(`What changed under '${el.dataset.set}'`,
        [report.summary, "", ...(report.detail ? report.detail.split("\n") : [])],
        "Compared with the last backup — a dry scan; nothing was captured.");
    });
  },

  "backup-full"(el) {
    const name = el.dataset.set;
    openDialog(`
      <h3>Full backup of '${esc(name)}'</h3>
      <p class="dlg-sub">Re-reads and re-captures every byte, ignoring prior versions — hours where an
      incremental takes minutes. Use it after suspected source-side corruption or a disk swap; an ordinary
      backup already catches every change.</p>
      <div class="dlg-actions">
        <button type="button" class="btn" data-action="close-dialog">Cancel</button>
        <button type="button" class="btn primary" data-action="backup-full-go" data-set="${esc(name)}">Run the full backup</button>
      </div>`);
  },

  async "backup-full-go"(el) {
    await withBusy(el, async () => {
      const result = await run(
        { command: "run_backup", setName: el.dataset.set, full: true },
        { errToast: "Backup refused" });
      if (result?.result === "job_accepted") {
        closeDialog();
        toast("ok", `Full backup of '${el.dataset.set}' queued as job ${result.jobId}.`);
        refreshJobs();
        location.hash = "#jobs";
      }
    });
  },

  "unpair-open"(el) {
    const fingerprint = el.dataset.fingerprint;
    const label = el.dataset.label;
    const known = S.destinations.find(destination =>
      destination.kind === "peer" && destination.fingerprint === fingerprint)?.endpoint ?? "";
    openDialog(`
      <h3>End the pairing with '${esc(label)}'</h3>
      <p class="dlg-sub">The peer is told while the pairing still authenticates the session, then the grant is
      revoked here with a tombstone — its next dial reads 'revoked', never 'never paired'. Nothing is deleted
      anywhere: what it stores for you, and what you store for it, stays put. A destination that references
      this peer must be deleted first — the service refuses otherwise.</p>
      <label class="field" for="unpair-endpoint">Peer address for the announcement <span class="plain">— host:port, optional</span></label>
      <input type="text" id="unpair-endpoint" class="mono" value="${esc(known)}" spellcheck="false" placeholder="192.168.1.20:9420">
      <label class="check-row"><input type="checkbox" id="unpair-notify" checked> Tell the peer now — otherwise it learns at its next dial</label>
      <label class="field" for="confirm-word">Type <b>${esc(label)}</b> to confirm</label>
      <input type="text" id="confirm-word" class="confirm-word" autocomplete="off" spellcheck="false"
             data-action-input="confirm-word" data-word="${esc(label.toLowerCase())}" data-enables="unpair-go">
      <div class="dlg-actions">
        <button type="button" class="btn" data-action="close-dialog">Cancel</button>
        <button type="button" class="btn danger" id="unpair-go" data-action="unpair-go" data-fingerprint="${esc(fingerprint)}" disabled>End the pairing</button>
      </div>`);
    document.getElementById("confirm-word").focus();
  },

  async "unpair-go"(el) {
    const endpoint = document.getElementById("unpair-endpoint").value.trim();
    const notify = document.getElementById("unpair-notify").checked;
    await withBusy(el, async () => {
      const result = await run({
        command: "unpair",
        fingerprint: el.dataset.fingerprint,
        notify,
        endpoint: endpoint || null,
      }, { errToast: "The service refused to unpair" });
      if (result?.result === "configuration_change") {
        closeDialog();
        reportDialog("Pairing ended", result.lines);
        refreshConfigData(); refreshStatus();
      }
    });
  },

  async "notice-ack"(el) {
    await withBusy(el, async () => {
      const result = await run(
        { command: "acknowledge_notice", id: el.dataset.id },
        { errToast: "Could not acknowledge" });
      if (result) { await refreshNotices(); refreshStatus(); }
    });
  },

  async "notice-ack-all"(el) {
    await withBusy(el, async () => {
      for (const notice of (S.notices ?? []).filter(current => !current.acknowledgedAt)) {
        await run({ command: "acknowledge_notice", id: notice.id }, { errToast: "Could not acknowledge" });
      }
      await refreshNotices(); refreshStatus();
    });
  },

  async "notice-history"(el) {
    S.noticesHistory = !S.noticesHistory;
    await withBusy(el, refreshNotices);
  },

  "close-dialog"() { closeDialog(); },
};

/* ---------------------------------------------------------------- configuration */

// The editor's draft. Rules are the source of truth; the tree and the chips
// are two renderings of them, and the raw editor is the escape hatch for
// anything the tree cannot say.
let E = null;

async function refreshConfigData() {
  const [destinations, pairings, invites] = await Promise.all([
    run({ command: "list_destinations" }),
    run({ command: "list_pairings" }),
    run({ command: "list_pairing_invites" }),
  ]);
  if (destinations?.result === "destinations") S.destinations = destinations.destinations;
  if (pairings?.result === "pairings") S.pairings = pairings.pairings;
  if (invites?.result === "pairing_invites") S.invites = invites.invites;
  await refreshSets();
  if (S.view === "config") renderConfigBody();
}

function renderConfig() {
  const el = document.getElementById("view-config");
  el.innerHTML = `
    <h2>Configuration</h2>
    <p class="view-sub">Sets, destinations and pairings — edited through the service, validated before anything is saved.
       Removals never delete data, and they say so.</p>
    <div id="config-body"><div class="card empty"><span class="big">⏳</span>Loading configuration…</div></div>`;
  renderConfigBody();
}

function retentionSummary(policy) {
  if (!policy) return "keeps everything";
  const parts = [];
  if (policy.keepDaily) parts.push(`${policy.keepDaily}d`);
  if (policy.keepWeekly) parts.push(`${policy.keepWeekly}w`);
  if (policy.keepMonthly) parts.push(`${policy.keepMonthly}m`);
  if (policy.minGenerations) parts.push(`≥${policy.minGenerations}`);
  return parts.length ? "retention " + parts.join("/") : "keeps everything";
}

function renderConfigBody() {
  const host = document.getElementById("config-body");
  if (!host) return;

  const sets = S.sets.map(set => `
    <div class="card">
      <h3>${esc(set.name)} <span class="chip">${retentionSummary(set.retention)}</span></h3>
      <p class="sub mono">${rootsOf(set).map(esc).join("<br>")}</p>
      <p class="sub">${set.schedule ? "runs " + esc(set.schedule) : "manual only"}
         ${set.includeRules.length ? `· ${set.includeRules.length} include rule(s)` : ""}
         ${set.excludeRules.length ? `· ${set.excludeRules.length} exclude rule(s)` : ""}</p>
      <div>${set.destinations.map(name => `<span class="chip">→ ${esc(name)}</span>`).join(" ")}</div>
      <div class="actions-row">
        <button type="button" class="btn small" data-action="cfg-edit-set" data-name="${esc(set.name)}">Edit</button>
        <button type="button" class="btn small" data-action="cfg-write-only" data-name="${esc(set.name)}">Write-only…</button>
        <button type="button" class="btn small" data-action="cfg-delete-set" data-name="${esc(set.name)}">Delete…</button>
      </div>
    </div>`).join("");

  const destinations = S.destinations.map(destination => `
    <tr>
      <td><b>${esc(destination.name)}</b>
          ${destination.addressDefect ? `<div class="detail">${badge({ cls: "warn", icon: "⚠" }, "address defect")} ${esc(destination.addressDefect)}</div>` : ""}</td>
      <td>${esc(destination.kind)}</td>
      <td class="mono detail">${esc(destination.path ?? (destination.endpoint ? destination.endpoint + " · " + (destination.fingerprint ?? "").slice(0, 10) + "…" : "—"))}</td>
      <td class="detail">${esc(destination.failureDomain ?? "derived")}</td>
      <td>
        <button type="button" class="btn small" data-action="cfg-edit-dest" data-id="${esc(destination.id)}">Edit</button>
        <button type="button" class="btn small" data-action="cfg-delete-dest" data-name="${esc(destination.name)}">Delete…</button>
      </td>
    </tr>`).join("");

  const pairings = S.pairings.map(pairing => `
    <tr>
      <td class="mono" title="${esc(pairing.fingerprint)}">${esc(pairing.fingerprint.slice(0, 12))}…</td>
      <td>${esc(pairing.label)}</td>
      <td class="detail">${esc(pairing.role)}</td>
      <td class="detail">${esc(rel(pairing.pairedAt))}</td>
      <td><button type="button" class="btn small" data-action="unpair-open"
            data-fingerprint="${esc(pairing.fingerprint)}" data-label="${esc(pairing.label)}">Unpair…</button></td>
    </tr>`).join("");

  const invites = S.invites.map(invite => `
    <tr>
      <td class="mono">${esc(invite.inviteId)}</td>
      <td>${esc(invite.label)}</td>
      <td class="detail">${esc(invite.role)}</td>
      <td class="detail">${invite.consumedBy
        ? `redeemed by <span class="mono">${esc(invite.consumedBy.slice(0, 10))}…</span>`
        : Number(invite.expiresAt) < Date.now() ? "expired" : "expires " + esc(fmtWhen(invite.expiresAt))}</td>
      <td>${invite.consumedBy ? "" : `<button type="button" class="btn small" data-action="invite-revoke" data-id="${esc(invite.inviteId)}">Revoke</button>`}</td>
    </tr>`).join("");

  host.innerHTML = `
    <div class="cfg-section">
      <div class="cfg-head"><h3>Backup sets</h3>
        <button type="button" class="btn primary small" data-action="cfg-add-set" ${S.destinations.length ? "" : "disabled title='Declare a destination first (FR-DEST-001)'"}>＋ Add backup set</button>
      </div>
      ${S.sets.length ? `<div class="grid cols-2">${sets}</div>`
        : `<div class="card empty"><span class="big">🗂</span>No backup sets yet.${S.destinations.length ? "" : "<br>Declare a destination below first — every set needs at least one."}</div>`}
    </div>

    <div class="cfg-section">
      <div class="cfg-head"><h3>Destinations</h3>
        <span>
          <button type="button" class="btn small" data-action="dest-add-local">＋ Local folder</button>
          <button type="button" class="btn small" data-action="dest-add-peer" ${S.pairings.length ? "" : "disabled title='Pair with a peer first'"}>＋ Peer destination</button>
          <button type="button" class="btn small" data-action="invite-open">✉ Invite a peer</button>
          <button type="button" class="btn small" data-action="pair-open">🔗 Pair with a remote service</button>
        </span>
      </div>
      ${S.destinations.length ? `<div class="card"><div class="table-wrap"><table class="data">
          <thead><tr><th>Name</th><th>Kind</th><th>Address</th><th>Failure domain</th><th></th></tr></thead>
          <tbody>${destinations}</tbody></table></div></div>`
        : `<div class="card empty"><span class="big">📦</span>No destinations declared. A local folder is the quickest start; a paired peer survives losing this machine.</div>`}
    </div>

    ${S.pairings.length ? `<div class="cfg-section">
      <div class="cfg-head"><h3>Pairings</h3></div>
      <div class="card"><div class="table-wrap"><table class="data">
        <thead><tr><th>Fingerprint</th><th>Label</th><th>Role</th><th>Paired</th><th></th></tr></thead>
        <tbody>${pairings}</tbody></table></div></div>
    </div>` : ""}

    ${S.invites.length ? `<div class="cfg-section">
      <div class="cfg-head"><h3>Pairing invites</h3></div>
      <div class="card"><div class="table-wrap"><table class="data">
        <thead><tr><th>Id</th><th>For</th><th>Role</th><th>State</th><th></th></tr></thead>
        <tbody>${invites}</tbody></table></div></div>
    </div>` : ""}`;
}

/* ----- the set editor ----- */

function newDraft(set) {
  // The tree's selection is a set of marks: full path -> "on"|"off", where
  // an unmarked node inherits its nearest marked ancestor (default off).
  // Saved roots become "on" marks; literal anchored excludes are consumed
  // back into "off" marks; every other rule stays a hand-edited chip.
  const roots = set?.roots?.length ? set.roots.map(r => ({ path: r.path, label: r.label ?? null }))
    : set?.root ? [{ path: set.root, label: null }] : [];
  const marks = new Map(roots.map(root => [root.path, "on"]));
  const handIncludes = [...(set?.includeRules ?? [])];
  const handExcludes = [];
  for (const rule of set?.excludeRules ?? []) {
    const consumed = consumeExcludeAsMark(rule, roots, marks);
    if (!consumed) handExcludes.push(rule);
  }

  return {
    id: set?.id ?? Array.from(crypto.getRandomValues(new Uint8Array(16)))
      .map(byte => byte.toString(16).padStart(2, "0")).join(""),
    isNew: !set,
    name: set?.name ?? "",
    roots,                    // saved roots, for label continuity on save
    marks,
    handIncludes,
    handExcludes,
    schedule: set?.schedule ?? null,
    destinations: new Set(set?.destinations ?? []),
    retention: set?.retention ?? null,
    overrides: { ...(set?.destinationRetention ?? {}) },
    tree: new Map(),          // full path -> {open, listing}; "" is the machine's drive list
    lastPreviewKey: null,     // what the live preview last compared, to skip no-op walks
  };
}

function openSetEditor(set) {
  E = newDraft(set);
  dialog.classList.add("wide");

  const scheduleMode = !E.schedule ? "manual"
    : /^every \d+[mhd]$/.test(E.schedule) ? "every"
    : /^daily at /.test(E.schedule) ? "daily" : "custom";
  const every = /^every (\d+)([mhd])$/.exec(E.schedule ?? "") ?? [null, "12", "h"];
  const daily = /^daily at (.+)$/.exec(E.schedule ?? "")?.[1] ?? "02:30";

  const policy = E.retention ?? {};
  openDialog(`
    <div id="set-editor">
    <h3>${E.isNew ? "New backup set" : "Edit backup set"}</h3>

    <div class="editor-section">
      <label class="field" for="set-name">Name</label>
      <input type="text" id="set-name" value="${esc(E.name)}" spellcheck="false" placeholder="documents">
    </div>

    <div class="editor-section">
      <label class="field">Schedule</label>
      <div class="radio-row">
        <label><input type="radio" name="sched-mode" value="manual" ${scheduleMode === "manual" ? "checked" : ""}> manual only</label>
        <label><input type="radio" name="sched-mode" value="every" ${scheduleMode === "every" ? "checked" : ""}> every
          <input type="text" id="sched-n" class="num" value="${esc(every[1])}">
          <select id="sched-unit">
            <option value="m" ${every[2] === "m" ? "selected" : ""}>minutes</option>
            <option value="h" ${every[2] === "h" ? "selected" : ""}>hours</option>
            <option value="d" ${every[2] === "d" ? "selected" : ""}>days</option>
          </select></label>
        <label><input type="radio" name="sched-mode" value="daily" ${scheduleMode === "daily" ? "checked" : ""}> daily at
          <input type="time" id="sched-time" value="${esc(daily)}"></label>
      </div>
      <div id="sched-preview" class="subtle"></div>
    </div>

    <div class="editor-section">
      <label class="field">What to capture <span class="plain">— on the service's machine</span></label>
      <p class="subtle">Tick any folders — several, on any drive, in one set. Untick a child to leave it out;
      everything ticked is captured, including what appears there later. Pattern rules refine further.</p>
      <div id="sel-tree" class="tree"></div>
      <div id="sel-summary" class="subtle"></div>
      <div class="field-row">
        <input type="text" id="rule-new" class="mono" spellcheck="false" placeholder="pattern rule, e.g.  *.iso   or   node_modules">
        <select id="rule-list"><option value="exclude">exclude</option><option value="include">include</option></select>
        <button type="button" class="btn small" data-action="rule-add-raw">Add rule</button>
      </div>
      <div id="rule-chips"></div>
      <div id="draft-defects"></div>
      <div class="field-row">
        <button type="button" class="btn small" data-action="preview-changes">Preview what a backup would capture</button>
      </div>
      <div id="change-preview"></div>
    </div>

    <div class="editor-section">
      <label class="field">Retention</label>
      <p class="subtle">Absent fields are "no rule"; with no rules at all, every snapshot is kept. Retention selects —
      deleting is a separate, confirmed act on the Maintenance page.</p>
      <div class="field-row wrap">
        <label class="mini">keep daily <input type="text" id="ret-daily" class="num" value="${policy.keepDaily ?? ""}"></label>
        <label class="mini">weekly <input type="text" id="ret-weekly" class="num" value="${policy.keepWeekly ?? ""}"></label>
        <label class="mini">monthly <input type="text" id="ret-monthly" class="num" value="${policy.keepMonthly ?? ""}"></label>
        <label class="mini">min generations <input type="text" id="ret-min" class="num" value="${policy.minGenerations ?? ""}"></label>
        <label class="mini">deferral days <input type="text" id="ret-defer" class="num" value="${policy.deferralDays ?? ""}"></label>
      </div>
    </div>

    <div class="editor-section">
      <label class="field">Destinations</label>
      <p class="subtle">Every set needs at least one. An override replaces the whole set policy for that
      destination — unset fields do not fall back.</p>
      <div id="dest-list">${S.destinations.map(destination => renderDestChoice(destination)).join("")}</div>
    </div>

    <div class="dlg-actions">
      <button type="button" class="btn" data-action="close-dialog">Cancel</button>
      <button type="button" class="btn primary" data-action="set-save">${E.isNew ? "Create set" : "Save changes"}</button>
    </div>
    </div>`);

  renderTree();
  renderRuleChips();
  renderSelectionSummary();
  validateDraftSoon();
  expandToMarks();
}

function rerenderDestChoices() {
  const host = document.getElementById("dest-list");
  if (host && E) host.innerHTML = S.destinations.map(destination => renderDestChoice(destination)).join("");
}

function renderDestChoice(destination) {
  const checked = E.destinations.has(destination.name);
  const override = E.overrides[destination.name];
  return `
    <div class="dest-choice">
      <label><input type="checkbox" data-dest-check="${esc(destination.name)}" ${checked ? "checked" : ""}>
        <b>${esc(destination.name)}</b> <span class="detail">${esc(destination.kind)}</span></label>
      ${checked ? `<label class="mini"><input type="checkbox" data-ovr-check="${esc(destination.name)}" ${override ? "checked" : ""}> override retention</label>` : ""}
      ${checked && override ? `
        <div class="field-row wrap ovr-fields" data-ovr-for="${esc(destination.name)}">
          <label class="mini">daily <input type="text" class="num" data-ovr="${esc(destination.name)}:keepDaily" value="${override.keepDaily ?? ""}"></label>
          <label class="mini">weekly <input type="text" class="num" data-ovr="${esc(destination.name)}:keepWeekly" value="${override.keepWeekly ?? ""}"></label>
          <label class="mini">monthly <input type="text" class="num" data-ovr="${esc(destination.name)}:keepMonthly" value="${override.keepMonthly ?? ""}"></label>
          <label class="mini">min gen <input type="text" class="num" data-ovr="${esc(destination.name)}:minGenerations" value="${override.minGenerations ?? ""}"></label>
        </div>` : ""}
    </div>`;
}

/* ----- selection tree (ADR-0040) ----- */
//
// The whole machine is the tree; checkboxes are the selection. A mark on a
// node overrides everything it inherits; unmarked nodes follow their nearest
// marked ancestor and default to off. The compiler below turns marks into
// what the contract speaks — roots plus exclude rules, never includes: the
// deepest fully-checked folder IS the root, so one root's selection can
// never force blanket includes on a sibling root.

function isUnder(child, parent) {
  if (child === parent) return false;
  return parent.endsWith("/") || parent.endsWith("\\")
    ? child.startsWith(parent)
    : child.startsWith(parent + "/") || child.startsWith(parent + "\\");
}

function relOf(child, parent) {
  const skip = parent.endsWith("/") || parent.endsWith("\\") ? parent.length : parent.length + 1;
  return child.slice(skip).replaceAll("\\", "/");
}

function joinPath(parent, rel) {
  const sep = parent.includes("\\") || /^[A-Za-z]:$/.test(parent) ? "\\" : "/";
  const base = parent.endsWith("/") || parent.endsWith("\\") ? parent : parent + sep;
  return base + rel.replaceAll("/", sep);
}

function ruleForPath(rel) {
  // A name carrying glob metacharacters can only be said exactly with the
  // regex form (rules-v1 has no glob escape).
  return /[*?]/.test(rel)
    ? "re:" + rel.replace(/[.^$|()[\]{}*+?\\]/g, match => "\\" + match)
    : rel;
}

// The service materialises labels the same way at upsert (leaf name,
// fallback "root", numeric-suffix dedupe); mirrored here only so compiled
// exclude rules can speak the label-prefixed coordinates a multi-root set
// saves under. Explicit labels (from an already-saved set) win untouched.
function labelledRoots(paths) {
  const savedLabels = new Map((E?.roots ?? []).map(root => [root.path, root.label]));
  if (paths.length <= 1) return paths.map(path => ({ path, label: savedLabels.get(path) ?? null }));
  const taken = new Set();
  const explicit = paths.filter(path => savedLabels.get(path));
  for (const path of explicit) taken.add(savedLabels.get(path).toLowerCase());
  return paths.map(path => {
    const saved = savedLabels.get(path);
    if (saved) return { path, label: saved };
    const leaf = path.replace(/[/\\]+$/, "").split(/[/\\]/).filter(Boolean).pop() ?? "";
    let base = [...leaf].filter(ch => !"/\\:*?".includes(ch)).join("").trim();
    if (!base || base === "." || base === "..") base = "root";
    base = base.normalize("NFC");
    let candidate = base;
    for (let suffix = 2; taken.has(candidate.toLowerCase()); suffix++) candidate = `${base}-${suffix}`;
    taken.add(candidate.toLowerCase());
    return { path, label: candidate };
  });
}

// A saved exclude that is a plain anchored path (no glob, no regex, has a
// separator) is the tree's own dialect — consume it back into an "off" mark
// so the checkboxes show it. Everything else keeps its exact meaning as a
// hand chip. Multi-root rules shed their label prefix to find their root.
function consumeExcludeAsMark(rule, roots, marks) {
  if (rule.startsWith("re:") || /[*?]/.test(rule) || !rule.includes("/")) return false;
  if (roots.length > 1) {
    const root = roots.find(candidate => candidate.label && rule.startsWith(candidate.label + "/"));
    if (!root) return false;
    marks.set(joinPath(root.path, rule.slice(root.label.length + 1)), "off");
    return true;
  }
  if (roots.length === 1) {
    marks.set(joinPath(roots[0].path, rule), "off");
    return true;
  }
  return false;
}

function effectiveState(path) {
  let best = null;
  for (const [key, state] of E.marks) {
    if (key === path || isUnder(path, key)) {
      if (!best || key.length > best.key.length) best = { key, state };
    }
  }
  return best?.state ?? "off";
}

// marks + loaded listings -> { roots, excludeRules, warnings }. Pure of the
// DOM, so the summary, the live preview and the save all agree by
// construction. The exclude-wins wall: a re-checked node under an unchecked
// parent cannot compile as exclude-parent + include-child (rules-v1 has no
// negation), so the parent's OTHER children become the excludes — and new
// arrivals under that parent will be captured, which the warning says.
function compileMarks() {
  const marks = [...E.marks.entries()].map(([path, state]) => ({ path, state }));
  const on = marks.filter(mark => mark.state === "on");
  const warnings = [];

  const rootPaths = on
    .filter(mark => !on.some(other => other.path !== mark.path && isUnder(mark.path, other.path)))
    .map(mark => mark.path)
    .sort();
  const roots = labelledRoots(rootPaths);

  const subjectFor = (root, rel) =>
    ruleForPath(roots.length > 1 ? root.label + "/" + rel : rel);

  const nearestMarked = path => {
    let best = null;
    for (const mark of marks) {
      if (mark.path !== path && isUnder(path, mark.path)) {
        if (!best || mark.path.length > best.path.length) best = mark;
      }
    }
    return best;
  };

  const excludeRules = [];
  for (const mark of marks) {
    if (mark.state !== "off") continue;
    const near = nearestMarked(mark.path);
    if (near?.state !== "on") continue; // off outside any selection needs no rule
    const root = roots.find(candidate => isUnder(mark.path, candidate.path));
    if (!root) continue;

    const reChecked = on.filter(other =>
      isUnder(other.path, mark.path)
      && nearestMarked(other.path)?.path === mark.path);
    if (reChecked.length === 0) {
      excludeRules.push(subjectFor(root, relOf(mark.path, root.path)));
      continue;
    }

    // The wall: enumerate the unchecked parent's other children as
    // excludes, recursing along the paths that lead to what was re-checked.
    let complete = true;
    const walk = dir => {
      const listing = E.tree.get(dir)?.listing;
      if (!listing) { complete = false; return; }
      const children = [
        ...listing.folders.map(folder => folder.path),
        ...(listing.files ?? []).map(file => joinPath(dir, file.name)),
      ];
      for (const child of children) {
        if (reChecked.some(keep => keep.path === child)) continue;
        if (reChecked.some(keep => isUnder(keep.path, child))) { walk(child); continue; }
        excludeRules.push(subjectFor(root, relOf(child, root.path)));
      }
    };
    walk(mark.path);
    warnings.push(`'${relOf(mark.path, root.path)}' is unticked but something inside it is kept, so its `
      + `current contents are excluded by name — anything NEW appearing there will be captured.`
      + (complete ? "" : " (Some of it is not listed yet — expand it so every sibling is seen.)"));
  }

  return { roots, excludeRules, warnings };
}

async function loadTreeDir(fullPath) {
  if (E.tree.has(fullPath)) return E.tree.get(fullPath);
  const listing = await run(
    { command: "browse_folders", path: fullPath || null, includeFiles: true }, { errToast: "Listing refused" });
  const node = { open: true, listing: listing?.result === "folder_listing" ? listing : null };
  E.tree.set(fullPath, node);
  return node;
}

// Best-effort: open the tree down to every mark, so a reopened editor shows
// its selection instead of a collapsed machine.
async function expandToMarks() {
  if (!E) return;
  const chains = new Set([""]);
  for (const [path, state] of E.marks) {
    const sep = path.includes("\\") ? "\\" : "/";
    const parts = path.split(sep).filter(Boolean);
    let current = sep === "/" ? "/" : parts.shift() + "\\";
    chains.add(current);
    const stop = state === "on" ? parts.length : parts.length - 1; // an off mark may be a file
    for (let index = 0; index < stop; index++) {
      current = joinPath(current, parts[index]);
      chains.add(current);
    }
  }
  for (const path of chains) {
    if (!E) return; // the dialog closed mid-expansion
    const node = await loadTreeDir(path);
    node.open = true;
  }
  renderTree();
  renderSelectionSummary();
}

function renderTree() {
  const host = document.getElementById("sel-tree");
  if (!host || !E) return;
  host.innerHTML = `<div class="tree-inner">${renderTreeDir("", 0)}</div>`;

  // Tri-state is a property, not an attribute — painted after the fact.
  for (const box of host.querySelectorAll("input[data-mark-path]")) {
    const path = box.dataset.markPath;
    const state = effectiveState(path);
    box.indeterminate = [...E.marks].some(([key, mark]) => isUnder(key, path) && mark !== state);
  }
}

function renderTreeDir(fullPath, depth) {
  const node = E.tree.get(fullPath);
  if (!node) {
    loadTreeDir(fullPath).then(() => { renderTree(); renderSelectionSummary(); });
    return `<p class="subtle">Loading…</p>`;
  }

  if (!node.listing) return `<p class="subtle">${"<span class='indent'></span>".repeat(depth)}This folder would not list.</p>`;

  const rows = [];
  for (const folder of node.listing.folders) {
    const state = effectiveState(folder.path);
    const excludedHere = E.marks.get(folder.path) === "off" && state === "off";
    const child = E.tree.get(folder.path);

    rows.push(`
      <div class="tree-row ${state === "off" ? "off" : ""}">
        ${"<span class='indent'></span>".repeat(depth)}
        <button type="button" class="twist" data-action="sel-open" data-path="${esc(folder.path)}">${child?.open ? "▾" : "▸"}</button>
        <input type="checkbox" class="mark" data-mark-path="${esc(folder.path)}" ${state === "on" ? "checked" : ""}
               aria-label="Capture ${esc(folder.name)}">
        <span class="kind-ico">📁</span>
        <span class="tree-name">${esc(folder.name)}</span>
        ${excludedHere && nearestOnAbove(folder.path) ? `<span class="badge warn">excluded</span>` : ""}
      </div>
      ${child?.open ? renderTreeDir(folder.path, depth + 1) : ""}`);
  }

  for (const file of node.listing.files ?? []) {
    const path = joinPath(node.listing.path, file.name);
    const state = effectiveState(path);
    rows.push(`
      <div class="tree-row ${state === "off" ? "off" : ""}">
        ${"<span class='indent'></span>".repeat(depth)}
        <span class="twist"></span>
        <input type="checkbox" class="mark" data-mark-path="${esc(path)}" ${state === "on" ? "checked" : ""}
               aria-label="Capture ${esc(file.name)}">
        <span class="kind-ico">📄</span>
        <span class="tree-name">${esc(file.name)}</span>
        <span class="detail">${fmtBytes(file.length)}</span>
        ${E.marks.get(path) === "off" && nearestOnAbove(path) ? `<span class="badge warn">excluded</span>` : ""}
      </div>`);
  }

  return rows.join("") || `<p class="subtle">${"<span class='indent'></span>".repeat(depth)}(empty)</p>`;
}

function nearestOnAbove(path) {
  return [...E.marks].some(([key, state]) => state === "on" && isUnder(path, key));
}

function toggleMark(path) {
  const target = effectiveState(path) === "on" ? "off" : "on";
  // A fresh decision about a subtree supersedes every finer one inside it.
  for (const key of [...E.marks.keys()]) {
    if (isUnder(key, path)) E.marks.delete(key);
  }
  E.marks.delete(path);
  if (effectiveState(path) !== target) E.marks.set(path, target);
  renderTree();
  renderSelectionSummary();
  validateDraftSoon();
  scheduleLivePreview();
}

function renderSelectionSummary() {
  const host = document.getElementById("sel-summary");
  if (!host || !E) return;
  const { roots, excludeRules, warnings } = compileMarks();
  if (roots.length === 0) {
    host.innerHTML = `Nothing selected yet — tick the folders to capture.`;
    return;
  }
  const named = roots.map(root => root.label ?? root.path.replace(/[/\\]+$/, "").split(/[/\\]/).pop() ?? root.path);
  const parts = [
    `<b>${roots.length}</b> folder${roots.length === 1 ? "" : "s"} selected (${named.map(esc).join(", ")})`,
  ];
  if (excludeRules.length) parts.push(`<b>${excludeRules.length}</b> exclusion${excludeRules.length === 1 ? "" : "s"} from the tree`);
  if (E.handExcludes.length || E.handIncludes.length) {
    parts.push(`${E.handExcludes.length + E.handIncludes.length} pattern rule(s)`);
  }
  host.innerHTML = parts.join(" · ")
    + (warnings.length ? `<ul class="warnings">${warnings.map(text => `<li>${esc(text)}</li>`).join("")}</ul>` : "");
}

// The authoritative feedback: a debounced dry walk over the service,
// classifying the draft selection against the set's last backup — or, for a
// brand-new set, against nothing (contract 1.10's draft mode). Skipped when
// the compiled output has not changed since the last walk.
let livePreviewTimer = null;

// One in-flight source walk at a time. A preview superseded by a newer edit,
// a save, or the dialog closing is aborted — and because the console opens a
// service connection per relayed request, the abort reaches the service as a
// hang-up that cancels the walk itself, not just this page's fetch. Without
// it, every settled edit of a large set stacked another multi-minute scan
// behind the reader lane (583916 ms and friends in the 2026-08-25 log).
let sourceScan = null;
function beginSourceScan() {
  sourceScan?.abort();
  sourceScan = new AbortController();
  return sourceScan.signal;
}

function endSourceScans() {
  sourceScan?.abort();
  sourceScan = null;
}

function scheduleLivePreview() {
  clearTimeout(livePreviewTimer);
  livePreviewTimer = setTimeout(async () => {
    if (!E) return;
    const { roots, excludeRules } = compileMarks();
    const host = document.getElementById("change-preview");
    if (!host || roots.length === 0) return;

    const includeRules = [...E.handIncludes];
    const allExcludes = [...excludeRules, ...E.handExcludes];
    const key = JSON.stringify([roots, includeRules, allExcludes]);
    if (key === E.lastPreviewKey) return;

    const result = await run({
      command: "preview_set_changes",
      setName: E.isNew ? (document.getElementById("set-name")?.value.trim() || "(draft)") : E.name,
      roots: roots.map(root => ({ path: root.path, label: root.label })),
      includeRules,
      excludeRules: allExcludes,
    }, { signal: beginSourceScan() });
    if (!E || !document.getElementById("change-preview")) return;
    if (result?.result !== "set_change_preview") return;
    E.lastPreviewKey = key;
    const report = comparisonReport(result);
    document.getElementById("change-preview").innerHTML =
      `<p class="subtle">${esc(report.summary)}</p>`
      + (report.detail ? `<pre class="report">${esc(report.detail)}</pre>` : "");
  }, 1200);
}

function renderRuleChips() {
  const host = document.getElementById("rule-chips");
  if (!host || !E) return;
  const chip = (rule, list) => `
    <span class="chip removable ${list === "exclude" ? "warn" : "accent"}">
      ${list === "exclude" ? "− " : "＋ "}${esc(rule)}
      <button type="button" class="chip-x" data-action="rule-remove" data-rule="${esc(rule)}" data-list="${list}" aria-label="Remove rule">×</button>
    </span>`;
  host.innerHTML =
    E.handIncludes.map(rule => chip(rule, "include")).join("")
    + E.handExcludes.map(rule => chip(rule, "exclude")).join("")
    || `<span class="subtle">No pattern rules — the checkboxes alone say what is captured.</span>`;
}

let draftTimer = null;
function validateDraftSoon() {
  clearTimeout(draftTimer);
  draftTimer = setTimeout(async () => {
    if (!E) return;
    const schedule = scheduleFromEditor();
    const compiled = compileMarks();
    const result = await run({
      command: "validate_set_draft",
      schedule,
      includeRules: [...E.handIncludes],
      excludeRules: [...compiled.excludeRules, ...E.handExcludes],

      // Roots and destinations together let the service answer where this
      // set would actually be durable (FR-SNP-007). Sent only once both
      // exist: an editor mid-way through choosing folders should not be
      // told its set survives nothing.
      roots: compiled.roots.map(root => root.path),
      destinations: [...E.destinations],
    });
    if (result?.result !== "set_draft_validation" || !E) return;

    const defects = document.getElementById("draft-defects");
    if (defects) {
      // Defects and warnings are shown together but never merged: a defect
      // refuses the save, a warning is a thing worth knowing before the
      // first backup rather than after it.
      defects.innerHTML = (result.defects.length || result.warnings?.length)
        ? `<ul class="warnings">`
            + result.defects.map(defect => `<li>${esc(defect)}</li>`).join("")
            + (result.warnings ?? []).map(warning => `<li class="advisory">${esc(warning)}</li>`).join("")
            + `</ul>`
        : "";
    }
    const preview = document.getElementById("sched-preview");
    if (preview) {
      preview.textContent = !schedule
        ? "Runs only when you press “Back up now”."
        : result.nextRuns.length
          ? "Next runs: " + result.nextRuns.map(when => fmtWhen(Date.parse(when))).join("  ·  ")
          : "";
    }
  }, 350);
}

function scheduleFromEditor() {
  const mode = document.querySelector("input[name=sched-mode]:checked")?.value ?? "manual";
  if (mode === "manual") return null;
  if (mode === "every") {
    const n = document.getElementById("sched-n").value.trim() || "0";
    const unit = document.getElementById("sched-unit").value;
    return `every ${n}${unit}`;
  }
  if (mode === "daily") return `daily at ${document.getElementById("sched-time").value || "00:00"}`;
  return E?.schedule ?? null; // custom: whatever the set already had
}

function readPolicyInputs(read) {
  const value = id => {
    const raw = read(id);
    if (raw === undefined || raw === null || String(raw).trim() === "") return null;
    const parsed = Number(String(raw).trim());
    return Number.isInteger(parsed) ? parsed : NaN;
  };
  return {
    keepDaily: value("daily"), keepWeekly: value("weekly"), keepMonthly: value("monthly"),
    minGenerations: value("min"), deferralDays: value("defer"),
  };
}

/* ----- configuration actions ----- */

Object.assign(actions, {
  "cfg-add-set"() { openSetEditor(null); },

  "cfg-edit-set"(el) {
    const set = S.sets.find(candidate => candidate.name === el.dataset.name);
    if (set) openSetEditor(set);
  },

  async "sel-open"(el) {
    const node = E.tree.get(el.dataset.path);
    if (node) node.open = !node.open;
    else await loadTreeDir(el.dataset.path);
    renderTree();
    renderSelectionSummary(); // a fresh listing can complete a wall's siblings
  },

  "rule-remove"(el) {
    const list = el.dataset.list === "exclude" ? "handExcludes" : "handIncludes";
    E[list] = E[list].filter(rule => rule !== el.dataset.rule);
    renderRuleChips(); renderSelectionSummary(); validateDraftSoon(); scheduleLivePreview();
  },

  "rule-add-raw"() {
    const input = document.getElementById("rule-new");
    const rule = input.value.trim();
    if (!rule) return;
    const list = document.getElementById("rule-list").value === "exclude" ? "handExcludes" : "handIncludes";
    if (!E[list].includes(rule)) E[list] = [...E[list], rule];
    input.value = "";
    renderRuleChips(); renderSelectionSummary(); validateDraftSoon(); scheduleLivePreview();
  },

  async "picker-browse"(el) {
    const path = document.getElementById(el.dataset.input)?.value.trim() || null;
    await renderFolderPicker(el.dataset.browser, el.dataset.input, path);
  },

  async "picker-to"(el) {
    await renderFolderPicker(el.dataset.browser, el.dataset.input, el.dataset.path || null);
  },

  "picker-choose"(el) {
    const input = document.getElementById(el.dataset.input);
    if (input) input.value = el.dataset.path;
    document.getElementById(el.dataset.browser).hidden = true;
  },

  async "preview-changes"(el) {
    const host = document.getElementById("change-preview");
    if (!host) return;
    const { roots, excludeRules } = compileMarks();
    if (roots.length === 0) { toast("warn", "Tick at least one folder first."); return; }

    await withBusy(el, async () => {
      host.innerHTML = `<p class="subtle">Walking the source…</p>`;
      const result = await run({
        command: "preview_set_changes",
        setName: E.isNew ? (document.getElementById("set-name").value.trim() || "(draft)") : E.name,
        roots: roots.map(root => ({ path: root.path, label: root.label })),
        includeRules: [...E.handIncludes],
        excludeRules: [...excludeRules, ...E.handExcludes],
      }, { errToast: "The service could not compare", signal: beginSourceScan() });
      if (!result || result.result !== "set_change_preview") { host.innerHTML = ""; return; }

      E.lastPreviewKey = JSON.stringify([roots, [...E.handIncludes], [...excludeRules, ...E.handExcludes]]);
      const report = comparisonReport(result);
      host.innerHTML = `
        <p class="subtle">${esc(report.summary)}</p>
        ${report.detail ? `<pre class="report">${esc(report.detail)}</pre>` : ""}`;
    });
  },

  async "set-save"(el) {
    E.name = document.getElementById("set-name").value.trim();
    const compiled = compileMarks();
    if (!E.name || compiled.roots.length === 0) {
      toast("warn", "A set needs a name and at least one ticked folder.");
      return;
    }

    const retention = readPolicyInputs(id => document.getElementById("ret-" + id)?.value);
    if (Object.values(retention).some(Number.isNaN)) {
      toast("warn", "Retention values are whole numbers of days, weeks, months or snapshots.");
      return;
    }

    const destinations = [...document.querySelectorAll("[data-dest-check]")]
      .filter(box => box.checked).map(box => box.dataset.destCheck);

    const overrides = {};
    for (const name of destinations) {
      if (!document.querySelector(`[data-ovr-check="${CSS.escape(name)}"]`)?.checked) continue;
      const policy = {};
      for (const field of ["keepDaily", "keepWeekly", "keepMonthly", "minGenerations"]) {
        const raw = document.querySelector(`[data-ovr="${CSS.escape(name)}:${field}"]`)?.value.trim() ?? "";
        policy[field] = raw === "" ? null : Number(raw);
      }
      if (Object.values(policy).some(value => value !== null && !Number.isInteger(value))) {
        toast("warn", `The override for '${name}' has a non-numeric value.`); return;
      }
      overrides[name] = policy;
    }

    const includeRules = [...E.handIncludes];
    const excludeRules = [...compiled.excludeRules, ...E.handExcludes];
    const payload = {
      id: E.id, name: E.name,
      root: compiled.roots[0].path, // back-fill for anything pre-1.10 reading it
      roots: compiled.roots.map(root => ({ path: root.path, label: root.label })),
      schedule: scheduleFromEditor(),
      includeRules, excludeRules,
      destinations,
      retention,           // all-null clears; values set — always explicit from this editor
      destinationRetention: overrides,
    };

    // A material edit — the roots or the rules — changes what future backups
    // hold: files can silently leave the backup. So it is never applied on
    // one click: step two states the edit and only its explicit Apply saves
    // (ADR-0038). Compared as sorted sets, so mere reordering is not
    // material. Everything else (name, schedule, retention) saves directly.
    const saved = E.isNew ? null : S.sets.find(set => set.id === E.id);
    const sortedEqual = (left, right) =>
      JSON.stringify([...left].sort()) === JSON.stringify([...right].sort());
    const material = saved && (
      !sortedEqual(rootsOf(saved), payload.roots.map(root => root.path))
      || !sortedEqual(saved.includeRules ?? [], includeRules)
      || !sortedEqual(saved.excludeRules ?? [], excludeRules));

    if (!material) {
      await withBusy(el, () => applySetUpsert(payload));
      return;
    }

    // The confirm panel opens at once. The comparison that informs it is a
    // full walk of the source — ten minutes for a newly added 10K-file
    // folder, and it used to run right here, pinning this button to a
    // spinner for the duration (the 583916 ms preview in the 2026-08-25
    // log was exactly this await). It now fills the open panel in from the
    // background, and Apply never waits for it: on a material save the
    // service queues its own rescan job and the findings land under
    // Notices either way (ADR-0038).
    E.pendingSave = payload;
    renderSetSaveConfirm(saved, null, null, { comparing: true });

    const signal = beginSourceScan();
    let preview = null;
    let previewError = null;
    try {
      const result = await api({
        command: "preview_set_changes",
        setName: saved.name,
        roots: payload.roots,
        includeRules, excludeRules,
      }, { signal });
      if (result.result === "set_change_preview") preview = result;
      else previewError = result.message ?? "the service answered unexpectedly";
    } catch (error) {
      if (error.kind === "aborted") return;
      previewError = error.message;
    }

    // Filled in only if the operator is still looking at this very step —
    // not after Back, not after Apply, not in a dialog reopened for
    // something else.
    if (E?.pendingSave === payload && document.getElementById("set-confirm")) {
      renderSetSaveConfirm(saved, preview, previewError);
    }
  },

  "set-save-back"() {
    endSourceScans(); // the walk was informing a step that no longer exists
    document.getElementById("set-confirm")?.remove();
    const editor = document.getElementById("set-editor");
    if (editor) editor.hidden = false;
    E.pendingSave = null;
  },

  async "set-save-apply"(el) {
    const payload = E.pendingSave;
    if (!payload) return;
    // The advisory walk yields the reader lane before the save queues the
    // authoritative rescan job onto it.
    endSourceScans();
    await withBusy(el, () => applySetUpsert(payload));
  },

  "cfg-delete-set"(el) {
    const name = el.dataset.name;
    openDialog(`
      <h3>Delete backup set</h3>
      <p class="dlg-sub">This removes '<b>${esc(name)}</b>' from the configuration only. Its staging archive and
      every destination's copies remain on disk — deleting data is retention's job, never a config edit's.</p>
      <label class="field" for="confirm-word">Type <b>${esc(name)}</b> to confirm</label>
      <input type="text" id="confirm-word" class="confirm-word" autocomplete="off" spellcheck="false"
             data-action-input="confirm-word" data-word="${esc(name.toLowerCase())}" data-enables="delete-set-go">
      <div class="dlg-actions">
        <button type="button" class="btn" data-action="close-dialog">Cancel</button>
        <button type="button" class="btn danger" id="delete-set-go" data-action="cfg-delete-set-go" data-name="${esc(name)}" disabled>Remove from configuration</button>
      </div>`);
    document.getElementById("confirm-word").focus();
  },

  async "cfg-delete-set-go"(el) {
    await withBusy(el, async () => {
      const result = await run(
        { command: "delete_backup_set", name: el.dataset.name }, { errToast: "Delete refused" });
      if (result?.result === "configuration_change") {
        reportDialog("Backup set removed", result.lines);
        refreshConfigData(); refreshStatus();
      }
    });
  },

  // The write-only setup ceremony (ADR-0042). The passphrase goes to the
  // CONSOLE PROCESS on this machine, which derives the keys and seals the
  // write bundle to the service; it never crosses the command contract. One
  // dialog serves creation and adoption — the console tells them apart by
  // whether a v2 archive already exists.
  "cfg-write-only"(el) {
    const name = el.dataset.name;
    openDialog(`
      <h3>Make '${esc(name)}' write-only</h3>
      <p class="dlg-sub">One long passphrase becomes the set's only key: the service keeps a sealing
      public key and a metadata key, and can add to the backup but <b>never read file contents back</b>.
      Restoring — or moving the archive to another machine — means entering the passphrase again.</p>
      <label class="field" for="wo-passphrase">Passphrase</label>
      <input type="password" id="wo-passphrase" autocomplete="new-password">
      <ul class="warnings"><li>The passphrase can never change, and there is no reset, no export and no
      recovery kit that restores without it. <b>If it is lost, this backup is unrecoverable.</b></li></ul>
      <label class="check-row"><input type="checkbox" id="wo-ack">
        I understand that losing this passphrase loses the backup, permanently.</label>
      <div class="dlg-actions">
        <button type="button" class="btn" data-action="close-dialog">Cancel</button>
        <button type="button" class="btn danger" data-action="cfg-write-only-go" data-name="${esc(name)}">Provision write-only</button>
      </div>`);
    document.getElementById("wo-passphrase").focus();
  },

  async "cfg-write-only-go"(el) {
    const passphrase = document.getElementById("wo-passphrase")?.value ?? "";
    const acknowledged = document.getElementById("wo-ack")?.checked ?? false;
    if (!passphrase) { toast("warn", "Enter the passphrase."); return; }
    if (!acknowledged) { toast("warn", "Provisioning needs the loss acknowledgement."); return; }
    await withBusy(el, async () => {
      let response;
      try {
        response = await fetch("/api/provision-write-only", {
          method: "POST",
          headers: { "Content-Type": "application/json", "Authorization": "Bearer " + token },
          body: JSON.stringify({ setName: el.dataset.name, passphrase, acknowledged }),
        });
      } catch {
        toast("warn", "The console process stopped answering.");
        return;
      }
      const body = await safeJson(response);
      if (!response.ok) { toast("bad", body?.message ?? "The ceremony refused."); return; }
      if (body?.outcome === "provisioned") {
        reportDialog("Write-only provisioned", body.lines ?? []);
        refreshConfigData(); refreshStatus();
      } else {
        toast("bad", body?.detail ?? "The ceremony refused.");
      }
    });
  },

  "dest-add-local"() { openDestEditor("local-path", null); },
  "dest-add-peer"() { openDestEditor("peer", null); },

  "cfg-edit-dest"(el) {
    const destination = S.destinations.find(candidate => candidate.id === el.dataset.id);
    if (destination) openDestEditor(destination.kind, destination);
  },

  async "dest-save"(el) {
    const kind = el.dataset.kind;
    const descriptor = {
      id: el.dataset.id || null,
      name: document.getElementById("dest-name").value.trim(),
      kind,
      path: kind === "local-path" ? document.getElementById("dest-path").value.trim() : null,
      fingerprint: kind === "peer" ? document.getElementById("dest-fp").value.trim() : null,
      endpoint: kind === "peer" ? document.getElementById("dest-endpoint").value.trim() : null,
      failureDomain: document.getElementById("dest-domain").value || null,
      deepVerifyIntervalDays: Number(document.getElementById("dest-sweep").value.trim()) || null,
    };
    if (!descriptor.name) { toast("warn", "A destination needs a name."); return; }

    await withBusy(el, async () => {
      const result = await run(
        { command: "upsert_destination", destination: descriptor }, { errToast: "The service refused the destination" });
      if (result) {
        toast("ok", `Destination '${descriptor.name}' saved.`);
        closeDialog();
        refreshConfigData(); refreshStatus();
      }
    });
  },

  "cfg-delete-dest"(el) {
    const name = el.dataset.name;
    openDialog(`
      <h3>Delete destination</h3>
      <p class="dlg-sub">This stops the hub managing '<b>${esc(name)}</b>'. Nothing stored there is deleted —
      the copies it holds stay until you deal with them where they live (FR-DEST-007).</p>
      <div class="dlg-actions">
        <button type="button" class="btn" data-action="close-dialog">Cancel</button>
        <button type="button" class="btn danger" data-action="cfg-delete-dest-go" data-name="${esc(name)}">Remove destination</button>
      </div>`);
  },

  async "cfg-delete-dest-go"(el) {
    await withBusy(el, async () => {
      const result = await run(
        { command: "delete_destination", name: el.dataset.name }, { errToast: "Delete refused" });
      if (result?.result === "configuration_change") {
        reportDialog("Destination removed", result.lines);
        refreshConfigData(); refreshStatus();
      }
    });
  },

  "invite-open"() {
    openDialog(`
      <h3>Invite a peer</h3>
      <p class="dlg-sub">Issuing the invite is <b>your approval</b>: you choose now what the redeeming machine
      will be to this one. Read the code to the other operator over a call — it is spoken once, never sent.</p>
      <label class="field" for="inv-label">What to call them here</label>
      <input type="text" id="inv-label" spellcheck="false" placeholder="anna's laptop">
      <label class="field">Their role</label>
      <div class="radio-row">
        <label><input type="radio" name="inv-role" value="stores-here" checked> they back up <b>to</b> this machine</label>
        <label><input type="radio" name="inv-role" value="stores-for-us"> this machine backs up <b>to them</b></label>
        <label><input type="radio" name="inv-role" value="both"> both ways</label>
      </div>
      <div class="field-row wrap">
        <label class="mini">quota (GiB, blank = none) <input type="text" id="inv-quota" class="num"></label>
        <label class="mini">valid for (hours) <input type="text" id="inv-ttl" class="num" value="24"></label>
      </div>
      <div class="dlg-actions">
        <button type="button" class="btn" data-action="close-dialog">Cancel</button>
        <button type="button" class="btn primary" data-action="invite-create">Issue invite</button>
      </div>`);
    document.getElementById("inv-label").focus();
  },

  async "invite-create"(el) {
    const label = document.getElementById("inv-label").value.trim();
    if (!label) { toast("warn", "Name the peer this invite is for."); return; }
    const role = document.querySelector("input[name=inv-role]:checked").value;
    const quotaGib = Number(document.getElementById("inv-quota").value.trim() || "0");
    const ttlHours = Number(document.getElementById("inv-ttl").value.trim() || "24");

    await withBusy(el, async () => {
      const result = await run({
        command: "create_pairing_invite",
        label, role,
        quotaBytes: quotaGib > 0 ? Math.round(quotaGib * 1024 * 1024 * 1024) : null,
        timeToLiveMinutes: Math.max(1, Math.round(ttlHours * 60)),
      }, { errToast: "The service refused the invite" });
      if (result?.result !== "pairing_invite") return;

      openDialog(`
        <h3>Invite issued</h3>
        <p class="dlg-sub">Read this to the other operator. It is shown <b>once</b> — the service keeps only
        what it needs to recognise it, not the code itself.</p>
        <div class="code-display mono" id="invite-code">${esc(result.code)}</div>
        <div class="actions-row">
          <button type="button" class="btn small" data-action="invite-copy">⧉ Copy code</button>
        </div>
        <p class="dlg-sub">
          ${result.listeningEndpoint
            ? `They should pair against <b class="mono">${esc(result.listeningEndpoint)}</b> (use this machine's reachable address with that port).`
            : ""}
          Expires ${esc(fmtWhen(result.expiresAt))}.
        </p>
        ${result.warning ? `<ul class="warnings"><li>${esc(result.warning)}</li></ul>` : ""}
        <div class="dlg-actions"><button type="button" class="btn primary" data-action="close-dialog">Done</button></div>`);
      refreshConfigData();
    });
  },

  async "invite-copy"() {
    try {
      await navigator.clipboard.writeText(document.getElementById("invite-code").textContent.trim());
      toast("ok", "Code copied.");
    } catch {
      toast("warn", "The browser refused the clipboard — select the code and copy it by hand.");
    }
  },

  async "invite-revoke"(el) {
    const result = await run(
      { command: "revoke_pairing_invite", inviteId: el.dataset.id }, { errToast: "Revoke refused" });
    if (result) { toast("ok", "Invite revoked."); refreshConfigData(); }
  },

  "pair-open"() {
    openDialog(`
      <h3>Pair with a remote service</h3>
      <p class="dlg-sub">Entering the code is <b>your approval</b>. This service dials theirs, both prove they
      hold the invite, and each pins the other's key — the code is spent the moment it works.</p>
      <label class="field" for="pair-code">Invite code <span class="plain">— as the other operator read it</span></label>
      <input type="text" id="pair-code" class="mono" spellcheck="false" autocomplete="off" placeholder="xxxx-xxxx-xxxx-xxxx-xxxx-xxxx-xxxx-xxxx-xx">
      <div class="field-row">
        <span class="grow"><label class="field" for="pair-host">Their address</label>
        <input type="text" id="pair-host" class="mono" spellcheck="false" placeholder="203.0.113.7 or vault.example"></span>
        <span><label class="field" for="pair-port">Port</label>
        <input type="text" id="pair-port" class="num" value=""></span>
      </div>
      <label class="field" for="pair-label">What to call them here</label>
      <input type="text" id="pair-label" spellcheck="false" placeholder="the vault">
      <div class="dlg-actions">
        <button type="button" class="btn" data-action="close-dialog">Cancel</button>
        <button type="button" class="btn primary" data-action="pair-go">Pair</button>
      </div>`);
    document.getElementById("pair-code").focus();
  },

  async "pair-go"(el) {
    const code = document.getElementById("pair-code").value.trim();
    const host = document.getElementById("pair-host").value.trim();
    const port = Number(document.getElementById("pair-port").value.trim());
    const label = document.getElementById("pair-label").value.trim() || host;
    if (!code || !host || !port) { toast("warn", "The code, the address and the port are all needed."); return; }

    await withBusy(el, async () => {
      const result = await run(
        { command: "pair_with_invite", code, host, port, label }, { errToast: "Pairing failed" });
      if (result?.result !== "pairing_completed") return;

      openDialog(`
        <h3>✔ Paired</h3>
        <p class="dlg-sub"><b>${esc(result.label)}</b> is pinned as
        <span class="mono">${esc(result.fingerprint)}</span>. They recorded this machine as
        <b>${esc(result.theirRole)}</b>${result.quotaBytes ? ` with a ${fmtBytes(result.quotaBytes)} quota` : ""}.</p>
        <p class="dlg-sub">To back up to them, add them as a destination and reference it from a set.</p>
        <div class="dlg-actions">
          <button type="button" class="btn" data-action="close-dialog">Later</button>
          <button type="button" class="btn primary" data-action="pair-add-dest"
                  data-label="${esc(result.label)}" data-fp="${esc(result.fingerprint)}"
                  data-endpoint="${esc(host)}:${port}">Add as destination</button>
        </div>`);
      refreshConfigData();
    });
  },

  "pair-add-dest"(el) {
    openDestEditor("peer", {
      id: null,
      name: el.dataset.label,
      kind: "peer",
      fingerprint: el.dataset.fp,
      endpoint: el.dataset.endpoint,
      path: null, failureDomain: null, deepVerifyIntervalDays: null,
    });
  },
});

/* ----- destination editor & root browser ----- */

function openDestEditor(kind, destination) {
  const isNew = !destination?.id;
  const domains = ["", "same-volume", "same-machine", "same-site", "independent"];
  openDialog(`
    <h3>${isNew ? (kind === "peer" ? "New peer destination" : "New local destination") : "Edit destination"}</h3>
    ${kind === "peer" ? `<p class="dlg-sub">A peer destination points a set at a machine you have paired with.
      The grant holds the key; this holds the address (FR-DEST-006).</p>` : ""}
    <label class="field" for="dest-name">Name <span class="plain">— what sets reference</span></label>
    <input type="text" id="dest-name" spellcheck="false" value="${esc(destination?.name ?? "")}">
    ${kind === "local-path" ? `
      <label class="field" for="dest-path">Folder — on the service's machine</label>
      <div class="field-row">
        <input type="text" id="dest-path" class="mono" spellcheck="false" value="${esc(destination?.path ?? "")}">
        <button type="button" class="btn" data-action="picker-browse" data-browser="dest-browser" data-input="dest-path">Browse…</button>
      </div>
      <div id="dest-browser" hidden></div>` : `
      <label class="field" for="dest-fp">Paired peer</label>
      <select id="dest-fp">${S.pairings.map(pairing =>
        `<option value="${esc(pairing.fingerprint)}" ${pairing.fingerprint === destination?.fingerprint ? "selected" : ""}>
           ${esc(pairing.label)} — ${esc(pairing.fingerprint.slice(0, 12))}… (${esc(pairing.role)})</option>`).join("")}
      </select>
      <label class="field" for="dest-endpoint">Endpoint <span class="plain">host:port</span></label>
      <input type="text" id="dest-endpoint" class="mono" spellcheck="false" value="${esc(destination?.endpoint ?? "")}">`}
    <div class="field-row wrap">
      <label class="mini">failure domain
        <select id="dest-domain">${domains.map(domain =>
          `<option value="${domain}" ${domain === (destination?.failureDomain ?? "") ? "selected" : ""}>${domain || "derive by kind"}</option>`).join("")}
        </select></label>
      <label class="mini">deep-verify every (days) <input type="text" id="dest-sweep" class="num" value="${destination?.deepVerifyIntervalDays ?? ""}"></label>
    </div>
    <div class="dlg-actions">
      <button type="button" class="btn" data-action="close-dialog">Cancel</button>
      <button type="button" class="btn primary" data-action="dest-save" data-kind="${esc(kind)}" data-id="${esc(destination?.id ?? "")}">
        ${isNew ? "Declare destination" : "Save changes"}</button>
    </div>`);
  document.getElementById("dest-name").focus();
}

// One picker serves every folder-shaped input — the set root, the restore
// output, the local destination path — routed by data attributes so each
// dialog can host its own browser div. Choosing fills the input; the input
// stays editable, so naming a not-yet-existing subfolder still works.
async function renderFolderPicker(browserId, inputId, path) {
  const host = document.getElementById(browserId);
  if (!host) return;
  host.hidden = false;
  const listing = await run({ command: "browse_folders", path }, { errToast: "Listing refused" });
  if (listing?.result !== "folder_listing") { host.hidden = true; return; }

  const ids = `data-browser="${esc(browserId)}" data-input="${esc(inputId)}"`;
  host.innerHTML = `
    <div class="picker">
      <div class="picker-head">
        <span class="mono">${esc(listing.path ?? "This machine")}</span>
        <span>
          ${listing.path ? `<button type="button" class="btn tiny" data-action="picker-to" ${ids} data-path="${esc(listing.parent ?? "")}">↑ up</button>
          <button type="button" class="btn tiny" data-action="picker-choose" ${ids} data-path="${esc(listing.path)}">choose this folder</button>` : ""}
        </span>
      </div>
      ${listing.folders.map(folder => `
        <div class="picker-row ${folder.hidden ? "dim" : ""}">
          <a data-action="picker-to" ${ids} data-path="${esc(folder.path)}">📁 ${esc(folder.name)}</a>
          ${folder.inaccessible ? `<span class="detail">not readable</span>`
            : `<button type="button" class="btn tiny" data-action="picker-choose" ${ids} data-path="${esc(folder.path)}">choose</button>`}
        </div>`).join("") || `<p class="subtle">No folders in here.</p>`}
    </div>`;
}

/* ----- snapshot browser ----- */

async function openBrowser(snapshotId, path) {
  const result = await run({ command: "list_directory", snapshotId, path: path || null }, { errToast: "Listing refused" });
  if (result?.result !== "directory") return;

  const parts = path ? path.split("/") : [];
  const crumbs = [`<a data-action="browse-to" data-snapshot="${esc(snapshotId)}" data-path="">snapshot root</a>`];
  let walked = "";
  for (const part of parts) {
    walked = walked ? walked + "/" + part : part;
    crumbs.push(`<span class="sep">/</span><a data-action="browse-to" data-snapshot="${esc(snapshotId)}" data-path="${esc(walked)}">${esc(part)}</a>`);
  }

  // The set's own snapshots, newest first as the service lists them — the
  // time machine's rail for the older/newer jumps at the same path.
  const here = S.snapshots.find(row => row.snapshotId === snapshotId);
  const rail = here ? S.snapshots.filter(row => row.backupSetId === here.backupSetId) : [];
  const at = rail.findIndex(row => row.snapshotId === snapshotId);
  const older = at >= 0 ? rail[at + 1] : null;
  const newer = at > 0 ? rail[at - 1] : null;

  const CHANGE = { new: ["accent", "new"], changed: ["warn", "changed"], same: ["", ""] };
  const icon = { directory: "📁", file: "📄", symlink: "🔗", special: "⚙️" };
  const rows = result.entries.map(entry => {
    const childPath = path ? path + "/" + entry.name : entry.name;
    const isDir = entry.kind === "directory";
    const [changeCls, changeLabel] = CHANGE[entry.change] ?? ["", ""];
    return `<tr class="${isDir ? "rowlink" : ""}" ${isDir ? `data-action-row="browse-to" data-snapshot="${esc(snapshotId)}" data-path="${esc(childPath)}"` : ""}>
      <td><span class="kind-ico">${icon[entry.kind] ?? "•"}</span> ${esc(entry.name)}${entry.kind === "symlink" ? ` <span class="detail">symlink</span>` : ""}
          ${changeLabel ? ` <span class="badge ${changeCls}">${changeLabel}</span>` : ""}</td>
      <td class="detail">${entry.modifiedAt ? esc(fmtWhen(entry.modifiedAt)) : ""}</td>
      <td class="num">${entry.kind === "file" ? fmtBytes(entry.length) : ""}</td>
      <td><button type="button" class="btn small" data-action="restore" data-snapshot="${esc(snapshotId)}" data-path="${esc(childPath)}">Restore…</button></td>
    </tr>`;
  }).join("");

  // What the previous snapshot held here and this one does not: absence is
  // how a snapshot says "deleted" (FR-SNP-002), shown rather than implied.
  const deleted = (result.deleted ?? []).map(name => `
    <tr class="gone" title="present in the previous snapshot only">
      <td><span class="kind-ico">🕓</span> <s>${esc(name)}</s> <span class="badge bad">deleted</span></td>
      <td class="detail" colspan="2"></td>
      <td>${result.previousSnapshotId ? `<button type="button" class="btn small" data-action="browse-to"
        data-snapshot="${esc(result.previousSnapshotId)}" data-path="${esc(path)}">View in previous…</button>` : ""}</td>
    </tr>`).join("");

  const legend = result.previousSnapshotId
    ? `changes are vs the previous snapshot of this set (${esc(result.previousSnapshotId.slice(0, 12))}…)`
    : "the first snapshot of this set — nothing to compare against";

  openDialog(`
    <h3>Browse snapshot</h3>
    <p class="dlg-sub"><span class="mono">${esc(snapshotId.slice(0, 24))}…</span>
      ${here ? ` · captured ${esc(fmtWhen(here.capturedAt))}` : ""} · ${legend}</p>
    <div class="actions-row">
      <button type="button" class="btn small" data-action="browse-to" ${older ? `data-snapshot="${esc(older.snapshotId)}" data-path="${esc(path)}"` : "disabled"}>‹ older${older ? ` (${esc(rel(older.capturedAt))})` : ""}</button>
      <button type="button" class="btn small" data-action="browse-to" ${newer ? `data-snapshot="${esc(newer.snapshotId)}" data-path="${esc(path)}"` : "disabled"}>newer${newer ? ` (${esc(rel(newer.capturedAt))})` : ""} ›</button>
    </div>
    <div class="crumbs">${crumbs.join("")}</div>
    <div class="table-wrap"><table class="data">
      <tbody>${(rows + deleted) || `<tr><td class="detail">This directory is empty.</td></tr>`}</tbody>
    </table></div>
    <div class="dlg-actions">
      <button type="button" class="btn" data-action="restore" data-snapshot="${esc(snapshotId)}" data-path="${esc(path)}">Restore this ${path ? "folder" : "snapshot"}…</button>
      <button type="button" class="btn primary" data-action="close-dialog">Close</button>
    </div>`);
}

/* ----- the guided restore wizard (ADR-0041) ----- */
//
// Six steps: unlock -> source -> effective date -> files -> target ->
// review/run. The passphrase is verified by the CONSOLE PROCESS against the
// archive's key files on this machine (/api/restore-gate) and never sent to
// the service; the restore itself reads through a server-side source handle
// — the staging archive, a local replica, or a peer's replica over the wire.

let W = null;

function openRestoreWizard(prefill) {
  W = {
    step: 1,
    passphrase: "",
    gate: null,               // null | "verified" | "wrong" | "unavailable"
    gateDetail: null,
    gateAck: false,
    grantEnvelope: null,      // sealed restore grant for a write-only set (ADR-0042); opaque hex
    sets: [], dests: [],
    setName: null, destinationName: null,
    source: null,             // RestoreSourceOpenedResult
    date: new Date().toISOString().slice(0, 10),
    snapshot: null,           // the resolved SnapshotDescriptor
    tree: new Map(),          // snapshot path ("" = root) -> DirectoryResult|null, plus open flags
    open: new Set([""]),
    marks: new Map(),         // snapshot path -> "on"|"off"; unmarked inherit, default off
    target: "folder",
    output: "",
    existing: "rename",
    plan: null,
    result: null,
    prefill: prefill?.snapshotId ? prefill : null,
  };
  dialog.classList.add("wide");
  rstRender();
  document.getElementById("rst-passphrase")?.focus();
}

const RST_STEPS = ["Unlock", "Source", "Date", "Files", "Target", "Restore"];

function rstHeader() {
  return `<h3>Restore</h3>
    <div class="rst-steps">${RST_STEPS.map((name, index) => `
      <span class="rst-step ${index + 1 === W.step ? "now" : index + 1 < W.step ? "done" : ""}">${index + 1}. ${name}</span>`).join("")}
    </div>`;
}

function rstNav(continueLabel = "Continue", danger = false) {
  return `<div class="dlg-actions">
    <button type="button" class="btn" data-action="close-dialog">Cancel</button>
    ${W.step > 1 ? `<button type="button" class="btn" data-action="rst-back">‹ Back</button>` : ""}
    <button type="button" class="btn ${danger ? "danger" : "primary"}" data-action="rst-continue">${esc(continueLabel)}</button>
  </div>`;
}

function rstRender() {
  const body = [rstStep1, rstStep2, rstStep3, rstStep4, rstStep5, rstStep6][W.step - 1]();
  openDialog(`<div id="rst">${rstHeader()}${body}</div>`);
  if (W.step === 4) rstPaintTriState();
}

/* step 1 — unlock */
function rstStep1() {
  return `
    <p class="dlg-sub">Enter the repository passphrase. It is checked by <b>this console process</b> against the
    archive's own key files on this machine and sent nowhere — not even to the service.</p>
    <label class="field" for="rst-passphrase">Passphrase</label>
    <input type="password" id="rst-passphrase" autocomplete="off" value="${esc(W.passphrase)}">
    ${W.gate === "wrong" ? `<ul class="warnings"><li>That passphrase does not open the repository.</li></ul>` : ""}
    ${W.gate === "unavailable" ? `
      <ul class="warnings"><li>The passphrase cannot be verified from here${W.gateDetail ? ` — ${esc(W.gateDetail)}` : ""}.
      You can continue; the service still decrypts with its own keys.</li></ul>
      <label class="check-row"><input type="checkbox" id="rst-gate-ack" ${W.gateAck ? "checked" : ""}>
        Continue without local verification</label>` : ""}
    ${rstNav()}`;
}

/* step 2 — source */
function rstStep2() {
  const options = [];
  options.push(`
    <label class="radio-block"><input type="radio" name="rst-src" value="staging" ${W.destinationName === null ? "checked" : ""}>
      <b>This machine's staging archive</b> <span class="detail">the service's own copy</span></label>`);
  for (const destination of W.dests) {
    if (destination.kind !== "local-path" && destination.kind !== "peer") continue;
    const label = destination.kind === "local-path"
      ? `replica at <span class="mono">${esc(destination.path ?? "")}</span>`
      : `replica at peer <span class="mono">${esc(destination.endpoint ?? "")}</span>, over the network`;
    options.push(`
      <label class="radio-block"><input type="radio" name="rst-src" value="${esc(destination.name)}"
        ${W.destinationName === destination.name ? "checked" : ""}>
        <b>${esc(destination.name)}</b> <span class="detail">${label}</span></label>`);
  }

  return `
    <p class="dlg-sub">Where to restore <b>from</b>. A destination's replica serves when this machine's own
    archive is damaged or gone — a peer's travels the paired connection.</p>
    <label class="field" for="rst-set">Backup set</label>
    <select id="rst-set">${W.sets.map(set =>
      `<option ${set.name === W.setName ? "selected" : ""}>${esc(set.name)}</option>`).join("")}</select>
    <label class="field">Source</label>
    ${options.join("")}
    ${W.sourceError ? `<ul class="warnings"><li>${esc(W.sourceError)}</li></ul>` : ""}
    ${rstNav("Open source")}`;
}

/* step 3 — effective date */
function rstStep3() {
  const resolved = rstResolveSnapshot();
  const dates = [...new Set(W.source.snapshots.map(row =>
    new Date(Number(row.capturedAt)).toISOString().slice(0, 10)))];
  return `
    <p class="dlg-sub">The restore uses the <b>last backup at or before</b> this date
    ${W.source.warnings?.length ? `<br>Source notes: ${W.source.warnings.map(esc).join("; ")}` : ""}</p>
    <label class="field" for="rst-date">Effective date</label>
    <input type="date" id="rst-date" value="${esc(W.date)}">
    <p class="subtle">Backups exist for: ${dates.map(esc).join(" · ") || "(none)"}</p>
    ${resolved
      ? `<p class="dlg-sub">Effective backup: <b>${esc(fmtWhen(resolved.capturedAt))}</b>
         <span class="mono">${esc(resolved.snapshotId.slice(0, 16))}…</span> · ${fmtCount(resolved.files)} files
         ${resolved.captureStatus === 1 ? "" : " · partial capture"}</p>`
      : `<ul class="warnings"><li>No backup exists at or before this date — the earliest here is
         ${esc(fmtWhen(W.source.snapshots[0]?.capturedAt))}.</li></ul>`}
    ${rstNav()}`;
}

function rstResolveSnapshot() {
  // End of the chosen day, in this browser's clock — snapshots are stamped
  // in Unix ms, so the comparison is exact.
  const cutoff = new Date(W.date + "T23:59:59.999").getTime();
  const eligible = W.source.snapshots.filter(row => Number(row.capturedAt) <= cutoff);
  return eligible.length ? eligible[eligible.length - 1] : null;
}

/* step 4 — files */
function rstStep4() {
  return `
    <p class="dlg-sub">Tick the files and folders to restore from the backup of
    <b>${esc(fmtWhen(W.snapshot.capturedAt))}</b>.</p>
    <div id="rst-tree" class="tree">${rstRenderDir("", 0)}</div>
    <div id="rst-summary" class="subtle">${rstSummary()}</div>
    ${rstNav()}`;
}

function rstListing(path) {
  if (W.tree.has(path)) return W.tree.get(path);
  W.tree.set(path, null); // one fetch per path
  run({ command: "list_directory", snapshotId: W.snapshot.snapshotId, path: path || null, source: W.source.sourceId })
    .then(result => {
      if (!W || W.snapshot === null) return;
      W.tree.set(path, result?.result === "directory" ? result : { entries: [] });
      if (W.step === 4) rstRender();
    });
  return null;
}

function rstIsUnder(child, parent) {
  return parent === "" ? child !== "" : child !== parent && child.startsWith(parent + "/");
}

function rstEffective(path) {
  let best = null;
  for (const [key, state] of W.marks) {
    if (key === path || (key === "" ? true : rstIsUnder(path, key))) {
      if (best === null || key.length > best.key.length) best = { key, state };
    }
  }
  return best?.state ?? "off";
}

function rstRenderDir(path, depth) {
  const listing = rstListing(path);
  if (listing === null) return `<p class="subtle">${"<span class='indent'></span>".repeat(depth)}Loading…</p>`;

  const rows = [];
  for (const entry of listing.entries) {
    const childPath = path ? path + "/" + entry.name : entry.name;
    const state = rstEffective(childPath);
    const isDir = entry.kind === "directory";
    rows.push(`
      <div class="tree-row ${state === "off" ? "off" : ""}">
        ${"<span class='indent'></span>".repeat(depth)}
        ${isDir
          ? `<button type="button" class="twist" data-action="rst-open" data-path="${esc(childPath)}">${W.open.has(childPath) ? "▾" : "▸"}</button>`
          : `<span class="twist"></span>`}
        <input type="checkbox" class="mark" data-rst-mark="${esc(childPath)}" ${state === "on" ? "checked" : ""}
               aria-label="Restore ${esc(entry.name)}">
        <span class="kind-ico">${isDir ? "📁" : entry.kind === "symlink" ? "🔗" : "📄"}</span>
        <span class="tree-name">${esc(entry.name)}</span>
        ${entry.kind === "file" ? `<span class="detail">${fmtBytes(entry.length)}</span>` : ""}
      </div>
      ${isDir && W.open.has(childPath) ? rstRenderDir(childPath, depth + 1) : ""}`);
  }

  return rows.join("") || `<p class="subtle">${"<span class='indent'></span>".repeat(depth)}(empty)</p>`;
}

function rstPaintTriState() {
  for (const box of document.querySelectorAll("#rst-tree input[data-rst-mark]")) {
    const path = box.dataset.rstMark;
    const state = rstEffective(path);
    box.indeterminate = [...W.marks].some(([key, mark]) => rstIsUnder(key, path) && mark !== state);
  }
}

function rstToggle(path) {
  const target = rstEffective(path) === "on" ? "off" : "on";
  for (const key of [...W.marks.keys()]) {
    if (rstIsUnder(key, path)) W.marks.delete(key);
  }
  W.marks.delete(path);
  if (rstEffective(path) !== target) W.marks.set(path, target);
  rstRender();
}

// marks + loaded listings -> the exact prefixes the plan takes. A folder
// ticked whole becomes one prefix; unticking inside it recurses to the kept
// siblings — exact, because the snapshot is frozen (unlike the set editor's
// wall, nothing new can appear here).
function rstCompile() {
  const prefixes = [];
  const walk = path => {
    const listing = W.tree.get(path);
    if (!listing) return; // not loaded: nothing below was ever marked
    for (const entry of listing.entries) {
      const childPath = path ? path + "/" + entry.name : entry.name;
      const state = rstEffective(childPath);
      const offBelow = [...W.marks].some(([key, mark]) => mark === "off" && rstIsUnder(key, childPath));
      const onBelow = [...W.marks].some(([key, mark]) => mark === "on" && rstIsUnder(key, childPath));
      if (state === "on") {
        if (offBelow) walk(childPath);
        else prefixes.push(childPath);
      } else if (onBelow) {
        walk(childPath);
      }
    }
  };
  walk("");
  return prefixes;
}

function rstSummary() {
  const prefixes = rstCompile();
  return prefixes.length === 0
    ? "Nothing selected yet — tick what to restore."
    : `<b>${prefixes.length}</b> selection(s): ${prefixes.slice(0, 6).map(esc).join(", ")}${prefixes.length > 6 ? ` … and ${prefixes.length - 6} more` : ""}`;
}

/* step 5 — target */
function rstStep5() {
  const set = W.sets.find(candidate => candidate.name === W.setName);
  const roots = set ? rootsOf(set) : [];
  return `
    <p class="dlg-sub">Where the files land — a path on the <b>service's</b> machine; content never crosses
    to this page.</p>
    <label class="radio-block"><input type="radio" name="rst-target" value="folder" ${W.target === "folder" ? "checked" : ""}>
      <b>A folder I choose</b></label>
    <div class="field-row rst-indent">
      <input type="text" id="rst-output" class="mono" value="${esc(W.output)}" spellcheck="false"
             placeholder="/home/you/restore-${esc(W.date)}">
      <button type="button" class="btn" data-action="picker-browse" data-browser="rst-browser" data-input="rst-output">Browse…</button>
    </div>
    <div id="rst-browser" hidden></div>
    <label class="radio-block"><input type="radio" name="rst-target" value="original" ${W.target === "original" ? "checked" : ""}
      ${roots.length === 0 ? "disabled" : ""}>
      <b>The original location</b>
      <span class="detail">${roots.length ? roots.map(esc).join(", ") : "the set is no longer configured"}</span></label>
    <label class="field">If a file already exists there</label>
    <div class="radio-row">
      <label><input type="radio" name="rst-existing" value="rename" ${W.existing === "rename" ? "checked" : ""}>
        keep both — the restored copy is written beside it as <span class="mono">name (restored ${esc(W.date)}).ext</span></label>
      <label><input type="radio" name="rst-existing" value="overwrite" ${W.existing === "overwrite" ? "checked" : ""}>
        overwrite the existing file</label>
    </div>
    ${rstNav()}`;
}

/* step 6 — review, run, result */
function rstStep6() {
  if (W.result) {
    const clean = W.result.outcome === "complete";
    return `
      <h3>${clean ? "✔ Restore complete" : "◐ Restore " + esc(W.result.outcome)}</h3>
      <div class="plan-figures">
        <div class="fig"><b>${fmtCount(W.result.restored)}</b><span>files written</span></div>
        <div class="fig"><b>${fmtCount(W.result.failed)}</b><span>failures</span></div>
        <div class="fig"><b>${fmtCount(W.result.writtenBeside)}</b><span>kept beside</span></div>
        <div class="fig"><b>${fmtCount(W.result.displaced)}</b><span>moved aside</span></div>
      </div>
      <p class="dlg-sub">Written on the service's machine to
        <span class="mono">${esc(W.result.outputDirectory)}</span>.
        Receipt: <span class="mono">${esc(W.result.receiptPath ?? "—")}</span></p>
      ${W.result.failedSample?.length ? `<pre class="report">${esc(W.result.failedSample.join("\n"))}</pre>` : ""}
      <div class="dlg-actions"><button type="button" class="btn primary" data-action="close-dialog">Close</button></div>`;
  }

  const plan = W.plan;
  const figures = plan ? `
    <div class="plan-figures">
      <div class="fig"><b>${fmtCount(plan.files)}</b><span>files</span></div>
      <div class="fig"><b>${fmtBytes(plan.bytes)}</b><span>to write</span></div>
      <div class="fig"><b>${fmtCount(plan.missingObjects?.length ?? 0)}</b><span>missing objects</span></div>
      <div class="fig"><b>${fmtCount(plan.conflicts ?? 0)}</b><span>conflicts</span></div>
    </div>
    ${plan.missingObjects?.length ? `<ul class="warnings"><li>${fmtCount(plan.missingObjects.length)} object(s) the plan
      needs cannot be found — those files will fail.</li></ul>` : ""}
    ${plan.conflictSample?.length ? `<pre class="report">${esc(plan.conflictSample.join("\n"))}</pre>` : ""}
    ${plan.degradations?.length ? `<p class="subtle">${plan.degradations.map(esc).join("<br>")}</p>` : ""}`
    : `<p class="subtle">Planning…</p>`;

  return `
    <p class="dlg-sub">Restoring the backup of <b>${esc(fmtWhen(W.snapshot.capturedAt))}</b> from
    <b>${esc(W.source.location)}</b> to
    <b>${W.target === "original" ? "the original location" : `<span class="mono">${esc(W.output)}</span>`}</b>,
    ${W.existing === "rename" ? "keeping existing files (restored copies beside them)" : "overwriting existing files"}.</p>
    ${figures}
    <label class="field" for="confirm-word">Type <b>restore</b> to confirm</label>
    <input type="text" id="confirm-word" class="confirm-word" autocomplete="off" spellcheck="false"
           data-action-input="confirm-word" data-word="restore" data-enables="rst-run-go">
    <div class="dlg-actions">
      <button type="button" class="btn" data-action="close-dialog">Cancel</button>
      <button type="button" class="btn" data-action="rst-back">‹ Back</button>
      <button type="button" class="btn danger" id="rst-run-go" data-action="rst-run" disabled>Restore</button>
    </div>`;
}

async function rstGateCheck() {
  W.passphrase = document.getElementById("rst-passphrase")?.value ?? W.passphrase;
  if (!W.passphrase) { toast("warn", "Enter the passphrase."); return false; }
  let response;
  try {
    response = await fetch("/api/restore-gate", {
      method: "POST",
      headers: { "Content-Type": "application/json", "Authorization": "Bearer " + token },
      body: JSON.stringify({ passphrase: W.passphrase }),
    });
  } catch {
    toast("warn", "The console process stopped answering.");
    return false;
  }
  const body = await safeJson(response);
  if (!response.ok) { toast("bad", body?.message ?? "The gate refused."); return false; }
  W.gate = body?.outcome ?? "unavailable";
  W.gateDetail = body?.detail ?? null;
  // A write-only archive's gate answers with a sealed restore grant
  // (ADR-0042): opaque here, opened only by the service; it rides the
  // source open at step 2. v1 archives answer none.
  W.grantEnvelope = body?.envelope ?? null;
  if (W.gate === "verified") return true;
  if (W.gate === "unavailable") {
    W.gateAck = document.getElementById("rst-gate-ack")?.checked ?? false;
    return W.gateAck;
  }
  return false;
}

async function rstOpenSource() {
  W.setName = document.getElementById("rst-set")?.value ?? W.setName;
  const chosen = document.querySelector("input[name=rst-src]:checked")?.value ?? "staging";
  W.destinationName = chosen === "staging" ? null : chosen;
  W.sourceError = null;

  if (W.source) {
    run({ command: "close_restore_source", sourceId: W.source.sourceId });
    W.source = null;
  }

  const result = await run({
    command: "open_restore_source",
    setName: W.setName,
    destinationName: W.destinationName,
    envelope: W.grantEnvelope ?? undefined,
  }, { errToast: "The source would not open" });
  if (result?.result !== "restore_source") {
    W.sourceError = "The source did not open — see the message above.";
    return false;
  }

  W.source = result;
  W.snapshot = null;
  W.tree = new Map();
  W.open = new Set([""]);
  W.marks = new Map();
  return true;
}

const rstActions = {
  async continue1() {
    // Collect ack state before the check so an already-shown warning's tick
    // counts on this click.
    W.gateAck = document.getElementById("rst-gate-ack")?.checked ?? W.gateAck;
    if (!await rstGateCheck()) { rstRender(); return; }
    // The wizard fetches its own data: S.sets/S.destinations belong to
    // other views and may be stale or empty.
    const [sets, dests] = await Promise.all([
      run({ command: "list_backup_sets" }),
      run({ command: "list_destinations" }),
    ]);
    W.sets = sets?.result === "backup_sets" ? sets.sets : [];
    W.dests = dests?.result === "destinations" ? dests.destinations : [];
    if (W.sets.length === 0) { toast("warn", "No backup sets are configured."); return; }
    if (W.prefill) {
      const row = S.snapshots.find(candidate => candidate.snapshotId === W.prefill.snapshotId);
      const set = row ? W.sets.find(candidate => candidate.id === row.backupSetId) : null;
      W.setName = set?.name ?? W.sets[0].name;
    } else {
      W.setName ??= W.sets[0].name;
    }
    W.step = 2;
    rstRender();
  },

  async continue2() {
    if (!await rstOpenSource()) { rstRender(); return; }
    if (W.source.snapshots.length === 0) {
      W.sourceError = "This source holds no backups of the set.";
      rstRender();
      return;
    }
    if (W.prefill && W.source.snapshots.some(row => row.snapshotId === W.prefill.snapshotId)) {
      W.date = new Date(Number(
        W.source.snapshots.find(row => row.snapshotId === W.prefill.snapshotId).capturedAt))
        .toISOString().slice(0, 10);
    }
    W.step = 3;
    rstRender();
  },

  continue3() {
    W.date = document.getElementById("rst-date")?.value || W.date;
    const resolved = rstResolveSnapshot();
    if (!resolved) { rstRender(); return; }
    if (W.snapshot?.snapshotId !== resolved.snapshotId) {
      W.snapshot = resolved;
      W.tree = new Map();
      W.open = new Set([""]);
      W.marks = new Map();
      if (W.prefill?.path && W.prefill.snapshotId === resolved.snapshotId) {
        W.marks.set(W.prefill.path, "on");
      }
    }
    W.step = 4;
    rstRender();
  },

  continue4() {
    if (rstCompile().length === 0) { toast("warn", "Tick at least one file or folder."); return; }
    W.step = 5;
    rstRender();
  },

  async continue5() {
    W.target = document.querySelector("input[name=rst-target]:checked")?.value ?? "folder";
    W.existing = document.querySelector("input[name=rst-existing]:checked")?.value ?? "rename";
    W.output = document.getElementById("rst-output")?.value.trim() ?? "";
    if (W.target === "folder" && !W.output) {
      toast("warn", "Choose an output folder — it is a path on the service's machine.");
      return;
    }
    W.step = 6;
    W.plan = null;
    W.result = null;
    rstRender();

    const plan = await run({
      command: "plan_restore",
      snapshotId: W.snapshot.snapshotId,
      path: null,
      source: W.source.sourceId,
      paths: rstCompile(),
    }, { errToast: "The plan refused" });
    if (!W || W.step !== 6) return;
    if (plan?.result === "restore_plan") {
      W.plan = plan;
      rstRender();
      // The typed word gates the button; re-enable path is the input event.
      document.getElementById("confirm-word")?.focus();
    }
  },
};

Object.assign(actions, {
  "rst-back"() {
    W.step = Math.max(1, W.step - 1);
    W.result = null;
    rstRender();
  },

  async "rst-continue"(el) {
    await withBusy(el, async () => {
      await rstActions[`continue${W.step}`]();
    });
  },

  "rst-open"(el) {
    const path = el.dataset.path;
    if (W.open.has(path)) W.open.delete(path);
    else W.open.add(path);
    rstRender();
  },

  async "rst-run"(el) {
    await withBusy(el, async () => {
      const result = await run({
        command: "run_restore",
        snapshotId: W.snapshot.snapshotId,
        path: null,
        outputDirectory: W.target === "original" ? "" : W.output,
        source: W.source.sourceId,
        paths: rstCompile(),
        target: W.target,
        existing: W.existing,
        inPlace: true,
      }, { errToast: "Restore refused" });
      if (result?.result === "restore") {
        W.result = result;
        rstRender();
        refreshJobs();
      }
    });
  },
});

/* ---------------------------------------------------------------- toast */

function toast(kind, text) {
  const host = document.getElementById("toasts");
  const el = document.createElement("div");
  el.className = "toast " + kind;
  el.textContent = text;
  host.appendChild(el);
  setTimeout(() => el.remove(), 7000);
}

/* ---------------------------------------------------------------- wiring */

function boot() {
  // Theme: system by default; the toggle pins an explicit choice.
  const savedTheme = localStorage.getItem("fbp-theme");
  if (savedTheme) document.documentElement.dataset.theme = savedTheme;
  document.getElementById("theme-toggle").addEventListener("click", () => {
    const current = document.documentElement.dataset.theme
      ?? (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
    const next = current === "dark" ? "light" : "dark";
    document.documentElement.dataset.theme = next;
    localStorage.setItem("fbp-theme", next);
  });

  document.addEventListener("click", event => {
    const row = event.target.closest("[data-action-row]");
    if (row && !event.target.closest("button")) {
      actions[row.dataset.actionRow]?.(row);
      return;
    }
    const el = event.target.closest("[data-action]");
    if (!el) return;
    if (U && el.dataset.action.startsWith("setup-")) { setupActions[el.dataset.action]?.(el); return; }
    actions[el.dataset.action]?.(el);
  });

  document.addEventListener("input", event => {
    // First-run setup owns the page when it is showing, so its fields are
    // matched before anything else looks at the event.
    const setupField = event.target.closest("[data-action-input^='setup-']");
    if (setupField && U) {
      setupActions[setupField.dataset.actionInput]?.(setupField);
      return;
    }

    const el = event.target.closest("[data-action-input='confirm-word']");
    if (!el) return;
    const go = document.getElementById(el.dataset.enables);
    if (go) go.disabled = el.value.trim().toLowerCase() !== el.dataset.word;
  });

  document.addEventListener("change", event => {
    if (U && event.target.matches("[data-action-change^='setup-']")) {
      setupActions[event.target.dataset.actionChange]?.(event.target);
      return;
    }

    if (event.target.matches("[data-action-change='filter-snapshots']")) {
      S.snapshotFilter = event.target.value;
      renderSnapshots();
    }

    // The restore wizard's file picker, before the set-editor guard below —
    // the two trees are separate state machines behind one delegated
    // listener.
    if (W && event.target.matches("[data-rst-mark]")) {
      rstToggle(event.target.dataset.rstMark);
      return;
    }

    // The set editor's live pieces: selection checkboxes update marks,
    // schedule edits refresh the preview, destination toggles reshape the
    // checklist.
    if (!E || !event.target.closest("#set-editor")) return;
    if (event.target.matches("[data-mark-path]")) {
      toggleMark(event.target.dataset.markPath);
      return;
    }
    if (event.target.matches("[name=sched-mode], #sched-n, #sched-unit, #sched-time")) {
      validateDraftSoon();
    }
    if (event.target.matches("[data-dest-check]")) {
      const name = event.target.dataset.destCheck;
      if (event.target.checked) E.destinations.add(name);
      else { E.destinations.delete(name); delete E.overrides[name]; }
      rerenderDestChoices();
    }
    if (event.target.matches("[data-ovr-check]")) {
      const name = event.target.dataset.ovrCheck;
      if (event.target.checked) E.overrides[name] = E.overrides[name] ?? {};
      else delete E.overrides[name];
      rerenderDestChoices();
    }
  });

  document.addEventListener("keydown", event => {
    if (event.key === "Enter" && event.target.matches("#rule-new")) {
      event.preventDefault();
      actions["rule-add-raw"]();
    }
  });

  document.addEventListener("input", event => {
    if (E && event.target.matches("#sched-n, #sched-time")) validateDraftSoon();
  });

  window.addEventListener("hashchange", route);
  route();
  renderConn();
  refreshAll();
  connectEvents();

  // The data pollers pause while sign-in (or setup) stands in place of the
  // views: every one of these commands would be refused for want of a
  // session, and a poll that can only be refused is traffic without
  // information. The describe poll below is the signed-out heartbeat — it is
  // answered without a session, and it is how the page notices the service
  // going away or its setup state changing while somebody is not signed in.
  const signedOut = () => S.signInRequired || S.setupRequired;
  setInterval(() => { if (!document.hidden && !signedOut()) refreshStatus(); }, 5000);
  setInterval(() => { if (!document.hidden && !signedOut()) refreshJobs(); }, 3000);
  setInterval(() => { if (!document.hidden && !signedOut()) { refreshSets(); refreshSnapshots(); } }, 30000);
  setInterval(() => { if (!document.hidden && signedOut()) refreshDesc(); }, 30000);
  setInterval(() => { if (!S.connected) renderConn(); }, 10000); // keep the age fresh
  document.addEventListener("visibilitychange", () => { if (!document.hidden) refreshAll(); });
}

/* ---------------------------------------------------------------- entry */
// Last, after every module-level binding above exists: boot reaches all of
// them, and a call from the top of the file lands in the temporal dead zone.

if (!token) {
  gateEl.hidden = false;
} else {
  appEl.hidden = false;
  boot();
}
