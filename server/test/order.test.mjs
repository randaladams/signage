// Each playlist has its own order: new items go to the end, reordering one playlist doesn't touch
// another, edits keep an item's place, and screens get items in that order.
import { readFileSync } from 'node:fs';
const A = 'http://127.0.0.1:8788', D = 'http://127.0.0.1:8787';
let fail = 0;
const ck = (n, c, x = '') => { console.log((c ? 'PASS' : 'FAIL') + '  ' + n + ' ' + x); if (!c) fail++; };
const adm = (m, p, b) => fetch(A + '/api' + p, { method: m, headers: { Origin: A, 'Content-Type': 'application/json' }, body: b && JSON.stringify(b) });
const jpg = readFileSync(new URL('./fixtures/test.jpg', import.meta.url));

const pa = (await (await adm('POST', '/playlists', { name: 'Order A' })).json()).id;
const pb = (await (await adm('POST', '/playlists', { name: 'Order B' })).json()).id;
const pc = (await (await adm('POST', '/playlists', { name: 'Order C' })).json()).id;
const ids = {};
for (const t of ['a', 'b', 'c', 'd']) {
  const fd = new FormData();
  fd.append('file', new Blob([jpg]), 'x.jpg'); fd.append('playlists', JSON.stringify([pa, pb])); fd.append('title', 'ord-' + t);
  ids[t] = (await (await fetch(A + '/api/images', { method: 'POST', headers: { Origin: A }, body: fd })).json()).id;
}
const name = Object.fromEntries(Object.entries(ids).map(([k, v]) => [v, k]));
const order = async (pid) => ((await (await adm('GET', '/state')).json()).playlist_order[pid] || []).map((id) => name[id]).join('');

ck('new items go to the end, in upload order', (await order(pa)) === 'abcd' && (await order(pb)) === 'abcd');

let r = await adm('POST', `/playlists/${pa}/order`, { ids: [ids.d, ids.c, ids.b, ids.a] });
ck('reorder a playlist', r.ok && (await order(pa)) === 'dcba', await order(pa));
ck("other playlists keep their own order", (await order(pb)) === 'abcd');

// screens get the playlist's order
const reg = await (await fetch(D + '/v1/register', { method: 'POST' })).json();
await adm('POST', '/devices/pair', { code: reg.code, name: 'order-test', playlist_id: pa });
const man = await (await fetch(D + '/v1/manifest', { headers: { Authorization: 'Bearer ' + reg.token } })).json();
ck('screen receives items in playlist order', man.images.map((i) => name[i.id]).join('') === 'dcba', man.images.map((i) => name[i.id]).join(''));

// partial list: listed ones first, the rest keep their relative order; unknown ids ignored
r = await adm('POST', `/playlists/${pa}/order`, { ids: [ids.a, 'f'.repeat(32)] });
ck('partial reorder keeps the rest in order', (await order(pa)) === 'adcb', await order(pa));

// editing an item keeps its place on playlists it stays on; joining a playlist puts it at the end
await adm('PATCH', '/images/' + ids.c, { title: 'renamed', playlists: [pa, pb] });
ck('edit keeps the item in place', (await order(pa)) === 'adcb' && (await order(pb)) === 'abcd');
await adm('PATCH', '/images/' + ids.a, { playlists: [pa] });
ck('removed from a playlist', (await order(pb)) === 'bcd');
await adm('PATCH', '/images/' + ids.a, { playlists: [pa, pb] });
ck('re-added goes to the end', (await order(pb)) === 'bcda' && (await order(pa)) === 'adcb', await order(pb));

// bulk add / move append to the end of the target playlist
await adm('POST', '/images/bulk', { action: 'add', playlist_id: pc, ids: [ids.d, ids.b] });
ck('bulk add appends in the order given', (await order(pc)) === 'db', await order(pc));
await adm('POST', '/images/bulk', { action: 'move', playlist_id: pc, ids: [ids.a] });
ck('bulk move appends', (await order(pc)) === 'dba' && !(await order(pa)).includes('a'), `${await order(pc)} / ${await order(pa)}`);

ck('unknown playlist rejected', (await adm('POST', '/playlists/nosuchlist/order', { ids: [ids.a] })).status === 400);
ck('empty list rejected', (await adm('POST', `/playlists/${pa}/order`, { ids: [] })).status === 400);
ck('cross-site reorder blocked', (await fetch(A + `/api/playlists/${pa}/order`, { method: 'POST', headers: { Origin: 'https://evil.example', 'Content-Type': 'application/json' }, body: JSON.stringify({ ids: [ids.b] }) })).status === 403);

// clean up
await adm('POST', '/images/bulk', { action: 'delete', ids: Object.values(ids) });
const st = await (await adm('GET', '/state')).json();
for (const d of st.devices.filter((x) => x.name === 'order-test')) await adm('DELETE', '/devices/' + d.id);
for (const p of [pa, pb, pc]) await adm('DELETE', '/playlists/' + p);
console.log(fail ? `${fail} FAILED` : 'ALL PASSED');
process.exit(fail ? 1 : 0);
