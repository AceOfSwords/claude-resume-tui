using System.Globalization;
using System.Text;

namespace ClaudeHistory;

/// <summary>Terminal-cell-aware text helpers. \x01 / \x02 mark the start / end of a search highlight and take no space.</summary>
static class Text
{
    public const char HlOn = '\x01', HlOff = '\x02';

    public static int Width(Rune r)
    {
        int cp = r.Value;
        if (cp == HlOn || cp == HlOff) return 0;
        if (cp < 0x20) return 0;
        var cat = Rune.GetUnicodeCategory(r);
        if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        return cp >= 0x1100 && (cp <= 0x115f || cp == 0x2329 || cp == 0x232a
            || (cp >= 0x2e80 && cp <= 0xa4cf && cp != 0x303f) || (cp >= 0xac00 && cp <= 0xd7a3)
            || (cp >= 0xf900 && cp <= 0xfaff) || (cp >= 0xfe30 && cp <= 0xfe6f) || (cp >= 0xff00 && cp <= 0xff60)
            || (cp >= 0xffe0 && cp <= 0xffe6) || (cp >= 0x1f300 && cp <= 0x1faff) || (cp >= 0x20000 && cp <= 0x3fffd)) ? 2 : 1;
    }

    public static int Width(string s)
    {
        int w = 0;
        foreach (var r in s.EnumerateRunes()) w += Width(r);
        return w;
    }

    /// <summary>Single line: whitespace and control characters collapsed into single spaces.</summary>
    public static string Flatten(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (var c in s)
        {
            if (c == HlOn || c == HlOff) { sb.Append(c); continue; }
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (!space && sb.Length > 0) sb.Append(' ');
                space = true;
                continue;
            }
            sb.Append(c);
            space = false;
        }
        return sb.ToString().TrimEnd();
    }

    public static string Truncate(string s, int width)
    {
        if (width <= 0) return "";
        if (Width(s) <= width) return s;
        var sb = new StringBuilder();
        int w = 0;
        foreach (var r in s.EnumerateRunes())
        {
            int rw = Width(r);
            if (w + rw > width - 1) break;
            sb.Append(r.ToString());
            w += rw;
        }
        return sb.Append('…').ToString();
    }

    public static string PadLeft(string s, int width) => new string(' ', Math.Max(0, width - Width(s))) + s;

    public static string PadRight(string s, int width) => s + new string(' ', Math.Max(0, width - Width(s)));

    /// <summary>Word-wraps a flattened string into at most <paramref name="maxLines"/> lines; the last line gets an ellipsis if text remains.</summary>
    public static List<string> Wrap(string s, int width, int maxLines)
    {
        var lines = new List<string>();
        if (width <= 0 || maxLines <= 0) return lines;
        s = Flatten(s);
        var line = new StringBuilder();
        int w = 0;
        int i = 0;
        var words = s.Split(' ');
        for (; i < words.Length; i++)
        {
            var word = words[i];
            int ww = Width(word);
            if (w > 0 && w + 1 + ww > width)
            {
                lines.Add(line.ToString());
                if (lines.Count == maxLines) break;
                line.Clear();
                w = 0;
            }
            if (ww > width)
            {
                // A single word longer than the line: hard-break it.
                foreach (var r in word.EnumerateRunes())
                {
                    int rw = Width(r);
                    if (w + rw > width)
                    {
                        lines.Add(line.ToString());
                        if (lines.Count == maxLines) return Ellipsize(lines, width);
                        line.Clear();
                        w = 0;
                    }
                    line.Append(r.ToString());
                    w += rw;
                }
                continue;
            }
            if (w > 0) { line.Append(' '); w++; }
            line.Append(word);
            w += ww;
        }
        if (lines.Count < maxLines && line.Length > 0) lines.Add(line.ToString());
        else if (i < words.Length) Ellipsize(lines, width);
        return lines;
    }

    static List<string> Ellipsize(List<string> lines, int width)
    {
        if (lines.Count > 0) lines[^1] = Width(lines[^1]) + 1 <= width ? lines[^1] + "…" : Truncate(lines[^1], width);
        return lines;
    }

    public static string Age(long unixMs)
    {
        var d = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        if (d.TotalMinutes < 1) return "now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays}d";
        if (d.TotalDays < 365) return $"{(int)(d.TotalDays / 30)}mo";
        return $"{(int)(d.TotalDays / 365)}y";
    }

    public static string Date(long unixMs) =>
        unixMs == 0 ? "?" : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
}
