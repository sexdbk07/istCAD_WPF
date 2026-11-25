using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace iStCAD_1.Rendering;

/// <summary>Minimal vector PDF writer: one page per <see cref="PlotSheet"/>, Flate content, clip rectangles, dashes, fills.</summary>
public static class PdfPlotWriter
{
    private const double PtPerMm = 72.0 / 25.4;

    public static void Write(string path, IReadOnlyList<PlotSheet> sheets, string title = "iStCAD plot")
    {
        if (sheets.Count == 0) throw new ArgumentException("No sheets to plot.", nameof(sheets));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        Write(stream, sheets, title);
    }

    public static void Write(Stream output, IReadOnlyList<PlotSheet> sheets, string title = "iStCAD plot")
    {
        var offsets = new List<long>();
        var position = 0L;
        void Raw(byte[] bytes) { output.Write(bytes, 0, bytes.Length); position += bytes.Length; }
        void Text(string s) => Raw(Encoding.ASCII.GetBytes(s));
        void BeginObject(int id) { while (offsets.Count < id) offsets.Add(0); offsets[id - 1] = position; Text($"{id} 0 obj\n"); }

        // Real text: every distinct TrueType face used by the sheets is embedded (Type0/Identity-H + ToUnicode) so the text stays editable.
        var faces = new List<PdfFontFace>();
        var faceIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var faceChars = new List<SortedDictionary<ushort, int>>(); // glyph id -> first unicode seen
        foreach (var sheet in sheets)
            foreach (var run in sheet.Texts)
            {
                var face = PdfFontCatalog.Resolve(run.FontFamily, run.Bold, run.Italic);
                if (face is null) continue;
                if (!faceIndex.TryGetValue(face.Key, out var index)) { index = faces.Count; faceIndex[face.Key] = index; faces.Add(face); faceChars.Add(new()); }
                var map = face.Glyph.CharacterToGlyphMap;
                foreach (var ch in run.Text) if (map.TryGetValue(ch, out var gid)) faceChars[index].TryAdd(gid, ch);
            }
        var infoObject = 4 + sheets.Count * 2;
        var fontBase = infoObject + 1;
        string FontResources() => "/Font << /F1 3 0 R" + string.Concat(faces.Select((f, i) => $" /F{i + 2} {fontBase + i * 5} 0 R")) + " >>";

        // Object layout: 1 catalog, 2 pages, 3 font, then per sheet: page, content; then info; then 5 objects per embedded face.
        Text("%PDF-1.5\n%âãÏÓ\n");
        BeginObject(1); Text("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObject(2);
        var kids = string.Join(" ", Enumerable.Range(0, sheets.Count).Select(i => $"{4 + i * 2} 0 R"));
        Text($"<< /Type /Pages /Kids [{kids}] /Count {sheets.Count} >>\nendobj\n");
        BeginObject(3); Text("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        for (var i = 0; i < sheets.Count; i++)
        {
            var sheet = sheets[i];
            var pageId = 4 + i * 2; var contentId = pageId + 1;
            var widthPt = sheet.WidthMm * PtPerMm; var heightPt = sheet.HeightMm * PtPerMm;
            BeginObject(pageId);
            Text($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(widthPt)} {F(heightPt)}] /Resources << {FontResources()} >> /Contents {contentId} 0 R >>\nendobj\n");
            var content = Encoding.ASCII.GetBytes(BuildContent(sheet, faceIndex));
            using var compressed = new MemoryStream();
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(content, 0, content.Length);
            BeginObject(contentId);
            Text($"<< /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n");
            Raw(compressed.ToArray());
            Text("\nendstream\nendobj\n");
        }
        var infoId = infoObject;
        BeginObject(infoId);
        Text($"<< /Title ({Escape(title)}) /Producer (iStCAD) /CreationDate (D:{DateTime.Now:yyyyMMddHHmmss}) >>\nendobj\n");
        for (var i = 0; i < faces.Count; i++) WriteFace(faces[i], faceChars[i], fontBase + i * 5, BeginObject, Text, Raw);
        var xref = position;
        Text($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Text($"{offset:D10} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info {infoId} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        output.Flush();
    }

    private static void WriteFace(PdfFontFace face, SortedDictionary<ushort, int> chars, int firstId, Action<int> begin, Action<string> text, Action<byte[]> raw)
    {
        var type0 = firstId; var cid = firstId + 1; var descriptor = firstId + 2; var file = firstId + 3; var toUnicode = firstId + 4;
        var glyph = face.Glyph;
        var name = face.PdfName;
        begin(type0);
        text($"<< /Type /Font /Subtype /Type0 /BaseFont /{name} /Encoding /Identity-H /DescendantFonts [{cid} 0 R] /ToUnicode {toUnicode} 0 R >>\nendobj\n");
        var widths = new StringBuilder();
        foreach (var gid in chars.Keys)
        {
            var advance = glyph.AdvanceWidths.TryGetValue(gid, out var aw) ? aw : 0.5;
            widths.Append(gid).Append(" [").Append(Math.Round(advance * 1000).ToString(CultureInfo.InvariantCulture)).Append("] ");
        }
        begin(cid);
        text($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{name} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor {descriptor} 0 R /CIDToGIDMap /Identity /DW 1000 /W [{widths}] >>\nendobj\n");
        var ascent = Math.Round(glyph.Baseline * 1000); var descent = Math.Round(Math.Max(0, glyph.Height - glyph.Baseline) * 1000);
        begin(descriptor);
        text($"<< /Type /FontDescriptor /FontName /{name} /Flags {(face.Italic ? 96 : 32)} /FontBBox [-500 {F(-descent)} 1500 {F(ascent)}] /ItalicAngle {(face.Italic ? -12 : 0)} /Ascent {F(ascent)} /Descent {F(-descent)} /CapHeight {F(Math.Round(glyph.CapsHeight * 1000))} /StemV 80 /FontFile2 {file} 0 R >>\nendobj\n");
        var data = File.ReadAllBytes(face.FilePath);
        using (var packed = new MemoryStream())
        {
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data, 0, data.Length);
            begin(file);
            text($"<< /Length {packed.Length} /Length1 {data.Length} /Filter /FlateDecode >>\nstream\n");
            raw(packed.ToArray());
            text("\nendstream\nendobj\n");
        }
        var cmap = new StringBuilder();
        cmap.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        var entries = chars.ToList();
        for (var i = 0; i < entries.Count; i += 100)
        {
            var chunk = entries.Skip(i).Take(100).ToList();
            cmap.Append(chunk.Count).Append(" beginbfchar\n");
            foreach (var (gid, unicode) in chunk) cmap.Append($"<{gid:X4}> <{unicode:X4}>\n");
            cmap.Append("endbfchar\n");
        }
        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        var cmapBytes = Encoding.ASCII.GetBytes(cmap.ToString());
        begin(toUnicode);
        text($"<< /Length {cmapBytes.Length} >>\nstream\n");
        raw(cmapBytes);
        text("\nendstream\nendobj\n");
    }

    private static string BuildContent(PlotSheet sheet, Dictionary<string, int> faceIndex)
    {
        var sb = new StringBuilder(1 << 16);
        sb.Append("q\n1 J 1 j\n");
        // White paper.
        sb.Append("1 1 1 rg 0 0 ").Append(F(sheet.WidthMm * PtPerMm)).Append(' ').Append(F(sheet.HeightMm * PtPerMm)).Append(" re f\n");
        // Upside-down rotates the whole sheet 180° around its centre.
        if (sheet.UpsideDown) sb.Append("-1 0 0 -1 ").Append(F(sheet.WidthMm * PtPerMm)).Append(' ').Append(F(sheet.HeightMm * PtPerMm)).Append(" cm\n");
        foreach (var item in sheet.Items)
        {
            if (item.Figures.Count == 0) continue;
            sb.Append("q\n");
            if (item.Clip is { } c && c.X1 > c.X0 && c.Y1 > c.Y0)
                sb.Append(F(c.X0 * PtPerMm)).Append(' ').Append(F(c.Y0 * PtPerMm)).Append(' ').Append(F((c.X1 - c.X0) * PtPerMm)).Append(' ').Append(F((c.Y1 - c.Y0) * PtPerMm)).Append(" re W n\n");
            var color = $"{F(item.R / 255.0)} {F(item.G / 255.0)} {F(item.B / 255.0)}";
            if (item.Fill) sb.Append(color).Append(" rg\n");
            else
            {
                sb.Append(color).Append(" RG\n").Append(F(item.WidthMm * PtPerMm)).Append(" w\n");
                if (item.DashMm is { Length: > 0 } dash) sb.Append('[').Append(string.Join(' ', dash.Select(d => F(d * PtPerMm)))).Append("] 0 d\n");
            }
            for (var f = 0; f < item.Figures.Count; f++)
            {
                var p = item.Figures[f];
                sb.Append(F(p[0] * PtPerMm)).Append(' ').Append(F(p[1] * PtPerMm)).Append(" m\n");
                for (var k = 2; k + 1 < p.Length; k += 2)
                    sb.Append(F(p[k] * PtPerMm)).Append(' ').Append(F(p[k + 1] * PtPerMm)).Append(" l\n");
                if (item.Fill) sb.Append("h\n");
                else sb.Append(item.Closed[f] ? "s\n" : "S\n");
            }
            if (item.Fill) sb.Append(item.EvenOdd ? "f*\n" : "f\n");
            sb.Append("Q\n");
        }
        foreach (var run in sheet.Texts)
        {
            var face = PdfFontCatalog.Resolve(run.FontFamily, run.Bold, run.Italic);
            if (face is null || !faceIndex.TryGetValue(face.Key, out var index) || run.EmMm <= 0) continue;
            var map = face.Glyph.CharacterToGlyphMap;
            var hex = new StringBuilder(run.Text.Length * 4);
            foreach (var ch in run.Text) hex.Append(map.TryGetValue(ch, out var gid) ? gid.ToString("X4") : "0000");
            double cos = Math.Cos(run.RotationRad), sin = Math.Sin(run.RotationRad), tan = Math.Tan(run.ObliqueRad);
            sb.Append("q\n");
            if (run.Clip.X1 > run.Clip.X0 && run.Clip.Y1 > run.Clip.Y0)
                sb.Append(F(run.Clip.X0 * PtPerMm)).Append(' ').Append(F(run.Clip.Y0 * PtPerMm)).Append(' ').Append(F((run.Clip.X1 - run.Clip.X0) * PtPerMm)).Append(' ').Append(F((run.Clip.Y1 - run.Clip.Y0) * PtPerMm)).Append(" re W n\n");
            sb.Append(F(run.R / 255.0)).Append(' ').Append(F(run.G / 255.0)).Append(' ').Append(F(run.B / 255.0)).Append(" rg\nBT\n/F").Append(index + 2).Append(' ').Append(F(run.EmMm * PtPerMm)).Append(" Tf\n");
            // Text matrix: x axis = width factor * (cos, sin); y axis = oblique skew then rotation.
            sb.Append(F(run.WidthFactor * cos, 5)).Append(' ').Append(F(run.WidthFactor * sin, 5)).Append(' ')
              .Append(F(tan * cos - sin, 5)).Append(' ').Append(F(tan * sin + cos, 5)).Append(' ')
              .Append(F(run.X * PtPerMm)).Append(' ').Append(F(run.Y * PtPerMm)).Append(" Tm\n<").Append(hex).Append("> Tj\nET\nQ\n");
        }
        if (sheet.StampLines.Count > 0)
        {
            sb.Append("0.3 0.3 0.3 rg\nBT\n/F1 6 Tf\n");
            var y = sheet.Printable.Y0 * PtPerMm + 2;
            var x = sheet.Printable.X0 * PtPerMm + 2;
            foreach (var line in sheet.StampLines.AsEnumerable().Reverse())
            {
                sb.Append("1 0 0 1 ").Append(F(x)).Append(' ').Append(F(y)).Append(" Tm (").Append(Escape(line)).Append(") Tj\n");
                y += 8;
            }
            sb.Append("ET\n");
        }
        sb.Append("Q\n");
        return sb.ToString();
    }

    private static string F(double v, int decimals = 3) => (double.IsFinite(v) ? v : 0).ToString(decimals == 3 ? "0.###" : "0." + new string('#', decimals), CultureInfo.InvariantCulture);

    private static string Escape(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = ch == 'đ' ? 'd' : ch == 'Đ' ? 'D' : ch;
            if (c < 32 || c > 126) c = '?';
            if (c is '(' or ')' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
