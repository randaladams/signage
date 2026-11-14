using System;
using System.Collections.Generic;
using System.IO;

namespace SignagePlayer;

/// <summary>
/// Rewrites a fragmented MP4 (what the admin page's browser recorder makes: moov + moof/mdat pairs, no sample index)
/// as a plain MP4 with a full index (moov with stts/stss/stsz/stco, then one mdat). Same frames, no re-encoding.
///
/// Why: Windows (Media Foundation, used by WPF MediaElement) plays the fragmented files badly. It freezes on the first
/// frame until the second keyframe (~3.8 s of still picture at the start of every clip), doesn't know their length,
/// can't seek in them and can't rewind them once ended. The rewritten file plays from its first frame and has all of
/// those. Measured on the owner's PC, Nov 2026. Files with more than one track, or anything unexpected, are left alone.
/// </summary>
static class Mp4Fix
{
    /// <summary>Rewrites the file in place if it is fragmented. Returns true if it was rewritten.</summary>
    public static bool FixInPlace(string path)
    {
        byte[] d;
        try
        {
            if (!IsFragmented(path)) return false;
            d = File.ReadAllBytes(path);
        }
        catch (Exception e)
        {
            Log.Warn($"Could not read {Path.GetFileName(path)} to check it", e);
            return false;
        }
        try
        {
            var plain = Rewrite(d);
            if (plain == null) return false;
            var tmp = path + ".fix";
            File.WriteAllBytes(tmp, plain);
            File.Move(tmp, path, overwrite: true);
            Log.Info($"Rewrote {Path.GetFileName(path)} as a plain MP4 ({d.Length / 1000} KB -> {plain.Length / 1000} KB) so Windows plays it properly");
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"Could not rewrite {Path.GetFileName(path)}; playing it as it is", e);
            Store.TryDelete(path + ".fix");
            return false;
        }
    }

    /// <summary>Looks only at the top-level box headers: a 'moof' box means fragmented.</summary>
    public static bool IsFragmented(string path)
    {
        using var f = File.OpenRead(path);
        var hdr = new byte[16];
        long pos = 0;
        for (var n = 0; n < 8 && pos + 8 <= f.Length; n++)
        {
            f.Position = pos;
            if (f.Read(hdr, 0, 16) < 8) break;
            long size = U32(hdr, 0);
            var type = System.Text.Encoding.ASCII.GetString(hdr, 4, 4);
            if (size == 1) size = (long)U64(hdr, 8);
            else if (size == 0) size = f.Length - pos;
            if (type == "moof") return true;
            if (size < 8) break;
            pos += size;
        }
        return false;
    }

    private sealed record Box(string Type, int Start, int Content, int End);

    private static IEnumerable<Box> Boxes(byte[] d, int s, int e)
    {
        var i = s;
        while (i + 8 <= e)
        {
            long size = U32(d, i);
            var hdr = 8;
            if (size == 1) { size = (long)U64(d, i + 8); hdr = 16; }
            else if (size == 0) size = e - i;
            if (size < hdr || i + size > e) yield break;
            yield return new Box(System.Text.Encoding.ASCII.GetString(d, i + 4, 4), i, i + hdr, (int)(i + size));
            i += (int)size;
        }
    }

    private static Box? Child(byte[] d, Box parent, string type)
    {
        foreach (var b in Boxes(d, parent.Content, parent.End)) if (b.Type == type) return b;
        return null;
    }

    private static byte[]? Rewrite(byte[] d)
    {
        var top = new List<Box>(Boxes(d, 0, d.Length));
        var ftyp = top.Find(b => b.Type == "ftyp");
        var moov = top.Find(b => b.Type == "moov");
        if (ftyp == null || moov == null) return null;
        var traks = new List<Box>();
        foreach (var b in Boxes(d, moov.Content, moov.End)) if (b.Type == "trak") traks.Add(b);
        if (traks.Count != 1) return null;   // only the recorder's single video track is handled
        var trak = traks[0];
        var tkhd = Child(d, trak, "tkhd");
        var mdia = Child(d, trak, "mdia");
        if (tkhd == null || mdia == null) return null;
        var mdhd = Child(d, mdia, "mdhd");
        var hdlr = Child(d, mdia, "hdlr");
        var minf = Child(d, mdia, "minf");
        if (mdhd == null || hdlr == null || minf == null) return null;
        if (System.Text.Encoding.ASCII.GetString(d, hdlr.Content + 8, 4) != "vide") return null;
        var vmhd = Child(d, minf, "vmhd");
        var dinf = Child(d, minf, "dinf");
        var stbl = Child(d, minf, "stbl");
        if (vmhd == null || dinf == null || stbl == null) return null;
        var stsd = Child(d, stbl, "stsd");
        if (stsd == null) return null;
        var mdhdVer = d[mdhd.Content];
        var timescale = mdhdVer == 0 ? U32(d, mdhd.Content + 12) : U32(d, mdhd.Content + 20);
        if (timescale == 0) return null;

        // Collect every sample from the fragments.
        var samples = new List<(long off, uint size, uint dur, int cto, bool key)>();
        foreach (var moof in top)
        {
            if (moof.Type != "moof") continue;
            foreach (var traf in Boxes(d, moof.Content, moof.End))
            {
                if (traf.Type != "traf") continue;
                long baseOff = moof.Start;
                uint dfltDur = 0, dfltSize = 0, dfltFlags = 0;
                foreach (var b in Boxes(d, traf.Content, traf.End))
                {
                    if (b.Type == "tfhd")
                    {
                        var fl = U24(d, b.Content + 1);
                        var p = b.Content + 8;
                        if ((fl & 0x1) != 0) { baseOff = (long)U64(d, p); p += 8; }
                        if ((fl & 0x2) != 0) p += 4;
                        if ((fl & 0x8) != 0) { dfltDur = U32(d, p); p += 4; }
                        if ((fl & 0x10) != 0) { dfltSize = U32(d, p); p += 4; }
                        if ((fl & 0x20) != 0) { dfltFlags = U32(d, p); }
                    }
                    else if (b.Type == "trun")
                    {
                        var ver = d[b.Content];
                        var fl = U24(d, b.Content + 1);
                        var n = U32(d, b.Content + 4);
                        var p = b.Content + 8;
                        var off = baseOff;
                        if ((fl & 0x1) != 0) { off = baseOff + (int)U32(d, p); p += 4; }
                        uint? first = null;
                        if ((fl & 0x4) != 0) { first = U32(d, p); p += 4; }
                        for (var k = 0; k < n; k++)
                        {
                            uint dur = dfltDur, size = dfltSize, sf = dfltFlags;
                            var cto = 0;
                            if ((fl & 0x100) != 0) { dur = U32(d, p); p += 4; }
                            if ((fl & 0x200) != 0) { size = U32(d, p); p += 4; }
                            if ((fl & 0x400) != 0) { sf = U32(d, p); p += 4; }
                            if ((fl & 0x800) != 0) { cto = ver == 0 ? (int)U32(d, p) : (int)U32(d, p); p += 4; }
                            if (k == 0 && first != null) sf = first.Value;
                            if (off < 0 || off + size > d.Length) return null;
                            samples.Add((off, size, dur, cto, (sf & 0x10000) == 0));
                            off += size;
                        }
                    }
                }
            }
        }
        if (samples.Count == 0 || !samples[0].key) return null;

        ulong total = 0;
        foreach (var s in samples) total += s.dur;
        if (total == 0 || total > uint.MaxValue) return null;

        // Sample tables.
        var stts = new List<(uint count, uint dur)>();
        foreach (var s in samples)
        {
            if (stts.Count > 0 && stts[^1].dur == s.dur) stts[^1] = (stts[^1].count + 1, s.dur);
            else stts.Add((1, s.dur));
        }
        var anyCto = samples.Exists(s => s.cto != 0);
        var ctts = new List<(uint count, int off)>();
        if (anyCto)
            foreach (var s in samples)
            {
                if (ctts.Count > 0 && ctts[^1].off == s.cto) ctts[^1] = (ctts[^1].count + 1, s.cto);
                else ctts.Add((1, s.cto));
            }
        var keys = new List<uint>();
        for (var i = 0; i < samples.Count; i++) if (samples[i].key) keys.Add((uint)i + 1);

        long payload = 0;
        foreach (var s in samples) payload += s.size;

        byte[] BuildMoov(uint mdatDataStart)
        {
            var w = new BoxWriter();
            w.Begin("moov");
            // mvhd v0
            w.Begin("mvhd"); w.U32(0); w.U32(0); w.U32(0); w.U32(timescale); w.U32((uint)total);
            w.U32(0x00010000); w.U16(0x0100); w.Zero(10);
            foreach (var m in new uint[] { 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000 }) w.U32(m);
            w.Zero(24); w.U32(2); w.End();
            w.Begin("trak");
            var th = Slice(d, tkhd);
            if (th[8] == 0) PutU32(th, 8 + 4 + 16, (uint)total); else PutU64(th, 8 + 4 + 24, total);
            w.Raw(th);
            w.Begin("mdia");
            var mh = Slice(d, mdhd);
            if (mdhdVer == 0) PutU32(mh, 8 + 4 + 12, (uint)total); else PutU64(mh, 8 + 4 + 20, total);
            w.Raw(mh);
            w.Raw(Slice(d, hdlr));
            w.Begin("minf");
            w.Raw(Slice(d, vmhd));
            w.Raw(Slice(d, dinf));
            w.Begin("stbl");
            w.Raw(Slice(d, stsd));
            w.Begin("stts"); w.U32(0); w.U32((uint)stts.Count); foreach (var (c, du) in stts) { w.U32(c); w.U32(du); } w.End();
            if (anyCto)
            {
                var negative = ctts.Exists(c => c.off < 0);
                w.Begin("ctts"); w.U32(negative ? 0x01000000u : 0); w.U32((uint)ctts.Count);
                foreach (var (c, o) in ctts) { w.U32(c); w.U32((uint)o); }
                w.End();
            }
            w.Begin("stss"); w.U32(0); w.U32((uint)keys.Count); foreach (var k in keys) w.U32(k); w.End();
            w.Begin("stsc"); w.U32(0); w.U32(1); w.U32(1); w.U32((uint)samples.Count); w.U32(1); w.End();
            w.Begin("stsz"); w.U32(0); w.U32(0); w.U32((uint)samples.Count); foreach (var s in samples) w.U32(s.size); w.End();
            w.Begin("stco"); w.U32(0); w.U32(1); w.U32(mdatDataStart); w.End();
            w.End(); // stbl
            w.End(); // minf
            w.End(); // mdia
            w.End(); // trak
            w.End(); // moov
            return w.ToArray();
        }

        var ftypBytes = Slice(d, ftyp);
        var moovLen = BuildMoov(0).Length;
        var dataStart = (long)ftypBytes.Length + moovLen + 8;
        if (dataStart + payload > uint.MaxValue) return null;
        var moovBytes = BuildMoov((uint)dataStart);

        var outBuf = new byte[dataStart + payload];
        Buffer.BlockCopy(ftypBytes, 0, outBuf, 0, ftypBytes.Length);
        Buffer.BlockCopy(moovBytes, 0, outBuf, ftypBytes.Length, moovBytes.Length);
        var mdatAt = ftypBytes.Length + moovBytes.Length;
        PutU32(outBuf, mdatAt, (uint)(8 + payload));
        outBuf[mdatAt + 4] = (byte)'m'; outBuf[mdatAt + 5] = (byte)'d'; outBuf[mdatAt + 6] = (byte)'a'; outBuf[mdatAt + 7] = (byte)'t';
        var o = (int)dataStart;
        foreach (var s in samples)
        {
            Buffer.BlockCopy(d, (int)s.off, outBuf, o, (int)s.size);
            o += (int)s.size;
        }
        return outBuf;
    }

    // ---- byte helpers

    private static byte[] Slice(byte[] d, Box b) => d[b.Start..b.End];
    private static uint U32(byte[] d, int i) => (uint)(d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3]);
    private static uint U24(byte[] d, int i) => (uint)(d[i] << 16 | d[i + 1] << 8 | d[i + 2]);
    private static ulong U64(byte[] d, int i) => (ulong)U32(d, i) << 32 | U32(d, i + 4);
    private static void PutU32(byte[] d, int i, uint v) { d[i] = (byte)(v >> 24); d[i + 1] = (byte)(v >> 16); d[i + 2] = (byte)(v >> 8); d[i + 3] = (byte)v; }
    private static void PutU64(byte[] d, int i, ulong v) { PutU32(d, i, (uint)(v >> 32)); PutU32(d, i + 4, (uint)v); }

    private sealed class BoxWriter
    {
        private readonly MemoryStream _m = new();
        private readonly Stack<long> _open = new();
        public void Begin(string type) { _open.Push(_m.Position); U32(0); foreach (var c in type) _m.WriteByte((byte)c); }
        public void End()
        {
            var start = _open.Pop();
            var end = _m.Position;
            _m.Position = start;
            U32((uint)(end - start));
            _m.Position = end;
        }
        public void U32(uint v) { _m.WriteByte((byte)(v >> 24)); _m.WriteByte((byte)(v >> 16)); _m.WriteByte((byte)(v >> 8)); _m.WriteByte((byte)v); }
        public void U16(ushort v) { _m.WriteByte((byte)(v >> 8)); _m.WriteByte((byte)v); }
        public void Zero(int n) { for (var i = 0; i < n; i++) _m.WriteByte(0); }
        public void Raw(byte[] b) => _m.Write(b);
        public byte[] ToArray() => _m.ToArray();
    }
}
