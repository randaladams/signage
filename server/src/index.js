import { verifyAccessJwt } from './access.js';
import adminHtml from './admin.html';

// How often screens check in (seconds). Commands and new images reach a screen within this time.
const POLL_SEC = 120;
// A screen is shown as "online" if it checked in within this many seconds.
const ONLINE_WINDOW_SEC = POLL_SEC * 3;
// Images are deleted automatically this many days after their end date.
const DELETE_AFTER_END_DAYS = 30;
const MAX_UPLOAD_BYTES = 15 * 1024 * 1024;
const PAIR_ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789'; // no 0/O, 1/I/L
const DEFAULT_TZ = 'America/New_York';
const APK_KEY = 'apk/signage-player.apk';

export default {
  async fetch(req, env, ctx) {
    try {
      if (env.ROLE === 'admin') return await handleAdmin(req, env);
      if (env.ROLE === 'device') return await handleDevice(req, env);
      return text('ROLE not configured', 500);
    } catch (err) {
      if (err instanceof HttpError) return json({ error: err.message }, err.status);
      console.error(err && err.stack ? err.stack : err);
      return json({ error: 'Server error' }, 500);
    }
  },

  async scheduled(event, env, ctx) {
    ctx.waitUntil(cleanup(env));
  },
};

// ---------------------------------------------------------------- helpers

class HttpError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

const SECURITY_HEADERS = {
  'X-Content-Type-Options': 'nosniff',
  'Referrer-Policy': 'no-referrer',
  'X-Frame-Options': 'DENY',
};

function json(data, status = 200, extra = {}) {
  return new Response(JSON.stringify(data), {
    status,
    headers: { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store', ...SECURITY_HEADERS, ...extra },
  });
}

function text(body, status = 200) {
  return new Response(body, { status, headers: { 'Content-Type': 'text/plain; charset=utf-8', ...SECURITY_HEADERS } });
}

function randomHex(bytes) {
  const b = crypto.getRandomValues(new Uint8Array(bytes));
  return [...b].map((x) => x.toString(16).padStart(2, '0')).join('');
}

function pairCode() {
  const b = crypto.getRandomValues(new Uint8Array(6));
  return [...b].map((x) => PAIR_ALPHABET[x % PAIR_ALPHABET.length]).join('');
}

async function sha256Hex(s) {
  const d = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(s));
  return [...new Uint8Array(d)].map((x) => x.toString(16).padStart(2, '0')).join('');
}

function todayIn(tz) {
  const fmt = (zone) =>
    new Intl.DateTimeFormat('en-CA', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
  try {
    return fmt(tz || DEFAULT_TZ);
  } catch {
    return fmt('UTC');
  }
}

function isValidTz(tz) {
  try {
    new Intl.DateTimeFormat('en-US', { timeZone: tz });
    return true;
  } catch {
    return false;
  }
}

function addDays(ymd, days) {
  const d = new Date(`${ymd}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}

function cleanDate(v) {
  if (v === undefined || v === null || v === '') return null;
  const s = String(v).trim();
  if (!/^\d{4}-\d{2}-\d{2}$/.test(s) || isNaN(Date.parse(`${s}T00:00:00Z`))) throw new HttpError(400, `Invalid date: ${s}`);
  return s;
}

function cleanText(v, max) {
  return String(v ?? '').replace(/[\u0000-\u001f]/g, ' ').trim().slice(0, max);
}

async function readJson(req) {
  try {
    return await req.json();
  } catch {
    throw new HttpError(400, 'Invalid JSON');
  }
}

async function getSetting(env, key, fallback) {
  const row = await env.DB.prepare('SELECT value FROM settings WHERE key = ?').bind(key).first();
  return row ? row.value : fallback;
}

/** Removes EXIF/XMP (APP1), IPTC (APP13) and comment segments from a JPEG. */
function stripJpegMetadata(buf) {
  const b = new Uint8Array(buf);
  if (b.length < 4 || b[0] !== 0xff || b[1] !== 0xd8) throw new HttpError(400, 'Only JPEG images are accepted');
  const parts = [b.subarray(0, 2)];
  let i = 2;
  while (i < b.length) {
    if (b[i] !== 0xff) throw new HttpError(400, 'Corrupt JPEG');
    const m = b[i + 1];
    if (m === 0xff) { i++; continue; }
    if (m === 0xda) { parts.push(b.subarray(i)); break; } // start of scan: image data follows
    if (m === 0xd9) { parts.push(b.subarray(i, i + 2)); break; }
    if ((m >= 0xd0 && m <= 0xd7) || m === 0x01) { parts.push(b.subarray(i, i + 2)); i += 2; continue; }
    const len = (b[i + 2] << 8) | b[i + 3];
    if (len < 2 || i + 2 + len > b.length) throw new HttpError(400, 'Corrupt JPEG');
    const drop = m === 0xe1 || m === 0xed || m === 0xfe;
    if (!drop) parts.push(b.subarray(i, i + 2 + len));
    i += 2 + len;
  }
  const total = parts.reduce((n, p) => n + p.length, 0);
  const out = new Uint8Array(total);
  let o = 0;
  for (const p of parts) { out.set(p, o); o += p.length; }
  return out;
}

// ---------------------------------------------------------------- device API

async function handleDevice(req, env) {
  const url = new URL(req.url);
  const path = url.pathname;

  if (req.method === 'GET' && path === '/') return text('Signage device API');

  // Public download so a Fire Stick can install the player with the Downloader app.
  if (req.method === 'GET' && (path === '/get' || path === '/signage-player.apk')) {
    const obj = await env.IMAGES.get(APK_KEY);
    if (!obj) return text('Player app has not been built yet', 404);
    return new Response(obj.body, {
      headers: {
        'Content-Type': 'application/vnd.android.package-archive',
        'Content-Disposition': 'attachment; filename="signage-player.apk"',
        'Cache-Control': 'no-store',
      },
    });
  }

  if (req.method === 'POST' && path === '/v1/register') return deviceRegister(req, env);

  // Everything below needs a valid screen token.
  const device = await authDevice(req, env);
  if (!device) return json({ error: 'unauthorized' }, 401);

  if (req.method === 'GET' && path === '/v1/manifest') return deviceManifest(req, env, device);

  const m = path.match(/^\/v1\/image\/([a-f0-9]{32})$/);
  if (req.method === 'GET' && m) return deviceImage(env, device, m[1]);

  return json({ error: 'not found' }, 404);
}

async function authDevice(req, env) {
  const h = req.headers.get('Authorization') || '';
  const token = h.startsWith('Bearer ') ? h.slice(7).trim() : '';
  if (!/^[a-f0-9]{64}$/.test(token)) return null;
  return env.DB.prepare('SELECT * FROM devices WHERE token_hash = ?').bind(await sha256Hex(token)).first();
}

async function deviceRegister(req, env) {
  // Light abuse limit: pairing requests are only kept for a day anyway.
  const hourAgo = Date.now() - 3600 * 1000;
  const recent = await env.DB.prepare("SELECT COUNT(*) AS n FROM devices WHERE status = 'pending' AND created_at > ?")
    .bind(hourAgo)
    .first();
  if (recent && recent.n >= 30) return json({ error: 'Too many pairing requests, try again later' }, 429);

  const id = randomHex(16);
  const token = randomHex(32);
  const tokenHash = await sha256Hex(token);
  for (let attempt = 0; attempt < 5; attempt++) {
    const code = pairCode();
    try {
      await env.DB.prepare(
        "INSERT INTO devices (id, token_hash, pair_code, status, created_at, info) VALUES (?, ?, ?, 'pending', ?, ?)"
      )
        .bind(id, tokenHash, code, Date.now(), cleanText(req.headers.get('X-Device-Info'), 300))
        .run();
      return json({ device_id: id, token, code });
    } catch (e) {
      if (!String(e).includes('UNIQUE')) throw e;
    }
  }
  return json({ error: 'Could not allocate a pairing code' }, 503);
}

async function deviceManifest(req, env, device) {
  const appVersion = cleanText(req.headers.get('X-App-Version'), 40) || null;
  const info = cleanText(req.headers.get('X-Device-Info'), 300) || null;
  await env.DB.prepare('UPDATE devices SET last_seen = ?, app_version = COALESCE(?, app_version), info = COALESCE(?, info), pending_command = NULL WHERE id = ?')
    .bind(Date.now(), appVersion, info, device.id)
    .run();

  if (device.status !== 'paired') {
    return json({ status: 'pending', code: device.pair_code, poll_sec: 5, server_time: Date.now() });
  }

  const tz = await getSetting(env, 'tz', DEFAULT_TZ);
  const today = todayIn(tz);
  let playlist = await env.DB.prepare('SELECT * FROM playlists WHERE id = ?').bind(device.playlist_id || 'default').first();
  if (!playlist) playlist = await env.DB.prepare("SELECT * FROM playlists WHERE id = 'default'").first();

  // Send images that are live now or start within a week, so they're cached before they go live.
  const horizon = addDays(today, 7);
  const { results } = await env.DB.prepare(
    `SELECT i.id, i.title, i.start_date, i.end_date, i.bytes, i.width, i.height
       FROM images i JOIN image_playlists ip ON ip.image_id = i.id
      WHERE ip.playlist_id = ?
        AND (i.start_date IS NULL OR i.start_date <= ?)
        AND (i.end_date IS NULL OR i.end_date >= ?)
      ORDER BY i.sort_order, i.created_at`
  )
    .bind(playlist.id, horizon, today)
    .all();

  return json({
    status: 'paired',
    name: device.name,
    tz,
    today,
    poll_sec: POLL_SEC,
    server_time: Date.now(),
    command: device.pending_command || null,
    playlist: { id: playlist.id, name: playlist.name, interval_sec: playlist.interval_sec, sync: !!playlist.sync },
    images: results.map((r) => ({ ...r, url: `/v1/image/${r.id}` })),
  });
}

async function deviceImage(env, device, imageId) {
  if (device.status !== 'paired') return json({ error: 'not paired' }, 403);
  const allowed = await env.DB.prepare('SELECT 1 AS ok FROM image_playlists WHERE image_id = ? AND playlist_id = ?')
    .bind(imageId, device.playlist_id || 'default')
    .first();
  if (!allowed) return json({ error: 'not found' }, 404);
  const obj = await env.IMAGES.get(`img/${imageId}`);
  if (!obj) return json({ error: 'not found' }, 404);
  return new Response(obj.body, {
    headers: { 'Content-Type': 'image/jpeg', 'Content-Length': String(obj.size), 'Cache-Control': 'private, no-store' },
  });
}

// ---------------------------------------------------------------- admin

async function handleAdmin(req, env) {
  const url = new URL(req.url);

  // Local development only: `wrangler dev` with DEV_NO_AUTH=1 in .dev.vars, on localhost.
  const devBypass = env.DEV_NO_AUTH === '1' && (url.hostname === 'localhost' || url.hostname === '127.0.0.1');
  let user = 'local-dev';
  if (!devBypass) {
    if (!env.ACCESS_TEAM_DOMAIN || !env.ACCESS_AUD) {
      return text('Admin is locked: Cloudflare Access is not configured yet (see README, step 4).', 503);
    }
    const token = req.headers.get('Cf-Access-Jwt-Assertion');
    try {
      const payload = await verifyAccessJwt(token, env.ACCESS_TEAM_DOMAIN, env.ACCESS_AUD);
      user = payload.email || payload.sub || 'unknown';
    } catch (e) {
      return text('Forbidden', 403);
    }
  }

  // Block cross-site form posts.
  if (req.method !== 'GET' && req.method !== 'HEAD') {
    const origin = req.headers.get('Origin');
    if (origin !== url.origin) return json({ error: 'Bad origin' }, 403);
  }

  const path = url.pathname;
  if (req.method === 'GET' && (path === '/' || path === '/index.html')) {
    return new Response(adminHtml, {
      headers: {
        'Content-Type': 'text/html; charset=utf-8',
        'Cache-Control': 'no-store',
        'Content-Security-Policy':
          "default-src 'self'; img-src 'self' blob: data:; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'",
        ...SECURITY_HEADERS,
      },
    });
  }

  if (!path.startsWith('/api/')) return text('Not found', 404);
  return adminApi(req, env, path.slice(4), user);
}

async function adminApi(req, env, path, user) {
  const method = req.method;
  let m;

  if (method === 'GET' && path === '/state') return json(await adminState(env, user));

  // ---- settings
  if (method === 'PUT' && path === '/settings') {
    const body = await readJson(req);
    if (body.tz !== undefined) {
      if (!isValidTz(body.tz)) throw new HttpError(400, 'Unknown time zone');
      await env.DB.prepare('INSERT INTO settings (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value')
        .bind('tz', body.tz)
        .run();
    }
    return json({ ok: true });
  }

  // ---- images
  if (method === 'POST' && path === '/images') return uploadImage(req, env, user);

  if (method === 'POST' && path === '/images/reorder') {
    const body = await readJson(req);
    const ids = Array.isArray(body.ids) ? body.ids.filter((x) => /^[a-f0-9]{32}$/.test(x)) : [];
    if (!ids.length) throw new HttpError(400, 'No ids');
    await env.DB.batch(ids.map((id, idx) => env.DB.prepare('UPDATE images SET sort_order = ? WHERE id = ?').bind(idx, id)));
    return json({ ok: true });
  }

  if ((m = path.match(/^\/images\/([a-f0-9]{32})\/file$/)) && method === 'GET') {
    const obj = await env.IMAGES.get(`img/${m[1]}`);
    if (!obj) return text('Not found', 404);
    return new Response(obj.body, { headers: { 'Content-Type': 'image/jpeg', 'Cache-Control': 'private, max-age=3600' } });
  }

  if ((m = path.match(/^\/images\/([a-f0-9]{32})$/))) {
    const id = m[1];
    const exists = await env.DB.prepare('SELECT id FROM images WHERE id = ?').bind(id).first();
    if (!exists) throw new HttpError(404, 'Image not found');

    if (method === 'PATCH') {
      const body = await readJson(req);
      const stmts = [];
      if (body.title !== undefined) stmts.push(env.DB.prepare('UPDATE images SET title = ? WHERE id = ?').bind(cleanText(body.title, 120), id));
      if (body.start_date !== undefined || body.end_date !== undefined) {
        const cur = await env.DB.prepare('SELECT start_date, end_date FROM images WHERE id = ?').bind(id).first();
        const s = body.start_date !== undefined ? cleanDate(body.start_date) : cur.start_date;
        const e = body.end_date !== undefined ? cleanDate(body.end_date) : cur.end_date;
        if (s && e && e < s) throw new HttpError(400, 'End date is before start date');
        stmts.push(env.DB.prepare('UPDATE images SET start_date = ?, end_date = ? WHERE id = ?').bind(s, e, id));
      }
      if (body.playlists !== undefined) stmts.push(...(await playlistLinkStmts(env, id, body.playlists)));
      if (stmts.length) await env.DB.batch(stmts);
      return json({ ok: true });
    }

    if (method === 'DELETE') {
      await deleteImage(env, id);
      return json({ ok: true });
    }
  }

  // ---- playlists
  if (method === 'POST' && path === '/playlists') {
    const body = await readJson(req);
    const name = cleanText(body.name, 60);
    if (!name) throw new HttpError(400, 'Name is required');
    const id = randomHex(8);
    await env.DB.prepare('INSERT INTO playlists (id, name, interval_sec, sync, created_at) VALUES (?, ?, 10, 1, ?)')
      .bind(id, name, Date.now())
      .run();
    return json({ ok: true, id });
  }

  if ((m = path.match(/^\/playlists\/([a-z0-9]{1,32})$/))) {
    const id = m[1];
    const pl = await env.DB.prepare('SELECT id FROM playlists WHERE id = ?').bind(id).first();
    if (!pl) throw new HttpError(404, 'Playlist not found');

    if (method === 'PATCH') {
      const body = await readJson(req);
      const stmts = [];
      if (body.name !== undefined) {
        const name = cleanText(body.name, 60);
        if (!name) throw new HttpError(400, 'Name is required');
        stmts.push(env.DB.prepare('UPDATE playlists SET name = ? WHERE id = ?').bind(name, id));
      }
      if (body.interval_sec !== undefined) {
        const n = Math.round(Number(body.interval_sec));
        if (!(n >= 3 && n <= 3600)) throw new HttpError(400, 'Interval must be 3–3600 seconds');
        stmts.push(env.DB.prepare('UPDATE playlists SET interval_sec = ? WHERE id = ?').bind(n, id));
      }
      if (body.sync !== undefined) stmts.push(env.DB.prepare('UPDATE playlists SET sync = ? WHERE id = ?').bind(body.sync ? 1 : 0, id));
      if (stmts.length) await env.DB.batch(stmts);
      return json({ ok: true });
    }

    if (method === 'DELETE') {
      if (id === 'default') throw new HttpError(400, 'The default playlist cannot be deleted');
      await env.DB.batch([
        env.DB.prepare("UPDATE devices SET playlist_id = 'default' WHERE playlist_id = ?").bind(id),
        env.DB.prepare('DELETE FROM image_playlists WHERE playlist_id = ?').bind(id),
        env.DB.prepare('DELETE FROM playlists WHERE id = ?').bind(id),
      ]);
      return json({ ok: true });
    }
  }

  // ---- screens
  if (method === 'POST' && path === '/devices/pair') {
    const body = await readJson(req);
    const code = String(body.code || '').toUpperCase().replace(/[^A-Z0-9]/g, '');
    const name = cleanText(body.name, 60) || 'Screen';
    const playlistId = await validPlaylistId(env, body.playlist_id);
    const dev = await env.DB.prepare("SELECT id FROM devices WHERE pair_code = ? AND status = 'pending'").bind(code).first();
    if (!dev) throw new HttpError(404, 'No screen is waiting with that code. Check the code on the TV.');
    await env.DB.prepare("UPDATE devices SET status = 'paired', pair_code = NULL, name = ?, playlist_id = ? WHERE id = ?")
      .bind(name, playlistId, dev.id)
      .run();
    return json({ ok: true, id: dev.id });
  }

  if ((m = path.match(/^\/devices\/([a-f0-9]{32})(\/command)?$/))) {
    const id = m[1];
    const dev = await env.DB.prepare("SELECT id FROM devices WHERE id = ? AND status = 'paired'").bind(id).first();
    if (!dev) throw new HttpError(404, 'Screen not found');

    if (m[2] && method === 'POST') {
      const body = await readJson(req);
      if (!['reload', 'restart'].includes(body.command)) throw new HttpError(400, 'Unknown command');
      await env.DB.prepare('UPDATE devices SET pending_command = ? WHERE id = ?').bind(body.command, id).run();
      return json({ ok: true });
    }
    if (!m[2] && method === 'PATCH') {
      const body = await readJson(req);
      const stmts = [];
      if (body.name !== undefined) stmts.push(env.DB.prepare('UPDATE devices SET name = ? WHERE id = ?').bind(cleanText(body.name, 60) || 'Screen', id));
      if (body.playlist_id !== undefined) {
        stmts.push(env.DB.prepare('UPDATE devices SET playlist_id = ? WHERE id = ?').bind(await validPlaylistId(env, body.playlist_id), id));
      }
      if (stmts.length) await env.DB.batch(stmts);
      return json({ ok: true });
    }
    if (!m[2] && method === 'DELETE') {
      // Revokes the screen's token immediately. The stick will show a new pairing code.
      await env.DB.prepare('DELETE FROM devices WHERE id = ?').bind(id).run();
      return json({ ok: true });
    }
  }

  return json({ error: 'not found' }, 404);
}

async function validPlaylistId(env, id) {
  const pid = String(id || 'default');
  const row = await env.DB.prepare('SELECT id FROM playlists WHERE id = ?').bind(pid).first();
  if (!row) throw new HttpError(400, 'Unknown playlist');
  return pid;
}

async function playlistLinkStmts(env, imageId, playlists) {
  const ids = Array.isArray(playlists) ? [...new Set(playlists.map(String))] : [];
  if (!ids.length) throw new HttpError(400, 'Choose at least one playlist');
  const placeholders = ids.map(() => '?').join(',');
  const { results } = await env.DB.prepare(`SELECT id FROM playlists WHERE id IN (${placeholders})`).bind(...ids).all();
  if (results.length !== ids.length) throw new HttpError(400, 'Unknown playlist');
  return [
    env.DB.prepare('DELETE FROM image_playlists WHERE image_id = ?').bind(imageId),
    ...ids.map((pid) => env.DB.prepare('INSERT INTO image_playlists (image_id, playlist_id) VALUES (?, ?)').bind(imageId, pid)),
  ];
}

async function uploadImage(req, env, user) {
  const len = Number(req.headers.get('Content-Length') || 0);
  if (len > MAX_UPLOAD_BYTES) throw new HttpError(413, 'Image is too large');
  let form;
  try {
    form = await req.formData();
  } catch {
    throw new HttpError(400, 'Expected a form upload');
  }
  const file = form.get('file');
  if (!file || typeof file === 'string') throw new HttpError(400, 'No file');
  if (file.size > MAX_UPLOAD_BYTES) throw new HttpError(413, 'Image is too large');

  const clean = stripJpegMetadata(await file.arrayBuffer());
  const start = cleanDate(form.get('start_date'));
  const end = cleanDate(form.get('end_date'));
  if (start && end && end < start) throw new HttpError(400, 'End date is before start date');
  let playlists;
  try {
    playlists = JSON.parse(form.get('playlists') || '[]');
  } catch {
    throw new HttpError(400, 'Bad playlists');
  }
  const width = Math.round(Number(form.get('width'))) || null;
  const height = Math.round(Number(form.get('height'))) || null;

  const id = randomHex(16);
  const links = await playlistLinkStmts(env, id, playlists); // validates before we store anything
  await env.IMAGES.put(`img/${id}`, clean, { httpMetadata: { contentType: 'image/jpeg' } });
  const maxRow = await env.DB.prepare('SELECT COALESCE(MAX(sort_order), -1) AS m FROM images').first();
  await env.DB.batch([
    env.DB.prepare(
      'INSERT INTO images (id, title, start_date, end_date, sort_order, width, height, bytes, created_at, created_by) VALUES (?,?,?,?,?,?,?,?,?,?)'
    ).bind(id, cleanText(form.get('title'), 120), start, end, maxRow.m + 1, width, height, clean.length, Date.now(), user),
    ...links,
  ]);
  return json({ ok: true, id });
}

async function deleteImage(env, id) {
  await env.IMAGES.delete(`img/${id}`);
  await env.DB.batch([
    env.DB.prepare('DELETE FROM image_playlists WHERE image_id = ?').bind(id),
    env.DB.prepare('DELETE FROM images WHERE id = ?').bind(id),
  ]);
}

async function adminState(env, user) {
  const tz = await getSetting(env, 'tz', null);
  const [playlists, images, links, devices, pending] = await env.DB.batch([
    env.DB.prepare('SELECT * FROM playlists ORDER BY (id = \'default\') DESC, name'),
    env.DB.prepare('SELECT * FROM images ORDER BY sort_order, created_at'),
    env.DB.prepare('SELECT * FROM image_playlists'),
    env.DB.prepare("SELECT id, name, playlist_id, created_at, last_seen, app_version, info FROM devices WHERE status = 'paired' ORDER BY name"),
    env.DB.prepare("SELECT COUNT(*) AS n FROM devices WHERE status = 'pending' AND created_at > ?").bind(Date.now() - 86400000),
  ]);
  const byImage = {};
  for (const l of links.results) (byImage[l.image_id] ||= []).push(l.playlist_id);
  const now = Date.now();
  return {
    user,
    tz,
    today: todayIn(tz || DEFAULT_TZ),
    now,
    delete_after_end_days: DELETE_AFTER_END_DAYS,
    playlists: playlists.results,
    images: images.results.map((i) => ({ ...i, playlists: byImage[i.id] || [] })),
    devices: devices.results.map((d) => ({ ...d, online: !!d.last_seen && now - d.last_seen < ONLINE_WINDOW_SEC * 1000 })),
    pending_count: pending.results[0].n,
  };
}

// ---------------------------------------------------------------- cron

async function cleanup(env) {
  await env.DB.prepare("DELETE FROM devices WHERE status = 'pending' AND created_at < ?").bind(Date.now() - 86400000).run();
  const tz = await getSetting(env, 'tz', DEFAULT_TZ);
  const cutoff = addDays(todayIn(tz), -DELETE_AFTER_END_DAYS);
  const { results } = await env.DB.prepare('SELECT id FROM images WHERE end_date IS NOT NULL AND end_date < ?').bind(cutoff).all();
  for (const r of results) await deleteImage(env, r.id);
  if (results.length) console.log(`cleanup: deleted ${results.length} expired images`);
}
