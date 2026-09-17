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
 * Setup: see docs/MONITORING.md.
 *
 * Bindings expected:
 *   KV namespace  STATS
 *   Secret        TOPMC_KEY       Top-Minecrafter API key
 *   Variable      SERVER_ID       server id on Top-Minecrafter (e.g. "6689")
 *   Variable      SERVER_ADDRESS  optional, used when the API reports no address
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

    if (request.method !== 'GET' && request.method !== 'HEAD') {
      return json({ ok: false, error: 'method_not_allowed' }, 405);
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

  await Promise.all([
    env.STATS.put(STATE_KEY, JSON.stringify(state)),
    env.STATS.put(PAYLOAD_KEY, JSON.stringify(render(state, env, now), null, 2)),
  ]);
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
    'access-control-allow-methods': 'GET, HEAD, OPTIONS',
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
