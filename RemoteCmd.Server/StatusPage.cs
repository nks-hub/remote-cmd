/// <summary>
/// Single-file status page served at /ui. The page itself carries no data and needs no token to
/// load; it asks for one in the browser, keeps it in sessionStorage and sends it as an X-Token
/// header, so the token never appears in a URL, in browser history or in the relay's access log.
/// Everything on it is built from text nodes — no markup is ever assembled from relay data.
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
<style>
  :root { color-scheme: light dark; --bg:#fff; --fg:#111; --muted:#666; --line:#e3e3e3; --ok:#1a7f37; --warn:#9a6700; --bad:#b42318; --card:#fafafa; --accent:#0969da; --hover:#f0f3f6; }
  @media (prefers-color-scheme: dark) { :root { --bg:#14161a; --fg:#e8e8e8; --muted:#9aa0a6; --line:#2a2e35; --ok:#3fb950; --warn:#d29922; --bad:#f85149; --card:#1b1e24; --accent:#58a6ff; --hover:#232830; } }
  * { box-sizing: border-box; }
  body { margin:0; padding:18px 24px 40px; background:var(--bg); color:var(--fg); width:100%;
         font:14px/1.5 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
  h1 { font-size:17px; margin:0 0 4px; }
  h2 { font-size:13px; text-transform:uppercase; letter-spacing:.08em; color:var(--muted); margin:26px 0 8px; }
  .sub { color:var(--muted); margin-bottom:18px; }
  .muted { color:var(--muted); }
  .cards { display:grid; grid-template-columns:repeat(auto-fill, minmax(132px, 1fr)); gap:10px; }
  .card { background:var(--card); border:1px solid var(--line); border-radius:8px; padding:10px 14px; }
  .card b { display:block; font-size:19px; font-weight:600; }
  .card span { color:var(--muted); font-size:12px; }
  .scroll { overflow-x:auto; border:1px solid var(--line); border-radius:8px; }
  table { border-collapse:collapse; width:100%; }
  th, td { text-align:left; padding:6px 10px; border-bottom:1px solid var(--line); white-space:nowrap; vertical-align:top; }
  tr:last-child td { border-bottom:none; }
  th { color:var(--muted); font-weight:600; background:var(--card); position:sticky; top:0; }
  .idle, .ok { color:var(--ok); } .stale, .bad { color:var(--bad); } .busy, .warn { color:var(--warn); }
  .k-exec { color:var(--accent); } .k-timeout, .k-auth { color:var(--bad); }
  .k-upload, .k-download { color:var(--warn); } .k-connect { color:var(--ok); } .k-gc { color:var(--muted); }
  td.msg { white-space:normal; word-break:break-word; width:100%; }
  #hist tr[data-id] { cursor:pointer; }
  #hist tr[data-id]:hover td { background:var(--hover); }
  #hist tr.sel td { background:var(--hover); box-shadow:inset 2px 0 0 var(--accent); }
  .empty { color:var(--muted); padding:8px 0; }
  .tools { display:flex; gap:8px; align-items:center; flex-wrap:wrap; margin-bottom:8px; }
  .tools input, .tools select, #gate input, #gate button, #dclose {
    font:inherit; padding:6px 9px; background:var(--card); color:var(--fg);
    border:1px solid var(--line); border-radius:6px; }
  .tools label { color:var(--muted); display:flex; align-items:center; gap:5px; }
  #q { min-width:240px; flex:1 1 240px; max-width:420px; }
  #gate { display:flex; gap:8px; align-items:center; margin:16px 0 22px; flex-wrap:wrap; }
  #gate input { min-width:280px; padding:8px 10px; }
  #gate button { padding:8px 16px; cursor:pointer; }
  #gate .err { color:var(--bad); }
  #backdrop { position:fixed; inset:0; background:rgba(0,0,0,.35); border:0; padding:0; }
  #detail { position:fixed; top:0; right:0; bottom:0; width:min(660px, 96vw); overflow:auto;
            background:var(--bg); border-left:1px solid var(--line); padding:16px 18px 32px;
            box-shadow:-10px 0 26px rgba(0,0,0,.22); }
  .dhead { display:flex; justify-content:space-between; align-items:center; gap:12px; margin-bottom:14px; }
  #dclose { cursor:pointer; }
  .kv { display:grid; grid-template-columns:auto 1fr; gap:3px 16px; margin-bottom:14px; }
  .kv span { color:var(--muted); }
  .dlabel { font-size:12px; text-transform:uppercase; letter-spacing:.08em; color:var(--muted); margin:12px 0 5px; }
  pre { margin:0; padding:10px; background:var(--card); border:1px solid var(--line); border-radius:6px;
        white-space:pre-wrap; word-break:break-word; max-height:38vh; overflow:auto; }
  .note { color:var(--warn); margin-bottom:10px; }
  [hidden] { display:none !important; }
</style>
</head>
<body>
<h1>RemoteCmd relay</h1>
<div class="sub" id="sub">loading…</div>

<form id="gate" hidden>
  <input id="token" type="password" placeholder="relay token" autocomplete="off" spellcheck="false">
  <button type="submit">Connect</button>
  <span class="err" id="gateErr"></span>
</form>

<div class="cards" id="cards"></div>

<section id="sessions">
  <h2>Sessions</h2>
  <div class="scroll"><table id="tbl">
    <thead><tr><th>name</th><th>id</th><th>address</th><th>token</th><th>state</th><th>last poll</th><th>run</th><th>queue</th><th>served</th><th>connected</th></tr></thead>
    <tbody></tbody>
  </table></div>
  <div class="empty" id="noclients" hidden>no clients registered</div>
</section>

<section id="history">
  <h2>History</h2>
  <div class="tools">
    <input id="q" type="search" placeholder="search command, client, message…" autocomplete="off" spellcheck="false">
    <select id="fClient"><option value="">all clients</option></select>
    <select id="fKind"><option value="">all events</option></select>
    <label><input type="checkbox" id="live" checked> live</label>
    <span class="muted" id="count"></span>
  </div>
  <div class="scroll"><table id="hist">
    <thead><tr><th>date</th><th>time</th><th>event</th><th>client</th><th>detail</th></tr></thead>
    <tbody></tbody>
  </table></div>
  <div class="empty" id="noevents" hidden>nothing yet</div>
</section>

<div id="backdrop" hidden></div>
<aside id="detail" hidden>
  <div class="dhead"><b id="dtitle">command</b><button id="dclose" type="button">close</button></div>
  <div id="detailBody"></div>
</aside>

<script>
const $ = (id) => document.getElementById(id);
const txt = (v) => document.createTextNode(String(v));
const gate = $('gate');
const gateErr = $('gateErr');
const sections = ['cards', 'sessions', 'history'].map($);
let token = sessionStorage.getItem('rcmd-token');

// Everything the page renders comes from the relay, so it is written with text nodes only —
// never innerHTML — and a command's output is escaped by the DOM rather than by us.
const state = { q: '', client: '', kind: '', selected: null };
let latest = null;
let lastDrawn = null;
// Set while the page is deliberately asking for a token, so the next refresh does not close the
// prompt out from under the person typing into it.
let gateWanted = false;

// A token in the query string still works (older bookmarks), but it is moved into the session and
// wiped from the address bar so it stops leaking through history and the referrer.
const fromUrl = new URLSearchParams(location.search).get('token');
if (fromUrl) {
  token = fromUrl;
  sessionStorage.setItem('rcmd-token', token);
  history.replaceState(null, '', location.pathname);
}

function showGate(message, hideBody) {
  gateWanted = true;
  gateErr.textContent = message || '';
  gate.hidden = false;
  if (hideBody) {
    token = null;
    sessionStorage.removeItem('rcmd-token');
    sections.forEach((el) => { el.hidden = true; });
    $('sub').textContent = 'this relay needs a token';
  }
  $('token').focus();
}

gate.addEventListener('submit', (e) => {
  e.preventDefault();
  const value = $('token').value.trim();
  if (!value) return;
  token = value;
  sessionStorage.setItem('rcmd-token', token);
  $('token').value = '';
  gateWanted = false;
  gate.hidden = true;
  gateErr.textContent = '';
  sections.forEach((el) => { el.hidden = false; });
  tick();
  if (state.selected) loadDetail(state.selected);
});

async function api(path) {
  const res = await fetch(path, { headers: token ? { 'X-Token': token } : {} });
  if (res.status === 401 || res.status === 429) {
    const err = new Error('auth');
    err.auth = res.status;
    throw err;
  }
  return res.json();
}

function dur(s) {
  if (s < 0) return '-';
  const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60);
  return h ? `${h}h ${m}m` : m ? `${m}m ${s % 60}s` : `${s}s`;
}

function ms(v) {
  return v >= 1000 ? `${(v / 1000).toFixed(v >= 10000 ? 0 : 1)}s` : `${v}ms`;
}

// Matches FormatBytes on the relay: whole bytes stay whole, so a few hundred of them never round
// away to "0 MB".
function bytes(v) {
  if (v < 1024) return `${v} B`;
  const units = ['kB', 'MB', 'GB', 'TB'];
  let n = v / 1024, i = 0;
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
  return `${n < 10 ? n.toFixed(1) : Math.round(n)} ${units[i]}`;
}

const pad = (n) => String(n).padStart(2, '0');
// The relay stamps events in UTC; the columns show them in the viewer's own zone, in fixed-width
// form so the two columns stay aligned whatever the browser locale would have done.
const localDate = (d) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
const localTime = (d) => `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;

function card(value, label) {
  const d = document.createElement('div');
  d.className = 'card';
  const b = document.createElement('b'); b.append(txt(value));
  const s = document.createElement('span'); s.append(txt(label));
  d.append(b, s);
  return d;
}

function cell(value, className) {
  const td = document.createElement('td');
  if (className) td.className = className;
  td.append(txt(value));
  return td;
}

// Keeps a <select>'s options in step with the values actually present, without losing the choice
// the user made (or dropping a value that has scrolled out of the current history window).
function syncOptions(select, values, allLabel) {
  const chosen = select.value;
  const wanted = [...new Set(values)].filter(Boolean).sort();
  if (chosen && !wanted.includes(chosen)) wanted.push(chosen);
  const current = [...select.options].slice(1).map((o) => o.value);
  if (current.length === wanted.length && current.every((v, i) => v === wanted[i])) return;
  select.replaceChildren();
  const all = document.createElement('option');
  all.value = '';
  all.append(txt(allLabel));
  select.append(all);
  for (const v of wanted) {
    const o = document.createElement('option');
    o.value = v;
    o.append(txt(v));
    select.append(o);
  }
  select.value = chosen;
}

function renderCards(clients, info) {
  $('cards').replaceChildren(
    card(`${clients.connected}/${clients.count}`, 'clients online'),
    card(info.stats.execs, 'commands'),
    card(info.stats.timeouts, 'timeouts'),
    card(info.stats.uploads, 'uploads'),
    card(info.stats.downloads, 'downloads'),
    card(bytes(info.stats.bytesUploaded), 'uploaded'),
    card(bytes(info.stats.bytesDownloaded), 'downloaded'),
    card(info.stats.authFailures, 'auth failures'),
  );
}

function renderSessions(clients) {
  const tbody = document.querySelector('#tbl tbody');
  tbody.replaceChildren();
  for (const c of clients.clients) {
    const tr = document.createElement('tr');
    tr.append(
      cell(c.name), cell(c.id.slice(0, 8)), cell(c.ip || '-'), cell(c.token),
      cell(c.state, c.state === 'idle' ? 'idle' : c.state === 'stale' ? 'stale' : 'busy'),
      cell(c.secondsAgo < 0 ? '-' : c.secondsAgo + 's'),
      cell(c.running), cell(c.queued), cell(c.served), cell(dur(c.connectedForSeconds)),
    );
    tbody.append(tr);
  }
  $('noclients').hidden = clients.count > 0;
  $('tbl').hidden = clients.count === 0;
}

function renderHistory(info) {
  // The relay hands history back oldest-first; sort by the timestamp and fall back on arrival order
  // so entries that share a second still come out newest-first instead of in an arbitrary order.
  const all = info.events
    .map((e, i) => ({ ...e, i, when: new Date(e.at) }))
    .sort((a, b) => (b.when - a.when) || (b.i - a.i));

  syncOptions($('fClient'), all.map((e) => e.client), 'all clients');
  syncOptions($('fKind'), all.map((e) => e.kind), 'all events');

  const needle = state.q.toLowerCase();
  const rows = all.filter((e) =>
    (!state.client || e.client === state.client) &&
    (!state.kind || e.kind === state.kind) &&
    (!needle || `${e.kind} ${e.client} ${e.message}`.toLowerCase().includes(needle)));

  $('count').textContent = rows.length === all.length
    ? `${all.length} event(s)`
    : `${rows.length} of ${all.length} event(s)`;
  $('noevents').hidden = rows.length > 0;
  $('hist').hidden = rows.length === 0;

  // Rebuilding the rows on every poll would yank them out from under a click and reset the text
  // selection, so redraw only when what would be drawn has actually changed.
  const signature = JSON.stringify([state.selected, rows.map((e) => [e.at, e.kind, e.client, e.message, e.id])]);
  if (signature === lastDrawn) return;
  lastDrawn = signature;

  const tbody = document.querySelector('#hist tbody');
  tbody.replaceChildren();
  for (const e of rows) {
    const tr = document.createElement('tr');
    if (e.id) {
      tr.dataset.id = e.id;
      tr.title = 'show output';
      if (e.id === state.selected) tr.className = 'sel';
    }
    tr.append(
      cell(localDate(e.when), 'muted'),
      cell(localTime(e.when), 'muted'),
      cell(e.kind, 'k-' + e.kind),
      cell(e.client),
      cell(e.message, 'msg'),
    );
    tbody.append(tr);
  }
}

document.querySelector('#hist tbody').addEventListener('click', (e) => {
  const tr = e.target.closest('tr[data-id]');
  if (!tr) return;
  state.selected = tr.dataset.id;
  if (latest) renderHistory(latest);
  openDetail();
  loadDetail(state.selected);
});

function openDetail() {
  $('backdrop').hidden = false;
  $('detail').hidden = false;
}

function closeDetail() {
  $('backdrop').hidden = true;
  $('detail').hidden = true;
  state.selected = null;
  if (latest) renderHistory(latest);
}

$('dclose').addEventListener('click', closeDetail);
$('backdrop').addEventListener('click', closeDetail);
document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !$('detail').hidden) closeDetail(); });

function detailNote(message) {
  const p = document.createElement('div');
  p.className = 'note';
  p.append(txt(message));
  $('detailBody').replaceChildren(p);
}

function field(label, value, className) {
  const k = document.createElement('span'); k.append(txt(label));
  const v = document.createElement('div');
  if (className) v.className = className;
  v.append(txt(value));
  return [k, v];
}

function block(label, value) {
  const h = document.createElement('div');
  h.className = 'dlabel';
  h.append(txt(label));
  const pre = document.createElement('pre');
  pre.append(txt(value.length ? value : '(empty)'));
  return [h, pre];
}

async function loadDetail(id) {
  $('dtitle').textContent = 'command output';
  detailNote('loading…');
  let d;
  try {
    d = await api('/api/command?id=' + encodeURIComponent(id));
  } catch (err) {
    // A relay started with --open-status serves the overview to anyone, but command output can
    // carry passwords, so it always needs a token — ask for one without hiding the rest of the page.
    if (err.auth === 401) { detailNote('command output needs a relay token'); showGate('', false); }
    else if (err.auth === 429) detailNote('too many attempts — wait a few minutes');
    else detailNote('relay unreachable');
    return;
  }
  if (!d || d.error) {
    detailNote(d && d.error ? d.error : 'no stored output — the command may still be running');
    return;
  }

  const started = new Date(d.startedUtc);
  const kv = document.createElement('div');
  kv.className = 'kv';
  kv.append(
    ...field('client', d.client),
    ...field('started', `${localDate(started)} ${localTime(started)}`),
    ...field('duration', ms(d.durationMs)),
    ...field('exit code', d.exitCode, d.exitCode === 0 ? 'ok' : 'bad'),
  );

  const parts = [kv];
  if (d.truncated) {
    const note = document.createElement('div');
    note.className = 'note';
    note.append(txt('output was truncated by the relay — only the first part is kept'));
    parts.push(note);
  }
  parts.push(...block('command', d.command), ...block('stdout', d.stdout), ...block('stderr', d.stderr));
  $('detailBody').replaceChildren(...parts);
}

for (const [id, key] of [['q', 'q'], ['fClient', 'client'], ['fKind', 'kind']]) {
  $(id).addEventListener('input', () => {
    state[key] = $(id).value;
    if (latest) renderHistory(latest);
  });
}

async function tick() {
  let clients, info;
  try {
    [clients, info] = await Promise.all([api('/api/clients'), api('/api/events?limit=200')]);
  } catch (e) {
    // A relay started with --open-status serves these without a token, so the form only appears
    // when the relay actually demands one.
    if (e.auth === 401) showGate(token ? 'wrong token' : '', true);
    else if (e.auth === 429) showGate('too many attempts — wait a few minutes', true);
    else $('sub').textContent = 'relay unreachable';
    return;
  }

  if (!gateWanted) gate.hidden = true;
  sections.forEach((el) => { el.hidden = false; });
  latest = info;

  $('sub').textContent =
    `up ${dur(info.uptimeSeconds)} · ${info.tls ? 'TLS' : 'plain http'} · ${info.tokens} token(s) accepted`;

  renderCards(clients, info);
  renderSessions(clients);
  renderHistory(info);
}

tick();
setInterval(() => { if ($('live').checked) tick(); }, 3000);
</script>
</body>
</html>
""";
}
