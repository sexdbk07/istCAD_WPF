using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>
/// VNFIX - repair Vietnamese text whose character encoding does not match its font (TCVN3/ABC, VNI, Unicode).
/// Follows the AutoCAD VNFIX approach: the encoding the text SHOULD have comes from its existing font (.VnTime -> TCVN3, .VnTimeH -> TCVN3 capitals,
/// VNI-* -> VNI, Arial/Times New Roman... -> Unicode); the encoding it HAS is detected per text run; the text is converted to match the font.
/// Ambiguous or unsafe text (fields, stacks, numeric escapes, unknown fonts) is skipped and reported, never guessed.
/// Fonts: most garbled text is just a missing legacy font, so each text gets the first INSTALLED font that can show it (own font, the drawing's other fonts, then Arial/Times New Roman/Tahoma).
/// VNFIX UNICODE always converts to Unicode in Arial.
/// </summary>
public partial class MainWindow
{
    private enum VnCode { Unknown, Unicode, Vni, Tcvn3, Tcvn3H }

    private sealed class VnSkip(string message) : Exception(message) { }

    private static class VnTables
    {
        public static readonly string U;
        public static readonly string[] V;
        public static readonly Dictionary<string, string> DecodeV = new(StringComparer.Ordinal);
        public static readonly Dictionary<char, string> EncodeV = new();
        public static readonly Dictionary<char, char> DecodeT = new();
        public static readonly Dictionary<char, char> EncodeT = new();

        static VnTables()
        {
            U = "àáảãạăằắẳẵặâầấẩẫậđèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵ".Normalize(NormalizationForm.FormC);
            // TCVN3 (ABC) code points for the same 67 letters, in the same order as U.
            int[] tcvn = { 0xB5,0xB8,0xB6,0xB7,0xB9, 0xA8,0xBB,0xBE,0xBC,0xBD,0xC6, 0xA9,0xC7,0xCA,0xC8,0xC9,0xCB, 0xAE,
                0xCC,0xD0,0xCE,0xCF,0xD1, 0xAA,0xD2,0xD5,0xD3,0xD4,0xD6, 0xD7,0xDD,0xD8,0xDC,0xDE,
                0xDF,0xE3,0xE1,0xE2,0xE4, 0xAB,0xE5,0xE8,0xE6,0xE7,0xE9, 0xAC,0xEA,0xED,0xEB,0xEC,0xEE,
                0xEF,0xF3,0xF1,0xF2,0xF4, 0xAD,0xF5,0xF8,0xF6,0xF7,0xF9, 0xFA,0xFD,0xFB,0xFC,0xFE };
            V = ("aø aù aû aõ aï aê aè aé aú aü aë aâ aà aá aå aã aä ñ " +
                 "eø eù eû eõ eï eâ eà eá eå eã eä ì í æ ó ò oø où oû oõ oï oâ oà oá oå oã oä " +
                 "ô ôø ôù ôû ôõ ôï uø uù uû uõ uï ö öø öù öû öõ öï yø yù yû yõ î").Split(' ');
            if (U.Length != tcvn.Length || U.Length != V.Length) throw new InvalidOperationException("Invalid Vietnamese encoding table.");
            for (var i = 0; i < U.Length; i++)
            {
                DecodeV[V[i]] = U[i].ToString();
                EncodeV[U[i]] = V[i];
                DecodeV[V[i].ToUpperInvariant()] = char.ToUpperInvariant(U[i]).ToString();
                EncodeV[char.ToUpperInvariant(U[i])] = V[i].ToUpperInvariant();
                var t = (char)tcvn[i];
                DecodeT[t] = U[i];
                EncodeT[U[i]] = t;
            }
            const string upperUnicode = "ĂÂĐÊÔƠƯ", upperTcvn = "¡¢§£¤¥¦";
            for (var i = 0; i < upperUnicode.Length; i++) { DecodeT[upperTcvn[i]] = upperUnicode[i]; EncodeT[upperUnicode[i]] = upperTcvn[i]; }
        }
    }

    private static readonly Regex s_vnUnicodeFonts = new(
        "^(arial|times new roman|tahoma|calibri|cambria|verdana|segoe ui|consolas|courier new|aptos|noto sans|noto serif|roboto|open sans|sans serif|serif)( |$)|^(arial(bd|bi|i)?|times(bd|bi|i)?|tahoma(bd)?|calibri(b|i|z)?|verdana(b|i|z)?)$",
        RegexOptions.Compiled);

    /// <summary>The encoding a font expects, from its name. Unknown for custom/SHX fonts: nothing is guessed.</summary>
    private static VnCode VnFontEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return VnCode.Unknown;
        var original = System.IO.Path.GetFileName(name.Trim());
        var n = original.ToLowerInvariant();
        if (n.EndsWith(".shx", StringComparison.Ordinal)) return VnCode.Unknown;
        if (n.EndsWith(".ttf", StringComparison.Ordinal) || n.EndsWith(".otf", StringComparison.Ordinal)) { n = n[..^4]; original = original[..^4]; }
        if (n.StartsWith("vni-", StringComparison.Ordinal)) return VnCode.Vni;
        if (n.StartsWith(".vn", StringComparison.Ordinal))
            return original.EndsWith('H') || (original == n && n.EndsWith('h')) ? VnCode.Tcvn3H : VnCode.Tcvn3;
        return s_vnUnicodeFonts.IsMatch(n) ? VnCode.Unicode : VnCode.Unknown;
    }

    private static bool VnHasVniPair(string value)
    {
        for (var i = 0; i < value.Length - 1; i++)
            if (VnTables.DecodeV.ContainsKey(value.Substring(i, 2))) return true;
        return false;
    }

    /// <summary>Per-run source encoding by character signatures. Heuristic scores, not probabilities; ambiguity throws instead of guessing.</summary>
    private static VnCode VnDetect(string value, VnCode target)
    {
        var input = value.Normalize(NormalizationForm.FormC);
        var hasNonAscii = input.Any(c => c > 127);
        if (!hasNonAscii && !VnHasVniPair(input)) return target;
        int unicodeEvidence = 0, tcvnEvidence = 0, vniEvidence = 0;
        var vietnamese = VnTables.U + VnTables.U.ToUpperInvariant() + "ĂÂĐÊÔƠƯăâđêôơư";
        foreach (var c in input)
        {
            if (vietnamese.IndexOf(c) >= 0) unicodeEvidence++;
            if (VnTables.DecodeT.ContainsKey(c) && vietnamese.IndexOf(c) < 0 && c > 127) tcvnEvidence++;
        }
        for (var pos = 0; pos < input.Length - 1;)
        {
            if (VnTables.DecodeV.ContainsKey(input.Substring(pos, 2)) && char.IsLetter(input[pos])) { vniEvidence++; pos += 2; }
            else pos++;
        }
        var candidates = new List<(VnCode Code, int Score)> { (VnCode.Unicode, unicodeEvidence * 3), (VnCode.Tcvn3, tcvnEvidence * 3), (VnCode.Vni, vniEvidence * 5) }
            .OrderByDescending(x => x.Score).ToList();
        var winner = candidates[0]; var second = candidates[1];
        if (winner.Score == 0)
        {
            if (target == VnCode.Unicode && input.All(c => c <= 127 || vietnamese.IndexOf(c) >= 0)) return VnCode.Unicode;
            throw new VnSkip("ambiguous encoding: no reliable character signatures");
        }
        if (winner.Score == second.Score || (winner.Score - second.Score < 3 && second.Score > 0)) throw new VnSkip("mixed or ambiguous encoding signatures");
        if (winner.Code == VnCode.Vni && vniEvidence == 1 && input.Length > 10) throw new VnSkip("single VNI marker in long text: insufficient evidence");
        if (winner.Code == VnCode.Tcvn3 && target == VnCode.Tcvn3H) return VnCode.Tcvn3H;
        return winner.Code;
    }

    private static string VnConvertRun(string value, VnCode source, VnCode target)
    {
        if (value.Length == 0) return value;
        if (target == VnCode.Unknown)
        {
            if (value.Any(c => c > 127)) throw new VnSkip("unknown font encoding (not guessing)");
            return value;
        }
        if (source == VnCode.Unknown) source = VnDetect(value, target);
        if (source == target) return value;
        var unicode = new StringBuilder();
        for (var i = 0; i < value.Length;)
        {
            var c = value[i];
            if (source == VnCode.Vni)
            {
                if (i + 1 < value.Length && VnTables.DecodeV.TryGetValue(value.Substring(i, 2), out var pair)) { unicode.Append(pair); i += 2; continue; }
                if (VnTables.DecodeV.TryGetValue(c.ToString(), out var single)) { unicode.Append(single); i++; continue; }
            }
            else if (source is VnCode.Tcvn3 or VnCode.Tcvn3H)
            {
                if (VnTables.DecodeT.TryGetValue(c, out var uc)) c = uc;
                if (source == VnCode.Tcvn3H) c = char.ToUpperInvariant(c);
            }
            unicode.Append(c); i++;
        }
        var normalized = unicode.ToString().Normalize(NormalizationForm.FormC);
        if (target == VnCode.Unicode) return normalized;
        var result = new StringBuilder();
        foreach (var c in normalized)
        {
            if (target == VnCode.Vni)
            {
                if (VnTables.EncodeV.TryGetValue(c, out var encoded)) { result.Append(encoded); continue; }
            }
            else
            {
                var key = c;
                if (target == VnCode.Tcvn3H)
                {
                    if (char.IsLower(c) && c > 127) throw new VnSkip("TCVN3 H font cannot keep lowercase appearance");
                    key = char.ToLowerInvariant(c);
                }
                if (VnTables.EncodeT.TryGetValue(key, out var encoded)) { result.Append(encoded); continue; }
            }
            if (c > 127) throw new VnSkip("character '" + c + "' not representable in the font's encoding");
            result.Append(c);
        }
        return result.ToString();
    }

    /// <summary>TEXT (with %%c/%%d/%%p codes) or MTEXT (formatting codes, font switches, braces) rewritten run by run.</summary>
    private static string VnRewrite(string raw, VnCode forcedSource, VnCode baseTarget, bool isMText, bool followFontSwitches)
    {
        if (raw.Contains("%<")) throw new VnSkip("field");
        if (Regex.IsMatch(raw, "%%[0-9]|\\\\U\\+", RegexOptions.IgnoreCase)) throw new VnSkip("numeric/Unicode escape");
        if (!isMText)
        {
            var text = new StringBuilder();
            foreach (var part in Regex.Split(raw, "(%%[A-Za-z%])"))
                text.Append(part.StartsWith("%%", StringComparison.Ordinal) ? part : VnConvertRun(part, forcedSource, baseTarget));
            return text.ToString();
        }
        var output = new StringBuilder(); var run = new StringBuilder(); var stack = new Stack<VnCode>();
        var current = baseTarget; var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (c != '{' && c != '}' && c != '\\' && !(c == '%' && i + 1 < raw.Length && raw[i + 1] == '%')) { run.Append(c); i++; continue; }
            output.Append(VnConvertRun(run.ToString(), forcedSource, current)); run.Clear();
            if (c == '%')
            {
                if (i + 2 >= raw.Length) throw new VnSkip("incomplete %% code");
                output.Append(raw, i, 3); i += 3; continue;
            }
            if (c == '{') { stack.Push(current); output.Append(c); i++; continue; }
            if (c == '}')
            {
                if (stack.Count == 0) throw new VnSkip("unbalanced MTEXT braces");
                current = stack.Pop(); output.Append(c); i++; continue;
            }
            if (i + 1 >= raw.Length) throw new VnSkip("incomplete MTEXT control");
            var control = raw[i + 1];
            if ("\\{}~PLlOoKkX".IndexOf(control) >= 0) { output.Append(raw, i, 2); i += 2; continue; }
            if (control == 'S') throw new VnSkip("stacked MTEXT");
            if ("fFACcHQTWp".IndexOf(control) < 0) throw new VnSkip("unsupported MTEXT control \\" + control);
            var finish = raw.IndexOf(';', i + 2);
            if (finish < 0) throw new VnSkip("incomplete MTEXT format");
            if ((control == 'f' || control == 'F') && followFontSwitches) current = VnFontEncoding(raw.Substring(i + 2, finish - i - 2).Split('|')[0]);
            output.Append(raw, i, finish - i + 1); i = finish + 1;
        }
        output.Append(VnConvertRun(run.ToString(), forcedSource, current));
        if (stack.Count != 0) throw new VnSkip("unbalanced MTEXT braces");
        return output.ToString();
    }

    /// <summary>Font of the first visible character: the \f switch in effect before it, else the text's own font.</summary>
    private static string VnFirstCharFont(string formatted, string fallback)
    {
        var font = fallback;
        for (var i = 0; i < formatted.Length;)
        {
            var c = formatted[i];
            if (c == '{' || c == '}') { i++; continue; }
            if (c != '\\') break;
            if (i + 1 >= formatted.Length) break;
            var control = formatted[i + 1];
            if ("\\{}".IndexOf(control) >= 0) break;
            if ("~PLlOoKkX".IndexOf(control) >= 0) { i += 2; continue; }
            var finish = formatted.IndexOf(';', i + 2);
            if (finish < 0) break;
            if (control is 'f' or 'F') { var name = formatted.Substring(i + 2, finish - i - 2).Split('|')[0]; if (name.Length > 0) font = name; }
            i = finish + 1;
        }
        return font;
    }

    /// <summary>VNFIX KEEP: the font of the first character is the text's font; the content is decoded to correct Unicode, then re-encoded for that font.</summary>
    private static (string Value, string? Formatted, string Font) VnKeepFont(string value, string? formattedValue, string ownFont, bool isMText, VnCode forced, bool requireInstalled = true)
    {
        var basis = isMText ? (string.IsNullOrEmpty(formattedValue) ? value : formattedValue!) : value;
        var font = isMText ? VnFirstCharFont(basis, ownFont) : ownFont;
        var encoding = VnFontEncoding(font);
        if (encoding == VnCode.Unknown) throw new VnSkip("first character's font '" + font + "' has no known Vietnamese encoding");
        if (requireInstalled && !VnFontAvailable(font, out _)) throw new VnSkip("first character's font '" + font + "' is not installed");
        // Step 1: decode every run to correct Unicode (each run detected by its own character signatures).
        string unicode, plainUnicode;
        try
        {
            unicode = VnRewrite(basis, forced, VnCode.Unicode, isMText, false);
            plainUnicode = isMText ? VnConvertRun(value, forced, VnCode.Unicode) : unicode;
        }
        catch (VnSkip) when (forced == VnCode.Unknown && encoding is VnCode.Tcvn3 or VnCode.Tcvn3H or VnCode.Vni)
        {
            // Signatures were ambiguous: trust the font the author chose, but only if the result is plainly Vietnamese.
            unicode = VnRewrite(basis, encoding, VnCode.Unicode, isMText, false);
            plainUnicode = isMText ? VnConvertRun(value, encoding, VnCode.Unicode) : unicode;
            var letters = VnTables.U + VnTables.U.ToUpperInvariant() + "ĂÂĐÊÔƠƯăâđêôơư";
            if (VnPlainForDetection(unicode).Any(c => c > 127 && letters.IndexOf(c) < 0)) throw new VnSkip("ambiguous encoding and the font's own encoding does not give clean Vietnamese");
        }
        if (encoding == VnCode.Unicode) return (isMText ? plainUnicode : unicode, isMText ? unicode : null, font);
        // Step 2: one font for the whole text, so drop inner font switches, then encode for that font.
        if (isMText) unicode = Regex.Replace(unicode, "\\\\[fF][^;]*;", string.Empty);
        var encoded = VnRewrite(unicode, VnCode.Unicode, encoding, isMText, false);
        var encodedPlain = isMText ? VnConvertRun(plainUnicode, VnCode.Unicode, encoding) : encoded;
        return (isMText ? encodedPlain : encoded, isMText ? encoded : null, font);
    }

    private static string VnName(VnCode code) => code switch { VnCode.Unicode => "Unicode", VnCode.Vni => "VNI", VnCode.Tcvn3 => "TCVN3", VnCode.Tcvn3H => "TCVN3-H", _ => "unknown" };

    // ---- Font availability: most "broken" Vietnamese text is simply text whose legacy font is not installed on this PC. ----

    private static HashSet<string>? s_vnInstalledFonts;

    private static HashSet<string> VnInstalledFonts()
    {
        if (s_vnInstalledFonts is not null) return s_vnInstalledFonts;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var family in System.Windows.Media.Fonts.SystemFontFamilies)
            {
                set.Add(family.Source);
                foreach (var name in family.FamilyNames.Values) set.Add(name);
            }
        }
        catch { }
        return s_vnInstalledFonts = set;
    }

    /// <summary>Maps font-file style names (arialbd, timesbi...) to the family name, then checks the installed fonts.</summary>
    private static bool VnFontAvailable(string? name, out string canonical)
    {
        canonical = name?.Trim() ?? string.Empty;
        if (canonical.Length == 0) return false;
        var lower = canonical.ToLowerInvariant();
        if (lower.StartsWith("arial", StringComparison.Ordinal) && VnFontEncoding(lower) == VnCode.Unicode) canonical = "Arial";
        else if (lower.StartsWith("times", StringComparison.Ordinal) && !lower.StartsWith("timesh", StringComparison.Ordinal) && VnFontEncoding(lower) == VnCode.Unicode) canonical = "Times New Roman";
        else if (lower.StartsWith("tahoma", StringComparison.Ordinal)) canonical = "Tahoma";
        else if (lower.StartsWith("calibri", StringComparison.Ordinal)) canonical = "Calibri";
        else if (lower.StartsWith("verdana", StringComparison.Ordinal)) canonical = "Verdana";
        return VnInstalledFonts().Contains(canonical);
    }

    /// <summary>
    /// Finds a font that displays the text correctly: the text's own font if installed, then the other fonts the drawing uses (most used first),
    /// then common Unicode fonts - the first installed one whose encoding the text can be safely converted to wins. Nothing is guessed:
    /// ambiguous text throws <see cref="VnSkip"/>.
    /// </summary>
    private static (string Font, VnCode Target, VnCode Source) VnChooseFont(string detectBasis, string ownFont, IReadOnlyList<string> drawingFonts, VnCode forced, bool unicodeOnly)
    {
        var candidates = new List<string>();
        if (!unicodeOnly)
        {
            candidates.Add(ownFont);
            candidates.AddRange(drawingFonts);
        }
        candidates.AddRange(new[] { "Arial", "Times New Roman", "Tahoma" });
        VnSkip? lastSkip = null;
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!VnFontAvailable(candidate, out var font)) continue;
            var encoding = VnFontEncoding(candidate);
            if (encoding == VnCode.Unknown)
            {
                if (!candidate.Equals(ownFont, StringComparison.OrdinalIgnoreCase)) continue;
                encoding = VnCode.Unicode; // an installed font the text's own style uses, with no legacy-name signature: treat as Unicode
            }
            try
            {
                var source = forced != VnCode.Unknown ? forced : VnDetect(detectBasis, encoding);
                VnConvertRun(detectBasis, source, encoding); // dry run: reject fonts that cannot represent the text
                return (font, encoding, source);
            }
            catch (VnSkip skip) { lastSkip = skip; }
        }
        throw lastSkip ?? new VnSkip("no installed font can display this text");
    }

    private static List<string> VnDrawingFonts(IEnumerable<CadEntity> pool, out Dictionary<string, int> use)
    {
        use = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in pool)
        {
            var font = e switch { CadTextEntity t => t.TextFontFamily, CadMTextEntity m => m.TextFontFamily, _ => null };
            var text = e switch { CadTextEntity t => t.Value, CadMTextEntity m => m.Value, _ => null };
            if (font is null || string.IsNullOrEmpty(text) || !text.Any(c => c > 127)) continue;
            use[font] = use.GetValueOrDefault(font) + 1;
        }
        return use.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }

    private static string VnDescribeFonts(Dictionary<string, int> use) => string.Join(", ", use.OrderByDescending(kv => kv.Value).Take(6)
        .Select(kv => $"{kv.Key} ×{kv.Value:N0} ({(VnFontAvailable(kv.Key, out _) ? "installed" : "NOT installed")}, {VnName(VnFontEncoding(kv.Key))})"));

    /// <summary>Properties palette rows: which font a text uses, whether it is installed, what encoding the characters are in, and the likely cause of garbling.</summary>
    private static void AddVnDiagnosisRows(string font, string value, Action<string, string> add)
    {
        var installed = VnFontAvailable(font, out _);
        var fontEncoding = VnFontEncoding(font);
        add("Font", $"{font} ({(installed ? "installed" : "NOT installed")}, {VnName(fontEncoding)})");
        if (string.IsNullOrEmpty(value) || !value.Any(c => c > 127)) { add("Text encoding", "ASCII only (no accents)"); return; }
        VnCode detected; string detectedText;
        try { detected = VnDetect(value, fontEncoding == VnCode.Unknown ? VnCode.Unicode : fontEncoding); detectedText = VnName(detected); }
        catch (VnSkip skip) { detected = VnCode.Unknown; detectedText = "unclear (" + skip.Message + ")"; }
        add("Text encoding", detectedText);
        var legacyFont = fontEncoding is VnCode.Tcvn3 or VnCode.Tcvn3H or VnCode.Vni;
        string cause;
        if (detected == VnCode.Unknown) cause = "cannot tell - run VNFIX INFO";
        else if (legacyFont && !installed && detected == fontEncoding) cause = "font is not installed on this PC (text itself is fine) - run VNFIX";
        else if (fontEncoding != VnCode.Unknown && VnName(detected) != VnName(fontEncoding)) cause = "text encoding does not match its font - run VNFIX";
        else if (!installed) cause = "font is not installed - run VNFIX";
        else cause = "OK";
        add("Diagnosis", cause);
    }

    private void VnFixInfo(IReadOnlyList<CadEntity> pool, VnCode forced, bool unicodeMode)
    {
        var drawingFonts = VnDrawingFonts(pool, out var use);
        AppendCommandHistory("VNFIX INFO: fonts used by non-ASCII text: " + (use.Count == 0 ? "none" : VnDescribeFonts(use)));
        int total = 0, legacy = 0, shown = 0;
        void Inspect(string? text, string font, string where)
        {
            if (string.IsNullOrEmpty(text)) return;
            total++;
            if (!text.Any(c => c > 127)) return;
            legacy++;
            if (shown >= 8) return;
            shown++;
            string verdict;
            try
            {
                var plain = VnPlainForDetection(text);
                var pick = VnChooseFont(plain, font, drawingFonts, forced, unicodeMode);
                var after = VnRewrite(text, pick.Source, pick.Target, text.Contains('\\'), false);
                verdict = $"detected {VnName(pick.Source)} → font \"{pick.Font}\" ({VnName(pick.Target)}) → \"{Clip(after)}\"";
            }
            catch (VnSkip skip) { verdict = "skipped: " + skip.Message; }
            var codes = string.Join(" ", text.Where(c => c > 127).Distinct().Take(12).Select(c => $"U+{(int)c:X4}"));
            AppendCommandHistory($"VNFIX INFO [{where}, font \"{font}\"] \"{Clip(text)}\" codes: {codes} — {verdict}");
        }
        static string Clip(string s) => (s.Length > 60 ? s[..60] + "..." : s).Replace('\n', ' ');
        foreach (var e in pool)
            switch (e)
            {
                case CadTextEntity t: Inspect(t.Value, t.TextFontFamily, "TEXT"); break;
                case CadMTextEntity m: Inspect(string.IsNullOrEmpty(m.FormattedValue) ? m.Value : m.FormattedValue, m.TextFontFamily, "MTEXT"); break;
                case ProxyCadEntity p:
                    Inspect(p.PreviewText, "Arial", p.NativeType);
                    if (p.ProxyGraphicsTexts is { Count: > 0 }) foreach (var pt in p.ProxyGraphicsTexts) Inspect(pt.Text, "Arial", p.NativeType + " block text");
                    break;
            }
        AppendCommandHistory($"VNFIX INFO: {total:N0} text string(s); {legacy:N0} contain non-ASCII characters (first {shown} shown).");
    }

    private void DwgCheckHelp()
    {
        AppendCommandHistory("DWGCHECK: AutoCAD shows \"Foreign DWG File\" when a DWG has no Autodesk TrustedDWG signature. Only Autodesk or Autodesk-licensed software can write it, so iStCAD cannot (and does not fake it).");
        AppendCommandHistory("  1) In AutoCAD type DWGCHECK and enter 0 (the command DWGCHECK was copied to the clipboard), or tick \"Always open DWG files regardless of origin\" in that dialog. One time per PC.");
        AppendCommandHistory("  2) Or use Save As > DXF: AutoCAD does not show this dialog for DXF files.");
        try { System.Windows.Clipboard.SetText("DWGCHECK"); } catch { }
    }

    private void VnFixCommand(string[] args)
    {
        var upper = args.Select(a => a.ToUpperInvariant()).ToArray();
        var scanOnly = upper.Any(a => a is "CHECK" or "SCAN");
        var unicodeMode = upper.Contains("UNICODE");
        var keepMode = !unicodeMode && !upper.Any(a => a is "AUTO" or "SUBSTITUTE");
        var forced = upper.Contains("TCVN3") ? VnCode.Tcvn3 : upper.Contains("VNI") ? VnCode.Vni : VnCode.Unknown;
        var pool = _selectedEntityIds.Count > 0
            ? _editor.Drawing.AllSpaceEntities.Where(e => _selectedEntityIds.Contains(e.Id)).ToList()
            : _editor.Drawing.AllSpaceEntities.ToList();
        if (upper.Contains("INFO")) { VnFixInfo(pool, forced, unicodeMode); return; }

        var drawingFonts = VnDrawingFonts(pool, out var fontUse);
        if (fontUse.Count > 0) AppendCommandHistory("VNFIX: fonts used by Vietnamese text: " + VnDescribeFonts(fontUse));

        var remove = new List<CadEntity>(); var add = new List<CadEntity>();
        var samples = new List<string>(); var notes = new List<string>(); var skipped = 0;
        var missingFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perEncoding = new Dictionary<VnCode, int>(); var fontChanges = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Skip(string where, string reason) { skipped++; if (notes.Count < 5) notes.Add($"{where}: {reason}"); }
        void NoteFont(string from, string to) { if (!from.Equals(to, StringComparison.OrdinalIgnoreCase)) { var key = from + " → " + to; fontChanges[key] = fontChanges.GetValueOrDefault(key) + 1; } }

        foreach (var e in pool)
        {
            var fallback = false;
            if (keepMode)
            {
                if (e is CadTextEntity kt && !string.IsNullOrEmpty(kt.Value) && (kt.Value.Any(c => c > 127) || VnHasVniPair(kt.Value)))
                {
                    try
                    {
                        var k = VnKeepFont(kt.Value, null, kt.TextFontFamily, false, forced);
                        if (k.Value != kt.Value) { remove.Add(kt); add.Add(kt with { Value = k.Value }); if (samples.Count < 3) samples.Add($"\"{kt.Value}\" → \"{k.Value}\" [{k.Font}]"); perEncoding[VnFontEncoding(k.Font)] = perEncoding.GetValueOrDefault(VnFontEncoding(k.Font)) + 1; }
                    }
                    catch (VnSkip) { fallback = true; }
                }
                else if (e is CadMTextEntity km && !string.IsNullOrEmpty(km.Value) && (km.Value.Any(c => c > 127) || (km.FormattedValue?.Any(c => c > 127) ?? false) || VnHasVniPair(km.Value)))
                {
                    try
                    {
                        var k = VnKeepFont(km.Value, km.FormattedValue, km.TextFontFamily, true, forced);
                        if (k.Value != km.Value || k.Formatted != km.FormattedValue || !k.Font.Equals(km.TextFontFamily, StringComparison.OrdinalIgnoreCase))
                        {
                            remove.Add(km); add.Add(km with { Value = k.Value, FormattedValue = k.Formatted, TextFontFamily = k.Font });
                            if (samples.Count < 3) samples.Add($"\"{km.Value}\" → \"{k.Value}\" [{k.Font}]");
                            perEncoding[VnFontEncoding(k.Font)] = perEncoding.GetValueOrDefault(VnFontEncoding(k.Font)) + 1;
                        }
                    }
                    catch (VnSkip) { fallback = true; }
                }
                if (!fallback) continue;
            }
            switch (e)
            {
                case CadTextEntity t when !string.IsNullOrEmpty(t.Value) && (t.Value.Any(c => c > 127) || VnHasVniPair(t.Value)):
                    try
                    {
                        var pick = VnChooseFont(t.Value, t.TextFontFamily, drawingFonts, forced, unicodeMode);
                        var result = VnRewrite(t.Value, pick.Source, pick.Target, false, false);
                        var fontChanged = !pick.Font.Equals(t.TextFontFamily, StringComparison.OrdinalIgnoreCase);
                        if (result == t.Value && !fontChanged) break;
                        remove.Add(t); add.Add(t with { Value = result, TextFontFamily = pick.Font });
                        perEncoding[pick.Source] = perEncoding.GetValueOrDefault(pick.Source) + 1; NoteFont(t.TextFontFamily, pick.Font);
                        if (samples.Count < 3) samples.Add($"\"{t.Value}\" → \"{result}\" [{pick.Font}]");
                    }
                    catch (VnSkip skip) { Skip("TEXT \"" + (t.Value.Length > 24 ? t.Value[..24] + "..." : t.Value) + "\"", skip.Message); }
                    break;
                case CadMTextEntity m when !string.IsNullOrEmpty(m.Value) && (m.Value.Any(c => c > 127) || (m.FormattedValue?.Any(c => c > 127) ?? false) || VnHasVniPair(m.Value)):
                    try
                    {
                        var basis = string.IsNullOrEmpty(m.FormattedValue) ? m.Value : m.FormattedValue;
                        var pick = VnChooseFont(VnPlainForDetection(basis), m.TextFontFamily, drawingFonts, forced, unicodeMode);
                        var formatted = VnRewrite(basis, pick.Source, pick.Target, true, false);
                        // \f font switches naming a font that is not installed would still render with a fallback glyph set: drop them.
                        formatted = Regex.Replace(formatted, "\\\\[fF]([^;|]*)[^;]*;", match => VnFontAvailable(match.Groups[1].Value, out _) ? match.Value : string.Empty);
                        var plain = VnConvertRun(m.Value, pick.Source, pick.Target);
                        var fontChanged = !pick.Font.Equals(m.TextFontFamily, StringComparison.OrdinalIgnoreCase);
                        if (formatted == m.FormattedValue && plain == m.Value && !fontChanged) break;
                        remove.Add(m); add.Add(m with { Value = plain, FormattedValue = formatted, TextFontFamily = pick.Font });
                        perEncoding[pick.Source] = perEncoding.GetValueOrDefault(pick.Source) + 1; NoteFont(m.TextFontFamily, pick.Font);
                        if (samples.Count < 3) samples.Add($"\"{m.Value}\" → \"{plain}\" [{pick.Font}]");
                    }
                    catch (VnSkip skip) { Skip("MTEXT", skip.Message); }
                    break;
                case ProxyCadEntity proxy:
                {
                    // DIMENSION and block (INSERT) texts live in the proxy preview; the style font is not available here, so they become Unicode in Arial.
                    string? newPreview = null; List<ProxyPreviewText>? newTexts = null; var encoding = VnCode.Unknown;
                    try
                    {
                        if (!string.IsNullOrEmpty(proxy.PreviewText) && proxy.PreviewText.Any(c => c > 127))
                        {
                            var source = forced != VnCode.Unknown ? forced : VnDetect(proxy.PreviewText, VnCode.Unicode);
                            var converted = VnConvertRun(proxy.PreviewText, source, VnCode.Unicode);
                            if (converted != proxy.PreviewText) { newPreview = converted; encoding = source; }
                        }
                        if (proxy.ProxyGraphicsTexts is { Count: > 0 })
                            for (var i = 0; i < proxy.ProxyGraphicsTexts.Count; i++)
                            {
                                var original = proxy.ProxyGraphicsTexts[i].Text;
                                if (string.IsNullOrEmpty(original) || !original.Any(c => c > 127)) continue;
                                var source = forced != VnCode.Unknown ? forced : VnDetect(original, VnCode.Unicode);
                                var converted = VnConvertRun(original, source, VnCode.Unicode);
                                if (converted == original) continue;
                                newTexts ??= proxy.ProxyGraphicsTexts.ToList();
                                newTexts[i] = proxy.ProxyGraphicsTexts[i] with { Text = converted }; encoding = source;
                            }
                    }
                    catch (VnSkip skip) { Skip(proxy.NativeType, skip.Message); break; }
                    if (encoding == VnCode.Unknown) break;
                    remove.Add(proxy);
                    add.Add(proxy with { PreviewText = newPreview ?? proxy.PreviewText, ProxyGraphicsTexts = (IReadOnlyList<ProxyPreviewText>?)newTexts ?? proxy.ProxyGraphicsTexts, TextFontFamily = "Arial" });
                    perEncoding[encoding] = perEncoding.GetValueOrDefault(encoding) + 1;
                    if (samples.Count < 3) samples.Add($"\"{newPreview ?? newTexts?[0].Text}\" (in {proxy.NativeType})");
                    break;
                }
            }
        }

        var breakdown = string.Join(", ", perEncoding.Select(kv => $"{VnName(kv.Key)} {kv.Value:N0}"));
        if (remove.Count == 0)
        {
            AppendCommandHistory(skipped > 0 ? $"VNFIX: nothing safe to convert; {skipped:N0} text(s) skipped." : "VNFIX: every Vietnamese text already displays with an installed, matching font.");
            foreach (var note in notes) AppendCommandHistory("  skipped - " + note);
            return;
        }
        AppendCommandHistory($"VNFIX: {remove.Count:N0} text object(s) {(scanOnly ? "need repair" : "repaired")} (source: {breakdown}); {skipped:N0} skipped. Examples: {string.Join("; ", samples)}");
        if (fontChanges.Count > 0) AppendCommandHistory("  fonts: " + string.Join("; ", fontChanges.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} ×{kv.Value:N0}")));
        foreach (var note in notes) AppendCommandHistory("  skipped - " + note);
        if (keepMode && missingFonts.Count > 0) AppendCommandHistory("  note: font(s) not installed on this PC: " + string.Join(", ", missingFonts) + " - the text is correct for that font; install it to see it properly.");
        if (scanOnly) { AppendCommandHistory("VNFIX: check only - run VNFIX to apply. VNFIX UNICODE = always Unicode in Arial; VNFIX TCVN3|VNI forces the source encoding; VNFIX INFO explains each decision."); return; }
        _editor.ApplyEntityChanges(remove, add);
        if (keepMode) { AppendCommandHistory("VNFIX: each text keeps the font of its first character when that font is installed (content decoded to correct Unicode, then re-encoded for that font); other texts use the first installed font that can show them. VNFIX AUTO = always substitute; VNFIX UNICODE = Unicode + Arial. U to undo."); return; }
        AppendCommandHistory("VNFIX: each text now uses the first installed font that can show it (its own font, other fonts of the drawing, then Arial / Times New Roman / Tahoma). U to undo. Note: the DWG text styles keep their original font names.");
    }

    /// <summary>MTEXT formatting codes carry ASCII only; strip them so they cannot dilute the signature scores.</summary>
    private static string VnPlainForDetection(string formatted) => Regex.Replace(formatted, "\\\\[A-Za-z][^;\\\\{}]*;|\\\\[LlOoKk~PX]|[{}]", string.Empty);
}
