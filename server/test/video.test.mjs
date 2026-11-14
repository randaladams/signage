import { readFileSync } from 'node:fs';
const A='http://127.0.0.1:8788', D='http://127.0.0.1:8787';
let fail=0; const ck=(n,c,x='')=>{console.log((c?'PASS':'FAIL')+'  '+n+' '+x); if(!c) fail++;};
const adm=(m,p,b)=>fetch(A+'/api'+p,{method:m,headers:{Origin:A,'Content-Type':'application/json'},body:b&&JSON.stringify(b)});
// fake MP4: 'ftyp' box header + padding (server only checks the container signature)
const mp4 = new Uint8Array(4096); mp4.set([0,0,0,0x18, 0x66,0x74,0x79,0x70, 0x69,0x73,0x6f,0x6d]);
const jpg = readFileSync(new URL('./fixtures/test.jpg', import.meta.url));
async function up(fields){ const fd=new FormData(); for (const [k,v] of Object.entries(fields)) fd.append(k, v); const r=await fetch(A+'/api/images',{method:'POST',headers:{Origin:A},body:fd}); return {status:r.status, ...(await r.json())}; }
const base = { playlists: JSON.stringify(['default']), title: 'clip' };
let v = await up({ ...base, file: new Blob([mp4]), kind: 'video', duration_ms: '12500', thumb: new Blob([jpg]) });
ck('video upload', v.status===200, JSON.stringify(v));
ck('video too long rejected', (await up({ ...base, file: new Blob([mp4]), kind: 'video', duration_ms: '90000' })).status===400);
ck('non-MP4 video rejected', (await up({ ...base, file: new Blob([jpg]), kind: 'video', duration_ms: '5000' })).status===400);
const img = await up({ ...base, file: new Blob([jpg]), title: 'photo' });
let s = await (await adm('GET','/state')).json();
const vi = s.images.find(i=>i.id===v.id);
ck('state has kind/duration/thumb', vi.kind==='video' && vi.duration_ms===12500 && vi.has_thumb===1);
let r = await fetch(A+`/api/images/${v.id}/thumb`); ck('video thumb is a JPEG', r.ok && r.headers.get('content-type')==='image/jpeg' && (await r.arrayBuffer()).byteLength===jpg.length);
r = await fetch(A+`/api/images/${img.id}/thumb`); ck('photo thumb falls back to photo', r.ok);
// device
const reg = await (await fetch(D+'/v1/register',{method:'POST'})).json();
await adm('POST','/devices/pair',{code:reg.code,name:'vtest',playlist_id:'default'});
const man = async (caps) => (await fetch(D+'/v1/manifest',{headers:{Authorization:'Bearer '+reg.token,...(caps?{'X-Capabilities':caps}:{})}})).json();
let m = await man('pause,video'); const mv = m.images.find(i=>i.id===v.id);
ck('video-capable player gets the video', !!mv && mv.kind==='video' && mv.duration_ms===12500);
m = await man('pause'); ck('old player does not get videos', !m.images.some(i=>i.id===v.id) && m.images.some(i=>i.id===img.id));
r = await fetch(D+mv.url,{headers:{Authorization:'Bearer '+reg.token}}); ck('device download served as video/mp4', r.ok && r.headers.get('content-type')==='video/mp4');
await adm('POST','/images/bulk',{action:'delete',ids:[v.id,img.id]});
r = await fetch(A+`/api/images/${v.id}/thumb`); ck('delete removes video + thumb', r.status===404);
const s2=await (await adm('GET','/state')).json(); await adm('DELETE','/devices/'+s2.devices.find(d=>d.name==='vtest').id);
console.log(fail?fail+' FAILED':'ALL PASSED'); process.exit(fail?1:0);
