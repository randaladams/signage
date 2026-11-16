// Migration 0004 gives each playlist its own order, starting from the old global order (incl. ties). No server needed.
import { DatabaseSync } from 'node:sqlite';
import { readFileSync } from 'node:fs';
const db = new DatabaseSync(':memory:');
const m = (n) => db.exec(readFileSync(new URL(`../migrations/${n}`, import.meta.url), 'utf8'));
m('0001_init.sql'); m('0002_video.sql'); m('0003_device_network.sql');
db.exec("INSERT INTO playlists (id,name,interval_sec,sync,created_at) VALUES ('lobby','Lobby',10,1,0)");
// global order: c(0) a(1) d(1, older) b(2) e(5); ties broken by created_at then id
const imgs = [['a',1,200],['b',2,100],['c',0,300],['d',1,150],['e',5,50]];
for (const [id, so, ca] of imgs) db.prepare("INSERT INTO images (id,title,sort_order,created_at) VALUES (?,?,?,?)").run(id, id, so, ca);
for (const [img, pl] of [['a','default'],['b','default'],['c','default'],['d','lobby'],['a','lobby'],['e','lobby'],['b','lobby']]) db.prepare('INSERT INTO image_playlists (image_id, playlist_id) VALUES (?,?)').run(img, pl);
m('0004_playlist_order.sql');
const order = (pl) => db.prepare('SELECT image_id, position FROM image_playlists WHERE playlist_id=? ORDER BY position').all(pl).map(r => `${r.image_id}${r.position}`).join(' ');
const want = { default: 'c0 a1 b2', lobby: 'd0 a1 b2 e3' };
let fail = 0;
for (const pl of ['default', 'lobby']) { const got = order(pl); const ok = got === want[pl]; if (!ok) fail++; console.log(`${ok ? 'PASS' : 'FAIL'}  ${pl}: ${got} (want ${want[pl]})`); }
console.log(fail ? 'FAILED' : 'ALL PASSED'); process.exit(fail);
