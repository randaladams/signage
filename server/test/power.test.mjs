const A='http://127.0.0.1:8788', D='http://127.0.0.1:8787';
let fail=0; const ck=(n,c)=>{console.log((c?'PASS':'FAIL')+'  '+n); if(!c) fail++;};
const adm=(m,p,b)=>fetch(A+'/api'+p,{method:m,headers:{Origin:A,'Content-Type':'application/json'},body:b&&JSON.stringify(b)});
const reg=await (await fetch(D+'/v1/register',{method:'POST'})).json();
await adm('POST','/devices/pair',{code:reg.code,name:'pi-test',playlist_id:'default'});
await fetch(D+'/v1/manifest',{headers:{Authorization:'Bearer '+reg.token,'X-App-Version':'pi-1.3.0'}});
const st=await (await adm('GET','/state')).json(); const dev=st.devices.find(d=>d.name==='pi-test');
ck('state shows pi- app version (buttons appear)', dev.app_version==='pi-1.3.0');
for (const c of ['reboot','shutdown']) {
  const r=await adm('POST',`/devices/${dev.id}/command`,{command:c}); ck(`${c} accepted`, r.ok);
  const m=await (await fetch(D+'/v1/manifest',{headers:{Authorization:'Bearer '+reg.token}})).json(); ck(`${c} delivered to the screen`, m.command===c);
  const m2=await (await fetch(D+'/v1/manifest',{headers:{Authorization:'Bearer '+reg.token}})).json(); ck(`${c} cleared after delivery (no reboot loop)`, m2.command===null);
}
ck('unknown command rejected', (await adm('POST',`/devices/${dev.id}/command`,{command:'format-disk'})).status===400);
await adm('DELETE','/devices/'+dev.id);
console.log(fail?fail+' FAILED':'ALL PASSED'); process.exit(fail?1:0);
