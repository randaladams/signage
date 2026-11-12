// Verifies the Cloudflare Access JWT on every admin request.
// Access already blocks unauthenticated visitors at the edge; this second check
// means the admin API stays closed even if Access is misconfigured or bypassed.

let certCache = { domain: '', keys: [], fetchedAt: 0 };

function b64urlToBytes(s) {
  s = s.replace(/-/g, '+').replace(/_/g, '/');
  while (s.length % 4) s += '=';
  const bin = atob(s);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

function b64urlToJson(s) {
  return JSON.parse(new TextDecoder().decode(b64urlToBytes(s)));
}

async function getKeys(domain, force) {
  const fresh = Date.now() - certCache.fetchedAt < 60 * 60 * 1000;
  if (!force && certCache.domain === domain && fresh && certCache.keys.length) return certCache.keys;
  const res = await fetch(`https://${domain}/cdn-cgi/access/certs`);
  if (!res.ok) throw new Error(`certs fetch failed: ${res.status}`);
  const body = await res.json();
  certCache = { domain, keys: body.keys || [], fetchedAt: Date.now() };
  return certCache.keys;
}

export function normalizeTeamDomain(d) {
  return String(d || '').trim().replace(/^https?:\/\//, '').replace(/\/.*$/, '');
}

/** Returns the verified payload (with .email) or throws. */
export async function verifyAccessJwt(token, teamDomainRaw, aud) {
  const domain = normalizeTeamDomain(teamDomainRaw);
  const parts = String(token || '').split('.');
  if (parts.length !== 3) throw new Error('malformed token');
  const [h, p, s] = parts;
  const header = b64urlToJson(h);
  const payload = b64urlToJson(p);
  if (header.alg !== 'RS256') throw new Error('unexpected alg');

  let keys = await getKeys(domain, false);
  let jwk = keys.find((k) => k.kid === header.kid);
  if (!jwk) {
    keys = await getKeys(domain, true);
    jwk = keys.find((k) => k.kid === header.kid);
  }
  if (!jwk) throw new Error('unknown key');

  const key = await crypto.subtle.importKey(
    'jwk',
    { kty: jwk.kty, n: jwk.n, e: jwk.e, alg: 'RS256', ext: true },
    { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' },
    false,
    ['verify']
  );
  const ok = await crypto.subtle.verify(
    'RSASSA-PKCS1-v1_5',
    key,
    b64urlToBytes(s),
    new TextEncoder().encode(`${h}.${p}`)
  );
  if (!ok) throw new Error('bad signature');

  const now = Math.floor(Date.now() / 1000);
  if (typeof payload.exp !== 'number' || payload.exp < now) throw new Error('expired');
  if (payload.nbf && payload.nbf > now + 60) throw new Error('not yet valid');
  const auds = Array.isArray(payload.aud) ? payload.aud : [payload.aud];
  if (!auds.includes(aud)) throw new Error('wrong audience');
  if (payload.iss !== `https://${domain}`) throw new Error('wrong issuer');
  return payload;
}
