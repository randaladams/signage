const A='http://127.0.0.1:8788', D='http://127.0.0.1:8787';
let fail=0; const ck=(n,c,x='')=>{console.log((c?'PASS':'FAIL')+'  '+n+' '+x); if(!c) fail++;};
const adm=(m,p,b)=>fetch(A+'/api'+p,{method:m,headers:{Origin:A,'Content-Type':'application/json'},body:b&&JSON.stringify(b)}).then(r=>r.json());
async function screen(name, ver, net) {
  const reg=await (await fetch(D+'/v1/register',{method:'POST'})).json();
  await adm('POST','/devices/pair',{code:reg.code,name,playlist_id:'default'});
  const h={Authorization:'Bearer '+reg.token,'X-App-Version':ver}; if(net!==undefined) h['X-Network']=net;
  await fetch(D+'/v1/manifest',{headers:h}); return reg.token;
}
await screen('Lobby TV','pi-1.4.0',JSON.stringify({type:'ethernet',ip:'10.184.196.74'}));
const t2=await screen('Breakroom','pi-1.4.0',JSON.stringify({type:'wifi',ssid:'RinconGuest',signal:57,ip:'10.184.204.6'}));
await screen('Hallway','pi-1.4.0',JSON.stringify({type:'wifi',ssid:'RinconGuest',signal:31,ip:'10.184.204.9'}));
await screen('Old player','pi-1.3.0',undefined);
await screen('Bad data','pi-1.4.0','{"type":"wifi","ssid":"<script>x</script>","signal":999,"ip":"not-an-ip","extra":"x"}');
await screen('Garbage','pi-1.4.0','not json at all');
const s=await adm('GET','/state'); const by=Object.fromEntries(s.devices.map(d=>[d.name,d.network]));
ck('ethernet stored', JSON.stringify(by['Lobby TV'])==='{"type":"ethernet","ip":"10.184.196.74"}');
ck('wifi stored with ssid/signal/ip', by['Breakroom'].ssid==='RinconGuest' && by['Breakroom'].signal===57 && by['Breakroom'].ip==='10.184.204.6');
ck('old player: no network', by['Old player']===null);
ck('bad values dropped (signal 999, bad ip, extra key)', by['Bad data'].signal===undefined && by['Bad data'].ip===undefined && by['Bad data'].extra===undefined && by['Bad data'].type==='wifi');
ck('garbage header ignored', by['Garbage']===null);
// switching cable -> wifi updates it; a check-in without the header keeps the last value
await fetch(D+'/v1/manifest',{headers:{Authorization:'Bearer '+t2,'X-Network':JSON.stringify({type:'ethernet',ip:'10.184.196.100'})}});
let s2=await adm('GET','/state'); ck('network updates on next check-in', s2.devices.find(d=>d.name==='Breakroom').network.type==='ethernet');
await fetch(D+'/v1/manifest',{headers:{Authorization:'Bearer '+t2}});
s2=await adm('GET','/state'); ck('missing header keeps last known value', s2.devices.find(d=>d.name==='Breakroom').network.type==='ethernet');
await fetch(D+'/v1/manifest',{headers:{Authorization:'Bearer '+t2,'X-Network':JSON.stringify({type:'wifi',ssid:'RinconGuest',signal:57,ip:'10.184.204.6'})}});
console.log(fail?fail+' FAILED':'ALL PASSED'); process.exit(fail?1:0);
