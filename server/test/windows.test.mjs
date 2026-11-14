// Windows player: download links, and a "win-" player checking in like the other players do.
const A='http://127.0.0.1:8788', D='http://127.0.0.1:8787';
let fail=0; const ck=(n,c)=>{console.log((c?'PASS':'FAIL')+'  '+n); if(!c) fail++;};
const adm=(m,p,b)=>fetch(A+'/api'+p,{method:m,headers:{Origin:A,'Content-Type':'application/json'},body:b&&JSON.stringify(b)});

// Nothing is built in a fresh local server: both links answer with a clear 404, not a server error.
for (const p of ['/get/windows','/SignagePlayer.exe']) {
  const r=await fetch(D+p); ck(`${p} before a build: 404 with a message`, r.status===404 && /not been built/.test(await r.text()));
}
const apk=await fetch(D+'/get'); ck('/get (Android) unaffected', apk.status===404 && /Player app/.test(await apk.text()));

const reg=await (await fetch(D+'/v1/register',{method:'POST',headers:{'X-Device-Info':'Windows 11 Pro 23H2 (10.0.22631) | Dell OptiPlex | 1920x1080'}})).json();
await adm('POST','/devices/pair',{code:reg.code,name:'win-test',playlist_id:'default'});
const net=JSON.stringify({type:'wifi',ssid:'Office',signal:72,ip:'192.168.1.50'});
const m=await (await fetch(D+'/v1/manifest',{headers:{Authorization:'Bearer '+reg.token,'X-App-Version':'win-1.0.0','X-Capabilities':'pause,video','X-Network':net}})).json();
ck('Windows player gets a paired manifest', m.status==='paired' && Array.isArray(m.images));
const st=await (await adm('GET','/state')).json(); const dev=st.devices.find(d=>d.name==='win-test');
ck('state shows win- version', dev && dev.app_version==='win-1.0.0');
ck('state shows Windows device info', dev && dev.info.startsWith('Windows 11'));
ck('state shows reported network', dev && dev.network && dev.network.type==='wifi' && dev.network.signal===72);
const r=await adm('POST',`/devices/${dev.id}/command`,{command:'restart'}); ck('restart command accepted', r.ok);
const m2=await (await fetch(D+'/v1/manifest',{headers:{Authorization:'Bearer '+reg.token}})).json(); ck('restart delivered', m2.command==='restart');
await adm('DELETE','/devices/'+dev.id);
const gone=await fetch(D+'/v1/manifest',{headers:{Authorization:'Bearer '+reg.token}}); ck('removed screen is cut off (401)', gone.status===401);
console.log(fail?fail+' FAILED':'ALL PASSED'); process.exit(fail?1:0);
