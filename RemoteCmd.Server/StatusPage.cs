/// <summary>
/// Single-file status page served at /ui. The page itself carries no data and needs no token to
/// load; it asks for one in the browser, keeps it in sessionStorage and sends it as an X-Token
/// header, so the token never appears in a URL, in browser history or in the relay's access log.
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
  :root { color-scheme: light dark; --bg:#fff; --fg:#111; --muted:#666; --line:#e3e3e3; --ok:#1a7f37; --warn:#9a6700; --bad:#b42318; --card:#fafafa; }
  @media (prefers-color-scheme: dark) { :root { --bg:#14161a; --fg:#e8e8e8; --muted:#9aa0a6; --line:#2a2e35; --ok:#3fb950; --warn:#d29922; --bad:#f85149; --card:#1b1e24; } }
  * { box-sizing: border-box; }
  body { margin:0; padding:24px; background:var(--bg); color:var(--fg);
         font:14px/1.5 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
  h1 { font-size:17px; margin:0 0 4px; }
  h2 { font-size:13px; text-transform:uppercase; letter-spacing:.08em; color:var(--muted); margin:28px 0 8px; }
  .sub { color:var(--muted); margin-bottom:20px; }
  .cards { display:flex; flex-wrap:wrap; gap:10px; }
  .card { background:var(--card); border:1px solid var(--line); border-radius:8px; padding:10px 14px; min-width:110px; }
  .card b { display:block; font-size:19px; font-weight:600; }
  .card span { color:var(--muted); font-size:12px; }
  .scroll { overflow-x:auto; }
  table { border-collapse:collapse; width:100%; min-width:640px; }
  th, td { text-align:left; padding:6px 10px; border-bottom:1px solid var(--line); white-space:nowrap; }
  th { color:var(--muted); font-weight:600; }
  .idle { color:var(--ok); } .stale { color:var(--bad); } .busy { color:var(--warn); }
  .ev { display:grid; grid-template-columns:78px 84px 150px 1fr; gap:8px; padding:3px 0; border-bottom:1px solid var(--line); }
  .ev .k { color:var(--muted); }
  .empty { color:var(--muted); padding:8px 0; }
  #gate { display:flex; gap:8px; align-items:center; margin:16px 0 24px; flex-wrap:wrap; }
  #gate input { font:inherit; padding:8px 10px; min-width:280px; background:var(--card); color:var(--fg);
                border:1px solid var(--line); border-radius:6px; }
  #gate button { font:inherit; padding:8px 16px; border-radius:6px; border:1px solid var(--line);
                 background:var(--card); color:var(--fg); cursor:pointer; }
  #gate .err { color:var(--bad); }
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

<h2>Sessions</h2>
<div class="scroll"><table id="tbl">
  <thead><tr><th>name</th><th>id</th><th>token</th><th>state</th><th>last poll</th><th>run</th><th>queue</th><th>served</th><th>connected</th></tr></thead>
  <tbody></tbody>
</table></div>
<div class="empty" id="noclients" hidden>no clients registered</div>

<h2>History</h2>
<div id="events"></div>

<script>
const txt = (v) => document.createTextNode(String(v));
const gate = document.getElementById('gate');
const gateErr = document.getElementById('gateErr');
const body = ['cards', 'tbl', 'events'].map((id) => document.getElementById(id));
let token = sessionStorage.getItem('rcmd-token');

// A token in the query string still works (older bookmarks), but it is moved into the session and
// wiped from the address bar so it stops leaking through history and the referrer.
const fromUrl = new URLSearchParams(location.search).get('token');
if (fromUrl) {
  token = fromUrl;
  sessionStorage.setItem('rcmd-token', token);
  history.replaceState(null, '', location.pathname);
}

function askForToken(message) {
  token = null;
  sessionStorage.removeItem('rcmd-token');
  gateErr.textContent = message || '';
  gate.hidden = false;
  body.forEach((el) => { el.hidden = true; });
  document.getElementById('noclients').hidden = true;
  document.getElementById('sub').textContent = 'this relay needs a token';
  document.getElementById('token').focus();
}

gate.addEventListener('submit', (e) => {
  e.preventDefault();
  const value = document.getElementById('token').value.trim();
  if (!value) return;
  token = value;
  sessionStorage.setItem('rcmd-token', token);
  document.getElementById('token').value = '';
  gate.hidden = true;
  gateErr.textContent = '';
  body.forEach((el) => { el.hidden = false; });
  tick();
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

function card(value, label) {
  const d = document.createElement('div');
  d.className = 'card';
  const b = document.createElement('b'); b.append(txt(value));
  const s = document.createElement('span'); s.append(txt(label));
  d.append(b, s);
  return d;
}

async function tick() {
  let clients, info;
  try {
    [clients, info] = await Promise.all([api('/api/clients'), api('/api/events?limit=80')]);
  } catch (e) {
    // A relay started with --open-status serves these without a token, so the form only appears
    // when the relay actually demands one.
    if (e.auth === 401) askForToken(token ? 'wrong token' : '');
    else if (e.auth === 429) askForToken('too many attempts — wait a few minutes');
    else document.getElementById('sub').textContent = 'relay unreachable';
    return;
  }

  gate.hidden = true;
  body.forEach((el) => { el.hidden = false; });

  document.getElementById('sub').textContent =
    `up ${dur(info.uptimeSeconds)} · ${info.tls ? 'TLS' : 'plain http'} · ${info.tokens} token(s) accepted`;

  const cards = document.getElementById('cards');
  cards.replaceChildren(
    card(`${clients.connected}/${clients.count}`, 'clients online'),
    card(info.stats.execs, 'commands'),
    card(info.stats.timeouts, 'timeouts'),
    card(info.stats.uploads, 'uploads'),
    card(info.stats.downloads, 'downloads'),
    card(Math.round(info.stats.bytesUploaded / 1048576) + ' MB', 'uploaded'),
    card(Math.round(info.stats.bytesDownloaded / 1048576) + ' MB', 'downloaded'),
    card(info.stats.authFailures, 'auth failures'),
  );

  const tbody = document.querySelector('#tbl tbody');
  tbody.replaceChildren();
  for (const c of clients.clients) {
    const tr = document.createElement('tr');
    const cells = [c.name, c.id.slice(0, 8), c.token, c.state,
                   c.secondsAgo < 0 ? '-' : c.secondsAgo + 's',
                   c.running, c.queued, c.served, dur(c.connectedForSeconds)];
    cells.forEach((v, i) => {
      const td = document.createElement('td');
      if (i === 3) td.className = v === 'idle' ? 'idle' : v === 'stale' ? 'stale' : 'busy';
      td.append(txt(v));
      tr.append(td);
    });
    tbody.append(tr);
  }
  document.getElementById('noclients').hidden = clients.count > 0;
  document.getElementById('tbl').hidden = clients.count === 0;

  const ev = document.getElementById('events');
  ev.replaceChildren();
  for (const e of info.events.slice().reverse()) {
    const row = document.createElement('div');
    row.className = 'ev';
    const at = new Date(e.at).toLocaleTimeString();
    for (const [cls, v] of [['k', at], ['k', e.kind], ['', e.client], ['', e.message]]) {
      const d = document.createElement('div');
      d.className = cls;
      d.append(txt(v));
      row.append(d);
    }
    ev.append(row);
  }
  if (!info.events.length) {
    const d = document.createElement('div');
    d.className = 'empty';
    d.append(txt('nothing yet'));
    ev.append(d);
  }
}

tick();
setInterval(tick, 3000);
</script>
</body>
</html>
""";
}
