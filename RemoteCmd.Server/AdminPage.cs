/// <summary>
/// The dashboard's management page (pairing, credentials, password) and its sign-in page. Both are
/// served only on the loopback dashboard listener. Everything shown is built from text nodes; no
/// markup is ever assembled from relay data.
/// </summary>
public static class AdminPage
{
    private const string Head = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Ccircle cx='8' cy='8' r='5.2' fill='none' stroke='%23c07a12' stroke-width='2.4'/%3E%3C/svg%3E">
<style>
  :root {
    color-scheme: light dark;
    --bg:#f5f2ec; --panel:#fff; --raise:#ede8df; --grid:#dcd5c8;
    --fg:#181512; --dim:#6a6255; --faint:#9a9184; --sig:#a85e00; --bad:#c0271a;
    --gut: clamp(14px, 1.2vw, 32px);
    --mono: ui-monospace, SFMono-Regular, "SF Mono", "Cascadia Mono", Menlo, Consolas, monospace;
    --face: "Segoe UI Variable Text", "Segoe UI", -apple-system, BlinkMacSystemFont, Roboto, system-ui, sans-serif;
  }
  @media (prefers-color-scheme: dark) {
    :root { --bg:#0f0e0c; --panel:#15130f; --raise:#1d1a15; --grid:#2a2620;
            --fg:#e6e0d4; --dim:#948c7c; --faint:#5d5648; --sig:#ffae3b; --bad:#ff5747; }
  }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--fg); font:13px/1.5 var(--face); }
  header { position:sticky; top:0; display:flex; align-items:center; gap:12px; height:46px;
           padding-inline:var(--gut); border-bottom:1px solid var(--grid); background:var(--bg); }
  .brand { font-size:11px; font-weight:600; letter-spacing:.16em; text-transform:uppercase; }
  .brand i { font-style:normal; color:var(--faint); }
  .grow { flex:1; }
  main { max-width:980px; padding:16px var(--gut) 48px; display:grid; gap:18px; }
  section { background:var(--panel); border:1px solid var(--grid); border-radius:6px; padding:14px 16px; }
  h2 { margin:0 0 10px; font-size:12px; letter-spacing:.08em; text-transform:uppercase; color:var(--dim); }
  p { margin:0 0 10px; color:var(--dim); }
  input, select, button, .btn { font:inherit; padding:6px 10px; border:1px solid var(--grid); border-radius:4px;
                                background:var(--panel); color:var(--fg); }
  button, .btn { cursor:pointer; text-decoration:none; }
  button.primary { background:var(--sig); border-color:var(--sig); color:#fff; }
  button.danger { color:var(--bad); }
  .row { display:flex; gap:8px; flex-wrap:wrap; align-items:center; }
  .code { font:600 28px/1.2 var(--mono); letter-spacing:.12em; color:var(--sig); }
  pre { margin:4px 0 10px; padding:8px 10px; background:var(--raise); border-radius:4px; font:12px/1.45 var(--mono);
        white-space:pre-wrap; word-break:break-all; }
  table { width:100%; border-collapse:collapse; }
  th, td { text-align:left; padding:6px 6px; border-bottom:1px solid var(--grid); }
  th { font-size:11px; color:var(--dim); font-weight:600; }
  td { font-family:var(--mono); font-size:12px; }
  .muted { color:var(--faint); }
  .err { color:var(--bad); }
  .secret { border:1px dashed var(--sig); padding:10px; border-radius:4px; }
  [hidden] { display:none !important; }
</style>
""";

    public const string Login = Head + """
<title>RemoteCmd — sign in</title>
</head>
<body>
<header><div class="brand">RemoteCmd <i>relay</i></div></header>
<main>
  <section>
    <h2>Dashboard password</h2>
    <form id="f" class="row">
      <input id="pw" type="password" autocomplete="current-password" autofocus placeholder="password" required>
      <button class="primary" type="submit">Sign in</button>
      <span class="err" id="err"></span>
    </form>
  </section>
</main>
<script>
document.getElementById('f').addEventListener('submit', async (e) => {
  e.preventDefault();
  const res = await fetch('/api/admin/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-RemoteCmd': '1' },
    body: JSON.stringify({ password: document.getElementById('pw').value }),
  });
  if (res.ok) { location.href = '/'; return; }
  document.getElementById('err').textContent = res.status === 429 ? 'Too many attempts, wait a few minutes' : 'Wrong password';
});
</script>
</body>
</html>
""";

    public const string Manage = Head + """
<title>RemoteCmd — pair & manage</title>
</head>
<body>
<header>
  <div class="brand">RemoteCmd <i>relay</i></div>
  <a class="btn" href="/">← Status</a>
  <div class="grow"></div>
  <span class="muted" id="ver"></span>
  <button id="logout" hidden>Sign out</button>
</header>
<main>
  <section>
    <h2>Pair a machine</h2>
    <p>Creates a one-time code. Run one of the commands on the machine you want to add: it installs the
       client, trades the code for that machine's own key and starts it as a service.</p>
    <form id="pairForm" class="row">
      <input id="pairName" placeholder="name (optional, else its hostname)" maxlength="64">
      <select id="pairTtl" aria-label="valid for">
        <option value="15">valid 15 min</option><option value="60">valid 1 hour</option><option value="1440">valid 24 hours</option>
      </select>
      <button class="primary" type="submit">Create code</button>
    </form>
    <div id="pending"></div>
  </section>

  <section>
    <h2>Relay address</h2>
    <p>The address machines use to reach this relay. It goes into the install commands above.</p>
    <form id="urlForm" class="row">
      <input id="url" size="40" placeholder="https://relay.example:7890">
      <button type="submit">Save</button>
      <span class="muted" id="urlNote"></span>
    </form>
    <p class="muted" id="tlsNote"></p>
  </section>

  <section>
    <h2>Access</h2>
    <p>Every paired machine has its own key and can only take commands, never send them. Controllers
       (scripts, curl, AI agents) send commands. Revoking cuts a machine or controller off at once.</p>
    <table>
      <thead><tr><th>name</th><th>role</th><th>last used</th><th>online</th><th></th></tr></thead>
      <tbody id="creds"></tbody>
    </table>
    <form id="tokForm" class="row" style="margin-top:12px">
      <input id="tokName" placeholder="controller name, e.g. ci-pipeline" maxlength="64" required>
      <button type="submit">New controller token</button>
    </form>
    <div id="newTok"></div>
  </section>

  <section>
    <h2>Dashboard password</h2>
    <p id="pwState"></p>
    <form id="pwForm" class="row">
      <input id="pw1" type="password" autocomplete="new-password" placeholder="new password (8+ chars)" minlength="8">
      <input id="pw2" type="password" autocomplete="new-password" placeholder="repeat">
      <button type="submit">Set password</button>
      <button type="button" class="danger" id="pwClear" hidden>Remove password</button>
      <span class="err" id="pwErr"></span>
    </form>
  </section>
</main>

<script>
const $ = (id) => document.getElementById(id);
const el = (tag, text, cls) => { const e = document.createElement(tag); if (text != null) e.textContent = text; if (cls) e.className = cls; return e; };

async function api(path, method = 'GET', body) {
  const res = await fetch(path, {
    method,
    headers: { 'Content-Type': 'application/json', 'X-RemoteCmd': '1' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (res.status === 401) { location.href = '/login'; throw new Error('auth'); }
  const data = res.headers.get('content-type')?.includes('json') ? await res.json() : null;
  if (!res.ok) throw new Error(data?.error || `HTTP ${res.status}`);
  return data;
}

function copyButton(text) {
  const b = el('button', 'Copy');
  b.type = 'button';
  b.addEventListener('click', async () => {
    await navigator.clipboard.writeText(text);
    b.textContent = 'Copied';
    setTimeout(() => { b.textContent = 'Copy'; }, 1500);
  });
  return b;
}

function ago(iso) {
  if (!iso) return 'never';
  const s = Math.max(0, (Date.now() - new Date(iso)) / 1000);
  if (s < 90) return 'just now';
  if (s < 5400) return `${Math.round(s / 60)} min ago`;
  if (s < 129600) return `${Math.round(s / 3600)} h ago`;
  return `${Math.round(s / 86400)} d ago`;
}

function renderPairing(p) {
  const box = el('div');
  box.style.marginTop = '14px';
  const head = el('div', null, 'row');
  head.append(el('span', p.code, 'code'));
  head.append(el('span', `${p.name ? 'for ' + p.name + ' · ' : ''}valid until ${new Date(p.expiresUtc).toLocaleTimeString()}`, 'muted'));
  const cancel = el('button', 'Cancel');
  cancel.type = 'button';
  cancel.addEventListener('click', async () => { await api(`/api/admin/pair/${encodeURIComponent(p.id)}`, 'DELETE'); loadPending(); });
  head.append(cancel);
  box.append(head);
  for (const [label, cmd] of Object.entries(p.commands)) {
    const row = el('div', null, 'row');
    row.style.marginTop = '8px';
    row.append(el('b', label), copyButton(cmd));
    box.append(row, el('pre', cmd));
  }
  return box;
}

async function loadPending() {
  const { pending } = await api('/api/admin/pair');
  $('pending').replaceChildren(...pending.map(renderPairing));
}

async function loadInfo() {
  const info = await api('/api/admin/info');
  $('ver').textContent = `v${info.version}`;
  $('url').value = info.publicUrl;
  $('urlNote').textContent = info.publicUrlSet ? '' : '(guessed — save it if machines reach the relay by another address)';
  $('tlsNote').textContent = info.tls
    ? `TLS on. Paired machines pin certificate ${info.certSha256}.`
    : 'TLS is off: commands stay encrypted, but tokens of hand-configured clients travel in clear text and a paired machine cannot verify it talks to this relay. Run the relay without --no-tls where you can.';
  $('pwState').textContent = info.passwordSet
    ? 'Set. Opening the dashboard asks for it.'
    : 'Not set. The dashboard listens on this machine only, so anyone with a session on it can open it.';
  $('pwClear').hidden = !info.passwordSet;
  $('logout').hidden = !info.passwordSet;
}

async function loadCreds() {
  const { credentials } = await api('/api/admin/credentials');
  const rows = credentials.map((c) => {
    const tr = el('tr');
    tr.append(el('td', c.name), el('td', c.ephemeral ? `${c.role} (command line)` : c.role),
              el('td', ago(c.lastUsedUtc)), el('td', c.online ? String(c.online) : ''));
    const td = el('td');
    if (!c.ephemeral) {
      const b = el('button', 'Revoke', 'danger');
      b.addEventListener('click', async () => {
        if (!confirm(`Revoke "${c.name}"? It is cut off immediately.`)) return;
        await api(`/api/admin/credentials/${encodeURIComponent(c.id)}`, 'DELETE');
        loadCreds();
      });
      td.append(b);
    } else {
      td.append(el('span', 'restart without it to remove', 'muted'));
    }
    tr.append(td);
    return tr;
  });
  $('creds').replaceChildren(...rows);
}

$('pairForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  await api('/api/admin/pair', 'POST', { name: $('pairName').value.trim() || null, ttlMinutes: Number($('pairTtl').value) });
  $('pairName').value = '';
  loadPending();
});

$('urlForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  try { await api('/api/admin/settings', 'POST', { publicUrl: $('url').value.trim() || null }); }
  catch (err) { $('urlNote').textContent = err.message; return; }
  await loadInfo();
  loadPending();
});

$('tokForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  const t = await api('/api/admin/credentials', 'POST', { name: $('tokName').value.trim(), role: 'controller' });
  $('tokName').value = '';
  const box = el('div', null, 'secret');
  box.style.marginTop = '10px';
  const row = el('div', null, 'row');
  row.append(el('b', `Token for ${t.name} — shown only now`), copyButton(t.token));
  box.append(row, el('pre', t.token));
  $('newTok').replaceChildren(box);
  loadCreds();
});

$('pwForm').addEventListener('submit', async (e) => {
  e.preventDefault();
  $('pwErr').textContent = '';
  if ($('pw1').value !== $('pw2').value) { $('pwErr').textContent = 'The passwords differ'; return; }
  try { await api('/api/admin/password', 'POST', { password: $('pw1').value }); }
  catch (err) { $('pwErr').textContent = err.message; return; }
  $('pw1').value = $('pw2').value = '';
  loadInfo();
});

$('pwClear').addEventListener('click', async () => {
  if (!confirm('Remove the password? Anyone with a session on this machine can then open the dashboard.')) return;
  await api('/api/admin/password', 'POST', { password: null });
  loadInfo();
});

$('logout').addEventListener('click', async () => {
  await api('/api/admin/logout', 'POST');
  location.href = '/login';
});

loadInfo(); loadPending(); loadCreds();
setInterval(() => { loadCreds(); loadPending(); }, 5000);
</script>
</body>
</html>
""";
}
