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
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Ccircle cx='8' cy='8' r='5.2' fill='none' stroke='%23c07a12' stroke-width='2.4'/%3E%3C/svg%3E">
<style>
  /* PHOSPHOR DECK — an amber dispatch desk. Warm graphite instead of slate, one amber accent, red
     for broken, blue for transfers. Nothing else carries colour: "fine" is meant to be invisible.
     At rest the only thing that moves on the page is the 14px poll heartbeat, so any motion in the
     corner of the eye is a real event. */
  :root {
    color-scheme: light dark;
    --bg:#f5f2ec; --panel:#fff; --raise:#ede8df; --grid:#dcd5c8; --grid-soft:#e8e2d6;
    --fg:#181512; --dim:#6a6255; --faint:#9a9184;
    --sig:#a85e00; --bad:#c0271a; --xfer:#2a4fbf;
    --t0:22%; --t1:11%; --t2:5%;
    --gut: clamp(14px, 1.2vw, 32px);
    --rail: 46px;
    --mono: ui-monospace, SFMono-Regular, "SF Mono", "Cascadia Mono", "Segoe UI Mono", Menlo, Consolas, "DejaVu Sans Mono", monospace;
    --face: "Segoe UI Variable Text", "Segoe UI", -apple-system, BlinkMacSystemFont, Roboto, system-ui, sans-serif;
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --bg:#0f0e0c; --panel:#15130f; --raise:#1d1a15; --grid:#2a2620; --grid-soft:#201d18;
      --fg:#e6e0d4; --dim:#948c7c; --faint:#5d5648;
      --sig:#ffae3b; --bad:#ff5747; --xfer:#7fa6ff;
      --t0:26%; --t1:12%; --t2:5%;
    }
  }
  * { box-sizing: border-box; }
  body { margin:0; padding:0; background:var(--bg); color:var(--fg);
         font:12.5px/1.45 var(--mono); }
  /* Two voices: anything the relay said is monospace, anything the developer wrote is the UI face. */
  .lbl, h2, th, button, label, .brand { font-family:var(--face); }
  b, .val, td, input, select { font-variant-numeric: tabular-nums; }

  /* ---------- A. header rail ---------- */
  #rail { position:sticky; top:0; z-index:20; height:var(--rail); background:var(--bg);
          border-bottom:1px solid var(--grid); display:flex; align-items:center; gap:10px;
          padding-inline:var(--gut); }
  .brand { font-size:11px; font-weight:600; letter-spacing:.16em; text-transform:uppercase;
           white-space:nowrap; }
  .brand i { font-style:normal; color:var(--faint); }
  #chips { display:flex; gap:8px; align-items:center; color:var(--dim); font-size:11px;
           white-space:nowrap; }
  #chips .sep { width:1px; height:16px; background:var(--grid); }
  #chips .bad { color:var(--bad); }
  .grow { flex:1 1 auto; }
  #beat { display:flex; align-items:center; gap:6px; color:var(--dim); font-size:11px;
          white-space:nowrap; }
  #ring { stroke:var(--sig); }
  #ring.dead { stroke:var(--bad); }
  #ring.paused { stroke:var(--faint); }
  #rail input, #rail select, #gate input, #gate button, #dclose, .btn {
    font:inherit; font-family:var(--face); font-size:12px; padding:5px 8px;
    background:var(--panel); color:var(--fg); border:1px solid var(--grid); border-radius:4px; }
  #rail label { color:var(--dim); display:flex; align-items:center; gap:5px; font-size:12px; }
  #q { min-width:220px; flex:0 1 300px; }
  @media (max-width: 1100px) { #chips { display:none; } }
  @media (max-width: 760px) { #rail { height:auto; padding-block:8px; flex-wrap:wrap; } }

  /* ---------- gate ---------- */
  #gate { display:flex; gap:8px; align-items:center; padding:16px var(--gut); flex-wrap:wrap; }
  #gate input { min-width:280px; padding:8px 10px; }
  #gate button { padding:8px 16px; cursor:pointer; }
  #gate .err { color:var(--bad); }

  /* ---------- B. KPI deck ---------- */
  #deckWrap { padding:12px var(--gut); }
  /* Hairlines are grid gaps, not borders — no doubled-up rules between tiles. */
  #deck { display:grid; gap:1px; background:var(--grid); border:1px solid var(--grid); }
  .kpi { background:var(--panel); padding:9px 13px 10px; min-width:0; position:relative; }
  @media (max-width:899px)  { #deck { grid-template-columns:repeat(2,1fr); } }
  @media (min-width:900px)  { #deck { grid-template-columns:repeat(4,1fr); } }
  @media (min-width:1500px) { #deck { grid-template-columns:repeat(10,1fr); }
                              .kpi.hero { grid-column:span 2; } }
  .kpi .lbl { font-size:10px; font-weight:600; letter-spacing:.13em; text-transform:uppercase;
              color:var(--dim); display:flex; align-items:baseline; gap:6px; }
  .kpi .val { display:block; margin:7px 0 3px; font-family:var(--mono); font-weight:600;
              font-size:clamp(18px,1.1vw,22px); letter-spacing:-.01em; line-height:1; }
  .kpi.hero .val { font-size:clamp(28px,2.1vw,40px); letter-spacing:-.02em; }
  .kpi .sub { font-size:11px; color:var(--dim); min-height:16px; display:block;
              overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  .kpi .chip { font-size:11px; font-weight:600; color:var(--sig); margin-left:auto; }
  .kpi.alarm { box-shadow:inset 2px 0 0 var(--bad); }
  .kpi.alarm .val { color:var(--bad); }
  .kpi.quiet .val, .kpi.quiet .sub { color:var(--faint); }
  .val.flash { animation:flash 320ms ease-out; }
  @keyframes flash { from { color:var(--sig); } to { color:inherit; } }
  .chip.rise { animation:rise 140ms ease-out; }
  @keyframes rise { from { opacity:0; transform:translateX(6px); } to { opacity:1; transform:none; } }
  #spark { display:block; width:100%; height:14px; }
  #spark polyline { fill:none; stroke:var(--sig); stroke-width:1.4; vector-effect:non-scaling-stroke; }
  #dismiss { border:0; background:none; color:var(--bad); font-size:11px; cursor:pointer;
             padding:0; text-decoration:underline; font-family:var(--face); }

  /* ---------- C. activity ---------- */
  #activity { padding:0 var(--gut) 4px; }
  #actHead { display:flex; align-items:baseline; gap:10px; }
  /* The baseline keeps the panel legible when the window is quiet and there is nothing to draw. */
  #histo { display:block; width:100%; height:76px; border-bottom:1px solid var(--grid); }
  @media (max-width:899px) { #histo { height:52px; } }
  /* Pre-allocated rects: a transition only fires on a node that already had a computed value, so
     the bars are created once and only their geometry is rewritten. */
  #histo rect.bar { transition: y 300ms ease-out, height 300ms ease-out; }
  #histo rect.blind { fill:var(--faint); opacity:.10; }
  #histo line.edge { stroke:var(--grid); stroke-width:1; vector-effect:non-scaling-stroke; }
  .ch-fault { fill:var(--bad); } .ch-exec { fill:var(--sig); } .ch-xfer { fill:var(--xfer); }
  .ch-conn { fill:var(--dim); } .ch-gc { fill:var(--faint); }
  #histNote { color:var(--faint); font-size:11px; padding-top:2px; display:flex; gap:14px;
              flex-wrap:wrap; }
  #histNote i { font-style:normal; }
  #histNote .key::before { content:"\2588"; margin-right:4px; }
  .key.fault { color:var(--bad); } .key.exec { color:var(--sig); } .key.xfer { color:var(--xfer); }

  /* ---------- D. main ---------- */
  .main { display:grid; gap:var(--gut); padding:10px var(--gut) 40px; align-items:start;
          grid-template-columns:minmax(0,1fr); }
  @media (min-width:1280px) { .main { grid-template-columns:minmax(0,1fr) clamp(340px,22vw,420px); } }
  /* A hidden grid item still holds its track, so the third column only exists while a command is
     actually selected. */
  @media (min-width:2200px) {
    /* Column order follows the DOM: stream, client rail, detail. The output is the wide one. */
    .main.detail-open { grid-template-columns:minmax(0,1fr) clamp(340px,14vw,420px) clamp(480px,24vw,680px); }
    .main.detail-open > #detail { position:static; width:auto; inset:auto; box-shadow:none;
                                  border:1px solid var(--grid); max-height:none; overflow:visible; }
    .main.detail-open ~ #backdrop { display:none !important; }
  }
  h2 { font-size:11px; text-transform:uppercase; letter-spacing:.16em; color:var(--dim);
       margin:0 0 7px; display:flex; align-items:baseline; gap:10px; font-weight:600; }
  h2 .n { color:var(--faint); letter-spacing:0; font-family:var(--mono); font-size:11px; }
  h2 .burst { color:var(--sig); font-family:var(--mono); letter-spacing:0; }

  table { border-collapse:collapse; width:100%; table-layout:fixed; }
  th, td { text-align:left; padding:5px 8px; border-bottom:1px solid var(--grid-soft);
           vertical-align:top; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
  th { color:var(--dim); font-size:10px; font-weight:600; letter-spacing:.11em;
       text-transform:uppercase; background:var(--bg); }
  tr:last-child td { border-bottom:none; }
  #stream th { position:sticky; top:var(--rail); z-index:3; }
  #clientBox { max-height:min(46vh,560px); overflow:auto; border:1px solid var(--grid); }
  #clientBox th { position:sticky; top:0; z-index:2; }
  #streamBox { border:1px solid var(--grid); }

  td.msg { white-space:normal; }
  /* Three lines, and the full text is on the cell's tooltip: a command cut mid-path is worth
     nothing to whoever is reading the history. */
  td.msg > div { display:-webkit-box; -webkit-line-clamp:3; -webkit-box-orient:vertical;
                 overflow:hidden; max-width:140ch; word-break:break-word; }
  td.ts, td.tag { color:var(--dim); }
  td.tag { font-weight:600; letter-spacing:.05em; }
  .gut { width:3px; padding:0; border-bottom:none; }

  #hist tr[data-id] { cursor:pointer; }
  #hist tr[data-id]:hover td { background:var(--raise); }
  #hist tr.sel td { background:var(--raise); }
  #hist tr.sel td.ts { box-shadow:inset 2px 0 0 var(--sig); }
  /* Age is four static buckets recomputed on every repaint — a fade would say "just arrived" and
     then make everything identical, which is useless to anyone glancing over after four minutes. */
  #hist tr[data-age="0"] td { background:color-mix(in oklab, var(--heat) var(--t0), transparent);
                              box-shadow:inset 0 1px 0 color-mix(in oklab, var(--heat) 30%, transparent); }
  #hist tr[data-age="0"] td.ts { box-shadow:inset 2px 0 0 var(--heat); }
  #hist tr[data-age="1"] td { background:color-mix(in oklab, var(--heat) var(--t1), transparent); }
  #hist tr[data-age="1"] td.ts { box-shadow:inset 2px 0 0 color-mix(in oklab, var(--heat) 45%, transparent); }
  #hist tr[data-age="2"] td { background:color-mix(in oklab, var(--heat) var(--t2), transparent); }
  #hist tr.in { animation:rowIn 160ms ease-out backwards; }
  @keyframes rowIn { from { opacity:0; transform:translateY(-4px); } to { opacity:1; transform:none; } }
  #hist tr:focus-visible td { outline:1px solid var(--sig); outline-offset:-1px; }

  #tbl td.st { font-weight:600; }
  #tbl tr.stale td.st { color:var(--bad); }
  #tbl tr.stale td.gut { background:var(--bad); }
  #tbl tr.busy td.gut { background:var(--xfer); }
  #tbl tr.idle td.gut { background:var(--grid); }
  #tbl td.late { color:var(--dim); font-style:italic; }
  #tbl td.dead { color:var(--bad); }
  #tbl tr.changed td { animation:wash 900ms ease-out; }
  @keyframes wash { from { background:var(--raise); } to { background:transparent; } }

  .empty { color:var(--faint); padding:10px 2px; }
  #backdrop { position:fixed; inset:0; background:rgba(0,0,0,.35); border:0; padding:0; z-index:30; }
  #detail { position:fixed; top:0; right:0; bottom:0; width:min(660px,96vw); overflow:auto; z-index:31;
            background:var(--panel); border-left:1px solid var(--grid); padding:14px 16px 32px;
            box-shadow:-10px 0 26px rgba(0,0,0,.22); }
  .dhead { display:flex; justify-content:space-between; align-items:center; gap:12px; margin-bottom:12px; }
  #dclose { cursor:pointer; }
  .kv { display:grid; grid-template-columns:auto 1fr; gap:3px 16px; margin-bottom:12px; }
  .kv span { color:var(--dim); }
  .dlabel { font-family:var(--face); font-size:10px; text-transform:uppercase; letter-spacing:.13em;
            color:var(--dim); margin:12px 0 5px; font-weight:600; }
  pre { margin:0; padding:9px 10px; background:var(--raise); border:1px solid var(--grid);
        border-radius:4px; white-space:pre-wrap; word-break:break-word; max-height:38vh; overflow:auto;
        font-size:12px; line-height:1.5; }
  .note { color:var(--sig); margin-bottom:10px; }
  .ok { color:var(--dim); } .bad { color:var(--bad); }
  [hidden] { display:none !important; }

  @media (prefers-reduced-motion: reduce) {
    *, *::before, *::after { animation-duration:.01ms !important; animation-iteration-count:1 !important;
                             transition-duration:.01ms !important; }
  }
</style>
</head>
<body>

<header id="rail">
  <div class="brand">RemoteCmd <i>relay</i></div>
  <div id="chips"></div>
  <div class="grow"></div>
  <div id="beat">
    <svg width="14" height="14" viewBox="0 0 14 14" aria-hidden="true">
      <circle cx="7" cy="7" r="6" fill="none" stroke="var(--grid)" stroke-width="1.6"></circle>
      <circle id="ring" cx="7" cy="7" r="6" fill="none" stroke-width="1.6" stroke-linecap="round"
              stroke-dasharray="37.7" transform="rotate(-90 7 7)"></circle>
    </svg>
    <span id="beatText"></span>
  </div>
  <input id="q" type="search" aria-label="search history" placeholder="search command, client, message…" autocomplete="off" spellcheck="false">
  <select id="fClient" aria-label="filter by client"><option value="">all clients</option></select>
  <select id="fKind" aria-label="filter by event kind"><option value="">all events</option></select>
  <label><input type="checkbox" id="live" checked> live</label>
</header>

<form id="gate" hidden>
  <input id="token" type="password" placeholder="relay token" autocomplete="off" spellcheck="false">
  <button type="submit">Connect</button>
  <span class="err" id="gateErr"></span>
</form>

<section id="deckWrap"><div id="deck"></div></section>

<section id="activity">
  <svg id="histo" viewBox="0 0 240 100" preserveAspectRatio="none" aria-hidden="true"></svg>
  <div id="histNote"></div>
</section>

<div class="main" id="main">
  <section id="stream">
    <h2>Stream <span class="n" id="count"></span><span class="burst" id="burst"></span></h2>
    <div id="streamBox"><table id="hist">
      <colgroup><col style="width:3px"><col style="width:84px"><col style="width:54px"><col style="width:132px"><col></colgroup>
      <thead><tr><th class="gut"></th><th>time</th><th>event</th><th>client</th><th>detail</th></tr></thead>
      <tbody></tbody>
    </table></div>
    <div class="empty" id="noevents" hidden>nothing yet</div>
  </section>

  <aside id="clients">
    <h2>Clients <span class="n" id="ccount"></span></h2>
    <div id="clientBox"><table id="tbl">
      <colgroup><col style="width:3px"><col><col style="width:74px"><col style="width:46px"><col style="width:42px"><col style="width:40px"></colgroup>
      <thead><tr><th class="gut"></th><th>name</th><th>state</th><th>last</th><th>r/q</th><th>srv</th></tr></thead>
      <tbody></tbody>
    </table></div>
    <div class="empty" id="noclients" hidden>no clients registered</div>
  </aside>

  <aside id="detail" role="dialog" aria-modal="true" aria-labelledby="dtitle" tabindex="-1" hidden>
    <div class="dhead"><b id="dtitle">command</b><button id="dclose" type="button">close</button></div>
    <div id="detailBody" aria-live="polite"></div>
  </aside>
</div>

<div id="backdrop" hidden></div>

<script>
const $ = (id) => document.getElementById(id);
const txt = (v) => document.createTextNode(String(v));
const gate = $('gate');
const gateErr = $('gateErr');
const sections = ['deckWrap', 'activity', 'main'].map($);
let token = sessionStorage.getItem('rcmd-token');

// Everything the page renders comes from the relay, so it is written with text nodes only —
// never innerHTML — and a command's output is escaped by the DOM rather than by us.
const state = { q: '', client: '', kind: '', selected: null };
let latest = null;
let lastDrawn = null;
let lastClients = null;
// Set while the page is deliberately asking for a token, so the next refresh does not close the
// prompt out from under the person typing into it.
let gateWanted = false;

// The system setting can be flipped while the page sits open all day, and WAAPI ignores the CSS
// override, so the flag is re-read rather than captured once.
const mq = matchMedia('(prefers-reduced-motion: reduce)');
let REDUCE = mq.matches;
mq.addEventListener('change', (e) => { REDUCE = e.matches; });

// A token in the query string still works (older bookmarks), but it is moved into the session and
// wiped from the address bar so it stops leaking through history and the referrer.
const fromUrl = new URLSearchParams(location.search).get('token');
if (fromUrl) {
  token = fromUrl;
  sessionStorage.setItem('rcmd-token', token);
  history.replaceState(null, '', location.pathname);
}

function showGate(message, hideBody) {
  // Only steal the caret the first time the prompt appears: stealing it on every repeat would pull
  // the operator out of the search box a few seconds after they clicked into it.
  const opening = gate.hidden;
  gateWanted = true;
  if (message !== null) gateErr.textContent = message || '';
  gate.hidden = false;
  if (hideBody) {
    token = null;
    sessionStorage.removeItem('rcmd-token');
    sections.forEach((el) => { el.hidden = true; });
    closeDetail();
    setBeat('dead', 'needs a token');
  }
  if (opening) $('token').focus();
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
  // A relay behind a dropped VPN accepts the connection and then says nothing, so without a deadline
  // the request never settles, the page never notices, and the pending fetches pile up.
  const res = await fetch(path, {
    headers: token ? { 'X-Token': token } : {},
    signal: AbortSignal.timeout(8000),
  });
  if (res.status === 401 || res.status === 429) {
    const err = new Error('auth');
    err.auth = res.status;
    throw err;
  }
  // A 500 or a proxy's HTML error page would otherwise die inside res.json() and be reported as
  // "no reply", sending whoever reads it off to check a network that is working fine.
  if (!res.ok) {
    const err = new Error('http');
    err.status = res.status;
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

function cell(value, className) {
  const td = document.createElement('td');
  if (className) td.className = className;
  td.append(txt(value));
  return td;
}

function el(tag, className, text) {
  const n = document.createElement(tag);
  if (className) n.className = className;
  if (text !== undefined) n.append(txt(text));
  return n;
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

// ---------------------------------------------------------------- heartbeat
// The only thing that moves while nothing is happening. A failed refresh does not restart it, so
// the ring freezes where it stood — absence of motion is the alarm, and it cannot be missed the way
// a toast can.
const ring = $('ring');
let beat = null;
let beatCountdown = null;

function setBeat(mode, text) {
  ring.setAttribute('class', mode === 'live' ? '' : mode);
  $('beatText').textContent = text || '';
  clearInterval(beatCountdown);
  beatCountdown = null;
  if (mode !== 'live') { if (beat) { beat.cancel(); beat = null; } return; }
  if (REDUCE) {
    // Dropping the handle without cancelling would leave visibilitychange replaying a three second
    // sweep on a tab whose owner just asked for no motion at all.
    if (beat) { beat.cancel(); beat = null; }
    // No sweep to watch, so the same information arrives as a plain countdown.
    let left = 3;
    $('beatText').textContent = left + 's';
    beatCountdown = setInterval(() => { left = left > 1 ? left - 1 : 3; $('beatText').textContent = left + 's'; }, 1000);
    return;
  }
  if (beat) beat.cancel();
  beat = ring.animate([{ strokeDashoffset: 37.7 }, { strokeDashoffset: 0 }],
    { duration: 3000, easing: 'linear', fill: 'forwards' });
  if (document.hidden) beat.pause();
}

// A throttled background tab must not look like a dead relay.
document.addEventListener('visibilitychange', () => {
  if (!beat) return;
  if (document.hidden) beat.pause(); else beat.play();
});

// ---------------------------------------------------------------- activity buckets
// The relay's history is a ring, so old entries fall out of it. Binning the current snapshot would
// therefore *shrink* the bars in the past — a chart that rewrites history is worse than no chart.
// Events are accumulated client-side into per-minute buckets instead, deduped by their content.
// The dedup keys are kept per minute alongside the counts, so pruning an old minute drops its keys
// with it. A flat Set that got cleared wholesale would let the next snapshot's overlap be counted a
// second time and visibly inflate the bars that are still on screen.
const seenByMinute = new Map();
const buckets = new Map();
const CH = { exec: 'exec', upload: 'xfer', download: 'xfer', connect: 'conn', timeout: 'fault', auth: 'fault', gc: 'gc' };
const CHANNELS = ['fault', 'exec', 'xfer', 'conn', 'gc'];
const BINS = 40;
const KEEP_MINUTES = 240;
let ringSaturated = false;

function absorb(events) {
  const nowMin = Math.floor(Date.now() / 60000);
  for (const e of events) {
    const t = Date.parse(e.at);
    if (!t) continue;
    // A client clock running ahead of the relay would park events in bins that never scroll into
    // view and never get pruned, so anything from the future is treated as "now".
    const idx = Math.min(Math.floor(t / 60000), nowMin);
    const key = `${e.at}|${e.kind}|${e.client}|${e.message}|${e.id || ''}`;
    let keys = seenByMinute.get(idx);
    if (!keys) { keys = new Set(); seenByMinute.set(idx, keys); }
    if (keys.has(key)) continue;
    keys.add(key);
    const b = buckets.get(idx) || { fault: 0, exec: 0, xfer: 0, conn: 0, gc: 0, n: 0 };
    b[CH[e.kind] || 'gc']++;
    b.n++;
    buckets.set(idx, b);
  }
  const floor = nowMin - KEEP_MINUTES;
  for (const k of [...buckets.keys()]) if (k < floor) buckets.delete(k);
  for (const k of [...seenByMinute.keys()]) if (k < floor) seenByMinute.delete(k);
}

// A relay that restarted is a different relay: its counters are back at zero, its history is gone
// and its saturation no longer says anything. Carrying any of that across would show a silenced
// auth alarm, a hatched blind spot for events that never existed, and a nonsense throughput delta.
function resetForNewRelay() {
  prev = null;
  seenByMinute.clear();
  buckets.clear();
  ringSaturated = false;
  nowHistory = [];
  lastDrawn = null;
  lastClients = null;
  drawnKeys = new Set();
  clientAges = [];
  authBase = 0;
  authBaseAt = '';
  sessionStorage.removeItem('rcmd-auth-base');
  sessionStorage.removeItem('rcmd-auth-at');
}

const histSvg = $('histo');
const bars = [];
let blindRect = null, blindEdge = null;

function buildHistogram() {
  const W = 240, H = 100, slot = W / BINS;
  blindRect = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
  blindRect.setAttribute('class', 'blind');
  blindRect.setAttribute('y', '0');
  blindRect.setAttribute('height', String(H));
  blindRect.setAttribute('x', '0');
  blindRect.setAttribute('width', '0');
  histSvg.append(blindRect);
  blindEdge = document.createElementNS('http://www.w3.org/2000/svg', 'line');
  blindEdge.setAttribute('class', 'edge');
  blindEdge.setAttribute('y1', '0');
  blindEdge.setAttribute('y2', String(H));
  blindEdge.setAttribute('x1', '0');
  blindEdge.setAttribute('x2', '0');
  blindEdge.setAttribute('opacity', '0');
  histSvg.append(blindEdge);
  for (let i = 0; i < BINS; i++) {
    const col = [];
    for (const ch of CHANNELS) {
      const r = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
      r.setAttribute('class', 'bar ch-' + ch);
      r.setAttribute('x', String(i * slot + slot * 0.12));
      r.setAttribute('width', String(slot * 0.76));
      r.setAttribute('y', String(H));
      r.setAttribute('height', '0');
      histSvg.append(r);
      col.push(r);
    }
    bars.push(col);
  }
}

function renderHistogram(eventCount) {
  const H = 100;
  const nowMin = Math.floor(Date.now() / 60000);
  const keys = [...buckets.keys()];
  const oldest = keys.length ? Math.min(...keys) : nowMin;
  const spanMin = Math.max(1, nowMin - oldest + 1);
  // Adaptive bin: never finer than a minute, never so coarse that the window lies about its size.
  const group = Math.min(6, Math.max(1, Math.ceil(spanMin / BINS)));
  const windowMin = BINS * group;
  const startMin = nowMin - windowMin + 1;

  const cols = [];
  let peak = 0, total = 0;
  for (let i = 0; i < BINS; i++) {
    const agg = { fault: 0, exec: 0, xfer: 0, conn: 0, gc: 0, n: 0 };
    for (let g = 0; g < group; g++) {
      const b = buckets.get(startMin + i * group + g);
      if (!b) continue;
      for (const ch of CHANNELS) agg[ch] += b[ch];
      agg.n += b.n;
    }
    cols.push(agg);
    total += agg.n;
    if (agg.n > peak) peak = agg.n;
  }
  const scale = Math.max(1, peak);

  for (let i = 0; i < BINS; i++) {
    let bottom = H;
    CHANNELS.forEach((ch, ci) => {
      const v = cols[i][ch];
      const h = v ? Math.max(2, (v / scale) * (H - 4)) : 0;
      const r = bars[i][ci];
      bottom -= h;
      r.setAttribute('y', String(bottom));
      r.setAttribute('height', String(h));
    });
    // The rightmost bin is still filling up, so it says so rather than pretending to be complete.
    bars[i].forEach((r) => r.setAttribute('opacity', i === BINS - 1 ? '.55' : '1'));
  }

  // Only claim a blind spot when the ring is actually saturated. Deriving it from timestamps would
  // hatch the whole chart on a quiet relay and claim "cannot see here" where there was just silence.
  if (ringSaturated && keys.length) {
    const firstCol = Math.max(0, Math.floor((oldest - startMin) / group));
    const x = (firstCol / BINS) * 240;
    blindRect.setAttribute('width', String(Math.max(0, x)));
    blindEdge.setAttribute('x1', String(x));
    blindEdge.setAttribute('x2', String(x));
    blindEdge.setAttribute('opacity', x > 1 ? '1' : '0');
  } else {
    blindRect.setAttribute('width', '0');
    blindEdge.setAttribute('opacity', '0');
  }

  const note = $('histNote');
  note.replaceChildren();
  const span = group === 1 ? `${windowMin}m` : `${windowMin}m (${group}m bins)`;
  note.append(
    el('i', '', `${eventCount} events held · ${span} window · peak ${peak}/bin · ${total} in window`),
    el('i', 'key fault', 'fault'), el('i', 'key exec', 'exec'), el('i', 'key xfer', 'transfer'),
  );
  if (ringSaturated) note.append(el('i', '', 'shaded: older than the relay remembers'));
}

// ---------------------------------------------------------------- KPI deck
const KPIS = [
  { k: 'link', label: 'link', hero: true },
  { k: 'now', label: 'now', hero: true, spark: true },
  { k: 'exec', label: 'exec', delta: true },
  { k: 'tmo', label: 'timeout rate', delta: true },
  { k: 'xfer', label: 'transfers' },
  { k: 'vol', label: 'volume' },
  { k: 'auth', label: 'auth', delta: true },
  { k: 'relay', label: 'relay' },
];
const tiles = {};
let prev = null;
let nowHistory = [];
let authBase = Number(sessionStorage.getItem('rcmd-auth-base') || '0');
let authBaseAt = sessionStorage.getItem('rcmd-auth-at') || '';

function buildDeck() {
  const deck = $('deck');
  deck.replaceChildren();
  for (const spec of KPIS) {
    const box = el('div', 'kpi' + (spec.hero ? ' hero' : ''));
    const lbl = el('div', 'lbl');
    lbl.append(el('span', '', spec.label));
    const chip = el('span', 'chip');
    lbl.append(chip);
    const val = el('b', 'val', '—');
    const sub = el('span', 'sub');
    box.append(lbl, val, sub);
    let spark = null;
    if (spec.spark) {
      spark = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      spark.setAttribute('id', 'spark');
      spark.setAttribute('viewBox', '0 0 60 14');
      spark.setAttribute('preserveAspectRatio', 'none');
      const line = document.createElementNS('http://www.w3.org/2000/svg', 'polyline');
      spark.append(line);
      box.append(spark);
      spark = line;
    }
    deck.append(box);
    tiles[spec.k] = { box, val, sub, chip, spark };
  }
}

function setTile(k, value, sub, opts) {
  const t = tiles[k];
  const o = opts || {};
  const changed = t.val.textContent !== String(value);
  t.val.textContent = value;
  // null means "this tile owns its own sub-line" — the auth tile keeps a live button down there.
  if (sub !== null) t.sub.replaceChildren(txt(sub));
  t.box.classList.toggle('alarm', !!o.alarm);
  t.box.classList.toggle('quiet', !!o.quiet);
  // Flashing four numbers every three seconds is exactly what people stop looking at, so only the
  // three tiles that mean something get it, and only when the value really moved.
  if (changed && o.flash && !REDUCE) {
    t.val.classList.remove('flash');
    void t.val.offsetWidth;
    t.val.classList.add('flash');
  }
}

function setChip(k, text) {
  const t = tiles[k];
  clearTimeout(t.chipTimer);
  if (!text) { t.chip.textContent = ''; return; }
  t.chip.textContent = text;
  if (!REDUCE) {
    t.chip.classList.remove('rise');
    void t.chip.offsetWidth;
    t.chip.classList.add('rise');
  }
  t.chipTimer = setTimeout(() => { t.chip.textContent = ''; }, REDUCE ? 4000 : 2400);
}

// The worst poll age among connected clients. Guards both ways an empty list bites: Math.max of
// nothing is -Infinity, and a client that has never polled reports -1.
function worstPoll(cs) {
  const on = cs.filter((x) => x.connected && x.secondsAgo >= 0);
  if (!on.length) return null;
  return on.reduce((a, b) => (b.secondsAgo > a.secondsAgo ? b : a));
}

function renderDeck(clients, info) {
  const now = Date.now();
  const s = info.stats;
  // Deltas are only honest across a fresh, uninterrupted interval: a paused "live" checkbox or a
  // throttled tab makes dt meaningless, and a restarted relay resets every counter to zero.
  const dt = prev ? now - prev.at : 0;
  const fresh = !!prev && dt < 6000 && info.uptimeSeconds >= prev.uptime;

  const running = clients.clients.reduce((a, c) => a + c.running, 0);
  const queued = clients.clients.reduce((a, c) => a + c.queued, 0);
  const worst = worstPoll(clients.clients);
  const linkBad = clients.connected < clients.count || (worst && worst.secondsAgo > 15);

  setTile('link', `${clients.connected}/${clients.count}`,
    worst ? `${worst.name} ${worst.secondsAgo}s` : 'no clients polling', { alarm: !!linkBad });

  nowHistory.push(running);
  if (nowHistory.length > 60) nowHistory.shift();
  setTile('now', running, `${queued} queued`, { quiet: running === 0 && queued === 0 });
  drawSpark(nowHistory);

  setTile('exec', s.execs, fresh ? recentLabel() : `${clients.count} client(s)`, { flash: true });
  if (fresh && s.execs > prev.execs) setChip('exec', `+${s.execs - prev.execs}`);

  const rate = s.execs === 0 ? '—' : `${(s.timeouts / s.execs * 100).toFixed(1)}%`;
  const rateBad = s.execs > 0 && s.timeouts / s.execs > 0.05;
  setTile('tmo', rate, `${s.timeouts} tmo of ${s.execs}`,
    { alarm: rateBad, quiet: s.timeouts === 0, flash: true });
  if (fresh && s.timeouts > prev.timeouts) setChip('tmo', `+${s.timeouts - prev.timeouts}`);

  setTile('xfer', s.uploads + s.downloads, `↑ ${s.uploads}   ↓ ${s.downloads}`,
    { quiet: s.uploads + s.downloads === 0 });

  const vol = s.bytesUploaded + s.bytesDownloaded;
  const dBytes = fresh ? vol - (prev.bytesUp + prev.bytesDown) : 0;
  setTile('vol', bytes(vol), dBytes > 0 ? `${bytes(Math.round(dBytes / (dt / 1000)))}/s` : '—',
    { quiet: vol === 0 });

  const sinceBase = Math.max(0, s.authFailures - authBase);
  setTile('auth', s.authFailures, null, { alarm: sinceBase > 0, quiet: s.authFailures === 0, flash: true });
  renderAuthSub(sinceBase);
  if (fresh && s.authFailures > prev.authFailures) setChip('auth', `+${s.authFailures - prev.authFailures}`);

  // The tile is one tenth of the deck, so the token count lives in the rail and the tooltip; only
  // the transport, which is the part that can be wrong, gets the line.
  setTile('relay', dur(info.uptimeSeconds), info.tls ? 'TLS' : 'PLAIN HTTP', { quiet: false });
  tiles.relay.sub.classList.toggle('bad', !info.tls);
  tiles.relay.box.title = `${info.tokens} token(s) accepted · up ${dur(info.uptimeSeconds)}`;

  prev = {
    at: now, execs: s.execs, timeouts: s.timeouts, uploads: s.uploads, downloads: s.downloads,
    bytesUp: s.bytesUploaded, bytesDown: s.bytesDownloaded, authFailures: s.authFailures,
    uptime: info.uptimeSeconds,
  };
}

// authFailures only ever climbs, so a delta-based alarm misses a brute force that started before
// the page was opened. The tile latches until it is explicitly dismissed.
// The label and the button are built once and only their text changes: rebuilding them every three
// seconds destroyed the node mid-click, so the button could not reliably be pressed at all.
function buildAuthSub() {
  const sub = tiles.auth.sub;
  const label = document.createElement('span');
  const b = document.createElement('button');
  b.id = 'dismiss';
  b.type = 'button';
  b.hidden = true;
  b.append(txt('clear'));
  b.addEventListener('click', () => {
    authBase = latest ? latest.stats.authFailures : authBase;
    authBaseAt = localTime(new Date());
    sessionStorage.setItem('rcmd-auth-base', String(authBase));
    sessionStorage.setItem('rcmd-auth-at', authBaseAt);
    renderAuthSub(0);
    tiles.auth.box.classList.remove('alarm');
  });
  sub.replaceChildren(label, b);
  tiles.auth.label = label;
  tiles.auth.button = b;
}

function renderAuthSub(sinceBase) {
  const { label, button } = tiles.auth;
  if (sinceBase <= 0) {
    label.textContent = 'none';
    button.hidden = true;
    return;
  }
  label.textContent = `${sinceBase}× rejected${authBaseAt ? ' since ' + authBaseAt : ''} `;
  button.hidden = false;
}

function recentLabel() {
  const from = Math.floor(Date.now() / 60000) - 5;
  let n = 0;
  for (const [k, b] of buckets) if (k >= from) n += b.exec;
  return `+${n} last 5m`;
}

function drawSpark(values) {
  const line = tiles.now.spark;
  if (!line) return;
  const max = Math.max(1, ...values);
  const step = values.length > 1 ? 60 / (values.length - 1) : 60;
  line.setAttribute('points', values.map((v, i) => `${(i * step).toFixed(1)},${(13 - (v / max) * 12).toFixed(1)}`).join(' '));
}

// ---------------------------------------------------------------- clients
let clientAges = [];

function renderSessions(clients) {
  const tbody = document.querySelector('#tbl tbody');
  // secondsAgo is deliberately NOT in the signature: it changes every second and would rebuild the
  // rows out from under a click. The baseline it feeds is refreshed separately, below.
  const signature = JSON.stringify(clients.clients.map((c) => [c.id, c.name, c.state, c.running, c.queued, c.served, c.token, c.ip]));
  $('ccount').textContent = clients.count ? `${clients.connected} online` : '';
  $('noclients').hidden = clients.count > 0;
  $('clientBox').hidden = clients.count === 0;

  const prevStates = lastClients ? lastClients.states : null;
  if (signature === (lastClients && lastClients.sig)) { refreshAges(clients); stampAges(); return; }

  tbody.replaceChildren();
  clientAges = [];
  for (const c of clients.clients) {
    const tr = document.createElement('tr');
    tr.className = c.state === 'stale' ? 'stale' : c.state === 'idle' ? 'idle' : 'busy';
    if (prevStates && prevStates.get(c.id) && prevStates.get(c.id) !== c.state) tr.classList.add('changed');
    const arrow = c.state === 'upload' ? '↑ upload' : c.state === 'download' ? '↓ download' : c.state;
    const last = cell('—', 'lp');
    tr.append(
      cell('', 'gut'), cell(c.name), cell(arrow, 'st'), last,
      cell(`${c.running}/${c.queued}`), cell(c.served),
    );
    // The rail is narrow; the fields that are read once a day rather than once a minute live here.
    // The name is chosen on the client machine, so its newlines are flattened rather than allowed to
    // forge extra lines in the tooltip.
    tr.title = `${flat(c.name)}\nid ${c.id}\naddress ${c.ip || 'unknown'}\ntoken ${c.token}\nconnected ${dur(c.connectedForSeconds)}`;
    tbody.append(tr);
    clientAges.push({ id: c.id, td: last, base: c.secondsAgo, at: Date.now() });
  }
  // A plain object would let a client calling itself "__proto__" or "constructor" collide with
  // Object.prototype and mis-report every state change.
  const states = new Map(clients.clients.map((c) => [c.id, c.state]));
  lastClients = { sig: signature, states };
  refreshAges(clients);
  stampAges();
}

const flat = (s) => String(s).replace(/\s+/g, ' ').trim();

// The rows survive a tick untouched when nothing structural changed, so their poll baseline has to
// be re-stamped here. Without it a perfectly healthy client that simply sits idle drifts past the
// "late" and "dead" thresholds on screen while the relay keeps reporting secondsAgo: 0 — and the
// LINK tile, built from the same payload, cheerfully says everything is online.
function refreshAges(clients) {
  const byId = new Map(clients.clients.map((c) => [c.id, c]));
  const now = Date.now();
  for (const a of clientAges) {
    const c = byId.get(a.id);
    if (!c) continue;
    a.base = c.secondsAgo;
    a.at = now;
  }
}

// The relay's secondsAgo is a snapshot; without this the page would show a frozen client as "3s"
// forever, which is a lie that costs real debugging time.
function stampAges() {
  for (const a of clientAges) {
    if (a.base < 0) { a.td.textContent = 'never'; a.td.className = 'lp dead'; continue; }
    const age = a.base + Math.round((Date.now() - a.at) / 1000);
    a.td.textContent = age + 's';
    a.td.className = 'lp' + (age > 30 ? ' dead' : age > 9 ? ' late' : '');
  }
}

// ---------------------------------------------------------------- stream
const HEAT = {
  exec: 'var(--sig)', upload: 'var(--xfer)', download: 'var(--xfer)', connect: 'var(--dim)',
  timeout: 'var(--bad)', auth: 'var(--bad)', gc: 'var(--faint)',
};
const TAG = {
  exec: 'EXEC', upload: 'UPLD', download: 'DNLD', connect: 'CONN',
  timeout: 'TMOU', auth: 'AUTH', gc: 'GC',
};
let drawnKeys = new Set();
let filterChanged = false;

const evKey = (e) => `${e.at}|${e.kind}|${e.client}|${e.message}|${e.id || ''}`;

function ageBucket(seconds) {
  return seconds < 15 ? '0' : seconds < 120 ? '1' : seconds < 900 ? '2' : '3';
}

function renderStream(info) {
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
    : `${rows.length} of ${all.length}`;
  $('noevents').hidden = rows.length > 0;
  $('streamBox').hidden = rows.length === 0;

  // Rebuilding the rows on every poll would yank them out from under a click and reset the text
  // selection, so redraw only when what would be drawn has actually changed. Age buckets are part
  // of the signature so a row does cool down on its own.
  const now = Date.now();
  const shaped = rows.map((e) => ({ e, key: evKey(e), age: ageBucket((now - e.when) / 1000) }));
  const signature = JSON.stringify([state.selected, shaped.map((r) => [r.key, r.age])]);
  if (signature === lastDrawn) return;
  lastDrawn = signature;

  const incoming = shaped.filter((r) => !drawnKeys.has(r.key));
  // Twenty rows arriving at once during an exec loop would turn the page into a strobe, so past a
  // handful the arrival is reported as a count instead of animated.
  const animate = !REDUCE && !filterChanged && incoming.length > 0 && incoming.length <= 8;
  if (!filterChanged && incoming.length > 8) {
    $('burst').textContent = `+${incoming.length}`;
    clearTimeout(renderStream.burstTimer);
    renderStream.burstTimer = setTimeout(() => { $('burst').textContent = ''; }, 2400);
  }
  filterChanged = false;

  const tbody = document.querySelector('#hist tbody');
  // Rebuilding throws away the focused row, so j/k navigation would snap back to the top every time
  // a row aged into the next bucket. Remember where the caret was and put it back.
  const focused = document.activeElement;
  const focusedId = focused && tbody.contains(focused) ? focused.dataset.id : null;
  tbody.replaceChildren();
  let staggered = 0;
  for (const r of shaped) {
    const e = r.e;
    const tr = document.createElement('tr');
    tr.style.setProperty('--heat', HEAT[e.kind] || 'var(--faint)');
    tr.dataset.age = r.age;
    if (e.id) {
      tr.dataset.id = e.id;
      tr.tabIndex = 0;
      tr.title = 'show output';
      // A screen reader would otherwise announce "table row" and give no hint that Enter does
      // anything at all.
      tr.setAttribute('role', 'button');
      tr.setAttribute('aria-label', `show output of ${e.kind} on ${flat(e.client)} at ${localTime(e.when)}`);
      if (e.id === state.selected) tr.className = 'sel';
    }
    if (animate && !drawnKeys.has(r.key) && staggered < 6) {
      tr.classList.add('in');
      tr.style.animationDelay = (staggered * 24) + 'ms';
      staggered++;
    }
    tr.append(
      cell('', 'gut'),
      cell(localTime(e.when), 'ts'),
      cell(TAG[e.kind] || 'EVT', 'tag'),
      cell(e.client),
      msgCell(e.message),
    );
    tr.dataset.date = localDate(e.when);
    tbody.append(tr);
  }
  drawnKeys = new Set(shaped.map((r) => r.key));
  if (focusedId) {
    const back = tbody.querySelector(`tr[data-id="${CSS.escape(focusedId)}"]`);
    if (back) back.focus({ preventScroll: true });
  }
}

function msgCell(message) {
  const td = document.createElement('td');
  td.className = 'msg';
  // Whatever the clamp hides is still one hover away. A property, not markup, so relay text stays
  // text no matter what it contains.
  td.title = message;
  const div = document.createElement('div');
  div.append(txt(message));
  td.append(div);
  return td;
}

document.querySelector('#hist tbody').addEventListener('click', (e) => {
  const tr = e.target.closest('tr[data-id]');
  if (!tr) return;
  select(tr.dataset.id);
});

function select(id) {
  state.selected = id;
  if (latest) renderStream(latest);
  openDetail();
  loadDetail(id);
}

// Where the caret was before the panel took over, so closing it can hand the caret back instead of
// dropping it on <body> with no way to return to the row by keyboard.
let returnFocusTo = null;

function openDetail() {
  if ($('detail').hidden) returnFocusTo = state.selected;
  $('main').classList.add('detail-open');
  $('backdrop').hidden = false;
  $('detail').hidden = false;
  $('dclose').focus({ preventScroll: true });
}

function closeDetail() {
  if ($('detail').hidden) return;
  const back = returnFocusTo;
  $('main').classList.remove('detail-open');
  $('backdrop').hidden = true;
  $('detail').hidden = true;
  state.selected = null;
  returnFocusTo = null;
  if (latest) renderStream(latest);
  if (back) {
    const row = document.querySelector(`#hist tbody tr[data-id="${CSS.escape(back)}"]`);
    if (row) row.focus({ preventScroll: true });
  }
}

$('dclose').addEventListener('click', closeDetail);
$('backdrop').addEventListener('click', closeDetail);

function detailNote(message) {
  const p = el('div', 'note', message);
  $('detailBody').replaceChildren(p);
}

function field(label, value, className) {
  const k = el('span', '', label);
  const v = el('div', className, value);
  return [k, v];
}

function block(label, value) {
  const h = el('div', 'dlabel', label);
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
    // Two quick clicks race, and the slower answer must not repaint a panel that has moved on.
    if (state.selected !== id) return;
    // Without --open-status the whole API needs a token; ask for one without hiding the page.
    if (err.auth === 401) { detailNote('this relay needs a token'); showGate('', false); }
    else if (err.auth === 429) detailNote('too many attempts — wait a few minutes');
    else if (err.status) detailNote(`relay answered ${err.status}`);
    else detailNote('relay unreachable');
    return;
  }
  if (state.selected !== id) return;
  if (!d || d.error) {
    detailNote(d && d.error ? d.error : 'no stored output — the command may still be running');
    return;
  }

  const started = new Date(d.startedUtc);
  const kv = el('div', 'kv');
  kv.append(
    ...field('client', d.client),
    ...field('started', `${localDate(started)} ${localTime(started)}`),
    ...field('duration', ms(d.durationMs)),
    ...field('exit code', d.exitCode, d.exitCode === 0 ? 'ok' : 'bad'),
  );

  const parts = [kv];
  if (d.truncated) parts.push(el('div', 'note', 'output was truncated by the relay — only the first part is kept'));
  parts.push(...block('command', d.command), ...block('stdout', d.stdout), ...block('stderr', d.stderr));
  $('detailBody').replaceChildren(...parts);
}

// ---------------------------------------------------------------- input
for (const [id, key] of [['q', 'q'], ['fClient', 'client'], ['fKind', 'kind']]) {
  $(id).addEventListener('input', () => {
    state[key] = $(id).value;
    // A new view of old rows is not an arrival, so nothing animates on a filter change.
    filterChanged = true;
    if (latest) renderStream(latest);
  });
}

$('live').addEventListener('change', () => {
  if ($('live').checked) tick();
  else setBeat('paused', 'paused');
});

// Terminal muscle memory: the page sits next to one all day.
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && !$('detail').hidden) { closeDetail(); return; }
  // Ctrl+L, Cmd+L, Ctrl+J and friends belong to the browser. Without this the address-bar shortcut
  // also switched off polling, and the operator came back to a frozen dashboard with no clue why.
  if (e.ctrlKey || e.metaKey || e.altKey) return;
  const typing = /^(INPUT|SELECT|TEXTAREA)$/.test(document.activeElement.tagName)
    || document.activeElement.isContentEditable;
  if (typing) return;
  // While the output panel is up it owns the keyboard; otherwise j/k would scroll a table the
  // operator cannot even see behind the backdrop.
  if (!$('detail').hidden) return;
  const rows = [...document.querySelectorAll('#hist tbody tr[data-id]')];
  if (e.key === '/') { e.preventDefault(); $('q').focus(); }
  else if (e.key === 'l') { $('live').checked = !$('live').checked; $('live').dispatchEvent(new Event('change')); }
  else if (e.key === 'j' || e.key === 'k') {
    if (!rows.length) return;
    e.preventDefault();
    const at = rows.findIndex((r) => r === document.activeElement);
    const next = e.key === 'j' ? Math.min(rows.length - 1, at + 1) : Math.max(0, at < 0 ? 0 : at - 1);
    rows[next].focus();
  } else if (e.key === 'Enter' && document.activeElement.dataset && document.activeElement.dataset.id) {
    select(document.activeElement.dataset.id);
  }
});

// ---------------------------------------------------------------- tick
function renderChips(info) {
  const c = $('chips');
  c.replaceChildren(
    el('span', '', `up ${dur(info.uptimeSeconds)}`),
    el('span', 'sep'),
    el('span', info.tls ? '' : 'bad', info.tls ? 'TLS' : 'PLAIN HTTP'),
    el('span', 'sep'),
    el('span', '', `${info.tokens} token(s)`),
  );
}

let inFlight = false;

async function tick() {
  // With the prompt up and no token in hand, every refresh is two more failed attempts. The relay's
  // own brute-force throttle then locks the operator's address out within fifteen seconds, and the
  // 401s flood the 500-entry history until nothing else is left in it. Wait for the submit instead.
  if (gateWanted && !token) return;
  // A stalled relay must not let requests pile up behind each other; the pile also makes the byte
  // rate nonsense, because dt collapses when they all resolve at once.
  if (inFlight) return;
  inFlight = true;
  let clients, info;
  try {
    [clients, info] = await Promise.all([api('/api/clients'), api('/api/events?limit=500')]);
  } catch (e) {
    // A relay started with --open-status serves these without a token, so the form only appears
    // when the relay actually demands one. The heartbeat is deliberately not restarted here.
    if (e.auth === 401) showGate(token ? 'wrong token' : null, true);
    else if (e.auth === 429) showGate('too many attempts — wait a few minutes', true);
    else if (e.status) setBeat('dead', `relay error ${e.status}`);
    else setBeat('dead', 'no reply');
    return;
  } finally {
    inFlight = false;
  }

  try {
    // Counters that went backwards mean a different process is answering now.
    if (prev && info.uptimeSeconds < prev.uptime) resetForNewRelay();

    if (!gateWanted) gate.hidden = true;
    sections.forEach((el2) => { el2.hidden = false; });
    latest = info;
    if (info.events.length >= 500) ringSaturated = true;

    renderChips(info);
    absorb(info.events);
    renderDeck(clients, info);
    renderHistogram(info.events.length);
    renderSessions(clients);
    renderStream(info);

    // The tab is 100px wide and hidden behind a terminal most of the day; make it say something.
    const running = clients.clients.reduce((a, c) => a + c.running, 0);
    const stale = clients.count > clients.connected;
    document.title = running ? `(${running} run) RemoteCmd` : stale ? '[!] RemoteCmd' : 'RemoteCmd relay';
  } catch (e) {
    // A payload that parsed but did not have the shape the page expects would otherwise leave the
    // last good numbers frozen on screen under a heartbeat still claiming to be live — for a
    // monitoring page that is a worse failure than saying nothing at all.
    setBeat('dead', 'bad reply');
    return;
  }

  // Restarted only once everything actually repainted, so the sweep means what it looks like.
  setBeat('live', '');
}

buildDeck();
buildAuthSub();
buildHistogram();
setBeat('paused', '');
tick();
setInterval(() => { if ($('live').checked) tick(); }, 3000);
setInterval(stampAges, 1000);
</script>
</body>
</html>
""";
}
