using System.Globalization;
using System.Text;

namespace iStCAD_1;

/// <summary>
/// The command line understands text typed with a Vietnamese input method still switched on: "pẻ" is "per" (Telex), so it suggests PERF.
/// Also accepts Vietnamese command words ("tròn" = CIRCLE, "xóa" = ERASE, "hiệu năng" = PERF...), with or without diacritics.
/// </summary>
public partial class MainWindow
{
    private static bool HasVietnameseLetters(string text) => text.Any(c => c > 127 && (char.IsLetter(c)));

    private static string StripVietnamese(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch is 'đ' ? 'd' : ch is 'Đ' ? 'D' : ch);
        }
        return sb.ToString();
    }

    /// <summary>Reverses Telex composition: ẻ → er, ơ → ow, ă → aw, â → aa, đ → dd. The tone key goes at the end of the word, as typed.</summary>
    private static string VietnameseToTelex(string text)
    {
        var sb = new StringBuilder(text.Length + 2); var tones = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            switch (ch)
            {
                case '́': tones.Append('s'); break;
                case '̀': tones.Append('f'); break;
                case '̉': tones.Append('r'); break;
                case '̃': tones.Append('x'); break;
                case '̣': tones.Append('j'); break;
                case '̂': if (sb.Length > 0) sb.Append(sb[^1]); break; // â ê ô: doubled vowel
                case '̆': case '̛': sb.Append(sb.Length > 0 && char.IsUpper(sb[^1]) ? 'W' : 'w'); break; // ă ơ ư
                case 'đ': sb.Append("dd"); break;
                case 'Đ': sb.Append("DD"); break;
                default:
                    if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
                    if (char.IsWhiteSpace(ch) && tones.Length > 0) { sb.Insert(sb.Length - 1, tones.ToString()); tones.Clear(); }
                    break;
            }
        }
        sb.Append(tones);
        return sb.ToString();
    }

    private static readonly (string Phrase, string Command)[] s_vietnameseCommandWords =
    {
        ("duong", "LINE"), ("duongthang", "LINE"), ("doanthang", "LINE"), ("datuyen", "PLINE"), ("duongdagiac", "PLINE"),
        ("tron", "CIRCLE"), ("hinhtron", "CIRCLE"), ("duongtron", "CIRCLE"), ("cung", "ARC"), ("cungtron", "ARC"),
        ("chunhat", "RECTANGLE"), ("hinhchunhat", "RECTANGLE"), ("xoa", "ERASE"), ("dichuyen", "MOVE"), ("doi", "MOVE"),
        ("saochep", "COPY"), ("nhanban", "COPY"), ("xoay", "ROTATE"), ("tile", "SCALE"), ("guong", "MIRROR"), ("doixung", "MIRROR"),
        ("cat", "TRIM"), ("catbo", "TRIM"), ("keodai", "EXTEND"), ("nodai", "EXTEND"), ("songsong", "OFFSET"), ("lech", "OFFSET"),
        ("bogoc", "FILLET"), ("vatgoc", "CHAMFER"), ("lop", "LAYER"), ("chu", "TEXT"), ("vanban", "TEXT"), ("chunhieudong", "MTEXT"),
        ("khoangcach", "DIST"), ("thuoctinh", "PROPERTIES"), ("hoantac", "UNDO"), ("lamlai", "REDO"), ("luu", "SAVE"),
        ("phongto", "ZOOM"), ("thunho", "ZOOM"), ("thuphong", "ZOOM"), ("keo", "PAN"), ("tomau", "HATCH"), ("gachchan", "HATCH"),
        ("khoi", "BLOCK"), ("mang", "ARRAY"), ("donrac", "OVERKILL"), ("lamsach", "OVERKILL"), ("suafont", "VNFIX"), ("loifont", "VNFIX"),
        ("tiengviet", "VNFIX"), ("dagiac", "POLYGON"), ("canchinh", "ALIGN"), ("danhsach", "LIST"), ("duongcong", "SPLINE"), ("chiadeu", "DIVIDE"), ("locdoituong", "QSELECT"), ("dagiacdeu", "RPOLYGON"), ("elip", "ELLIPSE"), ("vanhat", "DONUT"), ("keodai", "LENGTHEN"), ("daochieu", "REVERSE"), ("saochep", "COPYCLIP"), ("dan", "PASTECLIP"), ("hieunang", "PERF"), ("tocdo", "PERF"),
    };

    private bool CommandKnown(string command) =>
        CommandHints.Any(h => h.Name.Equals(command, StringComparison.OrdinalIgnoreCase) ||
            GetAliasesForCommand(h.Name).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(a => a.Equals(command, StringComparison.OrdinalIgnoreCase)));

    private string? VietnameseSynonym(string strippedLower)
    {
        foreach (var (phrase, command) in s_vietnameseCommandWords)
            if (phrase == strippedLower && CommandKnown(command)) return command;
        return null;
    }

    /// <summary>Candidate ASCII spellings of what the user typed, best first.</summary>
    private static List<string> VietCommandForms(string search)
    {
        var forms = new List<string>();
        if (HasVietnameseLetters(search)) { forms.Add(VietnameseToTelex(search).ToUpperInvariant()); forms.Add(StripVietnamese(search).ToUpperInvariant()); }
        forms.Add(search.ToUpperInvariant());
        return forms.Where(f => f.Length > 0).Distinct().ToList();
    }

    private (bool Matched, int Score, int Distance) ScoreCommandHint(CommandHint item, IReadOnlyList<string> forms, string strippedLower)
    {
        var aliases = GetAliasesForCommand(item.Name).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var tokens = new[] { item.Name }.Concat(aliases).ToArray();
        var bestScore = int.MaxValue; var bestDistance = int.MaxValue;
        for (var f = 0; f < forms.Count; f++)
        {
            var search = forms[f];
            var exact = tokens.Any(token => token.Equals(search, StringComparison.OrdinalIgnoreCase));
            var prefix = tokens.Any(token => token.StartsWith(search, StringComparison.OrdinalIgnoreCase));
            var contains = tokens.Any(token => token.Contains(search, StringComparison.OrdinalIgnoreCase));
            var distance = tokens.Min(token => CommandEditDistance(search, token));
            var allowed = search.Length <= 3 ? 1 : search.Length <= 6 ? 2 : 3;
            if (!(exact || prefix || contains || distance <= allowed)) continue;
            var score = (exact ? 0 : prefix ? 10 : contains ? 20 : 30 + distance) + f; // earlier forms (Telex reversal) win ties
            if (score < bestScore) { bestScore = score; bestDistance = distance; }
        }
        // Vietnamese command words ("tron" → CIRCLE), also by prefix once two letters are typed.
        foreach (var (phrase, command) in s_vietnameseCommandWords)
        {
            if (!command.Equals(item.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (phrase == strippedLower) { bestScore = Math.Min(bestScore, 3); bestDistance = 0; }
            else if (strippedLower.Length >= 2 && phrase.StartsWith(strippedLower, StringComparison.Ordinal)) bestScore = Math.Min(bestScore, 12);
        }
        return bestScore == int.MaxValue ? (false, 0, int.MaxValue) : (true, bestScore, bestDistance);
    }

    /// <summary>Rewrites the first word of a typed command that contains Vietnamese letters or is a Vietnamese command word.</summary>
    private string ResolveVietnameseCommand(string normalized)
    {
        if (normalized.Length == 0) return normalized;
        var space = normalized.IndexOf(' ');
        var word = space < 0 ? normalized : normalized[..space];
        var rest = space < 0 ? string.Empty : normalized[space..];
        var diacritics = HasVietnameseLetters(word);
        var stripped = StripVietnamese(word).ToLowerInvariant();
        if (!diacritics && (stripped.Length < 3 || CommandKnown(word))) return normalized;
        var candidates = new List<string>();
        if (VietnameseSynonym(stripped) is { } synonym) candidates.Add(synonym);
        if (diacritics) { candidates.Add(VietnameseToTelex(word).ToUpperInvariant()); candidates.Add(stripped.ToUpperInvariant()); }
        foreach (var candidate in candidates)
            if (CommandKnown(candidate)) return candidate + rest;
        return normalized;
    }
}
