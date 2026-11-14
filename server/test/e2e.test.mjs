// End-to-end test against local `wrangler dev` (device :8787, admin :8788).
import { readFileSync } from 'node:fs';
const DEV = 'http://127.0.0.1:8787', ADM = 'http://127.0.0.1:8788';
let failures = 0;
const check = (name, cond, extra = '') => { console.log(`${cond ? 'PASS' : 'FAIL'}  ${name} ${extra}`); if (!cond) failures++; };
const admin = (method, path, body, headers = {}) =>
  fetch(ADM + '/api' + path, {
    method,
    headers: { Origin: ADM, ...(body && !(body instanceof FormData) ? { 'Content-Type': 'application/json' } : {}), ...headers },
    body: body instanceof FormData ? body : body ? JSON.stringify(body) : undefined,
  });
const dev = (path, token, method = 'GET') => fetch(DEV + path, { method, headers: token ? { Authorization: 'Bearer ' + token, 'X-App-Version': 'test' } : {} });
const ymd = (d) => { const x = new Date(); x.setDate(x.getDate() + d); return x.toLocaleDateString('en-CA', { timeZone: 'America/Chicago' }); };

await admin('PUT', '/settings', { tz: 'America/Chicago' });

// 1. register + pending
let r = await dev('/v1/register', null, 'POST');
const reg = await r.json();
check('register', r.ok && /^[a-f0-9]{64}$/.test(reg.token) && /^[A-Z2-9]{6}$/.test(reg.code), JSON.stringify(reg.code));
r = await dev('/v1/manifest', reg.token);
let m = await r.json();
check('manifest pending', m.status === 'pending' && m.code === reg.code);

// 2. security checks
r = await dev('/v1/manifest', 'f'.repeat(64));
check('bad token rejected', r.status === 401);
r = await fetch(ADM + '/api/devices/pair', { method: 'POST', headers: { 'Content-Type': 'application/json', Origin: 'https://evil.example' }, body: '{}' });
check('cross-origin admin POST blocked', r.status === 403);

// 3. playlists + pair
r = await admin('POST', '/playlists', { name: 'Lobby' });
const lobby = (await r.json()).id;
r = await admin('POST', '/devices/pair', { code: reg.code.toLowerCase().slice(0, 3) + ' ' + reg.code.slice(3), name: 'Test TV', playlist_id: lobby });
check('pair (lowercase + space tolerated)', r.ok, await r.text());
r = await admin('POST', '/devices/pair', { code: reg.code, name: 'x', playlist_id: lobby });
check('code is single-use', r.status === 404);

// 4. uploads: inject a fake EXIF segment with "GPS" text to prove it is stripped
const jpg = readFileSync(new URL('./fixtures/test.jpg', import.meta.url));
const exifPayload = Buffer.from('Exif\0\0SECRET-GPS-48.85N-2.35E');
const app1 = Buffer.concat([Buffer.from([0xff, 0xe1, (exifPayload.length + 2) >> 8, (exifPayload.length + 2) & 255]), exifPayload]);
const withExif = Buffer.concat([jpg.subarray(0, 2), app1, jpg.subarray(2)]);
async function upload(title, playlists, start = '', end = '') {
  const fd = new FormData();
  fd.append('file', new Blob([withExif], { type: 'image/jpeg' }), 'a.jpg');
  fd.append('playlists', JSON.stringify(playlists));
  fd.append('title', title); fd.append('start_date', start); fd.append('end_date', end);
  fd.append('width', '320'); fd.append('height', '180');
  const res = await admin('POST', '/images', fd);
  return { status: res.status, body: await res.json() };
}
const live = await upload('live', [lobby]);
const allOnly = await upload('default only', ['default']);
const expired = await upload('expired', [lobby], ymd(-10), ymd(-1));
const soon = await upload('starts in 3 days', [lobby], ymd(3), ymd(20));
const later = await upload('starts in 30 days', [lobby], ymd(30));
check('uploads ok', [live, allOnly, expired, soon, later].every((u) => u.status === 200), JSON.stringify([live, expired].map((u) => u.body)));
const bad = await upload('bad dates', [lobby], ymd(5), ymd(1));
check('end before start rejected', bad.status === 400);
const fdBad = new FormData(); fdBad.append('file', new Blob(['not a jpeg']), 'x.jpg'); fdBad.append('playlists', JSON.stringify([lobby]));
r = await admin('POST', '/images', fdBad);
check('non-JPEG rejected', r.status === 400);

// 5. manifest contents
r = await dev('/v1/manifest', reg.token);
m = await r.json();
const ids = m.images.map((i) => i.id);
check('manifest paired', m.status === 'paired' && m.playlist.id === lobby && m.name === 'Test TV');
check('live image included', ids.includes(live.body.id));
check('upcoming (3 days) pre-cached', ids.includes(soon.body.id));
check('expired excluded', !ids.includes(expired.body.id));
check('far-future excluded', !ids.includes(later.body.id));
check('other playlist excluded', !ids.includes(allOnly.body.id));

// 6. image download + EXIF stripped + access control
r = await dev(m.images[0].url, reg.token);
const bytes = Buffer.from(await r.arrayBuffer());
check('image download', r.ok && bytes[0] === 0xff && bytes[1] === 0xd8, `${bytes.length} bytes`);
check('EXIF stripped', !bytes.includes(Buffer.from('SECRET-GPS')) && bytes.length === jpg.length);
r = await dev('/v1/image/' + allOnly.body.id, reg.token);
check("can't fetch image from another playlist", r.status === 404);
r = await dev('/v1/image/' + live.body.id, null);
check('image needs token', r.status === 401);

// 7. edit, reorder, command
r = await admin('PATCH', '/images/' + live.body.id, { end_date: ymd(-2) });
check('edit image', r.ok);
r = await dev('/v1/manifest', reg.token); m = await r.json();
check('edited-to-expired drops out', !m.images.some((i) => i.id === live.body.id));
const st = await (await admin('GET', '/state')).json();
r = await admin('POST', `/devices/${st.devices[0].id}/command`, { command: 'reload' });
r = await dev('/v1/manifest', reg.token); m = await r.json();
check('command delivered once', m.command === 'reload');
r = await dev('/v1/manifest', reg.token); m = await r.json();
check('command cleared after delivery', m.command === null);
check('state shows device online', st.devices[0].online === true && st.devices[0].app_version === 'test');

// 8. revoke
r = await admin('DELETE', '/devices/' + st.devices[0].id);
r = await dev('/v1/manifest', reg.token);
check('revoked token rejected', r.status === 401);

// 9. delete playlist moves screens & cleans links; delete image
r = await admin('DELETE', '/playlists/' + lobby);
check('delete playlist', r.ok);
r = await admin('DELETE', '/playlists/default');
check("can't delete default playlist", r.status === 400);
for (const u of [live, allOnly, expired, soon, later]) await admin('DELETE', '/images/' + u.body.id);
const st2 = await (await admin('GET', '/state')).json();
check('cleanup', st2.images.length === 0 && st2.playlists.length === 1);

console.log(failures ? `\n${failures} FAILED` : '\nALL PASSED');
process.exit(failures ? 1 : 0);
