using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaudeHistory;

sealed record Hit(Session Session, int Matches);

/// <summary>
/// A parsed search line: plain words (prefix matches), "exact phrases", -excluded words,
/// p:project filters and after:/before: date filters (7d, 2w, 3mo, 1y or 2026-09-01).
/// </summary>
sealed partial class Query
{
    public List<string> Words { get; } = [];
    public List<string> Phrases { get; } = [];
    public List<string> Excluded { get; } = [];
    public List<string> Projects { get; } = [];
    public long? After { get; private set; }
    public long? Before { get; private set; }

    public IEnumerable<string> Terms => Words.Concat(Phrases);
    public bool HasTerms => Words.Count + Phrases.Count > 0;

    public static Query Parse(string text)
    {
        var q = new Query();
        text = text.ToLowerInvariant();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == ' ') { i++; continue; }

            bool exclude = text[i] == '-' && i + 1 < text.Length && text[i + 1] != ' ';
            if (exclude) i++;

            string token;
            bool quoted = text[i] == '"';
            if (quoted)
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0) end = text.Length; // still typing the phrase
                token = text[(i + 1)..end].Trim();
                i = end + 1;
            }
            else
            {
                int end = text.IndexOf(' ', i);
                if (end < 0) end = text.Length;
                token = text[i..end];
                i = end;
            }
            if (token.Length == 0) continue;

            if (exclude) { q.Excluded.Add(token); continue; }
            if (!quoted)
            {
                if (Value(token, "p:", "project:") is { } project) { if (project.Length > 0) q.Projects.Add(project); continue; }
                if (Value(token, "after:") is { } after) { q.After = Date(after) ?? q.After; continue; }
                if (Value(token, "before:") is { } before) { q.Before = Date(before) ?? q.Before; continue; }
            }
            (quoted ? q.Phrases : q.Words).Add(token);
        }
        return q;
    }

    static string? Value(string token, params string[] prefixes)
    {
        foreach (var p in prefixes)
            if (token.StartsWith(p, StringComparison.Ordinal)) return token[p.Length..];
        return null;
    }

    [GeneratedRegex(@"^(\d+)(d|w|m|mo|y)$")]
    private static partial Regex RelativeDate();

    /// <summary>"7d", "2w", "3mo", "1y" back from now, or an absolute "2026-09-01" / "2026-09" (local time).</summary>
    static long? Date(string v)
    {
        var m = RelativeDate().Match(v);
        if (m.Success)
        {
            int n = int.Parse(m.Groups[1].Value);
            var now = DateTimeOffset.Now;
            var at = m.Groups[2].Value switch
            {
                "d" => now.AddDays(-n),
                "w" => now.AddDays(-7 * n),
                "y" => now.AddYears(-n),
                _ => now.AddMonths(-n),
            };
            return at.ToUnixTimeMilliseconds();
        }
        if (DateTime.TryParseExact(v, ["yyyy-MM-dd", "yyyy-MM"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d))
            return new DateTimeOffset(d).ToUnixTimeMilliseconds();
        return null;
    }

    /// <summary>FTS5 query for the content search: words as prefixes (2+ characters; single letters match nearly everything), phrases exact.</summary>
    public string? Fts()
    {
        var parts = Words.Where(w => w.Length >= 2).Select(w => Quote(w) + "*")
            .Concat(Phrases.Select(Quote))
            .ToList();
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    public static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}

/// <summary>One query box for everything: project matches first, then titles, then message content; newest first within each.</summary>
sealed class Search(Store store, List<Session> sessions, Settings settings)
{
    public int Total => Visible().Count();

    IEnumerable<Session> Visible() => settings.HideMissingFolders ? sessions.Where(s => s.Exists) : sessions;

    static string ProjectText(Session s) => (s.Project + " " + s.Folder).ToLowerInvariant();

    public List<Hit> Run(string text)
    {
        var q = Query.Parse(text);

        var candidates = Visible().Where(s =>
            q.Projects.All(ProjectText(s).Contains)
            && (q.After is not long after || s.LastTs >= after)
            && (q.Before is not long before || s.FirstTs < before));

        foreach (var term in q.Excluded)
        {
            var inContent = store.ContentHits(Query.Quote(term) + "*", settings.Kinds, settings.IncludeSubagents);
            candidates = candidates.Where(s => !inContent.ContainsKey(s.Id) && !s.Title.ToLowerInvariant().Contains(term) && !ProjectText(s).Contains(term));
        }

        if (!q.HasTerms)
            return candidates.OrderByDescending(s => s.LastTs).Select(s => new Hit(s, 0)).ToList();

        var fts = q.Fts();
        var content = fts == null ? [] : store.ContentHits(fts, settings.Kinds, settings.IncludeSubagents);
        var terms = q.Terms.ToList();

        var ranked = new List<(Hit Hit, int Tier)>();
        foreach (var s in candidates)
        {
            content.TryGetValue(s.Id, out var matches);
            var project = ProjectText(s);
            var title = s.Title.ToLowerInvariant();

            int tier;
            if (terms.All(project.Contains)) tier = 0;
            else if (terms.All(t => title.Contains(t) || project.Contains(t)) && terms.Any(title.Contains)) tier = 1;
            else if (matches > 0) tier = 2;
            else continue;

            ranked.Add((new Hit(s, matches), tier));
        }

        return ranked.OrderBy(r => r.Tier).ThenByDescending(r => r.Hit.Session.LastTs).Select(r => r.Hit).ToList();
    }

    public List<Snippet> Snippets(Session s, string text, int limit)
    {
        var fts = Query.Parse(text).Fts();
        return fts == null ? [] : store.Snippets(fts, s.Id, settings.Kinds, settings.IncludeSubagents, limit);
    }
}
