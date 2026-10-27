// RemoteCmd dashboard.
//
// Every value shown here comes from machines someone else controls: command lines, paths, host
// names, what a client says it is called. The page therefore never assembles markup from data —
// everything is built from elements and text nodes, and the relay's Content-Security-Policy refuses
// any script that is not this file.
'use strict';

// ---------------------------------------------------------------- helpers
const $ = (id) => document.getElementById(id);
const el = (tag, cls, text) => {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text != null) e.textContent = String(text);
  return e;
};
const button = (text, cls, onClick) => {
  const b = el('button', cls ? `btn ${cls}` : 'btn', text);
  b.type = 'button';
  if (onClick) b.addEventListener('click', onClick);
  return b;
};
/** Tags an element so keyboard focus can be put back on its replacement after a repaint. */
const keyed = (node, key) => { node.dataset.key = key; return node; };
const flat = (s) => String(s ?? '').replace(/\s+/g, ' ').trim();
const local = {
  get(k) { try { return localStorage.getItem(k); } catch { return null; } },
  set(k, v) { try { localStorage.setItem(k, v); } catch { /* private window */ } },
};

function ago(seconds) {
  if (seconds == null || seconds < 0) return 'never';
  if (seconds < 5) return 'just now';
  if (seconds < 60) return `${Math.floor(seconds)} s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ago`;
  return `${Math.floor(seconds / 86400)} d ago`;
}
const since = (iso) => (iso ? ago((Date.now() - new Date(iso)) / 1000) : 'never');
function dur(s) {
  if (s < 60) return `${s} s`;
  if (s < 3600) return `${Math.floor(s / 60)} min`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ${Math.floor(s % 3600 / 60)} min`;
  return `${Math.floor(s / 86400)} d ${Math.floor(s % 86400 / 3600)} h`;
}
function bytes(v) {
  if (v < 1024) return `${v} B`;
  if (v < 1048576) return `${(v / 1024).toFixed(1)} kB`;
  if (v < 1073741824) return `${(v / 1048576).toFixed(1)} MB`;
  return `${(v / 1073741824).toFixed(2)} GB`;
}
const clock = (iso) => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
const hhmm = (iso) => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

/** The system in words people use; clients report the runtime's own description. */
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
  return s.split('#')[0].slice(0, 56);
}

let toastTimer = 0;
function toast(text) {
  let t = document.querySelector('.toast');
  if (!t) { t = el('div', 'toast'); t.setAttribute('role', 'status'); document.body.append(t); }
  t.textContent = text;
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.hidden = true; }, 1800);
}

async function copy(text, what = 'Copied') {
  try {
    await navigator.clipboard.writeText(text);
    toast(what);
  } catch {
    toast('Copy failed — select the text and copy it by hand');
  }
}

/** Calls the relay. Every call carries the header that marks it as coming from this page. */
async function api(path, { method = 'GET', body, timeout = 10000 } = {}) {
  const res = await fetch(path, {
    method,
    headers: { 'Content-Type': 'application/json', 'X-RemoteCmd': '1' },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(timeout),
  });
  if (res.status === 401) { location.href = '/login'; throw new Error('signed out'); }
  const data = (res.headers.get('content-type') || '').includes('json') ? await res.json() : null;
  if (!res.ok) throw new Error(data?.error || `HTTP ${res.status}`);
  return data;
}

// ---------------------------------------------------------------- state
const state = {
  view: 'overview',
  show: 'all',
  picked: null,
  q: '',
  kind: '',
  limit: 80,
  clients: [],
  events: [],
  relay: null,       // /api/events: version, uptime, stats
  admin: null,       // /api/admin/info
  addresses: [],     // /api/admin/addresses
  pending: [],       // pairing codes not yet used
  recent: [],        // pairing codes used in the last hour
  creds: [],
  oauth: [],
  unknown: [],       // machines polling with a key nobody issued
};

// ---------------------------------------------------------------- views
const VIEWS = ['overview', 'access', 'agents', 'settings'];

function route() {
  // /manage and /ui are older addresses of this page; /manage was pairing and access.
  if (location.pathname === '/manage' || location.pathname === '/ui') {
    history.replaceState(null, '', `/${location.hash || (location.pathname === '/manage' ? '#access' : '#overview')}`);
  }
  const hash = location.hash.replace('#', '');
  if (hash === 'pair') { openPair(); history.replaceState(null, '', '/#overview'); }
  // Machines and activity share the overview; the old section links still land on it.
  show(VIEWS.includes(hash) ? hash : 'overview');
  if (hash === 'activity') $('activity').scrollIntoView();
}

function show(view) {
  state.view = view;
  for (const section of document.querySelectorAll('main .view')) section.hidden = section.dataset.view !== view;
  for (const a of document.querySelectorAll('.tabs-top a')) {
    if (a.dataset.view === view) a.setAttribute('aria-current', 'page');
    else a.removeAttribute('aria-current');
  }
  document.title = `${view[0].toUpperCase()}${view.slice(1)} · RemoteCmd`;
  renderView();
}

function renderView() {
  if (state.view === 'overview') { renderMachines(); renderActivity(); }
  else if (state.view === 'access') renderAccess();
  else if (state.view === 'agents') renderAgents();
  else if (state.view === 'settings') renderSettings();
}

window.addEventListener('hashchange', route);

// ---------------------------------------------------------------- header, rail, alerts
function renderChrome() {
  const r = state.relay;
  const line = $('relayLine');
  if (r) {
    const pills = [
      el('span', r.outdated ? 'pill warn' : 'pill', r.outdated ? `v${r.version} → v${r.latest}` : `v${r.version}`),
      el('span', 'pill', `up ${dur(r.uptimeSeconds)}`),
      el('span', r.tls ? 'pill ok' : 'pill bad', r.tls ? 'TLS' : 'plain HTTP'),
    ];
    line.replaceChildren(...pills);
  }
  const online = state.clients.filter((c) => c.connected).length;
  $('navMachines').textContent = state.clients.length ? `${online}/${state.clients.length}` : '';
  $('navAgents').textContent = state.oauth.length ? String(state.oauth.length) : '';
  $('navAccess').textContent = state.unknown.length ? String(state.unknown.length) : '';
  $('navSettings').textContent = state.admin && !state.admin.publicUrlSet && suspiciousAddress() ? '!' : '';
  if (state.admin) $('railFoot').textContent = `relay :${state.admin.port} · dashboard :${state.admin.adminPort}`;
  renderAlerts();
}

/** True when the guessed address is a VPN or virtual adapter while a LAN one exists. */
function suspiciousAddress() {
  const current = state.addresses.find((a) => a.url === state.admin?.publicUrl);
  return !!current && current.kind !== 'lan' && state.addresses.some((a) => a.kind === 'lan');
}

function alert(kind, title, detail, actions = []) {
  const box = el('div', `alert ${kind}`);
  const text = el('div', 'text');
  text.append(el('b', '', title));
  if (detail) text.append(document.createTextNode(' '), el('span', '', detail));
  box.append(el('span', 'lamp'), text, ...actions);
  return box;
}

/**
 * Replaces a container's children only when the data behind them changed. Rebuilding every few
 * seconds would pull buttons out from under the keyboard focus and the pointer, and make screen
 * readers announce live regions over and over. Relative times stay out of the signature for that
 * reason; the next real change refreshes them.
 */
const painted = new Map();
function paint(container, sig, build) {
  const key = typeof sig === 'string' ? sig : JSON.stringify(sig);
  if (painted.get(container) === key) return;
  painted.set(container, key);
  const focusKey = document.activeElement?.dataset?.key;
  container.replaceChildren(...build());
  if (focusKey) container.querySelector(`[data-key="${CSS.escape(focusKey)}"]`)?.focus();
}

function renderAlerts() {
  paint($('alerts'), [
    state.oauth.map((p) => p.id),
    state.unknown.map((m) => [m.peer, m.name, m.host]),
    state.recent.map((r) => [r.id, r.polled, r.revoked, (Date.now() - new Date(r.atUtc)) > 60000]),
    state.admin?.publicUrl, state.admin?.publicUrlSet, state.addresses.map((a) => a.url),
  ], buildAlerts);
}

function buildAlerts() {
  const list = [];
  for (const p of state.oauth) {
    list.push(alert('info', `${p.client} asks to control the relay`, `code ${p.userCode} — approve only if the agent shows the same code.`,
      [keyed(button('Review', 'small', () => { location.hash = '#agents'; }), `review-${p.id}`)]));
  }
  for (const m of state.unknown.slice(0, 3)) {
    // The name is whatever the sender claims, so it is presented as a claim.
    const who = m.name || m.host ? `A machine calling itself “${m.name || m.host}”` : 'A machine';
    list.push(alert('bad', `${who} at ${m.peer} keeps polling with a key this relay does not know`,
      `since ${hhmm(m.firstUtc)}. Usually an old client still running after the machine was paired again, or a revoked key.`,
      [keyed(button('Pair it', 'small primary', () => openPair(m.name || m.host || '')), `pair-${m.peer}`),
        keyed(button('Details', 'small', () => { location.hash = '#access'; }), `details-${m.peer}`)]));
  }
  if (state.unknown.length > 3) {
    list.push(alert('bad', `${state.unknown.length - 3} more machines with unknown keys`, '',
      [keyed(button('Show', 'small', () => { location.hash = '#access'; }), 'unknown-more')]));
  }
  for (const c of state.recent.filter((x) => !x.polled && !x.revoked && (Date.now() - new Date(x.atUtc)) > 60000)) {
    list.push(alert('', `${c.name} paired at ${hhmm(c.atUtc)} but has not connected since`,
      'Its client service is not running, or it cannot reach the relay address it was given.',
      [keyed(button('What to check', 'small', () => openPairedHelp(c)), `help-${c.id}`)]));
  }
  if (state.admin && !state.admin.publicUrlSet && suspiciousAddress()) {
    list.push(alert('', 'Install commands point at a VPN or virtual address',
      `Machines on your local network may not reach ${state.admin.publicUrl}.`,
      [keyed(button('Choose the address', 'small', () => { location.hash = '#settings'; }), 'choose-address')]));
  }
  return list;
}

// ---------------------------------------------------------------- machines
function machineState(c) {
  if (!c.connected) return { cls: 'lost', text: c.secondsAgo < 0 ? 'never seen' : 'offline' };
  if (c.state === 'upload') return { cls: 'busy', text: 'receiving file' };
  if (c.state === 'download') return { cls: 'busy', text: 'sending file' };
  if (c.running > 0) return { cls: 'busy', text: `running ${c.running}` };
  return { cls: 'on', text: 'online' };
}

function renderMachines() {
  const all = state.clients;
  const shown = all.filter((c) => state.show === 'all'
    || (state.show === 'online' && c.connected)
    || (state.show === 'offline' && !c.connected)
    || (state.show === 'update' && c.outdated));
  const online = all.filter((c) => c.connected).length;
  const outdated = all.filter((c) => c.outdated).length;
  $('machCount').textContent = all.length ? `${online}/${all.length} online${outdated ? ` · ${outdated} outdated` : ''}` : '';
  $('noMachines').hidden = all.length > 0;
  $('machFilter').hidden = all.length === 0;

  paint($('ports'), [state.show, state.picked, shown.map((c) => [c.id, c.name, c.host, c.user, c.os, c.version, c.outdated, c.connected, c.state, c.running, c.queued, c.served, c.ip])],
    () => shown.map(machineCard));
  for (const node of document.querySelectorAll('#ports .seen')) {
    const c = all.find((x) => x.id === node.dataset.id);
    if (c) node.textContent = c.connected ? `seen ${ago(c.secondsAgo)}` : `last seen ${ago(c.secondsAgo)}`;
  }
  syncTargets();
}

/**
 * Who the client runs as, in plain words. A Windows service runs as SYSTEM, which Windows reports
 * as the computer's own account name with a trailing "$".
 */
function account(c) {
  if (!c.user) return '';
  if (c.user.endsWith('$')) return 'service (SYSTEM)';
  if (c.user === 'root') return 'root';
  return `user ${c.user}`;
}

/** One card per machine. Clicking it narrows the activity below to that machine; a second click undoes it. */
function machineCard(c) {
  const st = machineState(c);
  const card = keyed(el('div', `port ${st.cls}${state.picked === c.name ? ' picked' : ''}`), `card-${c.id}`);
  card.tabIndex = 0;
  card.setAttribute('role', 'button');
  card.setAttribute('aria-pressed', String(state.picked === c.name));
  card.title = state.picked === c.name ? 'Show every machine\'s activity' : 'Show only its activity';
  const toggle = () => pickMachine(state.picked === c.name ? null : c.name);
  card.addEventListener('click', (e) => { if (!e.target.closest('button')) toggle(); });
  card.addEventListener('keydown', (k) => { if ((k.key === 'Enter' || k.key === ' ') && k.target === card) { k.preventDefault(); toggle(); } });

  const head = el('div', 'phd');
  head.append(el('span', 'dot'), el('span', 'alias', c.name), el('span', `state ${st.cls}`, st.text));
  card.append(head);
  card.append(el('div', 'host', c.host ? [c.host, account(c)].filter(Boolean).join(' · ') : 'client older than 2.6, reports no host'));
  card.append(el('div', 'sys', [system(c.os) || 'system unknown', c.ip].filter(Boolean).join(' · ')));
  const foot = el('div', 'foot');
  foot.append(el('span', c.outdated || !c.version ? 'tag old' : 'tag', c.version ? `v${c.version}${c.outdated ? ' ↑' : ''}` : 'old client'));
  const seen = el('span', 'seen', c.connected ? `seen ${ago(c.secondsAgo)}` : `last seen ${ago(c.secondsAgo)}`);
  seen.dataset.id = c.id;
  foot.append(seen, el('span', '', c.queued ? `${c.served} done · ${c.queued} queued` : `${c.served} done`), el('span', 'grow'));
  if (c.connected) foot.append(keyed(button('run', 'small', () => { $('target').value = c.name; $('cmd').focus(); }), `run-${c.id}`));
  foot.append(keyed(button('details', 'small ghost', () => openMachine(c.id)), `detail-${c.id}`));
  card.append(foot);
  return card;
}

function pickMachine(name) {
  state.picked = name;
  renderMachines();
  renderActivity();
}

for (const b of document.querySelectorAll('#machFilter button')) {
  b.addEventListener('click', () => {
    state.show = b.dataset.show;
    for (const o of document.querySelectorAll('#machFilter button')) o.setAttribute('aria-pressed', String(o === b));
    renderMachines();
  });
}

function openMachine(id) {
  const c = state.clients.find((x) => x.id === id);
  if (!c) return;
  const st = machineState(c);
  const body = [];
  const tags = el('div', 'row');
  tags.append(el('span', `tag ${st.cls === 'lost' ? 'bad' : 'on'}`, st.text), el('span', c.outdated ? 'tag old' : 'tag', c.version ? `client v${c.version}` : 'client version unknown'));
  body.push(tags);
  const kv = el('dl', 'kv');
  for (const [k, v] of [
    ['Host', c.host || '—'], ['Runs as', account(c) || '—'], ['System', c.os || '—'], ['Address', c.ip || '—'],
    ['Key', c.token || '—'], ['Online for', c.connected ? dur(c.connectedForSeconds) : '—'],
    ['Last seen', ago(c.secondsAgo)], ['Commands done', String(c.served)], ['Queued / running', `${c.queued} / ${c.running}`],
  ]) kv.append(el('dt', '', k), el('dd', '', v));
  body.push(kv);
  if (c.outdated) {
    // The install scripts upgrade in place and keep the pairing when run without a code.
    const windows = /windows/i.test(c.os || '');
    const cmd = windows
      ? '& ([scriptblock]::Create((irm https://raw.githubusercontent.com/nks-hub/remote-cmd/main/install.ps1)))'
      : 'curl -fsSL https://raw.githubusercontent.com/nks-hub/remote-cmd/main/install.sh | sudo sh -s -- client';
    const bar = el('div', 'bar');
    bar.append(el('span', 'hint warn', `Client v${state.relay?.latest} is out. To update, run on the machine${windows ? ' in PowerShell as administrator' : ''}:`),
      button('Copy', 'small', () => copy(cmd, 'Command copied')));
    const box = el('div', 'cmdRow');
    box.append(bar, el('pre', 'cmd', cmd));
    body.push(box);
  }

  const actions = el('div', 'row');
  if (c.connected) {
    actions.append(button('Run a command here', 'primary', () => {
      closeDrawer(); location.hash = '#overview';
      $('target').value = c.name; $('cmd').focus();
    }));
  }
  actions.append(button('Its activity', '', () => { closeDrawer(); pickMachine(c.name); $('activity').scrollIntoView(); }));
  actions.append(button('Pair again', '', () => { closeDrawer(); openPair(c.name); }));
  if (!c.connected) {
    actions.append(button('Remove from list', 'danger', async () => {
      if (!confirm(`Remove ${c.name} from the list? If it comes back, it reappears on its own. To lock it out, revoke its key in Access.`)) return;
      try { await api(`/api/admin/clients/${encodeURIComponent(c.id)}`, { method: 'DELETE' }); closeDrawer(); toast('Removed'); tick(); } catch (err) { toast(err.message); }
    }));
  }
  body.push(actions);

  const recent = state.events.filter((e) => e.client === c.name).slice(-12).reverse();
  body.push(el('h3', '', 'Latest activity'));
  body.push(recent.length ? logTable(recent) : el('p', 'hint', 'Nothing in the relay\'s memory yet.'));
  openDrawer(c.name, body);
}

// ---------------------------------------------------------------- run a command
function syncTargets() {
  const sel = $('target');
  const current = sel.value || local.get('rcmd-target') || '';
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
  local.set('rcmd-target', target);
  const timeoutSeconds = Number($('tmo').value);
  const meta = $('runMeta'), out = $('runOut'), btn = $('runBtn');
  btn.disabled = true;
  btn.textContent = 'Running…';
  meta.hidden = false;
  meta.replaceChildren(el('span', 'spinner'), el('span', '', `${target}: running…`));
  out.hidden = true;
  const started = performance.now();
  try {
    const r = await api(`/api/exec?client=${encodeURIComponent(target)}`, { method: 'POST', body: { command, timeoutSeconds }, timeout: (timeoutSeconds + 15) * 1000 });
    const secs = ((performance.now() - started) / 1000).toFixed(1);
    meta.replaceChildren(el('span', r.exitCode === 0 ? 'tag on' : 'tag bad', `exit ${r.exitCode}`), el('span', '', `${target} · ${secs} s`),
      button('Copy output', 'small', () => copy(r.output, 'Output copied')));
    out.textContent = r.output || '(no output)';
    out.hidden = false;
  } catch (err) {
    meta.replaceChildren(el('span', 'err', `Not run: ${err.message}`));
  } finally {
    btn.disabled = false;
    btn.textContent = 'Run';
    tick();
  }
});

// ---------------------------------------------------------------- activity
const KIND = {
  exec: ['exec', 'exec'], timeout: ['bad', 'timeout'], upload: ['xfer', 'upload'], download: ['xfer', 'download'],
  screenshot: ['xfer', 'screenshot'], connect: ['conn', 'connect'], gc: ['conn', 'cleanup'], auth: ['bad', 'refused'],
  pair: ['admin', 'pairing'], admin: ['admin', 'access'], oauth: ['admin', 'ai agent'],
};
const isProblem = (e) => (KIND[e.kind]?.[0]) === 'bad' || /failed|timeout|refused/i.test(e.message);

function logTable(events) {
  const wrap = el('div', 'tableWrap');
  const table = el('table', 'log');
  const tbody = el('tbody');
  tbody.append(...events.map(logRow));
  table.append(tbody);
  wrap.append(table);
  return wrap;
}

function logRow(e) {
  const [cls, label] = KIND[e.kind] ?? ['conn', e.kind];
  const tr = el('tr', isProblem(e) ? 'bad' : cls);
  tr.append(el('td', 't', clock(e.at)), el('td', 'k', label), el('td', 'm', e.client), el('td', 'd', e.message));
  if (e.id && e.kind === 'exec') {
    tr.dataset.id = e.id;
    keyed(tr, `row-${e.id}`);
    tr.tabIndex = 0;
    tr.title = 'Show the output';
    tr.addEventListener('click', () => openCommand(e));
    tr.addEventListener('keydown', (k) => { if (k.key === 'Enter') openCommand(e); });
  }
  return tr;
}

function renderActivity() {
  const chip = $('pickedChip');
  chip.replaceChildren();
  if (state.picked) {
    const c = el('span', 'chip', state.picked);
    const x = el('button', '', '×');
    x.type = 'button';
    x.setAttribute('aria-label', 'Show every machine');
    x.addEventListener('click', () => pickMachine(null));
    c.append(x);
    chip.append(c);
  }
  const q = state.q.toLowerCase();
  const list = state.events
    .filter((e) => !state.picked || e.client === state.picked)
    .filter((e) => !state.kind || (state.kind === 'bad' ? isProblem(e) : (KIND[e.kind]?.[0] ?? 'conn') === state.kind))
    .filter((e) => !q || `${e.client} ${e.message}`.toLowerCase().includes(q))
    .slice().reverse();
  const shown = list.slice(0, state.limit);
  $('noEvents').hidden = list.length > 0;
  $('logWrap').hidden = list.length === 0;
  $('more').hidden = list.length <= state.limit;
  paint($('rows'), [state.picked, state.kind, q, state.limit, shown.length, shown[0]?.at, shown[0]?.message], () => shown.map(logRow));
  renderNumbers();
}

$('q').addEventListener('input', () => { state.q = $('q').value; renderActivity(); });
$('kind').addEventListener('change', () => { state.kind = $('kind').value; renderActivity(); });
$('more').addEventListener('click', () => { state.limit += 100; renderActivity(); });

function renderNumbers() {
  const r = state.relay;
  if (!r) return;
  const s = r.stats;
  const rows = [
    ['commands', s.execs, 'since the relay started'],
    ['timed out', s.execs ? `${(s.timeouts / s.execs * 100).toFixed(1)} %` : '0 %', `${s.timeouts} of ${s.execs}`, s.execs > 0 && s.timeouts / s.execs > 0.05],
    ['files', `${s.uploads + s.downloads} · ${bytes(s.bytesUploaded + s.bytesDownloaded)}`, ''],
    ['refused', s.authFailures, 'wrong or missing key', s.authFailures > 0],
  ];
  const box = $('numbers');
  box.replaceChildren(el('h2', '', 'Since start'), ...rows.map(([label, value, sub, alarm]) => {
    const n = el('div', alarm ? 'n alarm' : 'n');
    const left = el('div');
    left.append(el('div', '', label), el('small', '', sub));
    n.append(left, el('b', '', value));
    return n;
  }));
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('class', 'spark');
  svg.setAttribute('viewBox', '0 0 30 10');
  svg.setAttribute('preserveAspectRatio', 'none');
  svg.setAttribute('role', 'img');
  svg.setAttribute('aria-label', 'Events per minute over the last 30 minutes');
  const now = Date.now();
  const bins = Array.from({ length: 30 }, () => ({ n: 0, bad: 0 }));
  for (const e of state.events) {
    const age = Math.floor((now - new Date(e.at)) / 60000);
    if (age >= 0 && age < 30) { bins[29 - age].n++; if (isProblem(e)) bins[29 - age].bad++; }
  }
  const peak = Math.max(1, ...bins.map((b) => b.n));
  bins.forEach((b, i) => {
    if (!b.n) return;
    const rect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
    const h = Math.max(0.6, b.n / peak * 10);
    rect.setAttribute('x', String(i + 0.1)); rect.setAttribute('width', '0.8');
    rect.setAttribute('y', String(10 - h)); rect.setAttribute('height', String(h));
    if (b.bad) rect.setAttribute('class', 'bad');
    svg.append(rect);
  });
  box.append(svg, el('small', 'dim', 'Activity, last 30 minutes'));
}

async function openCommand(e) {
  openDrawer(`Command on ${e.client}`, [el('p', 'hint', 'Loading the output…')]);
  try {
    const r = await api(`/api/command?id=${encodeURIComponent(e.id)}`);
    const kv = el('dl', 'kv');
    for (const [k, v] of [['Machine', r.client], ['Started', new Date(r.startedUtc).toLocaleString()], ['Took', `${(r.durationMs / 1000).toFixed(1)} s`], ['Exit code', r.exitCode]]) {
      kv.append(el('dt', '', k), el('dd', '', v));
    }
    const body = [kv, el('h3', '', 'Command'), el('pre', '', r.command)];
    body.push(el('h3', '', 'Output'), el('pre', '', r.stdout || '(none)'));
    if (r.stderr) body.push(el('h3', '', 'Errors'), el('pre', '', r.stderr));
    if (r.truncated) body.push(el('p', 'hint', 'Shortened: the relay keeps the first 64 kB.'));
    const row = el('div', 'row');
    row.append(button('Copy output', '', () => copy(r.stdout || '', 'Output copied')),
      button('Run again', '', () => { closeDrawer(); location.hash = '#overview'; $('target').value = r.client; $('cmd').value = r.command; $('cmd').focus(); }));
    body.push(row);
    fillDrawer(body);
  } catch {
    fillDrawer([el('p', 'hint', 'The relay no longer holds this output — it keeps the last 200 commands.')]);
  }
}

// ---------------------------------------------------------------- drawer
let drawerReturn = null;
function openDrawer(title, body) {
  // A drawer opened from inside the drawer keeps returning to what opened the first one.
  if ($('drawer').hidden) drawerReturn = document.activeElement?.dataset?.key ?? document.activeElement;
  $('dTitle').textContent = title;
  fillDrawer(body);
  $('drawer').hidden = false;
  $('shade').hidden = false;
  document.querySelector('.shell').inert = true;
  $('dClose').focus();
}
function fillDrawer(body) { $('dBody').replaceChildren(...body); }
function closeDrawer() {
  if ($('drawer').hidden) return;
  $('drawer').hidden = true;
  $('shade').hidden = true;
  document.querySelector('.shell').inert = false;
  // The element that opened it may have been repainted since; find its replacement by key.
  const back = typeof drawerReturn === 'string' ? document.querySelector(`[data-key="${CSS.escape(drawerReturn)}"]`) : drawerReturn;
  back?.focus?.();
}
$('dClose').addEventListener('click', closeDrawer);
$('shade').addEventListener('click', closeDrawer);
document.addEventListener('keydown', (k) => { if (k.key === 'Escape') closeDrawer(); });

// ---------------------------------------------------------------- access
function renderAccess() {
  const codes = state.pending;
  $('codesCard').hidden = codes.length === 0;
  paint($('codes'), codes.map((p) => [p.id, p.name, p.expiresUtc]), () => codes.map((p) => {
    const tr = el('tr');
    const actions = el('td', 'num');
    actions.append(keyed(button('Show', 'small', () => openPairFor(p)), `show-${p.id}`), document.createTextNode(' '),
      keyed(button('Cancel', 'small danger', () => act(api(`/api/admin/pair/${encodeURIComponent(p.id)}`, { method: 'DELETE' }), 'Code cancelled', refreshPairing)), `cancel-${p.id}`));
    tr.append(el('td', 'mono', p.code), el('td', '', p.name || 'its hostname'), el('td', '', hhmm(p.expiresUtc)), actions);
    return tr;
  }));

  const sig = [state.unknown.map((m) => [m.peer, m.name, m.host, m.attempts]), state.creds.map((c) => [c.id, c.name, c.role, c.online, c.lastUsedUtc])];
  paint($('creds'), sig, () => [...state.unknown.map((m) => {
    const tr = el('tr', 'bad');
    const td = el('td', 'num');
    td.append(keyed(button('Pair it', 'small primary', () => openPair(m.name || m.host || '')), `pairrow-${m.peer}`));
    tr.append(el('td', '', m.name || m.host ? `calls itself ${m.name || m.host}` : '(no name)'), el('td', '', 'unknown key'),
      el('td', '', `${m.attempts}× from ${m.peer}, last at ${hhmm(m.lastUtc)}`), el('td', 'num', '0'), td);
    return tr;
  }), ...state.creds.map((c) => {
    const tr = el('tr');
    const role = c.ephemeral ? `${c.role} · command line` : c.role;
    const td = el('td', 'num');
    if (!c.ephemeral) {
      td.append(keyed(button('Revoke', 'small danger', () => {
        if (!confirm(`Revoke "${c.name}"? Anything using it is cut off immediately.`)) return;
        act(api(`/api/admin/credentials/${encodeURIComponent(c.id)}`, { method: 'DELETE' }), 'Revoked', loadSlow);
      }), `revoke-${c.id}`));
    } else {
      td.append(el('span', 'hint', 'restart the relay without it to remove'));
    }
    tr.append(el('td', '', c.name), el('td', '', role), el('td', '', since(c.lastUsedUtc)), el('td', 'num', c.online ? String(c.online) : ''), td);
    return tr;
  })]);
}

/** Runs a change, then reports how it went: a toast either way, and a refresh after success. */
async function act(promise, done, then) {
  try {
    await promise;
    toast(done);
    await then?.();
  } catch (err) {
    toast(err.message);
  }
}

$('tokForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  try {
    const t = await api('/api/admin/credentials', { method: 'POST', body: { name: $('tokName').value.trim(), role: 'controller' } });
    $('tokName').value = '';
    const box = el('div', 'secret');
    const row = el('div', 'row');
    row.append(el('b', '', `Key for ${t.name} — shown only now`), button('Copy', 'small', () => copy(t.token, 'Key copied')));
    box.append(row, el('pre', '', t.token), el('p', 'hint', 'Send it in the X-Token header. Lost keys cannot be shown again; revoke and create a new one.'));
    $('newTok').replaceChildren(box);
    loadSlow();
  } catch (err) {
    toast(err.message);
  }
});

// ---------------------------------------------------------------- agents
function renderAgents() {
  paint($('oauthPending'), state.oauth.map((p) => p.id), () => (!state.oauth.length
    ? [el('p', 'hint', 'No agent is waiting.')]
    : state.oauth.map((p) => {
      const box = el('div', 'codeBox');
      const who = el('div', 'grow');
      who.append(el('b', '', p.client), el('div', 'hint', `returns to ${p.redirect} · asked at ${hhmm(p.createdUtc)}`));
      box.append(el('span', 'code', p.userCode), who,
        keyed(button('Approve', 'primary', () => decide(p, 'approve')), `approve-${p.id}`),
        keyed(button('Deny', 'danger', () => decide(p, 'deny')), `deny-${p.id}`));
      return box;
    })));
  const a = state.admin;
  if (!a) return;
  const cmds = [
    ['On this machine', `claude mcp add --transport http remote-cmd http://localhost:${a.adminPort}/mcp`],
    [a.tls ? "From another machine (it has to trust the relay's self-signed certificate)" : 'From another machine', `claude mcp add --transport http remote-cmd ${a.publicUrl}/mcp`],
  ];
  paint($('mcpCmds'), cmds, () => cmds.flatMap(([label, cmd], i) => {
    const bar = el('div', 'bar');
    bar.append(el('b', '', label), keyed(button('Copy', 'small', () => copy(cmd, 'Command copied')), `mcp-${i}`));
    return [bar, el('pre', 'cmd', cmd)];
  }));
}

async function decide(p, decision) {
  try {
    await api(`/api/admin/oauth/${encodeURIComponent(p.id)}/${decision}`, { method: 'POST' });
    toast(decision === 'approve' ? 'Approved' : 'Denied');
  } catch (err) { toast(err.message); }
  loadFast();
}

// ---------------------------------------------------------------- settings
const KIND_TEXT = {
  lan: 'local network',
  public: 'public address',
  vpn: 'VPN — only machines on that VPN reach it',
  virtual: 'virtual adapter — usually only this computer\'s VMs',
};

/**
 * Radio list of this machine's addresses plus a custom one. Returns a function that reads the
 * chosen URL.
 */
function addressPicker(container, name, selected) {
  const options = state.addresses.map((a) => ({ url: a.url, label: a.interface, kind: a.kind }));
  if (selected && !options.some((o) => o.url === selected)) options.unshift({ url: selected, label: 'saved address', kind: '' });
  const nodes = options.map((o) => {
    const row = el('label', 'addr');
    const input = el('input');
    input.type = 'radio'; input.name = name; input.value = o.url; input.checked = o.url === selected;
    const text = el('span');
    text.append(el('span', 'u', o.url), document.createTextNode(' '), el('small', '', `${o.label}${o.kind ? ' · ' + (KIND_TEXT[o.kind] ?? o.kind) : ''}`));
    row.append(input, text, o.kind === 'lan' ? el('span', 'tag on', 'LAN') : o.kind ? el('span', 'tag', o.kind.toUpperCase()) : el('span'));
    return row;
  });
  const other = el('label', 'addr');
  const otherRadio = el('input');
  otherRadio.type = 'radio'; otherRadio.name = name; otherRadio.value = '';
  const otherText = el('input');
  otherText.placeholder = 'other address, e.g. https://relay.example.com:7890';
  otherText.setAttribute('aria-label', 'Other relay address');
  otherText.addEventListener('focus', () => { otherRadio.checked = true; });
  other.append(otherRadio, otherText, el('span'));
  if (!nodes.some((n) => n.querySelector('input').checked)) {
    if (nodes.length) nodes[0].querySelector('input').checked = true;
    else otherRadio.checked = true;
  }
  container.replaceChildren(...nodes, other);
  return () => {
    const chosen = container.querySelector(`input[name="${name}"]:checked`);
    return chosen === otherRadio ? otherText.value.trim() : chosen?.value ?? '';
  };
}

let readSettingsUrl = () => '';
let settingsSig = '';
function renderSettings() {
  const a = state.admin;
  if (!a) return;
  const sig = JSON.stringify([a.publicUrl, a.publicUrlSet, state.addresses]);
  if (sig !== settingsSig) {
    settingsSig = sig;
    readSettingsUrl = addressPicker($('addrList'), 'relayAddr', a.publicUrl);
  }
  $('urlReset').hidden = !a.publicUrlSet;
  $('urlNote').textContent = a.publicUrlSet ? 'Saved.' : 'Detected automatically — save it to keep it fixed.';
  $('tlsNote').textContent = a.tls
    ? `TLS is on with a self-signed certificate. Paired machines pin it: SHA-256 ${a.certSha256}.`
    : 'TLS is off. Commands and pairing stay encrypted end to end, but hand-configured clients send their key in clear text and a paired machine cannot tell this relay from an impostor. Run the relay without --no-tls where you can.';
  $('pwState').textContent = a.passwordSet
    ? 'Set. Opening the dashboard asks for it.'
    : 'Not set. The dashboard listens on this machine only, so anyone with a session on it can open it.';
  $('pwClear').hidden = !a.passwordSet;
  $('logout').hidden = !a.passwordSet;
  const about = [
    ['Version', a.version + (state.relay?.outdated ? ` (v${state.relay.latest} is out)` : '')],
    ['Relay port', `${a.port} (${a.tls ? 'https' : 'http'})`],
    ['Dashboard', `http://localhost:${a.adminPort} (this machine only)`],
    ['Settings file', a.settingsPath],
  ];
  $('about').replaceChildren(...about.flatMap(([k, v]) => [el('dt', '', k), el('dd', '', v)]));
}

$('urlForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  try {
    await api('/api/admin/settings', { method: 'POST', body: { publicUrl: readSettingsUrl() || null } });
    toast('Relay address saved');
  } catch (err) { $('urlNote').textContent = err.message; return; }
  await loadSlow();
});
$('urlReset').addEventListener('click', () => act(api('/api/admin/settings', { method: 'POST', body: { publicUrl: null } }), 'Using the detected address', loadSlow));

$('pwForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  $('pwErr').textContent = '';
  if ($('pw1').value !== $('pw2').value) { $('pwErr').textContent = 'The passwords differ.'; return; }
  try { await api('/api/admin/password', { method: 'POST', body: { password: $('pw1').value } }); } catch (err) { $('pwErr').textContent = err.message; return; }
  $('pw1').value = $('pw2').value = '';
  toast('Password set');
  loadSlow();
});
$('pwClear').addEventListener('click', () => {
  if (!confirm('Remove the password? Anyone with a session on this machine can then open the dashboard.')) return;
  act(api('/api/admin/password', { method: 'POST', body: { password: null } }), 'Password removed', loadSlow);
});
$('logout').addEventListener('click', () => act(api('/api/admin/logout', { method: 'POST' }), 'Signed out', () => { location.href = '/login'; }));

// ---------------------------------------------------------------- pairing
const TAB_HINT = {
  windows: 'Paste into PowerShell opened as administrator. It installs the client, pairs it and starts the RemoteCmdClient service.',
  unix: 'Run in a terminal; it asks for sudo. Installs a systemd service on Linux or a launchd daemon on macOS.',
  installed: 'For a machine that already has the client: run it from the client\'s folder in an elevated prompt. The old client service is replaced.',
};
const pair = { open: false, id: null, entry: null, tab: local.get('rcmd-pair-tab') || 'windows', pairedSeen: 0, timer: 0, readUrl: () => '' };

function pairStep(n) {
  for (const s of document.querySelectorAll('#pairDlg [data-step]')) s.hidden = Number(s.dataset.step) !== n;
  [...$('pairSteps').children].forEach((li, i) => { li.className = i + 1 < n ? 'past' : i + 1 === n ? 'now' : ''; });
}

async function openPair(name = '') {
  if (!state.admin) await loadSlow().catch(() => {});
  pair.open = true;
  pair.id = null;
  pair.entry = null;
  pair.pairedSeen = 0;
  $('pairName').value = name;
  $('pairErr').textContent = '';
  $('pairRemember').checked = false;
  pair.readUrl = addressPicker($('pairAddrs'), 'pairAddr', state.admin?.publicUrl);
  pairStep(1);
  if (!$('pairDlg').open) $('pairDlg').showModal();
  (name ? $('pairCreate') : $('pairName')).focus();
}

/** Reopens the dialog on a code created earlier. */
function openPairFor(p) {
  pair.open = true;
  pair.pairedSeen = 0;
  showCode(p);
  if (!$('pairDlg').open) $('pairDlg').showModal();
}

function showCode(p) {
  pair.id = p.id;
  pair.entry = p;
  pair.misses = 0;
  $('pairCode').textContent = p.code;
  $('pairExp').textContent = `${p.name ? `for ${p.name} · ` : ''}valid until ${hhmm(p.expiresUtc)} · ${p.serverUrl}`;
  const tabs = (p.install || []).map((c) => {
    const t = el('button', '', c.label);
    t.type = 'button';
    t.setAttribute('role', 'tab');
    t.dataset.tab = c.id;
    t.addEventListener('click', () => { pair.tab = c.id; local.set('rcmd-pair-tab', c.id); renderTab(); });
    return t;
  });
  $('pairTabs').replaceChildren(...tabs);
  renderTab();
  setWait('', 'Waiting for the machine…', 'Run the command above on it. This window notices the moment it pairs.');
  pairStep(2);
  clearInterval(pair.timer);
  pair.timer = setInterval(refreshPairing, 1500);
}

function renderTab() {
  const install = pair.entry?.install || [];
  const current = install.find((c) => c.id === pair.tab) ?? install[0];
  if (!current) return;
  for (const t of $('pairTabs').children) t.setAttribute('aria-selected', String(t.dataset.tab === current.id));
  $('pairCmd').textContent = current.command;
  $('pairTabHint').textContent = TAB_HINT[current.id] ?? '';
}

function setWait(kind, title, text) {
  $('pairWait').className = `wait ${kind}`;
  $('pairWaitTitle').textContent = title;
  $('pairWaitText').textContent = text;
}

$('pairCopy').addEventListener('click', () => copy($('pairCmd').textContent, 'Command copied'));
$('pairOpen').addEventListener('click', () => openPair());
document.addEventListener('click', (e) => { if (e.target.closest?.('[data-action="pair"]')) openPair(); });
$('pairClose').addEventListener('click', () => $('pairDlg').close());
$('pairDlg').addEventListener('close', () => { pair.open = false; clearInterval(pair.timer); });
$('pairAnother').addEventListener('click', () => openPair());
$('pairShow').addEventListener('click', async () => {
  $('pairDlg').close();
  location.hash = '#overview';
  const sessionId = pair.done?.sessionId;
  if (!sessionId) return;
  // The machine list may predate its first poll; read it fresh rather than waiting for the next tick.
  try { state.clients = (await api('/api/clients')).clients; renderMachines(); } catch { /* tick() reports it */ }
  openMachine(sessionId);
});
$('pairCancel').addEventListener('click', async () => {
  if (pair.id) {
    try { await api(`/api/admin/pair/${encodeURIComponent(pair.id)}`, { method: 'DELETE' }); toast('Code cancelled'); } catch (err) { toast(err.message); }
  }
  $('pairDlg').close();
  refreshPairing();
});

$('pairStart').addEventListener('submit', async (e) => {
  e.preventDefault();
  $('pairErr').textContent = '';
  const serverUrl = pair.readUrl();
  if (!serverUrl) { $('pairErr').textContent = 'Choose or type the address machines reach this relay at.'; return; }
  $('pairCreate').disabled = true;
  try {
    if ($('pairRemember').checked && serverUrl !== state.admin?.publicUrl) {
      await api('/api/admin/settings', { method: 'POST', body: { publicUrl: serverUrl } });
      loadSlow();
    }
    const p = await api('/api/admin/pair', { method: 'POST', body: { name: $('pairName').value.trim() || null, ttlMinutes: Number($('pairTtl').value), serverUrl } });
    showCode(p);
    refreshPairing();
  } catch (err) {
    $('pairErr').textContent = err.message;
  } finally {
    $('pairCreate').disabled = false;
  }
});

/** Follows the open code from waiting, through paired, to the machine's first poll. */
function followPairing() {
  if (!pair.open || !pair.id || $('pairDlg').querySelector('[data-step="2"]').hidden) return;
  if (state.pending.some((p) => p.id === pair.id)) return; // still waiting
  const done = state.recent.find((r) => r.id === pair.id);
  if (!done) {
    // A list fetched just before the code was created does not have it yet; only an expired code is final.
    if (Date.now() < new Date(pair.entry.expiresUtc) && ++pair.misses < 3) return;
    setWait('warn', 'This code is no longer valid', 'It expired or was cancelled. Create a new one.');
    clearInterval(pair.timer);
    return;
  }
  pair.done = done;
  if (done.connected) {
    clearInterval(pair.timer);
    $('pairDoneTitle').textContent = `${done.name} is online`;
    $('pairDoneText').textContent = `Paired from ${done.peer} at ${hhmm(done.atUtc)}. Installed with the command above, its client runs as a service and comes back by itself after a restart.`;
    pairStep(3);
    tick();
    return;
  }
  if (!pair.pairedSeen) pair.pairedSeen = Date.now();
  const waited = (Date.now() - pair.pairedSeen) / 1000;
  if (waited < 30) {
    setWait('ok', `Paired as ${done.name} from ${done.peer}`, 'Waiting for its client to connect…');
  } else {
    setWait('warn', `Paired as ${done.name}, but its client has not connected`,
      'The service may have failed to start, or the machine cannot reach the address in the command. Check “What to check” on the Machines page.');
  }
}

function openPairedHelp(c) {
  openDrawer(`${c.name} has not connected`, [
    el('p', 'hint', `It traded its code for a key at ${hhmm(c.atUtc)} from ${c.peer}, but no poll with that key has arrived since.`),
    el('h3', '', 'On Windows'),
    el('pre', 'cmd', 'Get-Service RemoteCmd* | Format-Table Name,Status,StartType\nTest-Path "$env:ProgramData\\RemoteCmd\\client.json"   # its key lives here; do not paste it anywhere'),
    el('h3', '', 'On Linux / macOS'),
    el('pre', 'cmd', 'systemctl status RemoteCmdClient   # Linux\nsudo launchctl print system/cz.nks.remotecmd.RemoteCmdClient   # macOS'),
    el('p', 'hint', 'If an older client is still running under another service name, it keeps using its old key and shows up under Access as an unknown key. Stop it, or pair again — the install replaces the service.'),
    el('p', 'hint', `The client must reach ${state.admin?.publicUrl ?? 'the relay address'}. From the machine: curl that address; any HTTP answer (even 401) means it is reachable.`),
  ]);
}

// ---------------------------------------------------------------- refresh
let busy = false;
async function tick() {
  if (busy) return;
  busy = true;
  try {
    const [clients, relay] = await Promise.all([api('/api/clients'), api('/api/events?limit=500')]);
    state.clients = clients.clients;
    state.events = relay.events;
    state.relay = relay;
    $('beat').className = 'beat live';
    $('beat').title = `updated ${new Date().toLocaleTimeString()}`;
    renderChrome();
    if (state.view === 'overview') { renderMachines(); renderActivity(); }
  } catch (err) {
    $('beat').className = 'beat dead';
    $('beat').title = `no answer from the relay (${err.message})`;
  } finally {
    busy = false;
  }
}

async function refreshPairing() {
  try {
    const p = await api('/api/admin/pair', { timeout: 5000 });
    state.pending = p.pending;
    state.recent = p.recent;
    followPairing();
    if (state.view === 'access') renderAccess();
  } catch { /* tick() reports connection problems */ }
}

async function loadFast() {
  try {
    const [oauth, unknown] = await Promise.all([api('/api/admin/oauth/pending', { timeout: 5000 }), api('/api/admin/unknown-keys', { timeout: 5000 })]);
    state.oauth = oauth.pending;
    state.unknown = unknown.machines;
    await refreshPairing();
    renderChrome();
    if (state.view === 'agents') renderAgents();
    if (state.view === 'access') renderAccess();
  } catch { /* tick() reports connection problems */ }
}

async function loadSlow() {
  const [admin, addresses, creds] = await Promise.all([api('/api/admin/info'), api('/api/admin/addresses'), api('/api/admin/credentials')]);
  state.admin = admin;
  state.addresses = addresses.addresses;
  state.creds = creds.credentials;
  renderChrome();
  renderView();
}

route();
tick();
loadFast();
loadSlow().catch(() => {});
setInterval(tick, 3000);
setInterval(loadFast, 3000);
setInterval(() => loadSlow().catch(() => {}), 15000);
