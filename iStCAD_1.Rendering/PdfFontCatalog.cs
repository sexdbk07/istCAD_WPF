using System.IO;
using System.Windows;
using System.Windows.Media;

namespace iStCAD_1.Rendering;

/// <summary>A TrueType face that can be embedded in a PDF so plotted text stays real, editable text.</summary>
internal sealed class PdfFontFace
{
    public required string Key { get; init; }
    public required string PdfName { get; init; }
    public required GlyphTypeface Glyph { get; init; }
    public required string FilePath { get; init; }
    public bool Italic { get; init; }
}

/// <summary>Resolves (family, bold, italic) to an embeddable .ttf face. Anything else (TTC collections, CFF .otf, restricted licences) returns null and the text is plotted as outlines.</summary>
internal static class PdfFontCatalog
{
    private static readonly Dictionary<string, PdfFontFace?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    public static PdfFontFace? Resolve(string family, bool bold, bool italic)
    {
        var key = $"{family}|{(bold ? "b" : "")}{(italic ? "i" : "")}";
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            PdfFontFace? face = null;
            try
            {
                var typeface = new Typeface(new FontFamily(family), italic ? FontStyles.Italic : FontStyles.Normal,
                    bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                if (typeface.TryGetGlyphTypeface(out var glyph) && glyph.FontUri is { IsFile: true } uri &&
                    uri.LocalPath.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) && File.Exists(uri.LocalPath) &&
                    glyph.EmbeddingRights != FontEmbeddingRight.RestrictedLicense)
                {
                    var name = new string(family.Where(char.IsAsciiLetterOrDigit).ToArray());
                    if (name.Length == 0) name = "Font";
                    face = new PdfFontFace { Key = key, PdfName = name + (bold ? "-Bold" : "") + (italic ? "-Italic" : ""), Glyph = glyph, FilePath = uri.LocalPath, Italic = italic };
                }
            }
            catch { face = null; }
            Cache[key] = face;
            return face;
        }
    }

    /// <summary>True when the font can be embedded and every character of the text has a glyph in it.</summary>
    public static bool CanEncode(CadGeometryBuilder.EditableText text)
    {
        var face = Resolve(text.FontFamily, text.Bold, text.Italic);
        if (face is null) return false;
        var map = face.Glyph.CharacterToGlyphMap;
        foreach (var ch in text.Text)
            if (char.IsSurrogate(ch) || !map.ContainsKey(ch)) return false;
        return true;
    }
}
