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
 */

const API = 'https://api.github.com';

/** The feed is small and changes only on release. */
const FEED_CACHE_SECONDS = 300;

/** Packages are immutable once published. */
const ASSET_CACHE_SECONDS = 86400;

/** The catalog is edited by hand and should show up within a minute. */
const CATALOG_CACHE_SECONDS = 60;

export default {
  async fetch(request, env, ctx) {
    if (request.method !== 'GET' && request.method !== 'HEAD') {
      return text('method not allowed', 405);
    }

    const url = new URL(request.url);
    const name = decodeURIComponent(url.pathname.replace(/^\/+/, ''));

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
