/// <summary>
/// The dashboard's main page, served by the loopback dashboard listener at / and /ui. Machines come
/// first, as a panel of ports: each shows whether it is on the line, what it really is (host,
/// account, system) and whether its client is current. Under them a command line runs something on
/// any machine directly, and the activity log below can be narrowed to one machine with a click.
///
/// It carries no data itself and reads everything from the JSON endpoints. Everything on it is built
/// from text nodes — no markup is ever assembled from relay data, all of which comes from machines
/// someone else controls.
/// </summary>
public static class StatusPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>RemoteCmd relay</title>
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Crect x='2' y='4' width='12' height='8' rx='2' fill='none' stroke='%230e7c86' stroke-width='2'/%3E%3Ccircle cx='6' cy='8' r='1.3' fill='%230e7c86'/%3E%3Ccircle cx='10' cy='8' r='1.3' fill='%230e7c86'/%3E%3C/svg%3E">
<style>
  /* PATCH PANEL — machines are ports on a panel, each with its own status light. Teal: on the line.
     Cobalt: data moving. Ochre: needs an update. Brick: lost or failed. Nothing else carries colour,
     so a glance down the panel shows only what needs attention. */
  :root {
    color-scheme: light dark;
    --bg:#eef1f4; --panel:#fff; --raise:#e3e8ee; --grid:#cfd6de; --soft:#e4e9ee;
    --fg:#1b2430; --dim:#5b6878; --faint:#8d98a6;
    --on:#0e7c86; --xfer:#2f5bea; --warn:#a8670f; --bad:#c2412d;
    --onText:#fff;
    --mono: ui-monospace, "Cascadia Mono", "SF Mono", Menlo, Consolas, monospace;
    --face: "Segoe UI Variable Text", "Segoe UI", -apple-system, BlinkMacSystemFont, Roboto, system-ui, sans-serif;
    --gut: clamp(14px, 2vw, 32px);
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --bg:#13181f; --panel:#1a212a; --raise:#222b36; --grid:#2c3642; --soft:#222a34;
      --fg:#e4e9ef; --dim:#8b97a6; --faint:#5c6878;
      --on:#3cc4c9; --xfer:#7c9bff; --warn:#e2b04a; --bad:#ff7a63;
      --onText:#0b2224;
    }
  }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--fg); font:14px/1.45 var(--face); }
  button, input, select { font:inherit; color:inherit; }
  :focus-visible { outline:2px solid var(--on); outline-offset:2px; }
  [hidden] { display:none !important; }
  .mono { font-family:var(--mono); }
  .dim { color:var(--dim); }

  /* ---------- header ---------- */
  header { position:sticky; top:0; z-index:20; display:flex; align-items:center; gap:14px; flex-wrap:wrap;
           padding:10px var(--gut); background:var(--bg); border-bottom:1px solid var(--grid); }
  .brand { font-weight:700; font-size:16px; letter-spacing:-.01em; }
  #relayLine { color:var(--dim); font-size:13px; display:flex; gap:12px; flex-wrap:wrap; }
  #relayLine .warn { color:var(--warn); font-weight:600; }
  #relayLine .bad { color:var(--bad); font-weight:600; }
  .grow { flex:1; }
  .btn { padding:7px 12px; border:1px solid var(--grid); border-radius:6px; background:var(--panel); color:var(--fg);
         text-decoration:none; cursor:pointer; white-space:nowrap; }
  .btn:hover { border-color:var(--dim); }
  .btn.go { background:var(--on); border-color:var(--on); color:var(--onText); font-weight:600; }
  #beat { width:9px; height:9px; border-radius:50%; background:var(--faint); }
  #beat.live { background:var(--on); }
  #beat.dead { background:var(--bad); }
  #agentBar { display:block; padding:11px var(--gut); background:var(--on); color:var(--onText); font-weight:600;
              text-decoration:none; }

  main { padding:18px var(--gut) 48px; display:grid; gap:26px; }
  h2 { margin:0; font-size:17px; font-weight:650; }
  .secHead { display:flex; align-items:baseline; gap:12px; flex-wrap:wrap; margin-bottom:10px; }
  .secHead .dim { font-size:13px; }

  /* ---------- machines: the panel ---------- */
  .seg { display:inline-flex; border:1px solid var(--grid); border-radius:6px; overflow:hidden; }
  .seg button { border:0; background:var(--panel); padding:4px 10px; cursor:pointer; font-size:13px; }
  .seg button[aria-pressed="true"] { background:var(--fg); color:var(--bg); }
  #ports { display:grid; gap:12px; grid-template-columns:repeat(auto-fill, minmax(270px, 1fr)); }
  .port { position:relative; background:var(--panel); border:1px solid var(--grid); border-radius:8px;
          padding:12px 14px 12px 20px; display:grid; gap:2px; cursor:pointer; text-align:left; }
  .port::before { content:""; position:absolute; left:0; top:10px; bottom:10px; width:5px; border-radius:0 3px 3px 0;
                  background:var(--faint); }
  .port.on::before { background:var(--on); }
  .port.busy::before { background:var(--xfer); }
  .port.lost::before { background:var(--bad); }
  .port.lost { opacity:.72; }
  .port.picked { border-color:var(--fg); box-shadow:inset 0 0 0 1px var(--fg); }
  .port .alias { font-size:16px; font-weight:650; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; padding-right:70px; }
  .port .host { font-family:var(--mono); font-size:12.5px; color:var(--dim); overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .port .sys { font-size:12.5px; color:var(--dim); overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .port .foot { display:flex; gap:10px; align-items:center; margin-top:8px; font-size:12.5px; color:var(--dim); flex-wrap:wrap; }
  .port .state { position:absolute; top:12px; right:12px; font-size:12px; font-weight:600; color:var(--dim); }
  .port.on .state { color:var(--on); } .port.busy .state { color:var(--xfer); } .port.lost .state { color:var(--bad); }
  .ver { font-family:var(--mono); font-size:12px; padding:1px 6px; border-radius:4px; background:var(--raise); color:var(--dim); }
  .ver.old { background:color-mix(in oklab, var(--warn) 18%, transparent); color:var(--warn); font-weight:600; }
  .empty { padding:22px; border:1px dashed var(--grid); border-radius:8px; color:var(--dim); }
  .empty a { color:var(--on); font-weight:600; }

  /* ---------- run a command ---------- */
  #run { background:var(--panel); border:1px solid var(--grid); border-radius:8px; padding:12px 14px; display:grid; gap:10px; }
  #runForm { display:grid; grid-template-columns: minmax(140px, 200px) 1fr 92px auto; gap:8px; }
  @media (max-width: 760px) { #runForm { grid-template-columns:1fr; } }
  #runForm select, #runForm input { padding:8px 10px; border:1px solid var(--grid); border-radius:6px; background:var(--bg); min-width:0; }
  #cmd { font-family:var(--mono); font-size:13px; }
  #runOut { margin:0; }
  #runMeta { font-size:13px; color:var(--dim); display:flex; gap:12px; align-items:center; }
  #runMeta .bad { color:var(--bad); font-weight:600; }
  pre { margin:0; padding:10px 12px; background:var(--raise); border-radius:6px; font:12.5px/1.5 var(--mono);
        white-space:pre-wrap; word-break:break-word; max-height:42vh; overflow:auto; }

  /* ---------- activity + numbers ---------- */
  .lower { display:grid; gap:22px; grid-template-columns:minmax(0, 1fr); align-items:start; }
  @media (min-width:1100px) { .lower { grid-template-columns:minmax(0, 1fr) 300px; } }
  #filters { display:flex; gap:8px; flex-wrap:wrap; align-items:center; }
  #filters input, #filters select { padding:6px 9px; border:1px solid var(--grid); border-radius:6px; background:var(--panel); }
  #q { min-width:220px; }
  .chip { display:inline-flex; gap:6px; align-items:center; padding:4px 8px 4px 10px; border-radius:999px;
          background:var(--fg); color:var(--bg); font-size:13px; }
  .chip button { border:0; background:none; color:inherit; cursor:pointer; padding:0 2px; font-size:15px; line-height:1; }
  #log { background:var(--panel); border:1px solid var(--grid); border-radius:8px; overflow:hidden; }
  table { width:100%; border-collapse:collapse; table-layout:fixed; }
  td { padding:7px 10px; border-bottom:1px solid var(--soft); vertical-align:top; overflow:hidden; text-overflow:ellipsis; }
  tr:last-child td { border-bottom:0; }
  td.t { width:92px; white-space:nowrap; color:var(--dim); font-family:var(--mono); font-size:12.5px; }
  td.k { width:96px; font-size:12.5px; color:var(--dim); white-space:nowrap; }
  td.k::before { content:""; display:inline-block; width:8px; height:8px; border-radius:50%; margin-right:7px; background:var(--faint); }
  tr.exec td.k::before { background:var(--on); } tr.xfer td.k::before { background:var(--xfer); }
  tr.bad td.k::before { background:var(--bad); } tr.bad td.k { color:var(--bad); }
  td.m { width:150px; white-space:nowrap; font-weight:600; }
  td.d { font-family:var(--mono); font-size:12.5px; white-space:pre-wrap; word-break:break-word; }
  tr[data-id] { cursor:pointer; }
  tr[data-id]:hover td { background:var(--raise); }
  @media (max-width: 760px) { td.m { width:96px; } td.k { width:70px; } }
  #more { display:block; width:100%; border:0; border-top:1px solid var(--soft); background:none; padding:9px; cursor:pointer; color:var(--dim); }

  #numbers { background:var(--panel); border:1px solid var(--grid); border-radius:8px; padding:14px; display:grid; gap:12px; }
  .num { display:flex; justify-content:space-between; align-items:baseline; gap:8px; }
  .num b { font-size:20px; font-weight:650; font-variant-numeric:tabular-nums; }
  .num span { color:var(--dim); font-size:13px; }
  .num.alarm b { color:var(--bad); }
  #spark { width:100%; height:44px; display:block; }
  #spark rect { fill:var(--on); }
  #spark rect.bad { fill:var(--bad); }

  /* ---------- command detail ---------- */
  #detail { position:fixed; top:0; right:0; bottom:0; width:min(680px, 100vw); z-index:40; background:var(--panel);
            border-left:1px solid var(--grid); padding:16px 18px 28px; overflow:auto; display:grid; gap:12px; align-content:start; }
  #shade { position:fixed; inset:0; background:rgba(10,14,20,.38); z-index:39; }
  .kv { display:grid; grid-template-columns:auto 1fr; gap:4px 14px; font-size:13px; }
  .kv span { color:var(--dim); }

  @media (prefers-reduced-motion: no-preference) {
    .port.flash { animation:flash 900ms ease-out; }
    @keyframes flash { from { box-shadow:0 0 0 3px color-mix(in oklab, var(--on) 45%, transparent); } to { box-shadow:none; } }
  }
</style>
</head>
<body>

<header>
  <span id="beat" title="refresh"></span>
  <span class="brand">RemoteCmd</span>
  <span id="relayLine"></span>
  <span class="grow"></span>
  <a class="btn go" href="/manage">Pair a machine</a>
  <a class="btn" href="/manage#access">Manage access</a>
</header>
<a id="agentBar" href="/manage#agents" hidden></a>

<main>
  <section>
    <div class="secHead">
      <h2>Machines</h2>
      <span class="dim" id="machCount"></span>
      <span class="grow"></span>
      <div class="seg" role="group" aria-label="show machines">
        <button type="button" data-show="all" aria-pressed="true">All</button>
        <button type="button" data-show="online" aria-pressed="false">Online</button>
        <button type="button" data-show="update" aria-pressed="false">Need update</button>
      </div>
    </div>
    <div id="ports"></div>
    <div class="empty" id="noMachines" hidden>No machine has connected yet. <a href="/manage">Pair the first one</a> — it takes one command on that machine.</div>
  </section>

  <section id="run">
    <div class="secHead" style="margin:0"><h2>Run a command</h2><span class="dim">PowerShell on Windows, bash elsewhere</span></div>
    <form id="runForm">
      <select id="target" aria-label="machine"></select>
      <input id="cmd" placeholder="e.g. Get-Volume  ·  df -h  ·  uptime" autocomplete="off" spellcheck="false" aria-label="command">
      <select id="tmo" aria-label="time limit">
        <option value="30">30 s</option><option value="60" selected>1 min</option><option value="300">5 min</option><option value="1800">30 min</option>
      </select>
      <button class="btn go" type="submit" id="runBtn">Run</button>
    </form>
    <div id="runMeta" hidden></div>
    <pre id="runOut" hidden></pre>
  </section>

  <div class="lower">
    <section>
      <div class="secHead">
        <h2>Activity</h2>
        <span id="pickedChip"></span>
        <span class="grow"></span>
        <div id="filters">
          <input id="q" type="search" placeholder="Search commands, paths, messages" aria-label="search activity">
          <select id="kind" aria-label="event type">
            <option value="">Everything</option><option value="exec">Commands</option><option value="xfer">Transfers</option>
            <option value="conn">Connections</option><option value="bad">Problems</option><option value="admin">Pairing and access</option>
          </select>
        </div>
      </div>
      <div id="log"><table><tbody id="rows"></tbody></table><button id="more" type="button" hidden>Show older</button></div>
      <div class="empty" id="noEvents" hidden>Nothing matches.</div>
    </section>

    <aside id="numbers" aria-label="relay numbers"></aside>
  </div>
</main>

<div id="shade" hidden></div>
<aside id="detail" role="dialog" aria-modal="true" aria-labelledby="dTitle" hidden>
  <div class="secHead" style="margin:0"><h2 id="dTitle">Command</h2><span class="grow"></span><button class="btn" id="dClose" type="button">Close</button></div>
  <div class="kv" id="dKv"></div>
  <div id="dBody"></div>
</aside>

<script>
const $ = (id) => document.getElementById(id);
const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = String(text); return e; };
const flat = (s) => String(s ?? '').replace(/\s+/g, ' ').trim();
const store = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { localStorage.setItem(k, v); } catch { /* private window */ } },
};

const state = { show: 'all', picked: null, q: '', kind: '', limit: 60, clients: [], events: [], info: null };

async function api(path, init) {
  const res = await fetch(path, { ...init, signal: AbortSignal.timeout(init?.timeout ?? 8000) });
  if (res.status === 401) { location.href = '/login'; throw new Error('signed out'); }
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json();
}

function ago(s) {
  if (s < 0) return 'never';
  if (s < 60) return `${s}s ago`;
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  return `${Math.floor(s / 86400)} d ago`;
}
function dur(s) {
  if (s < 60) return `${s}s`;
  if (s < 3600) return `${Math.floor(s / 60)}m`;
  if (s < 86400) return `${Math.floor(s / 3600)}h ${Math.floor(s % 3600 / 60)}m`;
  return `${Math.floor(s / 86400)}d ${Math.floor(s % 86400 / 3600)}h`;
}
function bytes(v) {
  if (v < 1024) return `${v} B`;
  if (v < 1048576) return `${(v / 1024).toFixed(1)} kB`;
  if (v < 1073741824) return `${(v / 1048576).toFixed(1)} MB`;
  return `${(v / 1073741824).toFixed(2)} GB`;
}

/** The system in words people use: the client reports the runtime's own description. */
function system(os) {
  if (!os) return '';
  const s = flat(os);
  const arch = /arm64/i.test(s) ? ' (ARM)' : '';
  let m;
  if ((m = s.match(/Windows\s+([\d.]+)/i))) {
    const build = Number(m[1].split('.')[2] || 0);
    return (build >= 22000 ? 'Windows 11' : 'Windows 10') + ` build ${build}` + arch;
  }
  if ((m = s.match(/Darwin\s+(\d+)/i))) return `macOS ${Number(m[1]) - 9}` + arch;
  if (/android/i.test(s)) return 'Android' + arch;
  if (/linux|ubuntu|debian|fedora|alpine/i.test(s)) return s.split('#')[0].slice(0, 48);
  return s.slice(0, 48);
}

function machineState(c) {
  if (!c.connected) return { cls: 'lost', text: c.secondsAgo < 0 ? 'never seen' : `lost ${ago(c.secondsAgo)}` };
  if (c.state === 'upload' || c.state === 'download') return { cls: 'busy', text: c.state === 'upload' ? 'receiving file' : 'sending file' };
  if (c.running > 0) return { cls: 'busy', text: `running ${c.running}` };
  return { cls: 'on', text: 'online' };
}

// ---------- machines ----------
let lastPortsSig = '';
function renderPorts() {
  const all = state.clients;
  const shown = all.filter((c) => state.show === 'all' || (state.show === 'online' ? c.connected : c.outdated));
  const online = all.filter((c) => c.connected).length;
  const outdated = all.filter((c) => c.outdated).length;
  $('machCount').textContent = all.length ? `${online} of ${all.length} online${outdated ? `, ${outdated} need an update` : ''}` : '';
  $('noMachines').hidden = all.length > 0;

  const sig = JSON.stringify([state.show, state.picked, shown.map((c) => [c.id, c.name, c.host, c.user, c.os, c.version, c.outdated, c.connected, c.state, c.running, c.queued])]);
  if (sig === lastPortsSig) { refreshSeen(); return; }
  lastPortsSig = sig;

  $('ports').replaceChildren(...shown.map((c) => {
    const st = machineState(c);
    const port = el('button', `port ${st.cls}${state.picked === c.name ? ' picked' : ''}`);
    port.type = 'button';
    port.dataset.name = c.name;
    port.append(el('span', 'state', st.text), el('span', 'alias', c.name));
    const who = [c.host, c.user].filter(Boolean).join(' \\ ');
    port.append(el('span', 'host', who || c.ip || '—'));
    port.append(el('span', 'sys', system(c.os) || 'client too old to report its system'));
    const foot = el('span', 'foot');
    foot.append(el('span', c.outdated ? 'ver old' : 'ver', c.version ? `v${c.version}${c.outdated ? ' — update' : ''}` : 'version ?'));
    const seen = el('span', 'seen', c.connected ? `seen ${ago(c.secondsAgo)}` : '');
    seen.dataset.id = c.id;
    foot.append(seen);
    if (c.queued) foot.append(el('span', '', `${c.queued} queued`));
    foot.append(el('span', '', `${c.served} done`));
    port.append(foot);
    port.title = `${flat(c.name)}\nhost ${flat(c.host || '?')}\naccount ${flat(c.user || '?')}\nsystem ${flat(c.os || '?')}\naddress ${c.ip || '?'}\ncredential ${flat(c.token)}\nconnected for ${dur(c.connectedForSeconds)}\n\nClick to show only its activity`;
    port.addEventListener('click', () => pick(state.picked === c.name ? null : c.name));
    return port;
  }));
  syncTargets();
}

function refreshSeen() {
  for (const node of document.querySelectorAll('.port .seen')) {
    const c = state.clients.find((x) => x.id === node.dataset.id);
    if (c && c.connected) node.textContent = `seen ${ago(c.secondsAgo)}`;
  }
}

function pick(name) {
  state.picked = name;
  const chip = $('pickedChip');
  chip.replaceChildren();
  if (name) {
    const c = el('span', 'chip', name);
    const x = el('button', '', '×');
    x.type = 'button';
    x.setAttribute('aria-label', 'show all machines');
    x.addEventListener('click', () => pick(null));
    c.append(x);
    chip.append(c);
    $('target').value = name;
  }
  lastPortsSig = '';
  renderPorts();
  renderLog();
}

for (const b of document.querySelectorAll('.seg button')) {
  b.addEventListener('click', () => {
    state.show = b.dataset.show;
    for (const o of document.querySelectorAll('.seg button')) o.setAttribute('aria-pressed', String(o === b));
    renderPorts();
  });
}

// ---------- run a command ----------
function syncTargets() {
  const sel = $('target');
  const current = sel.value || store.get('rcmd-target') || '';
  const names = state.clients.filter((c) => c.connected).map((c) => c.name);
  const sig = names.join('\n');
  if (sel.dataset.sig === sig) return;
  sel.dataset.sig = sig;
  sel.replaceChildren(...(names.length ? names : ['no machine online']).map((n) => { const o = el('option', '', n); o.value = names.length ? n : ''; return o; }));
  if (names.includes(current)) sel.value = current;
  $('runBtn').disabled = names.length === 0;
}

$('runForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  const target = $('target').value;
  const command = $('cmd').value.trim();
  if (!target || !command) return;
  store.set('rcmd-target', target);
  const timeoutSeconds = Number($('tmo').value);
  const meta = $('runMeta'), out = $('runOut'), btn = $('runBtn');
  btn.disabled = true;
  btn.textContent = 'Running…';
  meta.hidden = false;
  meta.replaceChildren(el('span', '', `${target}: running…`));
  out.hidden = true;
  const started = performance.now();
  try {
    const r = await api(`/api/exec?client=${encodeURIComponent(target)}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-RemoteCmd': '1' },
      body: JSON.stringify({ command, timeoutSeconds }),
      timeout: (timeoutSeconds + 15) * 1000,
    });
    const secs = ((performance.now() - started) / 1000).toFixed(1);
    meta.replaceChildren(el('span', r.exitCode === 0 ? '' : 'bad', `exit ${r.exitCode}`), el('span', '', `${target}, ${secs} s`));
    const copy = el('button', 'btn', 'Copy output');
    copy.type = 'button';
    copy.addEventListener('click', () => navigator.clipboard.writeText(r.output));
    meta.append(copy);
    out.textContent = r.output || '(no output)';
    out.hidden = false;
  } catch (err) {
    meta.replaceChildren(el('span', 'bad', `Not run: ${err.message}`));
  } finally {
    btn.disabled = false;
    btn.textContent = 'Run';
    tick();
  }
});

// ---------- activity ----------
const KIND = {
  exec: ['exec', 'Command'], timeout: ['bad', 'Timed out'], upload: ['xfer', 'Upload'], download: ['xfer', 'Download'],
  connect: ['conn', 'Connected'], gc: ['conn', 'Cleanup'], auth: ['bad', 'Refused'], pair: ['admin', 'Pairing'],
  admin: ['admin', 'Access'], oauth: ['admin', 'AI agent'],
};

function renderLog() {
  const q = state.q.toLowerCase();
  const list = state.events
    .filter((e) => !state.picked || e.client === state.picked)
    .filter((e) => !state.kind || (KIND[e.kind]?.[0] ?? 'conn') === state.kind || (state.kind === 'bad' && /failed|timeout/.test(e.message)))
    .filter((e) => !q || `${e.client} ${e.message}`.toLowerCase().includes(q))
    .slice().reverse();
  const shown = list.slice(0, state.limit);
  $('noEvents').hidden = list.length > 0;
  $('log').hidden = list.length === 0;
  $('more').hidden = list.length <= state.limit;
  $('rows').replaceChildren(...shown.map((e) => {
    const [cls, label] = KIND[e.kind] ?? ['conn', e.kind];
    const tr = el('tr', /failed|timeout/.test(e.message) ? 'bad' : cls);
    tr.append(el('td', 't', new Date(e.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })),
              el('td', 'k', label), el('td', 'm', e.client), el('td', 'd', e.message));
    if (e.id && e.kind === 'exec') {
      tr.dataset.id = e.id;
      tr.tabIndex = 0;
      tr.title = 'Show output';
      tr.addEventListener('click', () => openDetail(e));
      tr.addEventListener('keydown', (k) => { if (k.key === 'Enter') openDetail(e); });
    }
    return tr;
  }));
}

$('q').addEventListener('input', () => { state.q = $('q').value; renderLog(); });
$('kind').addEventListener('change', () => { state.kind = $('kind').value; renderLog(); });
$('more').addEventListener('click', () => { state.limit += 100; renderLog(); });

// ---------- numbers ----------
function renderNumbers(info) {
  const s = info.stats;
  const rows = [
    ['Commands', s.execs, ''],
    ['Timed out', s.execs ? `${(s.timeouts / s.execs * 100).toFixed(1)} %` : '0 %', `${s.timeouts} of ${s.execs}`, s.execs > 0 && s.timeouts / s.execs > .05],
    ['Files moved', s.uploads + s.downloads, bytes(s.bytesUploaded + s.bytesDownloaded)],
    ['Refused requests', s.authFailures, 'wrong or missing key', s.authFailures > 0],
  ];
  const box = $('numbers');
  box.replaceChildren(el('h2', '', 'Since start'), ...rows.map(([label, value, sub, alarm]) => {
    const d = el('div', `num${alarm ? ' alarm' : ''}`);
    const left = el('div');
    left.append(el('div', '', label), el('span', '', sub));
    d.append(left, el('b', '', value));
    return d;
  }));
  // Events per minute over the last half hour, from the history the relay keeps.
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.id = 'spark';
  svg.setAttribute('viewBox', '0 0 30 10');
  svg.setAttribute('preserveAspectRatio', 'none');
  const now = Date.now();
  const bins = Array.from({ length: 30 }, () => ({ n: 0, bad: 0 }));
  for (const e of state.events) {
    const age = Math.floor((now - new Date(e.at)) / 60000);
    if (age >= 0 && age < 30) { bins[29 - age].n++; if ((KIND[e.kind]?.[0]) === 'bad') bins[29 - age].bad++; }
  }
  const peak = Math.max(1, ...bins.map((b) => b.n));
  bins.forEach((b, i) => {
    if (!b.n) return;
    const r = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
    const h = Math.max(.6, b.n / peak * 10);
    r.setAttribute('x', i + .1); r.setAttribute('width', .8); r.setAttribute('y', 10 - h); r.setAttribute('height', h);
    if (b.bad) r.setAttribute('class', 'bad');
    svg.append(r);
  });
  const cap = el('div', 'dim', 'Activity, last 30 minutes');
  cap.style.fontSize = '12.5px';
  box.append(svg, cap);
}

function renderRelayLine(info) {
  const line = $('relayLine');
  const ver = info.outdated
    ? el('span', 'warn', `relay v${info.version} — v${info.latest} is out`)
    : el('span', '', info.latest === info.version ? `relay v${info.version}, up to date` : `relay v${info.version}`);
  line.replaceChildren(ver, el('span', '', `up ${dur(info.uptimeSeconds)}`),
    el('span', info.tls ? '' : 'bad', info.tls ? 'TLS' : 'plain HTTP'),
    el('span', '', `${info.tokens} keys`));
}

// ---------- detail ----------
async function openDetail(e) {
  $('dTitle').textContent = `Command on ${e.client}`;
  $('dKv').replaceChildren();
  $('dBody').replaceChildren(el('div', 'dim', 'Loading output…'));
  $('detail').hidden = false;
  $('shade').hidden = false;
  $('dClose').focus();
  try {
    const r = await api(`/api/command?id=${encodeURIComponent(e.id)}`);
    const kv = $('dKv');
    for (const [k, v] of [['Machine', r.client], ['Started', new Date(r.startedUtc).toLocaleString()], ['Took', `${(r.durationMs / 1000).toFixed(1)} s`], ['Exit code', r.exitCode]])
      kv.append(el('span', '', k), el('div', 'mono', v));
    const body = [el('h2', '', 'Command'), el('pre', '', r.command), el('h2', '', 'Output'), el('pre', '', r.stdout || '(none)')];
    if (r.stderr) body.push(el('h2', '', 'Errors'), el('pre', '', r.stderr));
    if (r.truncated) body.push(el('div', 'dim', 'Shortened: the relay keeps the first 64 kB.'));
    $('dBody').replaceChildren(...body);
  } catch {
    $('dBody').replaceChildren(el('div', 'dim', 'The relay no longer holds this output (it keeps the last 200 commands).'));
  }
}
function closeDetail() { $('detail').hidden = true; $('shade').hidden = true; }
$('dClose').addEventListener('click', closeDetail);
$('shade').addEventListener('click', closeDetail);
document.addEventListener('keydown', (k) => { if (k.key === 'Escape') closeDetail(); });

// ---------- refresh ----------
let busy = false;
async function tick() {
  if (busy) return;
  busy = true;
  try {
    const [clients, info] = await Promise.all([api('/api/clients'), api('/api/events?limit=500')]);
    state.clients = clients.clients;
    state.events = info.events;
    state.info = info;
    renderPorts();
    renderLog();
    renderNumbers(info);
    renderRelayLine(info);
    $('beat').className = 'live';
    $('beat').title = `updated ${new Date().toLocaleTimeString()}`;
  } catch (err) {
    $('beat').className = 'dead';
    $('beat').title = `no answer from the relay (${err.message})`;
  } finally {
    busy = false;
  }
}

async function checkAgents() {
  try {
    const { pending } = await api('/api/admin/oauth/pending', { timeout: 5000 });
    const bar = $('agentBar');
    bar.hidden = pending.length === 0;
    bar.textContent = pending.length === 1
      ? `${pending[0].client} asks for access — code ${pending[0].userCode}. Review it`
      : `${pending.length} AI agents ask for access. Review them`;
  } catch { /* the main refresh reports connection problems */ }
}

tick();
checkAgents();
setInterval(tick, 3000);
setInterval(checkAgents, 3000);
</script>
</body>
</html>
""";
}
