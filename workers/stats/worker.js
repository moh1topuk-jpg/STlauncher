/**
 * STlauncher server monitoring collector.
 *
 * Polls the Top-Minecrafter API on a schedule, keeps a rolling history of the readings
 * in Workers KV and serves a small public JSON that the launcher reads at startup.
 *
 * Why this exists: no free public API returns a player-count time series, and the
 * launcher can only sample while it happens to be open. One collector fixes both - and
 * keeps the API key here, where players cannot read it. That key also unlocks the vote
 * and donation lists, so shipping it inside the launcher would publish those too.
 *
 * Setup: see docs/monitoring.md.
 *
 * Bindings expected:
 *   KV namespace  STATS
 *   Secret        TOPMC_KEY       Top-Minecrafter API key
 *   Variable      SERVER_ID       server id on Top-Minecrafter (e.g. "6689")
 *   Variable      SERVER_ADDRESS  optional, used when the API reports no address
 *
 * Optional, for counting launcher users (see docs/monitoring.md):
 *   Analytics Engine  USAGE           dataset "stlauncher_usage" - where launch pings land
 *   Variable          CF_ACCOUNT_ID   the Cloudflare account id
 *   Secret            CF_API_TOKEN    a token with "Account Analytics: Read", to query the dataset
 */

const API_BASE = 'https://public-api.top-minecrafter.com/v1';

/** Readings closer together than this add nothing to an hourly chart. */
const MIN_INTERVAL_MS = 4 * 60 * 1000;

/**
 * How long raw readings are kept: the longest range the launcher draws plus a day of
 * slack. Keeping a month instead would quadruple both the stored value and the work
 * done inside the free plan's 10 ms of CPU.
 */
const RETENTION_MS = 8 * 24 * 60 * 60 * 1000;


/** The live dashboard, served at /dashboard. Source: workers/stats/dashboard.html - edit
 * that file and re-embed it here; the page fetches the JSON from the same origin. */
const DASHBOARD_HTML = String.raw`<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Пульс STlauncher</title>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Manrope:wght@600;800&family=Source+Sans+3:wght@400;600&display=swap">
<style>
  :root {
    --bg: #f6f3f4; --surface: #ffffff; --surface-2: #efe9eb;
    --ink: #1c1418; --ink-2: #5a4c52; --muted: #8f7f86; --line: #e2d8dc;
    --accent: #b8283f; --accent-soft: rgba(184, 40, 63, 0.14);
    --good: #2f8a5a; --good-soft: rgba(47, 138, 90, 0.16);
    --bad: #b8283f; --bad-soft: rgba(184, 40, 63, 0.16);
    --warn: #b06a11; --warn-soft: rgba(176, 106, 17, 0.16);
    --grid: #ebe4e7;
    --display: "Manrope", "Segoe UI", system-ui, sans-serif;
    --body: "Source Sans 3", "Segoe UI", system-ui, sans-serif;
    color-scheme: light dark;
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --bg: #0f0c10; --surface: #17121a; --surface-2: #211a24;
      --ink: #f2ebee; --ink-2: #b9aab2; --muted: #7d6f76; --line: #2b2330;
      --accent: #e0546f; --accent-soft: rgba(224, 84, 111, 0.18);
      --good: #62c58e; --good-soft: rgba(98, 197, 142, 0.18);
      --bad: #ef6f80; --bad-soft: rgba(239, 111, 128, 0.18);
      --warn: #e2a24a; --warn-soft: rgba(226, 162, 74, 0.18);
      --grid: #241d28;
    }
  }
  * { box-sizing: border-box; }
  body { margin: 0; background: var(--bg); color: var(--ink); font-family: var(--body); font-size: 15px; line-height: 1.45; padding-inline: 16px; padding-block: 28px 40px; }
  .wrap { max-width: 860px; margin: 0 auto; display: grid; gap: 18px; }
  header { display: flex; flex-wrap: wrap; align-items: baseline; justify-content: space-between; gap: 6px 16px; }
  h1 { font-family: var(--display); font-weight: 800; font-size: 26px; letter-spacing: -0.02em; margin: 0; }
  .stamp { color: var(--muted); font-size: 13px; }
  .stamp a { color: inherit; }
  .stamp .dot { display: inline-block; width: 8px; height: 8px; border-radius: 4px; background: var(--good); margin-right: 5px; vertical-align: 1px; }
  .stamp.stale .dot { background: var(--bad); }
  .label { font-size: 12px; letter-spacing: 0.08em; text-transform: uppercase; color: var(--muted); font-weight: 600; }
  .tiles { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 10px; }
  .tile { background: var(--surface); border-radius: 14px; padding: 14px 16px 12px; display: grid; gap: 2px; }
  .tile .value { font-family: var(--display); font-weight: 800; font-size: 34px; line-height: 1.1; letter-spacing: -0.02em; font-variant-numeric: tabular-nums; }
  .tile .sub { color: var(--ink-2); font-size: 13px; }
  @media (max-width: 640px) { .tiles { grid-template-columns: 1fr 1fr; } }
  .two { display: grid; grid-template-columns: 1fr 1fr; gap: 18px; }
  @media (max-width: 640px) { .two { grid-template-columns: 1fr; } }
  section.card { background: var(--surface); border-radius: 14px; padding: 16px 16px 14px; display: grid; gap: 12px; align-content: start; }
  section.card h2 { font-family: var(--display); font-weight: 600; font-size: 16px; margin: 0; }
  .card-head { display: flex; justify-content: space-between; align-items: baseline; gap: 12px; flex-wrap: wrap; }
  .card-head .note, .note { color: var(--muted); font-size: 13px; }
  .seg { display: inline-flex; background: var(--surface-2); border-radius: 999px; padding: 2px; }
  .seg button { border: 0; background: transparent; color: var(--ink-2); font: inherit; font-size: 13px; padding: 3px 10px; border-radius: 999px; cursor: pointer; }
  .seg button.on { background: var(--surface); color: var(--ink); font-weight: 600; }
  .seg button:focus-visible { outline: 2px solid var(--accent); }
  .rows { display: grid; gap: 8px; }
  .row { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: center; gap: 10px; }
  .row .name { display: flex; align-items: center; gap: 8px; min-width: 0; flex-wrap: wrap; }
  .row code { font-family: ui-monospace, "Cascadia Mono", Consolas, monospace; font-size: 13px; overflow-wrap: anywhere; }
  .row .detail { color: var(--muted); font-size: 13px; }
  .pill { font-size: 11px; font-weight: 600; letter-spacing: 0.04em; text-transform: uppercase; padding: 2px 7px; border-radius: 999px; white-space: nowrap; }
  .pill.ok { background: var(--good-soft); color: var(--good); }
  .pill.fail { background: var(--bad-soft); color: var(--bad); }
  .pill.warn { background: var(--warn-soft); color: var(--warn); }
  .pill.neutral { background: var(--surface-2); color: var(--ink-2); }
  .row .count { font-variant-numeric: tabular-nums; font-weight: 600; }
  .row .bar { grid-column: 1 / -1; height: 6px; border-radius: 3px; background: var(--surface-2); overflow: hidden; }
  .row .bar i { display: block; height: 100%; border-radius: 3px; background: var(--accent); }
  .row.ok .bar i { background: var(--good); }
  .row.fail .bar i { background: var(--bad); }
  .empty { color: var(--muted); font-size: 14px; }
  .chart { position: relative; }
  .chart svg { display: block; width: 100%; height: auto; overflow: visible; }
  .chart text { font-family: var(--body); font-size: 11px; fill: var(--muted); }
  .tip { position: absolute; pointer-events: none; background: var(--ink); color: var(--bg); font-size: 12px; padding: 5px 8px; border-radius: 8px; white-space: nowrap; transform: translate(-50%, -100%); opacity: 0; transition: opacity 120ms; }
  .tip.on { opacity: 1; }
  @media (prefers-reduced-motion: reduce) { .tip { transition: none; } }
  .server { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 8px; }
  .server .kv { background: var(--surface-2); border-radius: 10px; padding: 10px 12px; display: grid; gap: 1px; }
  .server .kv b { font-family: var(--display); font-weight: 800; font-size: 20px; font-variant-numeric: tabular-nums; }
  .server .kv span { color: var(--muted); font-size: 12px; }
  @media (max-width: 640px) { .server { grid-template-columns: 1fr 1fr; } }
  .table-wrap { overflow-x: auto; }
  table { border-collapse: collapse; width: 100%; font-size: 13px; }
  th { text-align: left; color: var(--muted); font-weight: 600; font-size: 11px; letter-spacing: 0.06em; text-transform: uppercase; padding: 4px 8px 6px; border-bottom: 1px solid var(--line); white-space: nowrap; }
  td { padding: 5px 8px; border-bottom: 1px solid var(--grid); vertical-align: top; white-space: nowrap; }
  td.wrap-cell { white-space: normal; min-width: 180px; }
  td code { font-family: ui-monospace, "Cascadia Mono", Consolas, monospace; font-size: 12px; }
  tr.crash td { background: var(--bad-soft); }
  footer { color: var(--muted); font-size: 12.5px; }
</style>
</head>
<body>
<div class="wrap">
  <header>
    <h1>Пульс STlauncher</h1>
    <div class="stamp" id="stamp"><span class="dot"></span>загружаю…</div>
  </header>

  <div class="tiles" id="tiles"></div>

  <section class="card">
    <div class="card-head">
      <h2>Запуски</h2>
      <div class="seg" role="group" aria-label="Период запусков">
        <button type="button" id="launch-day" class="on">по часам, сутки</button>
        <button type="button" id="launch-week">по дням, неделя</button>
      </div>
    </div>
    <div class="chart" id="launch-chart"></div>
  </section>

  <div class="two">
    <section class="card">
      <div class="card-head"><h2>Версии лаунчера</h2><span class="note">за 7 дней, по запускам</span></div>
      <div class="rows" id="versions"><div class="empty">…</div></div>
    </section>
    <section class="card">
      <div class="card-head"><h2>Проверки обновлений</h2><span class="note">за 7 дней</span></div>
      <div class="rows" id="outcomes"><div class="empty">…</div></div>
    </section>
  </div>

  <section class="card">
    <div class="card-head"><h2>Падения игры</h2><span class="note">за 7 дней · причина, мод, версия</span></div>
    <div class="rows" id="crashes"><div class="empty">…</div></div>
  </section>

  <section class="card">
    <div class="card-head">
      <h2>Онлайн сервера</h2>
      <div class="seg" role="group" aria-label="Период онлайна">
        <button type="button" id="range-day" class="on">24 часа</button>
        <button type="button" id="range-week">7 дней</button>
      </div>
    </div>
    <div class="chart" id="chart"></div>
    <div class="note" id="chart-note"></div>
  </section>

  <section class="card">
    <div class="card-head"><h2>Сервер</h2><span class="note" id="server-address"></span></div>
    <div class="server" id="server"></div>
  </section>

  <section class="card">
    <div class="card-head"><h2>Последние события</h2><span class="note">40 последних · id обрезан до 6 знаков</span></div>
    <div class="table-wrap"><table id="recent"><tbody><tr><td class="empty">…</td></tr></tbody></table></div>
  </section>

  <footer>Пользователь — одна установка лаунчера по случайному идентификатору; запуск — один старт программы. Ники, файлы и пути не передаются. Сборщик пересчитывает числа раз в пять минут, страница обновляется сама раз в минуту.</footer>
</div>

<script>
(function () {
  var data = null;
  var range = 'day';
  var launchRange = 'day';

  function el(id) { return document.getElementById(id); }
  function esc(s) { return String(s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
  function parseTs(t) {
    // Analytics Engine returns "2026-09-22 17:00:00" in UTC; the collector's own stamps are ISO.
    var s = String(t);
    if (s.indexOf('T') < 0 && s.indexOf(' ') > 0) s = s.replace(' ', 'T') + 'Z';
    return new Date(s);
  }
  function fmtTime(iso, withDay) {
    var d = parseTs(iso);
    return withDay
      ? d.toLocaleString('ru-RU', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
      : d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
  }
  function fmtDay(iso) { return parseTs(iso).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' }); }
  function fmtDate(iso) { return parseTs(iso).toLocaleString('ru-RU', { day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit' }); }

  function renderStamp() {
    var stamp = el('stamp');
    var age = Date.now() - parseTs(data.updatedAt).getTime();
    stamp.className = 'stamp' + (age > 6 * 3600 * 1000 ? ' stale' : '');
    stamp.innerHTML = '<span class="dot"></span>замер ' + esc(fmtDate(data.updatedAt)) + ' · <a href="./" target="_blank" rel="noopener">JSON</a>';
  }

  function sum(list) { return list.reduce(function (s, o) { return s + o.n; }, 0); }

  function renderTiles() {
    var L = data.launcher || {};
    var crashesWeek = sum(L.crashes || []);
    var rows = [
      ['Пользователей за сутки', L.usersToday, 'установок лаунчера'],
      ['За 7 дней', L.usersWeek, 'разных установок'],
      ['Запусков за сутки', L.launchesToday, 'стартов программы'],
      ['Падений за 7 дней', L.crashes ? crashesWeek : null, 'игра закрылась с ошибкой']
    ];
    el('tiles').innerHTML = rows.map(function (r) {
      var v = r[1] == null ? '—' : r[1];
      return '<div class="tile"><div class="label">' + r[0] + '</div><div class="value">' + v + '</div><div class="sub">' + r[2] + '</div></div>';
    }).join('');
  }

  function renderList(hostId, list, emptyText, classify, describe) {
    var host = el(hostId);
    if (!list || !list.length) { host.innerHTML = '<div class="empty">' + emptyText + '</div>'; return; }
    var total = sum(list);
    host.innerHTML = list.map(function (o) {
      var c = classify(o);
      var share = Math.round(o.n / total * 100);
      return '<div class="row ' + c.cls + '">' +
        '<div class="name">' + (c.pill ? '<span class="pill ' + c.cls + '">' + c.pill + '</span>' : '') + describe(o) + '</div>' +
        '<div class="count">' + o.n + ' · ' + share + '%</div>' +
        '<div class="bar"><i style="width:' + share + '%"></i></div></div>';
    }).join('');
  }

  function renderVersions() {
    var L = data.launcher || {};
    renderList('versions', L.versions, 'Данных пока нет.', function () { return { cls: 'neutral', pill: '' }; }, function (o) { return '<code>' + esc(o.outcome || '?') + '</code>'; });
  }

  function renderOutcomes() {
    var L = data.launcher || {};
    renderList('outcomes', L.updates, 'Пингов с исходом проверки ещё нет: они приходят с версии 0.3.4.',
      function (o) { var ok = String(o.outcome).indexOf('ok') === 0; return { cls: ok ? 'ok' : 'fail', pill: ok ? 'ок' : 'сбой' }; },
      function (o) { return '<code>' + esc(o.outcome) + '</code>'; });
  }

  function renderCrashes() {
    var L = data.launcher || {};
    renderList('crashes', L.crashes, 'Падений не было, либо игроки ещё на версии без отчётов о падениях (нужна 0.3.5).',
      function () { return { cls: 'fail', pill: '' }; },
      function (o) {
        var parts = ['<code>' + esc(o.cause || 'Unknown') + '</code>'];
        if (o.subject) parts.push('<span class="detail">' + esc(o.subject) + '</span>');
        if (o.game) parts.push('<span class="pill neutral">' + esc(o.game) + '</span>');
        return parts.join('');
      });
  }

  // Launches as columns: by the hour over a day, by the day over a week.
  function renderLaunches() {
    var L = data.launcher || {};
    var series = launchRange === 'day' ? (L.byHour || []) : (L.byDay || []);
    var host = el('launch-chart');
    if (!series.length) { host.innerHTML = '<div class="empty">Данных пока нет.</div>'; return; }
    var W = 720, H = 150, padL = 30, padR = 8, padT = 12, padB = 24;
    var n = series.length;
    var max = Math.max.apply(null, series.map(function (b) { return b.n; })) * 1.15 || 1;
    var slot = (W - padL - padR) / n;
    var bw = Math.max(4, Math.min(28, slot - 4));
    function y(v) { return padT + (H - padT - padB) * (1 - v / max); }
    var ticks = [0, Math.round(max / 2), Math.round(max)];
    var grid = ticks.map(function (t) {
      return '<line x1="' + padL + '" x2="' + (W - padR) + '" y1="' + y(t).toFixed(1) + '" y2="' + y(t).toFixed(1) + '" stroke="var(--grid)"/>' +
        '<text x="' + (padL - 6) + '" y="' + (y(t) + 4).toFixed(1) + '" text-anchor="end">' + t + '</text>';
    }).join('');
    var bars = series.map(function (b, i) {
      var x = padL + i * slot + (slot - bw) / 2;
      var top = y(b.n);
      var label = launchRange === 'day' ? fmtTime(b.t) : fmtDay(b.t);
      var every = launchRange === 'day' ? Math.max(1, Math.round(n / 8)) : 1;
      return '<rect x="' + x.toFixed(1) + '" y="' + top.toFixed(1) + '" width="' + bw.toFixed(1) + '" height="' + Math.max(0, y(0) - top).toFixed(1) + '" rx="3" fill="var(--accent)"><title>' + esc(label) + ' · ' + b.n + '</title></rect>' +
        (i % every === 0 ? '<text x="' + (x + bw / 2).toFixed(1) + '" y="' + (H - 8) + '" text-anchor="middle">' + esc(label) + '</text>' : '') +
        (b.n > 0 && n <= 30 ? '<text x="' + (x + bw / 2).toFixed(1) + '" y="' + (top - 4).toFixed(1) + '" text-anchor="middle" style="fill:var(--ink-2)">' + b.n + '</text>' : '');
    }).join('');
    host.innerHTML = '<svg viewBox="0 0 ' + W + ' ' + H + '" role="img" aria-label="Запуски лаунчера">' + grid + bars + '</svg>';
  }

  function renderChart() {
    var r = data.ranges && data.ranges[range];
    var host = el('chart');
    var note = el('chart-note');
    if (!r || !r.buckets || !r.buckets.length) { host.innerHTML = '<div class="empty">Замеров пока нет.</div>'; note.textContent = ''; return; }
    var all = r.buckets;
    var pts = all.map(function (b, i) { return [b.t, b.avg, b.peak, i]; }).filter(function (p) { return p[1] != null; });
    if (!pts.length) { host.innerHTML = '<div class="empty">Замеров пока нет.</div>'; note.textContent = ''; return; }

    var W = 720, H = 200, padL = 34, padR = 12, padT = 14, padB = 26;
    var n = all.length;
    var max = Math.max.apply(null, pts.map(function (p) { return p[2]; })) * 1.08 || 1;
    function x(i) { return padL + i * (W - padL - padR) / (n - 1); }
    function y(v) { return padT + (H - padT - padB) * (1 - v / max); }
    function f(v) { return v.toFixed(1); }

    var ticks = [0, Math.round(max / 2), Math.round(max)];
    var grid = ticks.map(function (t) {
      return '<line x1="' + padL + '" x2="' + (W - padR) + '" y1="' + f(y(t)) + '" y2="' + f(y(t)) + '" stroke="var(--grid)" stroke-width="1"/>' +
        '<text x="' + (padL - 6) + '" y="' + f(y(t) + 4) + '" text-anchor="end">' + t + '</text>';
    }).join('');

    var avgPath = '', peakPath = '', prevIndex = -2;
    pts.forEach(function (p) {
      var cmd = p[3] === prevIndex + 1 ? 'L' : 'M';
      avgPath += cmd + f(x(p[3])) + ',' + f(y(p[1])) + ' ';
      peakPath += cmd + f(x(p[3])) + ',' + f(y(p[2])) + ' ';
      prevIndex = p[3];
    });
    var area = '';
    var run = [];
    function flush() {
      if (!run.length) return;
      area += 'M' + f(x(run[0][3])) + ',' + f(y(0)) + ' ' + run.map(function (p) { return 'L' + f(x(p[3])) + ',' + f(y(p[1])); }).join(' ') + ' L' + f(x(run[run.length - 1][3])) + ',' + f(y(0)) + ' Z ';
      run = [];
    }
    pts.forEach(function (p, k) { if (k && p[3] !== pts[k - 1][3] + 1) flush(); run.push(p); });
    flush();

    var labels = all.map(function (b, i) {
      return i % 4 === 0 || i === n - 1 ? '<text x="' + f(x(i)) + '" y="' + (H - 8) + '" text-anchor="middle">' + esc(fmtTime(b.t, range === 'week')) + '</text>' : '';
    }).join('');

    var peakOf = pts.reduce(function (m, p, i) { return p[1] > pts[m][1] ? i : m; }, 0);
    var last = pts[pts.length - 1];
    host.innerHTML = '<svg viewBox="0 0 ' + W + ' ' + H + '" role="img" aria-label="Средний онлайн сервера">' + grid +
      '<path d="' + area + '" fill="var(--accent-soft)"/>' +
      '<path d="' + peakPath + '" fill="none" stroke="var(--accent)" stroke-opacity="0.35" stroke-width="1.5" stroke-dasharray="3 4"/>' +
      '<path d="' + avgPath + '" fill="none" stroke="var(--accent)" stroke-width="2" stroke-linejoin="round"/>' +
      '<circle cx="' + f(x(pts[peakOf][3])) + '" cy="' + f(y(pts[peakOf][1])) + '" r="4" fill="var(--accent)" stroke="var(--surface)" stroke-width="2"/>' +
      '<text x="' + f(x(pts[peakOf][3])) + '" y="' + f(y(pts[peakOf][1]) - 9) + '" text-anchor="middle" style="fill:var(--ink);font-weight:600">' + pts[peakOf][1] + '</text>' +
      '<circle cx="' + f(x(last[3])) + '" cy="' + f(y(last[1])) + '" r="4" fill="var(--accent)" stroke="var(--surface)" stroke-width="2"/>' +
      labels +
      '<line id="cross" x1="0" x2="0" y1="' + padT + '" y2="' + (H - padB) + '" stroke="var(--ink-2)" stroke-width="1" stroke-dasharray="2 3" opacity="0"/>' +
      '<rect x="' + padL + '" y="' + padT + '" width="' + (W - padL - padR) + '" height="' + (H - padT - padB) + '" fill="transparent" id="hit"/>' +
      '</svg><div class="tip" id="tip"></div>';
    note.textContent = 'средний онлайн · пунктир — пик · время ' + Intl.DateTimeFormat().resolvedOptions().timeZone;

    var svg = host.querySelector('svg'), tip = el('tip'), cross = el('cross'), hit = el('hit');
    hit.addEventListener('pointermove', function (ev) {
      var rect = svg.getBoundingClientRect();
      var px = (ev.clientX - rect.left) * W / rect.width;
      var i = Math.max(0, Math.min(n - 1, Math.round((px - padL) / ((W - padL - padR) / (n - 1)))));
      var b = all[i];
      cross.setAttribute('x1', x(i)); cross.setAttribute('x2', x(i)); cross.setAttribute('opacity', '1');
      tip.textContent = b.avg == null ? fmtTime(b.t, range === 'week') + ' · нет замера' : fmtTime(b.t, range === 'week') + ' · в среднем ' + b.avg + ' · пик ' + b.peak;
      tip.style.left = (x(i) * rect.width / W) + 'px';
      tip.style.top = (y(b.avg == null ? 0 : b.avg) * rect.height / H - 10) + 'px';
      tip.classList.add('on');
    });
    hit.addEventListener('pointerleave', function () { tip.classList.remove('on'); cross.setAttribute('opacity', '0'); });
  }

  function renderServer() {
    var S = data.server || {}, M = data.summary || {};
    el('server-address').textContent = S.address || '';
    var rows = [
      [S.online == null ? '—' : S.online, 'сейчас' + (S.max ? ' из ' + S.max : '')],
      [M.averageWeek == null ? '—' : M.averageWeek, 'в среднем за неделю'],
      [M.peak == null ? '—' : M.peak, 'рекорд' + (M.peakAt ? ', ' + parseTs(M.peakAt).toLocaleDateString('ru-RU') : '')],
      [M.rank == null ? '—' : '#' + M.rank, 'Top-Minecrafter']
    ];
    el('server').innerHTML = rows.map(function (r) { return '<div class="kv"><b>' + esc(r[0]) + '</b><span>' + esc(r[1]) + '</span></div>'; }).join('');
  }

  function renderRecent() {
    var L = data.launcher || {};
    var list = L.recent || [];
    var table = el('recent');
    if (!list.length) { table.innerHTML = '<tbody><tr><td class="empty">Событий пока нет.</td></tr></tbody>'; return; }
    var head = '<thead><tr><th>Когда</th><th>Событие</th><th>Установка</th><th>Версия</th><th>ОС</th><th>Подробности</th></tr></thead>';
    var body = list.map(function (e) {
      var crash = e.event === 'crash';
      var detail = crash
        ? '<code>' + esc(e.cause || 'Unknown') + '</code>' + (e.subject ? ' · ' + esc(e.subject) : '') + (e.game ? ' · ' + esc(e.game) : '')
        : (e.update ? '<code>' + esc(e.update) + '</code>' : '<span class="note">—</span>');
      return '<tr' + (crash ? ' class="crash"' : '') + '><td>' + esc(fmtTime(e.t, true)) + '</td>' +
        '<td><span class="pill ' + (crash ? 'fail' : 'neutral') + '">' + (crash ? 'падение' : 'запуск') + '</span></td>' +
        '<td><code>' + esc(e.id) + '</code></td><td>' + esc(e.v) + '</td><td>' + esc(e.os) + '</td><td class="wrap-cell">' + detail + '</td></tr>';
    }).join('');
    table.innerHTML = head + '<tbody>' + body + '</tbody>';
  }

  function render() { renderStamp(); renderTiles(); renderLaunches(); renderVersions(); renderOutcomes(); renderCrashes(); renderChart(); renderServer(); renderRecent(); }

  function load() {
    fetch('./', { cache: 'no-store' })
      .then(function (r) { if (!r.ok) throw new Error('HTTP ' + r.status); return r.json(); })
      .then(function (json) { data = json; render(); })
      .catch(function (e) {
        var stamp = el('stamp');
        stamp.className = 'stamp stale';
        stamp.innerHTML = '<span class="dot"></span>не удалось получить данные: ' + esc(e.message);
      });
  }

  function toggle(onId, offId, set) {
    el(onId).addEventListener('click', function () { set(); this.classList.add('on'); el(offId).classList.remove('on'); });
  }
  toggle('range-day', 'range-week', function () { range = 'day'; if (data) renderChart(); });
  toggle('range-week', 'range-day', function () { range = 'week'; if (data) renderChart(); });
  toggle('launch-day', 'launch-week', function () { launchRange = 'day'; if (data) renderLaunches(); });
  toggle('launch-week', 'launch-day', function () { launchRange = 'week'; if (data) renderLaunches(); });

  load();
  setInterval(load, 60 * 1000);
})();
</script>
</body>
</html>
`;

const STATE_KEY = 'state';
const PAYLOAD_KEY = 'payload';

/** Ranges the launcher asks for, pre-bucketed here so the client stays dumb. */
const RANGES = {
  day: { hours: 24, buckets: 24 },
  week: { hours: 24 * 7, buckets: 28 },
};

export default {
  async scheduled(event, env, ctx) {
    ctx.waitUntil(collect(env));
  },

  async fetch(request, env, ctx) {
    if (request.method === 'OPTIONS') {
      return new Response(null, { headers: corsHeaders() });
    }

    // One anonymous "I started" per launch. Analytics Engine writes are free and
    // unlimited on the Workers plan; a KV counter here would burn a write per player.
    if (request.method === 'POST' && new URL(request.url).pathname === '/ping') {
      // The body has to be read before the response goes out; afterwards the runtime
      // closes the request stream and the ping is lost.
      const body = await readJson(request);
      ctx.waitUntil(recordPing(body, env));
      return new Response(null, { status: 204, headers: corsHeaders() });
    }

    if (request.method !== 'GET' && request.method !== 'HEAD') {
      return json({ ok: false, error: 'method_not_allowed' }, 405);
    }

    // The owner's page: the same numbers, drawn. Served here because a page on this
    // origin may read the JSON from it, which a page hosted elsewhere may not.
    if (new URL(request.url).pathname === '/dashboard') {
      return new Response(DASHBOARD_HTML, {
        headers: { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'public, max-age=300' },
      });
    }

    // Deliberately a passthrough: the payload is built when a reading is taken, so
    // serving it costs one KV read. Bucketing here instead would put the work on every
    // player's launch, inside a 10 ms CPU budget.
    const payload = await env.STATS.get(PAYLOAD_KEY, 'text');

    // Belt and braces: if the schedule never fired - a trigger nobody added, an account
    // limit - a player opening the launcher still advances the history.
    ctx.waitUntil(collectIfDue(env));

    if (payload === null) {
      return json(empty(env), 200, { 'cache-control': 'public, max-age=30' });
    }

    return new Response(payload, {
      headers: {
        'content-type': 'application/json; charset=utf-8',
        ...corsHeaders(),
        // Short enough to stay current, long enough that a burst of launches at prime
        // time is served from the edge rather than from KV every time.
        'cache-control': 'public, max-age=60',
      },
    });
  },
};

async function collectIfDue(env) {
  const state = await readState(env);

  if (isDue(state)) {
    await collect(env, state);
  }
}

/** Takes one reading, appends it to the history and rebuilds the published payload. */
async function collect(env, known) {
  const state = known ?? (await readState(env));

  if (!isDue(state)) {
    return;
  }

  let info;

  try {
    info = await fetchInfo(env);
  } catch (error) {
    // A failed poll is a gap, not a zero: the server may be perfectly fine and the
    // monitoring API down. Leaving the history untouched is the honest outcome.
    console.log(`poll failed: ${error}`);
    return;
  }

  const now = Date.now();

  state.samples.push([now, info.players ?? 0]);
  state.samples = state.samples.filter(([time]) => now - time <= RETENTION_MS);
  state.updatedAt = now;
  state.info = info;

  // Launcher usage rides along on the same schedule, so it costs no extra KV writes.
  // A failed query keeps yesterday's numbers rather than blanking them.
  const usage = await queryUsage(env);

  if (usage) {
    state.usage = usage;
  }

  await Promise.all([
    env.STATS.put(STATE_KEY, JSON.stringify(state)),
    env.STATS.put(PAYLOAD_KEY, JSON.stringify(render(state, env, now), null, 2)),
  ]);
}

async function readJson(request) {
  try {
    return await request.json();
  } catch (error) {
    return null;
  }
}

/** Stores one launch: the installation id is the only identity, and it is random. */
async function recordPing(body, env) {
  if (!env.USAGE || !body) {
    return;
  }

  try {
    const id = String(body?.id ?? '').slice(0, 64);

    if (!id) {
      return;
    }

    const event = body?.event === 'crash' ? 'crash' : 'launch';

    env.USAGE.writeDataPoint({
      indexes: [id],
      // blob1 installation id   blob2 launcher version   blob3 os   blob4 language
      // blob5 update outcome ("ok:mirror", "fail:github=Blocked;mirror=Timeout")
      // blob6 event: launch | crash   blob7 crash cause   blob8 mod at fault   blob9 game version + loader
      blobs: [
        id,
        String(body?.v ?? '').slice(0, 32),
        String(body?.os ?? '').slice(0, 16),
        String(body?.lang ?? '').slice(0, 8),
        String(body?.update ?? '').slice(0, 120),
        event,
        String(body?.cause ?? '').slice(0, 40),
        String(body?.subject ?? '').slice(0, 60),
        String(body?.game ?? '').slice(0, 40),
      ],
      doubles: [event === 'crash' ? Number(body?.code ?? 0) : 1],
    });
  } catch (error) {
    console.log(`ping ignored: ${error}`);
  }
}

/**
 * Distinct installations and launches over a day and a week, from the pings. Needs an
 * API token; without one the payload simply carries no launcher block.
 *
 * Analytics Engine SQL knows no DISTINCT or conditional aggregates, so each window is
 * one query grouped by installation id: rows are users, their sum is launches. Sampled
 * rows are weighted by _sample_interval, as the documentation asks.
 */
async function queryUsage(env) {
  if (!env.CF_ACCOUNT_ID || !env.CF_API_TOKEN) {
    return null;
  }

  try {
    const [today, week, updates, versions, crashes, byHour, byDay, recent] = await Promise.all([
      queryWindow(env, "INTERVAL '1' DAY"),
      queryWindow(env, "INTERVAL '7' DAY"),
      queryGrouped(env, 'blob5', "blob5 != '' AND blob6 != 'crash'", "INTERVAL '7' DAY"),
      queryGrouped(env, 'blob2', "blob6 != 'crash'", "INTERVAL '7' DAY"),
      queryCrashes(env),
      queryTimeline(env, "INTERVAL '1' DAY", "INTERVAL '1' HOUR"),
      queryTimeline(env, "INTERVAL '7' DAY", "INTERVAL '1' DAY"),
      queryRecent(env),
    ]);

    if (!today || !week) {
      return null;
    }

    return {
      usersToday: today.users,
      usersWeek: week.users,
      launchesToday: today.launches,
      updates: updates ?? [],
      versions: versions ?? [],
      crashes: crashes ?? [],
      byHour: byHour ?? [],
      byDay: byDay ?? [],
      recent: recent ?? [],
      updatedAt: Date.now(),
    };
  } catch (error) {
    console.log(`usage query failed: ${error}`);
    return null;
  }
}

async function sqlQuery(env, sql) {
  const response = await fetch(`https://api.cloudflare.com/client/v4/accounts/${env.CF_ACCOUNT_ID}/analytics_engine/sql`, {
    method: 'POST',
    headers: { authorization: `Bearer ${env.CF_API_TOKEN}` },
    body: sql,
  });

  if (!response.ok) {
    console.log(`usage query failed: HTTP ${response.status} ${(await response.text()).slice(0, 200)}`);
    return null;
  }

  const body = await response.json();
  return Array.isArray(body?.data) ? body.data : [];
}

/** One column counted over a window, most common value first. */
async function queryGrouped(env, column, where, interval) {
  const rows = await sqlQuery(env, `SELECT ${column} AS value, SUM(_sample_interval) AS n
    FROM stlauncher_usage
    WHERE timestamp > NOW() - ${interval} AND ${where}
    GROUP BY value
    ORDER BY n DESC
    LIMIT 20`);

  return rows?.map(row => ({ outcome: String(row.value ?? ''), n: Number(row.n ?? 0) })) ?? null;
}

/** Game crashes by cause, with the mod at fault and the game version when they were reported. */
async function queryCrashes(env) {
  const rows = await sqlQuery(env, `SELECT blob7 AS cause, blob8 AS subject, blob9 AS game, SUM(_sample_interval) AS n
    FROM stlauncher_usage
    WHERE timestamp > NOW() - INTERVAL '7' DAY AND blob6 = 'crash'
    GROUP BY cause, subject, game
    ORDER BY n DESC
    LIMIT 30`);

  return rows?.map(row => ({
    cause: String(row.cause ?? ''),
    subject: String(row.subject ?? ''),
    game: String(row.game ?? ''),
    n: Number(row.n ?? 0),
  })) ?? null;
}

/** Launches per slice over a window - a day by the hour, a week by the day. */
async function queryTimeline(env, interval, step) {
  const rows = await sqlQuery(env, `SELECT toStartOfInterval(timestamp, ${step}) AS t, SUM(_sample_interval) AS n
    FROM stlauncher_usage
    WHERE timestamp > NOW() - ${interval} AND blob6 != 'crash'
    GROUP BY t
    ORDER BY t
    LIMIT 200`);

  return rows?.map(row => ({ t: String(row.t ?? ''), n: Number(row.n ?? 0) })) ?? null;
}

/** The last events, newest first. The id is cut to six characters: enough to spot a repeat. */
async function queryRecent(env) {
  const rows = await sqlQuery(env, `SELECT timestamp, blob1, blob2, blob3, blob5, blob6, blob7, blob8, blob9
    FROM stlauncher_usage
    WHERE timestamp > NOW() - INTERVAL '7' DAY
    ORDER BY timestamp DESC
    LIMIT 40`);

  return rows?.map(row => ({
    t: String(row.timestamp ?? ''),
    id: String(row.blob1 ?? '').slice(0, 6),
    v: String(row.blob2 ?? ''),
    os: String(row.blob3 ?? ''),
    update: String(row.blob5 ?? ''),
    event: String(row.blob6 ?? 'launch') || 'launch',
    cause: String(row.blob7 ?? ''),
    subject: String(row.blob8 ?? ''),
    game: String(row.blob9 ?? ''),
  })) ?? null;
}

async function queryWindow(env, interval) {
  const rows = await sqlQuery(env, `SELECT blob1 AS installation, SUM(_sample_interval) AS launches
    FROM stlauncher_usage
    WHERE timestamp > NOW() - ${interval} AND blob6 != 'crash'
    GROUP BY installation
    LIMIT 100000`);

  if (!rows) {
    return null;
  }

  return {
    users: rows.length,
    launches: rows.reduce((sum, row) => sum + Number(row.launches ?? 0), 0),
  };
}

async function fetchInfo(env) {
  const url = `${API_BASE}/servers/${env.SERVER_ID}/info?key=${encodeURIComponent(env.TOPMC_KEY)}`;

  // AbortController rather than AbortSignal.timeout: the former is what the Workers
  // runtime documents, and a hung upstream must not hold the invocation open.
  const abort = new AbortController();
  const timer = setTimeout(() => abort.abort(), 10000);

  try {
    const response = await fetch(url, {
      headers: { accept: 'application/json' },
      signal: abort.signal,
    });

    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }

    const body = await response.json();

    if (!body.ok || !body.result) {
      throw new Error(body?.error?.code ?? 'bad_payload');
    }

    return body.result;
  } finally {
    clearTimeout(timer);
  }
}

async function readState(env) {
  const stored = await env.STATS.get(STATE_KEY, 'json');

  if (!stored || !Array.isArray(stored.samples)) {
    return { samples: [], updatedAt: 0, info: null };
  }

  return stored;
}

function isDue(state) {
  return Date.now() - (state.updatedAt ?? 0) >= MIN_INTERVAL_MS;
}

/** What the launcher gets before the first reading lands. */
function empty(env) {
  return {
    schemaVersion: 1,
    updatedAt: null,
    sampleCount: 0,
    server: { name: null, address: env.SERVER_ADDRESS ?? null, online: null, max: null, isOnline: false },
    summary: { averageWeek: null, peak: null, peakAt: null, uptime: null, rank: null },
    ranges: {},
  };
}

/** Builds the payload the launcher consumes. Keep it small: it is fetched on every start. */
function render(state, env, now) {
  const info = state.info ?? {};

  return {
    schemaVersion: 1,
    updatedAt: state.updatedAt ? new Date(state.updatedAt).toISOString() : null,
    sampleCount: state.samples.length,
    server: {
      name: info.name ?? null,
      address: info.je_address ?? env.SERVER_ADDRESS ?? null,
      online: numberOrNull(info.players),
      max: numberOrNull(info.max_players),
      isOnline: info.status === 1,
    },
    summary: {
      averageWeek: numberOrNull(info.avg_players),
      peak: numberOrNull(info.peak_players),
      peakAt: info.peak_players_date ?? null,
      uptime: numberOrNull(info.uptime),
      rank: numberOrNull(info.global_rank_position),
    },
    ranges: Object.fromEntries(
      Object.entries(RANGES).map(([name, range]) => [name, buildRange(state.samples, range, now)]),
    ),
    launcher: state.usage
      ? {
          usersToday: state.usage.usersToday,
          usersWeek: state.usage.usersWeek,
          launchesToday: state.usage.launchesToday,
          updates: state.usage.updates ?? [],
          versions: state.usage.versions ?? [],
          crashes: state.usage.crashes ?? [],
          byHour: state.usage.byHour ?? [],
          byDay: state.usage.byDay ?? [],
          recent: state.usage.recent ?? [],
          updatedAt: new Date(state.usage.updatedAt).toISOString(),
        }
      : null,
  };
}

/**
 * Splits the window into equal slices. A slice with no reading comes back with nulls:
 * the collector was not running then, which is a gap in the data rather than an hour
 * with nobody online - and the chart draws the two differently.
 */
function buildRange(samples, { hours, buckets }, now) {
  const windowMs = hours * 60 * 60 * 1000;
  const from = now - windowMs;
  const slice = windowMs / buckets;

  // Samples are appended in order; the sort only guards against a clock that jumped
  // backwards, and costs nothing on an already ordered array.
  const relevant = samples.filter(([time]) => time >= from && time <= now).sort((a, b) => a[0] - b[0]);

  const result = [];
  let index = 0;

  for (let i = 0; i < buckets; i++) {
    const start = from + slice * i;
    const isLast = i === buckets - 1;
    const limit = isLast ? now + 1 : start + slice;

    let count = 0;
    let total = 0;
    let peak = 0;

    while (index < relevant.length && relevant[index][0] < limit) {
      const players = relevant[index][1];
      total += players;
      peak = Math.max(peak, players);
      count++;
      index++;
    }

    result.push({
      t: new Date(start).toISOString(),
      avg: count === 0 ? null : Math.round((total / count) * 10) / 10,
      peak: count === 0 ? null : peak,
    });
  }

  return {
    from: new Date(from).toISOString(),
    to: new Date(now).toISOString(),
    buckets: result,
  };
}

function numberOrNull(value) {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function corsHeaders() {
  return {
    'access-control-allow-origin': '*',
    'access-control-allow-methods': 'GET, HEAD, POST, OPTIONS',
    'access-control-allow-headers': 'content-type',
  };
}

function json(body, status = 200, extra = {}) {
  return new Response(JSON.stringify(body, null, 2), {
    status,
    headers: {
      'content-type': 'application/json; charset=utf-8',
      ...corsHeaders(),
      ...extra,
    },
  });
}
