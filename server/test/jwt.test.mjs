import { verifyAccessJwt } from '../src/access.js';
const { publicKey, privateKey } = await crypto.subtle.generateKey({ name: 'RSASSA-PKCS1-v1_5', modulusLength: 2048, publicExponent: new Uint8Array([1,0,1]), hash: 'SHA-256' }, true, ['sign','verify']);
const jwk = { ...(await crypto.subtle.exportKey('jwk', publicKey)), kid: 'k1' };
globalThis.fetch = async (u) => ({ ok: u === 'https://team.cloudflareaccess.com/cdn-cgi/access/certs', json: async () => ({ keys: [jwk] }) });
const b64 = (o) => Buffer.from(typeof o === 'string' ? o : JSON.stringify(o)).toString('base64url');
async function token(p, kid='k1', key=privateKey) {
  const h = b64({ alg: 'RS256', kid }), b = b64(p);
  const sig = Buffer.from(await crypto.subtle.sign('RSASSA-PKCS1-v1_5', key, new TextEncoder().encode(h + '.' + b))).toString('base64url');
  return `${h}.${b}.${sig}`;
}
const now = Math.floor(Date.now()/1000);
const good = { aud: ['AUD1'], exp: now + 600, iss: 'https://team.cloudflareaccess.com', email: 'a@b.c' };
const cases = [
  ['valid', await token(good), true],
  ['wrong aud', await token({ ...good, aud: ['X'] }), false],
  ['expired', await token({ ...good, exp: now - 5 }), false],
  ['wrong issuer', await token({ ...good, iss: 'https://evil.cloudflareaccess.com' }), false],
  ['tampered payload', (await token(good)).replace(/\.[^.]+\./, '.' + b64({ ...good, email: 'x@y.z' }) + '.'), false],
  ['unknown kid', await token(good, 'nope'), false],
  ['garbage', 'abc', false],
];
let fail = 0;
for (const [n, t, want] of cases) {
  let ok; try { await verifyAccessJwt(t, 'https://team.cloudflareaccess.com/', 'AUD1'); ok = true; } catch { ok = false; }
  console.log((ok === want ? 'PASS' : 'FAIL') + '  ' + n); if (ok !== want) fail++;
}
console.log(fail ? `${fail} FAILED` : 'ALL PASSED');
process.exit(fail);
