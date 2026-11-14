import { readFileSync } from 'node:fs';
const A='http://127.0.0.1:8788';
let fail=0; const ck=(n,c,x='')=>{console.log((c?'PASS':'FAIL')+'  '+n+' '+x); if(!c) fail++;};
const adm=(m,p,b)=>fetch(A+'/api'+p,{method:m,headers:{Origin:A,'Content-Type':'application/json'},body:b&&JSON.stringify(b)}).then(async r=>({status:r.status, ...(await r.json())}));
const lobby=(await adm('POST','/playlists',{name:'Lobby'})).id;
const brk=(await adm('POST','/playlists',{name:'Break Room'})).id;
const jpg=readFileSync(new URL('./fixtures/test.jpg', import.meta.url)); const ids=[];
const titles=['Employee of the Month – November','November birthdays','Holiday party','Safety reminder','Welcome new hires','Quarterly results','Parking lot repaving notice','Food drive'];
for (const [k,t] of titles.entries()) { const fd=new FormData(); fd.append('file',new Blob([jpg]),'a.jpg'); fd.append('playlists',JSON.stringify(k%3===0?['default',lobby]:k%3===1?['default']:[brk])); fd.append('title',t); if(k===2){fd.append('start_date','2026-12-01');fd.append('end_date','2026-12-20');} if(k===1) fd.append('end_date','2026-11-30');
  ids.push((await (await fetch(A+'/api/images',{method:'POST',headers:{Origin:A},body:fd})).json()).id); }
const pl=(id,s)=>s.images.find(i=>i.id===id).playlists.sort().join(',');
let r=await adm('POST','/images/bulk',{action:'move',playlist_id:brk,ids:[ids[0],ids[1]]}); let s=await adm('GET','/state');
ck('move replaces playlists', r.count===2 && pl(ids[0],s)===brk && pl(ids[1],s)===brk);
r=await adm('POST','/images/bulk',{action:'add',playlist_id:lobby,ids:[ids[0],ids[2]]}); s=await adm('GET','/state');
ck('add keeps existing', pl(ids[0],s)===[brk,lobby].sort().join(',') && pl(ids[2],s)===[brk,lobby].sort().join(','));
r=await adm('POST','/images/bulk',{action:'add',playlist_id:lobby,ids:[ids[0]]}); ck('add twice is harmless', r.status===200);
r=await adm('POST','/images/bulk',{action:'remove',playlist_id:brk,ids:[ids[1]]}); s=await adm('GET','/state');
ck('remove leaves image with no playlist (not deleted)', s.images.some(i=>i.id===ids[1]) && pl(ids[1],s)==='');
r=await adm('POST','/images/bulk',{action:'move',playlist_id:'nope',ids:[ids[0]]}); ck('unknown playlist rejected', r.status===400);
r=await adm('POST','/images/bulk',{action:'delete',ids:[]}); ck('empty selection rejected', r.status===400);
r=await adm('POST','/images/bulk',{action:'delete',ids:[ids[6],ids[7]]}); s=await adm('GET','/state');
ck('bulk delete', r.count===2 && s.images.length===6);
r=await fetch(A+'/api/images/bulk',{method:'POST',headers:{'Content-Type':'application/json',Origin:'https://evil.example'},body:JSON.stringify({action:'delete',ids:[ids[0]]})});
ck('cross-origin bulk delete blocked', r.status===403);
// put a sensible spread back for the UI preview
await adm('POST','/images/bulk',{action:'add',playlist_id:'default',ids:[ids[1],ids[3]]});
console.log(fail?fail+' FAILED':'ALL PASSED'); process.exit(fail?1:0);
