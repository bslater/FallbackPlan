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

const appEl = document.getElementById("app");
const gateEl = document.getElementById("gate");

/* ---------------------------------------------------------------- state */

const S = {
  connected: null,          // null until first answer; then true/false
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

function badge(meta, label) {
  return `<span class="badge ${meta.cls}">${meta.icon ?? ""} ${esc(label)}</span>`;
}

/* ---------------------------------------------------------------- api */

async function api(command) {
  let response;
  try {
    response = await fetch("/api/command", {
      method: "POST",
      headers: { "Content-Type": "application/json", "Authorization": "Bearer " + token },
      body: JSON.stringify(command),
    });
  } catch {
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
  return response.json();
}

async function safeJson(response) { try { return await response.json(); } catch { return null; } }

class ConsoleError extends Error {
  constructor(kind, message) { super(message); this.kind = kind; }
}

// A command whose ServiceError should surface as a toast rather than a throw.
async function run(command, { okToast, errToast } = {}) {
  try {
    const result = await api(command);
    if (result.result === "error") {
      toast("bad", `${errToast ?? "The service refused"}: ${result.message}`);
      return null;
    }
    if (okToast) toast("ok", okToast);
    return result;
  } catch (error) {
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
    if (S.view === "maintenance") renderMaintenance();
  }
}

function refreshAll() {
  refreshDesc();
  refreshSets().then(() => { refreshStatus(); refreshSnapshots(); });
  refreshJobs();
}

/* ---------------------------------------------------------------- SSE */

function connectEvents() {
  const source = new EventSource("/api/events?token=" + encodeURIComponent(token));
  source.onmessage = event => {
    const { progress } = JSON.parse(event.data);
    if (!progress) return;
    S.progress.set(progress.jobId, progress);
    if (S.view === "jobs") scheduleJobsRender();
  };
  // EventSource redials on its own; nothing to do on error — the poller is
  // what decides reachability, from actual answers.
}

let jobsRenderQueued = false;
function scheduleJobsRender() {
  if (jobsRenderQueued) return;
  jobsRenderQueued = true;
  requestAnimationFrame(() => { jobsRenderQueued = false; renderJobs(); });
}

/* ---------------------------------------------------------------- views */

const VIEWS = ["overview", "snapshots", "jobs", "notices", "config", "maintenance"];

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
     notices: renderNotices, config: renderConfig, maintenance: renderMaintenance })[S.view]();
  if (S.view === "config") refreshConfigData();
  if (S.view === "notices") refreshNotices();
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
    <p class="sub">${config ? esc(config.root) + " · " : ""}${esc(meta.blurb)}
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

    </div>`;
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
// instead of pretending to know the consequences.
function renderSetSaveConfirm(saved, preview, previewError) {
  document.getElementById("set-confirm")?.remove();
  const editor = document.getElementById("set-editor");
  if (editor) editor.hidden = true;

  const edits = [];
  if (saved.root !== E.root) edits.push(`folder: '${saved.root}' → '${E.root}'`);
  if (JSON.stringify(saved.includeRules ?? []) !== JSON.stringify(E.includeRules)) {
    edits.push(`include rules: ${(saved.includeRules ?? []).length} → ${E.includeRules.length}`);
  }
  if (JSON.stringify(saved.excludeRules ?? []) !== JSON.stringify(E.excludeRules)) {
    edits.push(`exclude rules: ${(saved.excludeRules ?? []).length} → ${E.excludeRules.length}`);
  }

  const warnings = [];
  if (saved.root !== E.root) {
    warnings.push("The folder changed: version history does not follow files across folders, and "
      + "everything under the new folder is captured as new.");
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
  const dropping = (preview?.noLongerIncluded.count ?? 0) > 0 || saved.root !== E.root;

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
      ${report.detail ? `<pre class="report">${esc(report.detail)}</pre>` : ""}` : ""}
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

const actions = {
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
        toast(clean ? "ok" : "bad",
          `Verified ${fmtCount(result.objectsChecked)} blob(s) at ${result.level} — ${fmtCount(result.failures)} failure(s).`);
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

  "restore"(el) { openRestoreDialog(el.dataset.snapshot, el.dataset.path ?? ""); },

  async "restore-plan-go"(el) {
    const snapshotId = el.dataset.snapshot;
    const path = document.getElementById("restore-path").value.trim();
    const output = document.getElementById("restore-output").value.trim();
    if (!output) { toast("warn", "Name an output directory — it is a path on the service's machine."); return; }
    await withBusy(el, async () => {
      const plan = await run({ command: "plan_restore", snapshotId, path: path || null }, { errToast: "Plan refused" });
      if (plan?.result !== "restore_plan") return;
      openDialog(renderRestoreConfirm(snapshotId, path, output, plan));
      document.getElementById("confirm-word").focus();
    });
  },

  async "restore-go"(el) {
    const { snapshot, path, output } = el.dataset;
    await withBusy(el, async () => {
      const result = await run(
        { command: "run_restore", snapshotId: snapshot, path: path || null, outputDirectory: output },
        { errToast: "Restore refused" });
      if (result?.result === "restore") {
        const clean = result.outcome === "complete";
        openDialog(`
          <h3>${clean ? "✔ Restore complete" : "◐ Restore " + esc(result.outcome)}</h3>
          <div class="plan-figures">
            <div class="fig"><b>${fmtCount(result.restored)}</b><span>files written</span></div>
            <div class="fig"><b>${fmtCount(result.failed)}</b><span>failures</span></div>
          </div>
          <p class="dlg-sub">Written on the service's machine to
          <span class="mono">${esc(result.outputDirectory)}</span> — content never crosses to this page (Q18).</p>
          <div class="dlg-actions"><button type="button" class="btn primary" data-action="close-dialog">Close</button></div>`);
      }
    });
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
      <p class="sub mono">${esc(set.root)}</p>
      <p class="sub">${set.schedule ? "runs " + esc(set.schedule) : "manual only"}
         ${set.includeRules.length ? `· ${set.includeRules.length} include rule(s)` : ""}
         ${set.excludeRules.length ? `· ${set.excludeRules.length} exclude rule(s)` : ""}</p>
      <div>${set.destinations.map(name => `<span class="chip">→ ${esc(name)}</span>`).join(" ")}</div>
      <div class="actions-row">
        <button type="button" class="btn small" data-action="cfg-edit-set" data-name="${esc(set.name)}">Edit</button>
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
  return {
    id: set?.id ?? Array.from(crypto.getRandomValues(new Uint8Array(16)))
      .map(byte => byte.toString(16).padStart(2, "0")).join(""),
    isNew: !set,
    name: set?.name ?? "",
    root: set?.root ?? "",
    schedule: set?.schedule ?? null,
    includeRules: [...(set?.includeRules ?? [])],
    excludeRules: [...(set?.excludeRules ?? [])],
    destinations: new Set(set?.destinations ?? []),
    retention: set?.retention ?? null,
    overrides: { ...(set?.destinationRetention ?? {}) },
    tree: new Map(),          // full path -> {open, listing}
    browsePath: null,         // the root picker's current directory
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
      <label class="field" for="set-root">Folder to back up <span class="plain">— on the service's machine</span></label>
      <div class="field-row">
        <input type="text" id="set-root" class="mono" value="${esc(E.root)}" spellcheck="false" placeholder="/home/you/documents">
        <button type="button" class="btn" data-action="picker-browse" data-browser="root-browser" data-input="set-root">Browse…</button>
      </div>
      <div id="root-browser" hidden></div>
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
      <label class="field">What to capture</label>
      <p class="subtle">Everything under the folder is captured unless you exclude it. "Only this" limits capture
      to the chosen subtrees — new folders outside them will not be captured.</p>
      <div id="sel-tree" class="tree"></div>
      <div class="field-row">
        <input type="text" id="rule-new" class="mono" spellcheck="false" placeholder="pattern rule, e.g.  *.iso   or   node_modules">
        <select id="rule-list"><option value="exclude">exclude</option><option value="include">include</option></select>
        <button type="button" class="btn small" data-action="rule-add-raw">Add rule</button>
      </div>
      <div id="rule-chips"></div>
      <div id="draft-defects"></div>
      ${E.isNew ? "" : `
      <div class="field-row">
        <button type="button" class="btn small" data-action="preview-changes" data-set="${esc(set.name)}">Preview changes since last backup</button>
      </div>
      <div id="change-preview"></div>`}
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
  validateDraftSoon();
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

/* ----- selection tree ----- */

function relFromFull(full) {
  let file = String(full).replaceAll("\\", "/");
  let root = String(E.root).replaceAll("\\", "/");
  if (!root.endsWith("/")) root += "/";
  return file.startsWith(root) ? file.slice(root.length) : null;
}

function ruleForPath(rel) {
  // A name carrying glob metacharacters can only be said exactly with the
  // regex form (rules-v1 has no glob escape).
  return /[*?]/.test(rel)
    ? "re:" + rel.replace(/[.^$|()[\]{}*+?\\]/g, match => "\\" + match)
    : rel;
}

function isExcludedExact(rel) { return E.excludeRules.includes(ruleForPath(rel)); }

function isExcludedByAncestor(rel) {
  return E.excludeRules.some(rule =>
    !rule.startsWith("re:") && !/[*?]/.test(rule) && rel !== rule && rel.startsWith(rule + "/"));
}

function isLimited(rel) { return E.includeRules.includes(rel + "/**"); }

async function loadTreeDir(fullPath) {
  if (E.tree.has(fullPath)) return E.tree.get(fullPath);
  const listing = await run(
    { command: "browse_folders", path: fullPath, includeFiles: true }, { errToast: "Listing refused" });
  const node = { open: true, listing: listing?.result === "folder_listing" ? listing : null };
  E.tree.set(fullPath, node);
  return node;
}

function renderTree() {
  const host = document.getElementById("sel-tree");
  if (!host || !E) return;

  if (!E.root.trim()) {
    host.innerHTML = `<p class="subtle">Choose the folder first; its contents appear here.</p>`;
    return;
  }

  host.innerHTML = `<div class="tree-inner">${renderTreeDir(E.root.trim(), 0)}</div>`;
}

function renderTreeDir(fullPath, depth) {
  const node = E.tree.get(fullPath);
  if (!node) {
    // Root not loaded yet: kick the load and show a placeholder.
    loadTreeDir(fullPath).then(renderTree);
    return `<p class="subtle">Loading…</p>`;
  }

  if (!node.listing) return `<p class="subtle">This folder would not list.</p>`;

  const rows = [];
  for (const folder of node.listing.folders) {
    const rel = relFromFull(folder.path);
    if (rel === null) continue;
    const excluded = isExcludedExact(rel);
    const inherited = !excluded && isExcludedByAncestor(rel);
    const limited = isLimited(rel);
    const child = E.tree.get(folder.path);

    rows.push(`
      <div class="tree-row ${excluded || inherited ? "off" : ""}">
        ${"<span class='indent'></span>".repeat(depth)}
        <button type="button" class="twist" data-action="sel-open" data-path="${esc(folder.path)}">${child?.open ? "▾" : "▸"}</button>
        <span class="kind-ico">📁</span>
        <span class="tree-name">${esc(folder.name)}</span>
        ${limited ? `<span class="badge accent">only this</span>` : ""}
        ${excluded ? `<span class="badge warn">excluded</span>` : inherited ? `<span class="detail">excluded with parent</span>` : ""}
        <span class="tree-actions">
          ${inherited ? "" : `<button type="button" class="btn tiny" data-action="sel-exclude" data-rel="${esc(rel)}">${excluded ? "include again" : "exclude"}</button>`}
          ${excluded || inherited ? "" : `<button type="button" class="btn tiny" data-action="sel-limit" data-rel="${esc(rel)}">${limited ? "not only this" : "only this"}</button>`}
        </span>
      </div>
      ${child?.open ? renderTreeDir(folder.path, depth + 1) : ""}`);
  }

  for (const file of node.listing.files ?? []) {
    const rel = relFromFull(
      (node.listing.path.replaceAll("\\", "/").endsWith("/") ? node.listing.path : node.listing.path + "/") + file.name);
    if (rel === null) continue;
    const excluded = isExcludedExact(rel);
    const inherited = !excluded && isExcludedByAncestor(rel);
    rows.push(`
      <div class="tree-row ${excluded || inherited ? "off" : ""}">
        ${"<span class='indent'></span>".repeat(depth)}
        <span class="twist"></span>
        <span class="kind-ico">📄</span>
        <span class="tree-name">${esc(file.name)}</span>
        <span class="detail">${fmtBytes(file.length)}</span>
        ${excluded ? `<span class="badge warn">excluded</span>` : ""}
        <span class="tree-actions">
          ${inherited ? "" : `<button type="button" class="btn tiny" data-action="sel-exclude" data-rel="${esc(rel)}">${excluded ? "include again" : "exclude"}</button>`}
        </span>
      </div>`);
  }

  return rows.join("") || `<p class="subtle">${"<span class='indent'></span>".repeat(depth)}(empty)</p>`;
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
    E.includeRules.map(rule => chip(rule, "include")).join("")
    + E.excludeRules.map(rule => chip(rule, "exclude")).join("")
    || `<span class="subtle">No rules — everything under the folder is captured.</span>`;
}

let draftTimer = null;
function validateDraftSoon() {
  clearTimeout(draftTimer);
  draftTimer = setTimeout(async () => {
    if (!E) return;
    const schedule = scheduleFromEditor();
    const result = await run({
      command: "validate_set_draft",
      schedule,
      includeRules: E.includeRules,
      excludeRules: E.excludeRules,
    });
    if (result?.result !== "set_draft_validation" || !E) return;

    const defects = document.getElementById("draft-defects");
    if (defects) {
      defects.innerHTML = result.defects.length
        ? `<ul class="warnings">${result.defects.map(defect => `<li>${esc(defect)}</li>`).join("")}</ul>` : "";
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
  },

  "sel-exclude"(el) {
    const rule = ruleForPath(el.dataset.rel);
    E.excludeRules = E.excludeRules.includes(rule)
      ? E.excludeRules.filter(candidate => candidate !== rule)
      : [...E.excludeRules, rule];
    renderTree(); renderRuleChips(); validateDraftSoon();
  },

  "sel-limit"(el) {
    const rule = el.dataset.rel + "/**";
    E.includeRules = E.includeRules.includes(rule)
      ? E.includeRules.filter(candidate => candidate !== rule)
      : [...E.includeRules, rule];
    renderTree(); renderRuleChips(); validateDraftSoon();
  },

  "rule-remove"(el) {
    const list = el.dataset.list === "exclude" ? "excludeRules" : "includeRules";
    E[list] = E[list].filter(rule => rule !== el.dataset.rule);
    renderTree(); renderRuleChips(); validateDraftSoon();
  },

  "rule-add-raw"() {
    const input = document.getElementById("rule-new");
    const rule = input.value.trim();
    if (!rule) return;
    const list = document.getElementById("rule-list").value === "exclude" ? "excludeRules" : "includeRules";
    if (!E[list].includes(rule)) E[list] = [...E[list], rule];
    input.value = "";
    renderTree(); renderRuleChips(); validateDraftSoon();
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
    if (el.dataset.input === "set-root" && E) {
      // The set editor's root drives more than the input: the selection tree
      // starts over from the new parent, and validation re-judges the rules.
      E.root = el.dataset.path;
      E.tree = new Map();
      renderTree(); validateDraftSoon();
    }
  },

  async "preview-changes"(el) {
    const host = document.getElementById("change-preview");
    if (!host) return;
    const root = document.getElementById("set-root").value.trim();
    if (!root) { toast("warn", "Choose a folder first."); return; }

    await withBusy(el, async () => {
      host.innerHTML = `<p class="subtle">Walking the source…</p>`;
      const result = await run({
        command: "preview_set_changes",
        setName: el.dataset.set,
        root,
        includeRules: E.includeRules,
        excludeRules: E.excludeRules,
      }, { errToast: "The service could not compare" });
      if (!result || result.result !== "set_change_preview") { host.innerHTML = ""; return; }

      const report = comparisonReport(result);
      host.innerHTML = `
        <p class="subtle">${esc(report.summary)}</p>
        ${report.detail ? `<pre class="report">${esc(report.detail)}</pre>` : ""}`;
    });
  },

  async "set-save"(el) {
    E.name = document.getElementById("set-name").value.trim();
    E.root = document.getElementById("set-root").value.trim();
    if (!E.name || !E.root) { toast("warn", "A set needs a name and a folder."); return; }

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

    const payload = {
      id: E.id, name: E.name, root: E.root,
      schedule: scheduleFromEditor(),
      includeRules: E.includeRules, excludeRules: E.excludeRules,
      destinations,
      retention,           // all-null clears; values set — always explicit from this editor
      destinationRetention: overrides,
    };

    // A material edit — the root or the rules — changes what future backups
    // hold: files can silently leave the backup. So it is never applied on
    // one click: step one compares the draft against the last backup and
    // shows the consequences; only the explicit Apply in that step saves
    // (ADR-0038). Everything else (name, schedule, retention) saves directly.
    const saved = E.isNew ? null : S.sets.find(set => set.id === E.id);
    const material = saved && (
      saved.root !== E.root
      || JSON.stringify(saved.includeRules ?? []) !== JSON.stringify(E.includeRules)
      || JSON.stringify(saved.excludeRules ?? []) !== JSON.stringify(E.excludeRules));

    if (!material) {
      await withBusy(el, () => applySetUpsert(payload));
      return;
    }

    await withBusy(el, async () => {
      let preview = null;
      let previewError = null;
      try {
        const result = await api({
          command: "preview_set_changes",
          setName: saved.name, root: E.root,
          includeRules: E.includeRules, excludeRules: E.excludeRules,
        });
        if (result.result === "set_change_preview") preview = result;
        else previewError = result.message ?? "the service answered unexpectedly";
      } catch (error) {
        previewError = error.message;
      }

      E.pendingSave = payload;
      renderSetSaveConfirm(saved, preview, previewError);
    });
  },

  "set-save-back"() {
    document.getElementById("set-confirm")?.remove();
    const editor = document.getElementById("set-editor");
    if (editor) editor.hidden = false;
    E.pendingSave = null;
  },

  async "set-save-apply"(el) {
    const payload = E.pendingSave;
    if (!payload) return;
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

/* ----- restore dialog ----- */

function openRestoreDialog(snapshotId, path) {
  openDialog(`
    <h3>Restore</h3>
    <p class="dlg-sub">The service writes the files on <b>its</b> machine; this page is told counts and a
    path, never sent content. The plan is shown before anything is written.</p>
    <label class="field" for="restore-path">Path within the snapshot <span class="plain">(empty restores everything)</span></label>
    <input type="text" id="restore-path" class="mono" value="${esc(path)}" spellcheck="false">
    <label class="field" for="restore-output">Output directory — on the service's machine</label>
    <div class="field-row">
      <input type="text" id="restore-output" class="mono" placeholder="/home/you/restore-2026-08-17" spellcheck="false">
      <button type="button" class="btn" data-action="picker-browse" data-browser="restore-browser" data-input="restore-output">Browse…</button>
    </div>
    <div id="restore-browser" hidden></div>
    <div class="dlg-actions">
      <button type="button" class="btn" data-action="close-dialog">Cancel</button>
      <button type="button" class="btn primary" data-action="restore-plan-go" data-snapshot="${esc(snapshotId)}">Plan restore</button>
    </div>`);
  document.getElementById("restore-output").focus();
}

function renderRestoreConfirm(snapshotId, path, output, plan) {
  const missing = plan.missingObjects?.length ?? 0;
  return `
    <h3>Confirm restore</h3>
    <p class="dlg-sub">${path ? `<span class="mono">${esc(path)}</span> from snapshot` : "The whole snapshot"}
      <span class="mono">${esc(snapshotId.slice(0, 16))}…</span> →
      <span class="mono">${esc(output)}</span></p>
    <div class="plan-figures">
      <div class="fig"><b>${fmtCount(plan.files)}</b><span>files</span></div>
      <div class="fig"><b>${fmtBytes(plan.bytes)}</b><span>to write</span></div>
      <div class="fig"><b>${fmtCount(missing)}</b><span>missing objects</span></div>
    </div>
    ${missing ? `<ul class="warnings"><li>${fmtCount(missing)} object(s) the plan needs cannot be found — those files will fail. The plan reports absence; verify reports damage.</li></ul>` : ""}
    <label class="field" for="confirm-word">Type <b>restore</b> to confirm</label>
    <input type="text" id="confirm-word" class="confirm-word" autocomplete="off" spellcheck="false"
           data-action-input="confirm-word" data-word="restore" data-enables="restore-confirm-go">
    <div class="dlg-actions">
      <button type="button" class="btn" data-action="close-dialog">Cancel</button>
      <button type="button" class="btn danger" id="restore-confirm-go" data-action="restore-go" disabled
              data-snapshot="${esc(snapshotId)}" data-path="${esc(path)}" data-output="${esc(output)}">Restore</button>
    </div>`;
}

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
    if (el) actions[el.dataset.action]?.(el);
  });

  document.addEventListener("input", event => {
    const el = event.target.closest("[data-action-input='confirm-word']");
    if (!el) return;
    const go = document.getElementById(el.dataset.enables);
    if (go) go.disabled = el.value.trim().toLowerCase() !== el.dataset.word;
  });

  document.addEventListener("change", event => {
    if (event.target.matches("[data-action-change='filter-snapshots']")) {
      S.snapshotFilter = event.target.value;
      renderSnapshots();
    }

    // The set editor's live pieces: schedule edits refresh the preview,
    // destination toggles reshape the checklist.
    if (!E || !event.target.closest("#set-editor")) return;
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

  setInterval(() => { if (!document.hidden) refreshStatus(); }, 5000);
  setInterval(() => { if (!document.hidden) refreshJobs(); }, 3000);
  setInterval(() => { if (!document.hidden) { refreshSets(); refreshSnapshots(); } }, 30000);
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
