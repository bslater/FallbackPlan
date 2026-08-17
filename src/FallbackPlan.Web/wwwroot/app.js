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
  busy: new Set(),          // action keys currently running
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
  if (S.view === "notices") renderNotices();
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

const VIEWS = ["overview", "snapshots", "jobs", "notices", "maintenance"];

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
     notices: renderNotices, maintenance: renderMaintenance })[S.view]();
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
        Sets, their destinations and their schedules live in the service's <code>config.json</code>.
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

function renderNotices() {
  const el = document.getElementById("view-notices");
  const notices = S.status?.notices ?? [];
  el.innerHTML = `
    <h2>Notices</h2>
    <p class="view-sub">Durable events awaiting a person — a peering ended, terms narrowed, a hold outstayed its deferral. They stay until acknowledged.</p>
    ${notices.length === 0
      ? `<div class="card empty"><span class="big">✅</span>Nothing awaits you.</div>`
      : `<div class="card notice-list">${notices.map(n => `<div class="notice"><span class="text">${esc(n)}</span></div>`).join("")}</div>
         <p class="view-sub mt-s">Acknowledge with <code>fallbackplan-agent notices --ack &lt;id&gt;</code> — the command contract has no acknowledge verb yet.</p>`}`;
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

function closeDialog() { dialog.close(); dialog.innerHTML = ""; }

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

  "close-dialog"() { closeDialog(); },
};

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

  const icon = { directory: "📁", file: "📄", symlink: "🔗", special: "⚙️" };
  const rows = result.entries.map(entry => {
    const childPath = path ? path + "/" + entry.name : entry.name;
    const isDir = entry.kind === "directory";
    return `<tr class="${isDir ? "rowlink" : ""}" ${isDir ? `data-action-row="browse-to" data-snapshot="${esc(snapshotId)}" data-path="${esc(childPath)}"` : ""}>
      <td><span class="kind-ico">${icon[entry.kind] ?? "•"}</span> ${esc(entry.name)}${entry.kind === "symlink" ? ` <span class="detail">symlink</span>` : ""}</td>
      <td class="num">${entry.kind === "file" ? fmtBytes(entry.length) : ""}</td>
      <td><button type="button" class="btn small" data-action="restore" data-snapshot="${esc(snapshotId)}" data-path="${esc(childPath)}">Restore…</button></td>
    </tr>`;
  }).join("");

  openDialog(`
    <h3>Browse snapshot</h3>
    <p class="dlg-sub mono">${esc(snapshotId.slice(0, 24))}…</p>
    <div class="crumbs">${crumbs.join("")}</div>
    <div class="table-wrap"><table class="data">
      <tbody>${rows || `<tr><td class="detail">This directory is empty.</td></tr>`}</tbody>
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
    <input type="text" id="restore-output" class="mono" placeholder="/home/you/restore-2026-08-17" spellcheck="false">
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
