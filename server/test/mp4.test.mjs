// The admin page's mp4Plain(): rewrites the browser recorder's fragmented MP4 as a plain MP4 before upload
// (Windows can't seek, rewind or properly start fragmented files). Runs the function straight out of admin.html
// on a synthetic file laid out like Chrome's MediaRecorder output (v1 boxes, trun flags 0x305, default-base-is-moof).
// No server needed.
import { readFileSync } from 'node:fs';
let fail = 0; const ck = (n, c) => { console.log((c ? 'PASS' : 'FAIL') + '  ' + n); if (!c) fail++; };

const html = readFileSync(new URL('../src/admin.html', import.meta.url), 'utf8');
const code = html.slice(html.indexOf('// MP4PLAIN-START'), html.indexOf('// MP4PLAIN-END'));
const mp4Plain = new Function(code + '\nreturn mp4Plain;')();

// ---- tiny MP4 writer
const cat = (...a) => { const n = a.reduce((s, x) => s + x.length, 0); const o = new Uint8Array(n); let p = 0; for (const x of a) { o.set(x, p); p += x.length; } return o; };
const u32 = (...v) => { const b = new Uint8Array(4 * v.length); const dv = new DataView(b.buffer); v.forEach((x, i) => dv.setUint32(i * 4, x >>> 0)); return b; };
const u64 = (v) => { const b = new Uint8Array(8); new DataView(b.buffer).setBigUint64(0, BigInt(v)); return b; };
const str = (s) => new TextEncoder().encode(s);
const box = (t, ...p) => { const body = cat(...p); return cat(u32(8 + body.length), str(t), body); };
const full = (t, v, f, ...p) => box(t, u32((v << 24) | f), ...p);

const TS = 30000, DUR = 1000;                       // 30 fps
const FRAGS = [[900, 120, 130], [800, 110, 115, 140], [700, 100]];  // sample sizes; first of each is a keyframe
let n = 0;
const frags = FRAGS.map((sizes) => sizes.map((size) => { const b = new Uint8Array(size); b.fill(++n); b[0] = n; return b; }));

const stsd = full('stsd', 0, 0, u32(1), box('avc1', new Uint8Array(78), box('avcC', new Uint8Array([1, 0x64, 0, 0x28, 0xff, 0xe1]))));
const moov = box('moov',
  full('mvhd', 1, 0, u64(0), u64(0), u32(1000), u64(0), u32(0x00010000), new Uint8Array([1, 0]), new Uint8Array(10), u32(0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000), new Uint8Array(24), u32(2)),
  box('trak',
    full('tkhd', 1, 3, u64(0), u64(0), u32(1, 0), u64(0), new Uint8Array(8), new Uint8Array(4), u32(0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000), u32(160 << 16, 90 << 16)),
    box('mdia',
      full('mdhd', 1, 0, u64(0), u64(0), u32(TS), u64(0), u32(0x55c40000)),
      full('hdlr', 0, 0, u32(0), str('vide'), new Uint8Array(12), str('VideoHandler\0')),
      box('minf', full('vmhd', 0, 1, new Uint8Array(8)), box('dinf', full('dref', 0, 0, u32(1), full('url ', 0, 1))),
        box('stbl', stsd, full('stts', 0, 0, u32(0)), full('stsc', 0, 0, u32(0)), full('stsz', 0, 0, u32(0, 0)), full('stco', 0, 0, u32(0)))))),
  box('mvex', full('trex', 0, 0, u32(1, 1, 0, 0, 0))));

let t = 0;
const fragments = frags.map((samples, fi) => {
  const build = (dataOffset) => box('moof', full('mfhd', 0, 0, u32(fi + 1)),
    box('traf',
      full('tfhd', 0, 0x020020, u32(1), u32(0x01010000)),                     // default-base-is-moof + default flags (non-sync)
      full('tfdt', 1, 0, u64(t)),
      full('trun', 1, 0x305, u32(samples.length, dataOffset, 0x02000000),     // data offset, first-sample flags (sync)
        ...samples.map((s) => u32(DUR, s.length)))));
  const moof = build(build(0).length + 8);
  t += samples.length * DUR;
  return cat(moof, box('mdat', ...samples));
});
const ftyp = box('ftyp', str('isom'), u32(0x200), str('isomiso6avc1mp41'));
const frag = cat(ftyp, moov, ...fragments);
const ab = (u8) => u8.buffer.slice(u8.byteOffset, u8.byteOffset + u8.byteLength);

// ---- rewrite and inspect
const r = mp4Plain(ab(frag));
ck('fragmented file is rewritten', !!r);
const out = r.bytes, dv = new DataView(out.buffer);
const top = []; for (let p = 0; p + 8 <= out.length;) { const s = dv.getUint32(p); top.push([String.fromCharCode(...out.subarray(p + 4, p + 8)), p, s]); p += s; }
ck('layout is ftyp, moov, mdat (no fragments)', top.map((b) => b[0]).join(' ') === 'ftyp moov mdat');
ck('box sizes add up to the file size', top.reduce((s, b) => s + b[2], 0) === out.length);
const all = frags.flat();
ck(`frame count (${r.frames})`, r.frames === all.length);
ck(`real length from the frames (${r.durationMs} ms)`, r.durationMs === Math.round((all.length * DUR * 1000) / TS));
const [, mdatAt, mdatSize] = top[2];
ck('every frame copied byte for byte, in order', Buffer.from(out.subarray(mdatAt + 8, mdatAt + mdatSize)).equals(Buffer.from(cat(...all))));
const find = (name) => { const i = Buffer.from(out).indexOf(name, 0, 'latin1'); return i < 0 ? -1 : i - 4; };
const stss = find('stss'); const keys = []; for (let k = 0; k < dv.getUint32(stss + 12); k++) keys.push(dv.getUint32(stss + 16 + 4 * k));
ck(`keyframes indexed (${keys})`, keys.join() === '1,4,8');
const stco = find('stco');
ck('sample index points at the frame data', dv.getUint32(stco + 16) === mdatAt + 8);
const stsz = find('stsz');
ck('frame sizes indexed', dv.getUint32(stsz + 16) === all.length && dv.getUint32(stsz + 20) === all[0].length);
const mvhd = find('mvhd');
ck('movie header has the length', dv.getUint32(mvhd + 24) === all.length * DUR && dv.getUint32(mvhd + 20) === TS);
ck('sample description copied unchanged', Buffer.from(out).indexOf(Buffer.from(stsd)) > 0);

// ---- files it must leave alone
ck('a plain MP4 is left alone', mp4Plain(ab(out)) === null);
ck('garbage is left alone', mp4Plain(new Uint8Array(500).fill(7).buffer) === null);
const twoTracks = cat(ftyp, box('moov', ...[...new Uint8Array(0)], moov.subarray(8), moov.subarray(8)), ...fragments);
ck('more than one track is left alone', mp4Plain(ab(twoTracks)) === null);
const truncated = frag.subarray(0, frag.length - 50);
ck('a truncated file is left alone', mp4Plain(ab(truncated)) === null);

console.log(fail ? fail + ' FAILED' : 'ALL PASSED'); process.exit(fail ? 1 : 0);
