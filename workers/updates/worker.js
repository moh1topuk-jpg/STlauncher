/**
 * STlauncher update mirror.
 *
 * Serves the launcher's release feed from Cloudflare instead of GitHub, for players whose
 * provider cuts the TLS handshake to github.com. The launcher reads this through
 * Velopack's SimpleWebSource, which asks for two things:
 *
 *   GET /releases.win.json   the feed
 *   GET /<file>.nupkg        the package named in it
 *
 * It also serves GET /catalog.json. The launcher learns the mirror's address from the
 * catalog, and the catalog lives on GitHub too - so a player who cannot reach GitHub at
 * all could never find the mirror. Serving the catalog from here closes that loop.
 *
 * The installer is the one file that is NOT taken from the latest release. SmartScreen
 * judges an unsigned file by how many people have already run that exact file, and a
 * new Setup.exe per release would start from zero every few days. So /STlauncher-win-
 * Setup.exe comes from the release named by "installerRelease" in catalog.json and
 * stays byte-identical until that field changes; the launcher it installs updates
 * itself to the current version on first start. GET /latest/<file> skips the pin.
 *
 * Everything is fetched from GitHub on Cloudflare's side and streamed back, so nothing
 * has to be uploaded or kept in sync by hand - publishing a release stays exactly as it is.
 *
 * Setup: see docs/updates.md.
 *
 * Bindings expected:
 *   Variable  REPO   "moh1topuk-jpg/STlauncher"
 *   Secret    TOKEN  optional; only needed if the repository is private
 *
 * Two more things live here because this is the one address every launcher knows:
 * support reports (POST /report) and the CurseForge proxy (/cf/...). Each is described
 * where its code starts and stays switched off until its secrets are set.
 */

const API = 'https://api.github.com';

/** The feed is small and changes only on release. */
const FEED_CACHE_SECONDS = 300;

/** Packages are immutable once published. */
const ASSET_CACHE_SECONDS = 86400;

/** The catalog is edited by hand and should show up within a minute. */
const CATALOG_CACHE_SECONDS = 60;

/**
 * Support reports from the launcher, forwarded to the owner's Telegram as a document.
 * Nothing is stored here: the zip goes straight to the chat and the response says so.
 *
 * Bindings (see docs/reports.md):
 *   Secret   TG_BOT_TOKEN   the bot's token from @BotFather
 *   Secret   TG_CHAT_ID     the chat the bot posts to (the owner's id, or a group's)
 */
const REPORT_MAX_BYTES = 8 * 1024 * 1024;

/**
 * CurseForge, the launcher's second mod source. Its API wants a key on every call, and a
 * key cannot ship inside an open-source launcher - so the launcher asks this worker, and
 * the worker asks CurseForge with the key added. Only the handful of calls the launcher
 * makes are let through, only for Minecraft, and answers are kept at the edge for a few
 * minutes so a page of search results costs the key one request, not one per player.
 *
 *   GET  /cf/ping   200 when the key is set and CurseForge accepts it, 503 otherwise
 *                   (the launcher hides CurseForge until this answers 200)
 *   GET  /cf/v1/... the calls listed in curseforgeRoute()
 *   POST /cf/v1/fingerprints/432   which CurseForge files a set of jars are
 *
 * Bindings (see docs/curseforge.md):
 *   Secret   CF_API_KEY   the API key from console.curseforge.com
 */
const CF_API = 'https://api.curseforge.com';

/** Minecraft. Nothing else is served: the key is the owner's, not a public gateway. */
const CF_GAME_ID = 432;

/** Mods, resource packs, shaders - the three tabs of the launcher's browser. */
const CF_CLASSES = ['6', '12', '6552'];

/** Search pages and file lists move slowly; five minutes is fresh enough to install from. */
const CF_CACHE_SECONDS = 300;

/** A mods folder is a few hundred jars at the very most. */
const CF_MAX_FINGERPRINTS = 1000;

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    const name = decodeURIComponent(url.pathname.replace(/^\/+/, ''));

    if (name === 'report') {
      // A GET answers 200 so the launcher can tell this mirror takes reports before showing
      // the button, and 503 while the bot's secrets are missing: a button that can only
      // fail is worse than none.
      if (request.method === 'POST') {
        return report(request, env);
      }

      return env.TG_BOT_TOKEN && env.TG_CHAT_ID
        ? text('post a report here')
        : text('reports are not set up on this mirror', 503);
    }

    if (name.startsWith('cf/')) {
      return curseforge(request, env, url, name.slice('cf/'.length));
    }

    if (request.method !== 'GET' && request.method !== 'HEAD') {
      return text('method not allowed', 405);
    }

    if (name === '' || name === 'health') {
      return text('ok');
    }

    // /latest/<file> is the owner's way past the installer pin.
    const wantLatest = name.startsWith('latest/');
    const file = wantLatest ? name.slice('latest/'.length) : name;

    // Only the launcher's own artefacts, and nothing that could walk out of them.
    if (!/^[A-Za-z0-9._-]+$/.test(file)) {
      return text('not found', 404);
    }

    try {
      if (file === 'catalog.json') {
        return await catalog(env);
      }

      const release = wantLatest ? await latestRelease(env) : await releaseFor(file, env);
      const asset = release.assets.find(a => a.name === file);

      if (!asset) {
        return text('not found', 404);
      }

      const upstream = await fetch(asset.browser_download_url, {
        headers: { 'user-agent': 'STlauncher-update-mirror' },
        cf: { cacheEverything: true, cacheTtl: ASSET_CACHE_SECONDS },
      });

      if (!upstream.ok) {
        return text(`upstream ${upstream.status}`, 502);
      }

      const headers = new Headers();
      headers.set('content-type', upstream.headers.get('content-type') ?? 'application/octet-stream');

      const length = upstream.headers.get('content-length');
      if (length) {
        headers.set('content-length', length);
      }

      // The feed changes on every release; the packages never do.
      headers.set(
        'cache-control',
        `public, max-age=${file.endsWith('.json') || file === 'RELEASES' ? FEED_CACHE_SECONDS : ASSET_CACHE_SECONDS}`,
      );
      headers.set('access-control-allow-origin', '*');

      return new Response(upstream.body, { status: 200, headers });
    } catch (error) {
      // A mirror that fails loudly is better than one that serves a broken feed: the
      // launcher falls back to telling the player to download by hand.
      console.log(`mirror failed: ${error}`);
      return text('mirror unavailable', 502);
    }
  },
};

/** The catalog from the main branch, fetched on Cloudflare's side of the block. */
async function catalog(env) {
  const upstream = await fetch(`https://raw.githubusercontent.com/${env.REPO}/main/catalog.json`, {
    headers: { 'user-agent': 'STlauncher-update-mirror' },
    cf: { cacheEverything: true, cacheTtl: CATALOG_CACHE_SECONDS },
  });

  if (!upstream.ok) {
    return text(`upstream ${upstream.status}`, 502);
  }

  return new Response(upstream.body, {
    status: 200,
    headers: {
      'content-type': 'application/json; charset=utf-8',
      'cache-control': `public, max-age=${CATALOG_CACHE_SECONDS}`,
      'access-control-allow-origin': '*',
    },
  });
}

/** Installers come from the pinned release; feed and packages always from the latest. */
async function releaseFor(name, env) {
  if (!/Setup\.exe$/i.test(name)) {
    return latestRelease(env);
  }

  const tag = await pinnedInstallerTag(env);
  return tag ? releaseByTag(tag, env) : latestRelease(env);
}

/** "installerRelease" from catalog.json, e.g. "v0.3.5"; null when unset or unreadable. */
async function pinnedInstallerTag(env) {
  try {
    const response = await catalog(env);

    if (!response.ok) {
      return null;
    }

    const tag = (await response.json()).installerRelease;
    return typeof tag === 'string' && /^v?\d+\.\d+\.\d+$/.test(tag) ? (tag.startsWith('v') ? tag : 'v' + tag) : null;
  } catch (error) {
    console.log(`installer pin unreadable: ${error}`);
    return null;
  }
}

async function releaseByTag(tag, env) {
  const response = await fetch(`${API}/repos/${env.REPO}/releases/tags/${tag}`, {
    headers: githubHeaders(env),
    cf: { cacheEverything: true, cacheTtl: ASSET_CACHE_SECONDS },
  });

  if (!response.ok) {
    throw new Error(`release ${tag} lookup failed: HTTP ${response.status}`);
  }

  return response.json();
}

function githubHeaders(env) {
  const headers = {
    accept: 'application/vnd.github+json',
    'user-agent': 'STlauncher-update-mirror',
  };

  if (env.TOKEN) {
    headers.authorization = `Bearer ${env.TOKEN}`;
  }

  return headers;
}

async function latestRelease(env) {
  const response = await fetch(`${API}/repos/${env.REPO}/releases/latest`, {
    headers: githubHeaders(env),
    cf: { cacheEverything: true, cacheTtl: FEED_CACHE_SECONDS },
  });

  if (!response.ok) {
    throw new Error(`release lookup failed: HTTP ${response.status}`);
  }

  return response.json();
}

function text(body, status = 200) {
  return new Response(body, {
    status,
    headers: {
      'content-type': 'text/plain; charset=utf-8',
      'access-control-allow-origin': '*',
    },
  });
}

/** Takes the launcher's multipart post apart and hands the zip to Telegram. */
async function report(request, env) {
  if (!env.TG_BOT_TOKEN || !env.TG_CHAT_ID) {
    return text('reports are not set up on this mirror', 503);
  }

  // The launcher always says who it is; a bare browser post does not.
  if (!request.headers.get('x-stlauncher')) {
    return text('forbidden', 403);
  }

  const length = Number(request.headers.get('content-length') || 0);
  if (length > REPORT_MAX_BYTES) {
    return text('report too large', 413);
  }

  let form;
  try {
    form = await request.formData();
  } catch (error) {
    return text('bad request', 400);
  }

  const file = form.get('file');
  if (!(file instanceof File) || file.size === 0 || file.size > REPORT_MAX_BYTES) {
    return text('no report file', 400);
  }

  const clean = (value, max) => String(value || '').replace(/[\r\n]+/g, ' ').trim().slice(0, max);
  const nick = clean(form.get('nick'), 40) || '?';
  const version = clean(form.get('version'), 40) || '?';
  const install = clean(form.get('install'), 40).slice(0, 8);
  const comment = clean(form.get('comment'), 600);

  const caption = [
    `Отчёт STlauncher ${version}`,
    `Ник: ${nick}` + (install ? `  ·  установка ${install}` : ''),
    comment ? `\n${comment}` : '',
  ].join('\n').trim().slice(0, 1000);

  const telegram = new FormData();
  telegram.set('chat_id', env.TG_CHAT_ID);
  telegram.set('caption', caption);
  telegram.set('document', file, clean(file.name, 80) || 'report.zip');

  const response = await fetch(`https://api.telegram.org/bot${env.TG_BOT_TOKEN}/sendDocument`, {
    method: 'POST',
    body: telegram,
  });

  if (!response.ok) {
    console.log(`telegram refused a report: ${response.status} ${await response.text()}`);
    return text('could not deliver the report', 502);
  }

  return text('delivered');
}

/** The CurseForge proxy. `path` is what follows /cf/ in the address. */
async function curseforge(request, env, url, path) {
  if (!env.CF_API_KEY) {
    return text('curseforge is not set up on this mirror', 503);
  }

  try {
    if (path === 'ping') {
      // A key that is set but refused would show the launcher a source that cannot answer,
      // so the probe asks CurseForge one small cached question with it.
      const probe = await curseforgeFetch(`${CF_API}/v1/games/${CF_GAME_ID}`, env);
      return probe.ok ? text('ok') : text(`curseforge refused the key (HTTP ${probe.status})`, 503);
    }

    // The launcher always says who it is; a script that found the address does not.
    if (!request.headers.get('x-stlauncher')) {
      return text('forbidden', 403);
    }

    if (request.method === 'POST') {
      return path === `v1/fingerprints/${CF_GAME_ID}`
        ? await curseforgeFingerprints(request, env)
        : text('not found', 404);
    }

    if (request.method !== 'GET') {
      return text('method not allowed', 405);
    }

    const route = curseforgeRoute(path, url.searchParams);

    if (!route) {
      return text('not found', 404);
    }

    // A mod id says nothing about its game, so the mod itself is asked first. The answer
    // sits in the edge cache, which makes the next call about the same mod free.
    if (route.modId && !(await isMinecraftMod(route.modId, env))) {
      return text('not found', 404);
    }

    return curseforgeAnswer(await curseforgeFetch(route.target, env), CF_CACHE_SECONDS);
  } catch (error) {
    console.log(`curseforge failed: ${error}`);
    return text('curseforge unavailable', 502);
  }
}

/**
 * Maps a launcher request to the CurseForge call it stands for, or null when it is not
 * one of the launcher's. The query is rebuilt from known parameters in a fixed order:
 * nothing unexpected travels on the owner's key, and the same question always lands on
 * the same cache entry.
 */
function curseforgeRoute(path, query) {
  const pick = (name, pattern) => {
    const value = query.get(name);
    return value !== null && pattern.test(value) ? value : null;
  };

  const build = (base, pairs) => {
    const out = new URLSearchParams();

    for (const [key, value] of pairs) {
      if (value !== null && value !== '') {
        out.set(key, value);
      }
    }

    const tail = out.toString();
    return `${CF_API}/${base}${tail ? '?' + tail : ''}`;
  };

  const paging = () => [
    ['index', pick('index', /^\d{1,4}$/) ?? '0'],
    ['pageSize', String(Math.min(Math.max(Number(pick('pageSize', /^\d{1,3}$/) ?? 20), 1), 50))],
  ];

  const gameVersion = () => pick('gameVersion', /^[0-9A-Za-z][0-9A-Za-z. _-]{0,31}$/);
  const loader = () => pick('modLoaderType', /^[0-6]$/);
  const classId = query.get('classId');

  if (path === 'v1/mods/search') {
    if (!CF_CLASSES.includes(classId)) {
      return null;
    }

    return {
      target: build('v1/mods/search', [
        ['gameId', String(CF_GAME_ID)],
        ['classId', classId],
        ['categoryId', pick('categoryId', /^\d{1,9}$/)],
        ['gameVersion', gameVersion()],
        ['modLoaderType', loader()],
        ['searchFilter', (query.get('searchFilter') ?? '').trim().slice(0, 100)],
        ['slug', pick('slug', /^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$/)],
        ['sortField', pick('sortField', /^([1-9]|1[0-2])$/)],
        ['sortOrder', pick('sortOrder', /^(asc|desc)$/)],
        ...paging(),
      ]),
    };
  }

  if (path === 'v1/categories') {
    return CF_CLASSES.includes(classId)
      ? { target: build('v1/categories', [['gameId', String(CF_GAME_ID)], ['classId', classId]]) }
      : null;
  }

  // /v1/mods/<id>, its description, its files, one file, and that file's download address.
  const mod = /^v1\/mods\/(\d{1,10})(\/description|\/files(?:\/\d{1,10}(?:\/download-url)?)?)?$/.exec(path);

  if (!mod) {
    return null;
  }

  const modId = mod[1];

  if (mod[2] === '/files') {
    return {
      modId,
      target: build(`v1/mods/${modId}/files`, [
        ['gameVersion', gameVersion()],
        ['modLoaderType', loader()],
        ...paging(),
      ]),
    };
  }

  return { modId, target: `${CF_API}/${path}` };
}

function curseforgeFetch(target, env) {
  return fetch(target, {
    headers: {
      accept: 'application/json',
      'x-api-key': env.CF_API_KEY,
      'user-agent': 'STlauncher-update-mirror',
    },
    cf: { cacheEverything: true, cacheTtl: CF_CACHE_SECONDS },
  });
}

/**
 * Mod ids already checked, for as long as this instance of the worker lives. A mod never
 * changes its game, so remembering it saves the lookup even where the edge cache does not.
 */
const minecraftMods = new Map();

/** True when the mod is a Minecraft one. */
async function isMinecraftMod(modId, env) {
  if (minecraftMods.has(modId)) {
    return minecraftMods.get(modId);
  }

  const response = await curseforgeFetch(`${CF_API}/v1/mods/${modId}`, env);

  // Not remembered: a refusal may be CurseForge having a bad minute.
  if (!response.ok) {
    return false;
  }

  const body = await response.json();
  const answer = body?.data?.gameId === CF_GAME_ID;

  if (minecraftMods.size >= 5000) {
    minecraftMods.clear();
  }

  minecraftMods.set(modId, answer);
  return answer;
}

/** Which CurseForge files a set of jars are. The body is rebuilt from numbers, never forwarded as sent. */
async function curseforgeFingerprints(request, env) {
  let body;
  try {
    body = await request.json();
  } catch (error) {
    return text('bad request', 400);
  }

  const list = Array.isArray(body?.fingerprints) ? body.fingerprints : [];
  const valid = list.length > 0 &&
    list.length <= CF_MAX_FINGERPRINTS &&
    list.every(n => Number.isInteger(n) && n >= 0 && n <= 0xffffffff);

  if (!valid) {
    return text('bad request', 400);
  }

  const upstream = await fetch(`${CF_API}/v1/fingerprints/${CF_GAME_ID}`, {
    method: 'POST',
    headers: {
      accept: 'application/json',
      'content-type': 'application/json',
      'x-api-key': env.CF_API_KEY,
      'user-agent': 'STlauncher-update-mirror',
    },
    body: JSON.stringify({ fingerprints: list }),
  });

  return curseforgeAnswer(upstream, 0);
}

/** CurseForge's JSON on success; otherwise a plain status the launcher can tell apart. */
function curseforgeAnswer(upstream, cacheSeconds) {
  // 403 on a download address is the author's "no third-party downloads", not a fault.
  if (upstream.status === 403 || upstream.status === 404) {
    return text(upstream.status === 403 ? 'forbidden' : 'not found', upstream.status);
  }

  if (!upstream.ok) {
    return text(`upstream ${upstream.status}`, 502);
  }

  return new Response(upstream.body, {
    status: 200,
    headers: {
      'content-type': 'application/json; charset=utf-8',
      'cache-control': cacheSeconds > 0 ? `public, max-age=${cacheSeconds}` : 'no-store',
      'access-control-allow-origin': '*',
    },
  });
}

