using System.Text;

namespace ClaudeHistory;

/// <summary>One terminal row, built left to right and clipped at a fixed number of cells.</summary>
sealed class Row(int max)
{
    readonly StringBuilder sb = new();
    public int Width { get; private set; }

    public Row Add(string s, string style = "")
    {
        if (style.Length > 0) sb.Append(style);
        bool styled = style.Length > 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (r.Value == Text.HlOn) { sb.Append(Ansi.Hl); styled = true; continue; }
            if (r.Value == Text.HlOff) { sb.Append(Ansi.Reset).Append(style); continue; }
            int w = Text.Width(r);
            if (Width + w > max) break;
            sb.Append(r.ToString());
            Width += w;
        }
        if (styled) sb.Append(Ansi.Reset);
        return this;
    }

    public Row Pad(int to)
    {
        to = Math.Min(to, max);
        if (to > Width) { sb.Append(' ', to - Width); Width = to; }
        return this;
    }

    public override string ToString() => sb.ToString();
}

static class Ansi
{
    public const string Reset = "\e[0m", Bold = "\e[1m", Dim = "\e[2m", Accent = "\e[36m", Hl = "\e[1;33m", Red = "\e[31m", Green = "\e[32m";
}

sealed class Tui(Search search, Settings settings, string initialQuery)
{
    string query = initialQuery;
    int cursor = initialQuery.Length;
    List<Hit> hits = [];

    // Layout of the last frame, for mapping mouse clicks and paging.
    int bodyHeight = 1, listWidth;
    const int SettingsHeaderLines = 3;
    bool helpOpen;
    bool queryDirty = true;
    long searchDue;

    int selected, top;

    bool settingsOpen;
    int settingsSel;

    string? toast;
    bool toastError;
    long toastUntil;
    long escUntil; // a second esc before this time quits

    readonly Shell shell = Native.DetectShell();
    (string Key, List<Snippet> List)? snippetCache;

    /// <summary>A settings line: a checkbox when <see cref="Toggle"/> is set, otherwise a value stepped with ←/→.</summary>
    sealed record Option(string Label, Func<string> Value, Action<int> Change, Func<bool>? Toggle = null);

    static readonly int[] Delays = [0, 50, 100, 150, 200, 300, 500, 800];

    Option[] Options =>
    [
        Check("also search thinking", () => settings.IncludeThinking, v => settings.IncludeThinking = v),
        Check("also search tool calls", () => settings.IncludeToolInput, v => settings.IncludeToolInput = v),
        Check("also search tool output", () => settings.IncludeToolOutput, v => settings.IncludeToolOutput = v),
        Check("also search subagents", () => settings.IncludeSubagents, v => settings.IncludeSubagents = v),
        Check("hide missing folders", () => settings.HideMissingFolders, v => settings.HideMissingFolders = v),
        Check("fork instead of resume", () => settings.ForkSession, v => settings.ForkSession = v),
        new("enter", () => settings.Enter switch { EnterMode.NewTab => "open new tab", EnterMode.Copy => "copy command", _ => "resume here" },
            dir => settings.Enter = (EnterMode)(((int)settings.Enter + dir + 3) % 3)),
        new("typing delay", () => settings.TypingDelayMs == 0 ? "off" : $"{settings.TypingDelayMs} ms",
            dir => settings.TypingDelayMs = Step(Delays, settings.TypingDelayMs, dir)),
    ];

    static Option Check(string label, Func<bool> get, Action<bool> set) => new(label, () => "", _ => set(!get()), get);

    static int Step(int[] values, int current, int dir)
    {
        int i = Array.FindIndex(values, v => v >= current);
        if (i < 0) i = values.Length - 1;
        return values[Math.Clamp(i + dir, 0, values.Length - 1)];
    }

    Stream output = Stream.Null;

    /// <summary>Runs the UI; returns the command to run in the parent shell, if any.</summary>
    public string? Run()
    {
        output = Console.OpenStandardOutput();
        Native.EnterRawInput();
        Write("\e[?1049h\e[?25l");
        string? result = null;
        try
        {
            int w = 0, h = 0;
            bool dirty = true;
            var action = Action.None;
            while (action == Action.None)
            {
                if (Console.WindowWidth != w || Console.WindowHeight != h) { w = Console.WindowWidth; h = Console.WindowHeight; dirty = true; }
                if (toast != null && Environment.TickCount64 > toastUntil) { toast = null; dirty = true; }
                if (escUntil != 0 && Environment.TickCount64 > escUntil) { escUntil = 0; dirty = true; }

                // Everything queued at once (e.g. a paste) is applied before the next search and redraw.
                foreach (var e in Native.ReadInput())
                {
                    action = e is KeyInput k ? Handle(k.Key) : Mouse((MouseInput)e);
                    dirty = true;
                    if (action != Action.None) break;
                }
                if (action == Action.Resume) result = Native.ResumeCommand(hits[selected].Session, shell, settings.ForkSession);
                if (action != Action.None) break;

                if (queryDirty && Environment.TickCount64 >= searchDue) { Refresh(); dirty = true; }

                if (dirty)
                {
                    Write(Render(w, h));
                    dirty = false;
                }
                else Thread.Sleep(15);
            }
        }
        finally
        {
            Write("\e[0m\e[?25h\e[?1049l");
            Native.RestoreInput();
        }
        return result;
    }

    /// <summary>A single frame as plain text, for checking the layout without an interactive terminal.</summary>
    public string Snapshot(int w, int h, int down, string? overlay)
    {
        Refresh();
        Move(down);
        settingsOpen = overlay == "settings";
        helpOpen = overlay == "help";
        var frame = System.Text.RegularExpressions.Regex.Replace(Render(w, h), @"\e\[\d+;1H", "\n");
        return System.Text.RegularExpressions.Regex.Replace(frame, @"\e\[[0-9;?]*[A-Za-z]", "").TrimStart('\n');
    }

    enum Action { None, Quit, Resume }

    Action Handle(ConsoleKeyInfo k)
    {
        bool ctrl = (k.Modifiers & ConsoleModifiers.Control) != 0;
        bool alt = (k.Modifiers & ConsoleModifiers.Alt) != 0;

        if (k.Key == ConsoleKey.C && ctrl && !alt) return Action.Quit;
        if (k.Key != ConsoleKey.Escape) escUntil = 0;

        if (helpOpen)
        {
            helpOpen = false; // any key closes it
            return Action.None;
        }

        if (settingsOpen)
        {
            switch (k.Key)
            {
                case ConsoleKey.Escape or ConsoleKey.F2:
                    settingsOpen = false;
                    settings.Save();
                    queryDirty = true;
                    searchDue = 0;
                    break;
                case ConsoleKey.UpArrow: settingsSel = Math.Max(0, settingsSel - 1); break;
                case ConsoleKey.DownArrow: settingsSel = Math.Min(Options.Length - 1, settingsSel + 1); break;
                case ConsoleKey.Spacebar or ConsoleKey.Enter or ConsoleKey.RightArrow: Options[settingsSel].Change(1); break;
                case ConsoleKey.LeftArrow: Options[settingsSel].Change(-1); break;
            }
            return Action.None;
        }

        int page = Math.Max(1, bodyHeight);
        switch (k.Key)
        {
            case ConsoleKey.Escape:
                if (query.Length > 0) { Edit("", 0); return Action.None; } // first esc clears the search
                if (Environment.TickCount64 < escUntil) return Action.Quit;
                escUntil = Environment.TickCount64 + 2000;
                return Action.None;
            case ConsoleKey.F1: helpOpen = true; return Action.None;
            case ConsoleKey.F2: settingsOpen = true; return Action.None;

            // list
            case ConsoleKey.UpArrow: Move(-1); return Action.None;
            case ConsoleKey.DownArrow: Move(1); return Action.None;
            case ConsoleKey.P when ctrl: Move(-1); return Action.None;
            case ConsoleKey.N when ctrl: Move(1); return Action.None;
            case ConsoleKey.PageUp: Move(-page); return Action.None;
            case ConsoleKey.PageDown: Move(page); return Action.None;
            case ConsoleKey.Home when ctrl: Move(int.MinValue / 2); return Action.None;
            case ConsoleKey.End when ctrl: Move(int.MaxValue / 2); return Action.None;
            case ConsoleKey.Enter: return Activate();
            case ConsoleKey.Y when ctrl: Copy(); return Action.None;

            // search line
            case ConsoleKey.LeftArrow: cursor = ctrl ? WordLeft(cursor) : Math.Max(0, cursor - 1); return Action.None;
            case ConsoleKey.RightArrow: cursor = ctrl ? WordRight(cursor) : Math.Min(query.Length, cursor + 1); return Action.None;
            case ConsoleKey.Home: cursor = 0; return Action.None;
            case ConsoleKey.End: cursor = query.Length; return Action.None;
            case ConsoleKey.Backspace:
                int from = ctrl ? WordLeft(cursor) : Math.Max(0, cursor - 1);
                if (from < cursor) Edit(query[..from] + query[cursor..], from);
                return Action.None;
            case ConsoleKey.Delete:
                int to = ctrl ? WordRight(cursor) : Math.Min(query.Length, cursor + 1);
                if (to > cursor) Edit(query[..cursor] + query[to..], cursor);
                return Action.None;
            case ConsoleKey.W when ctrl:
                int start = WordLeft(cursor);
                Edit(query[..start] + query[cursor..], start);
                return Action.None;
            case ConsoleKey.U when ctrl:
                Edit("", 0);
                return Action.None;
        }

        if (k.KeyChar == '\x7f') // ctrl+backspace in some terminals
        {
            int start = WordLeft(cursor);
            Edit(query[..start] + query[cursor..], start);
            return Action.None;
        }
        if (k.KeyChar == '?' && query.Length == 0) { helpOpen = true; return Action.None; }
        if (k.KeyChar >= ' ' && (!ctrl || alt))
            Edit(query[..cursor] + k.KeyChar + query[cursor..], cursor + 1);
        return Action.None;
    }

    /// <summary>What Enter (or a double-click) does, per the "enter" setting.</summary>
    Action Activate()
    {
        if (queryDirty) Refresh();
        if (hits.Count == 0) return Action.None;
        if (settings.Enter == EnterMode.Copy) { Copy(); return Action.None; }
        if (!hits[selected].Session.Exists) { Toast("project folder no longer exists — ^Y to copy anyway", error: true); return Action.None; }
        if (settings.Enter == EnterMode.NewTab)
        {
            if (Native.OpenInNewTab(hits[selected].Session, shell, settings.ForkSession)) return Action.Quit;
            Toast("couldn't open a Windows Terminal tab", error: true);
            return Action.None;
        }
        return Action.Resume;
    }

    Action Mouse(MouseInput m)
    {
        escUntil = 0;
        if (helpOpen)
        {
            if (m.Action != MouseAction.Wheel) helpOpen = false;
            return Action.None;
        }

        int bodyY = m.Y - 2; // below the search line and the separator
        if (settingsOpen)
        {
            int i = bodyY - SettingsHeaderLines;
            if (m.Action != MouseAction.Wheel && i >= 0 && i < Options.Length)
            {
                settingsSel = i;
                Options[i].Change(1);
            }
            return Action.None;
        }

        if (m.Action == MouseAction.Wheel) { Move(m.Wheel > 0 ? -3 : 3); return Action.None; }
        if (bodyY < 0 || bodyY >= bodyHeight || m.X >= listWidth) return Action.None;

        if (queryDirty) Refresh();
        int idx = top + bodyY;
        if (idx >= hits.Count) return Action.None;
        selected = idx;
        return m.Action == MouseAction.DoubleClick ? Activate() : Action.None;
    }

    void Edit(string newQuery, int newCursor)
    {
        query = newQuery;
        cursor = Math.Clamp(newCursor, 0, query.Length);
        queryDirty = true;
        int len = query.Trim().Length;
        // One or two characters match almost everything, so those wait twice as long.
        searchDue = len == 0 ? 0 : Environment.TickCount64 + settings.TypingDelayMs * (len <= 2 ? 2 : 1);
    }

    int WordLeft(int i)
    {
        while (i > 0 && query[i - 1] == ' ') i--;
        while (i > 0 && query[i - 1] != ' ') i--;
        return i;
    }

    int WordRight(int i)
    {
        while (i < query.Length && query[i] == ' ') i++;
        while (i < query.Length && query[i] != ' ') i++;
        return i;
    }

    void Move(int delta)
    {
        if (queryDirty) Refresh();
        if (hits.Count == 0) return;
        selected = Math.Clamp(selected + delta, 0, hits.Count - 1);
    }

    void Copy()
    {
        if (queryDirty) Refresh();
        if (hits.Count == 0) return;
        var cmd = Native.ResumeCommand(hits[selected].Session, shell, settings.ForkSession);
        if (Native.CopyToClipboard(cmd)) Toast("copied ✓");
        else Toast("clipboard unavailable", error: true);
    }

    void Toast(string text, bool error = false)
    {
        toast = text;
        toastError = error;
        toastUntil = Environment.TickCount64 + 2500;
    }

    void Refresh()
    {
        hits = search.Run(query);
        selected = 0;
        top = 0;
        queryDirty = false;
        snippetCache = null;
    }

    void Write(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        output.Write(bytes);
        output.Flush();
    }

    string Render(int w, int h)
    {
        var sb = new StringBuilder("\e[?25l");
        if (w < 20 || h < 6)
        {
            sb.Append("\e[H\e[2J").Append("terminal too small");
            return sb.ToString();
        }

        int bodyH = h - 4;
        bool preview = w >= 70;
        int listW = !preview ? w : w >= 120 ? w * 45 / 100 : w / 2;
        int prevW = preview ? w - listW - 3 : 0;
        bodyHeight = bodyH;
        listWidth = listW;
        bool overlay = settingsOpen || helpOpen;

        if (selected < top) top = selected;
        if (selected >= top + bodyH) top = selected - bodyH + 1;

        // Header: query and match count.
        var count = $"{(queryDirty ? "…" : hits.Count)}/{search.Total} ";
        var header = new Row(w).Add(" > ", Ansi.Accent).Add(query);
        int cursorCol = 4 + Text.Width(query[..cursor]);
        header.Pad(w - Text.Width(count)).Add(count, Ansi.Dim);
        Line(sb, 1, header);

        Line(sb, 2, new Row(w).Add(Separator(listW, w, preview && !overlay, '┬'), Ansi.Dim));

        var right = preview && !overlay ? PreviewLines(prevW, bodyH) : [];
        var left = settingsOpen ? SettingsLines(w) : helpOpen ? HelpLines(w) : null;

        for (int i = 0; i < bodyH; i++)
        {
            if (left != null)
            {
                Line(sb, 3 + i, i < left.Count ? left[i] : new Row(w));
                continue;
            }

            var row = new Row(w);
            int idx = top + i;
            if (idx < hits.Count) ListRow(row, hits[idx].Session, idx == selected, listW);
            else if (i == 0 && hits.Count == 0) row.Add("  no matches", Ansi.Dim);

            if (preview)
            {
                row.Pad(listW).Add(" │ ", Ansi.Dim);
                if (i < right.Count) row.Add(right[i].ToString());
            }
            Line(sb, 3 + i, row);
        }

        if (escUntil != 0)
            Line(sb, h - 1, new Row(w).Add(Text.PadRight(" press esc again to exit", w), "\e[7m"));
        else
            Line(sb, h - 1, new Row(w).Add(Separator(listW, w, preview && !overlay, '┴'), Ansi.Dim));
        Line(sb, h, BottomBar(w));

        // The real cursor sits in the search line.
        sb.Append($"\e[1;{Math.Min(cursorCol, w)}H");
        if (!overlay) sb.Append("\e[?25h");
        return sb.ToString();
    }

    static string Separator(int listW, int w, bool preview, char joint) =>
        preview ? new string('─', listW + 1) + joint + new string('─', w - listW - 2) : new string('─', w);

    static void Line(StringBuilder sb, int y, Row row) => sb.Append($"\e[{y};1H").Append(row).Append("\e[0m\e[K");

    static void ListRow(Row row, Session s, bool sel, int width)
    {
        var project = Text.Truncate((s.Exists ? "" : "✗ ") + s.Project, Math.Min(16, width / 3));
        var age = Text.PadLeft(Text.Age(s.LastTs), 3);
        var meta = $"{project} {age} ";
        int titleW = width - 2 - Text.Width(meta) - 2;

        row.Add(sel ? "▌" : " ", Ansi.Accent).Add(" ");
        row.Add(Text.Truncate(Text.Flatten(s.Title), titleW), sel ? Ansi.Bold : s.Exists ? "" : Ansi.Dim);
        row.Pad(width - Text.Width(meta)).Add(meta, sel ? "" : Ansi.Dim);
    }

    List<Row> PreviewLines(int w, int h)
    {
        var lines = new List<Row>();
        if (hits.Count == 0) return lines;
        var hit = hits[selected];
        var s = hit.Session;

        var head = new Row(w).Add(s.Folder, Ansi.Bold);
        if (!s.Exists) head.Add("  ✗ missing", Ansi.Red);
        lines.Add(head);

        var info = string.Join(" · ", new[] { s.Branch, s.Model }.Where(x => !string.IsNullOrEmpty(x)));
        if (info.Length > 0) lines.Add(new Row(w).Add(info, Ansi.Dim));
        lines.Add(new Row(w).Add("started  ", Ansi.Dim).Add(Text.Date(s.FirstTs)));
        lines.Add(new Row(w).Add("last     ", Ansi.Dim).Add(Text.Date(s.LastTs), Ansi.Bold));
        lines.Add(new Row(w).Add($"{s.Prompts} prompts · {s.Msgs} messages", Ansi.Dim));
        lines.Add(new Row(w).Add(s.Id, Ansi.Dim));
        lines.Add(new Row(w));

        var snippets = Snippets(s);
        if (snippets.Count > 0)
        {
            lines.Add(new Row(w).Add(hit.Matches == 1 ? "1 match" : $"{hit.Matches} matches", Ansi.Accent));
            foreach (var sn in snippets)
            {
                if (lines.Count >= h) break;
                var (mark, label) = sn.Kind switch
                {
                    Kind.User => ("»", "you"),
                    Kind.Assistant => ("●", "claude"),
                    Kind.Thinking => ("✱", "thinking"),
                    Kind.ToolInput => ("$", "tool"),
                    _ => ("←", "output"),
                };
                lines.Add(new Row(w).Add(mark + " ", Ansi.Accent).Add($"{label} · {Text.Date(sn.Ts)}", Ansi.Dim));
                AddWrapped(lines, sn.Text, w, 3);
            }
        }
        else
        {
            lines.Add(new Row(w).Add("first", Ansi.Accent));
            AddWrapped(lines, s.FirstPrompt ?? "", w, Math.Max(2, (h - lines.Count) / 2 - 2));
            if (s.Prompts > 1 && s.LastPrompt != null)
            {
                lines.Add(new Row(w));
                lines.Add(new Row(w).Add("last", Ansi.Accent));
                AddWrapped(lines, s.LastPrompt, w, Math.Max(2, h - lines.Count));
            }
        }
        return lines;
    }

    List<Snippet> Snippets(Session s)
    {
        var key = s.Id + "\n" + query;
        if (snippetCache is { } c && c.Key == key) return c.List;
        var list = search.Snippets(s, query, 12);
        snippetCache = (key, list);
        return list;
    }

    /// <summary>Wrapped, indented text; a highlight that spans a line break is carried onto the next line.</summary>
    static void AddWrapped(List<Row> lines, string text, int w, int maxLines)
    {
        bool open = false;
        foreach (var l in Text.Wrap(text, w - 2, maxLines))
        {
            lines.Add(new Row(w).Add("  " + (open ? Text.HlOn.ToString() : "") + l));
            foreach (var c in l)
            {
                if (c == Text.HlOn) open = true;
                else if (c == Text.HlOff) open = false;
            }
        }
    }

    static readonly (string Key, string What)[] HelpKeys =
    [
        ("type", "search as you type"),
        ("↑↓  PgUp PgDn", "move through sessions · wheel scrolls"),
        ("^Home ^End", "first / last session"),
        ("enter", "resume (or new tab / copy, see F2) · double-click too"),
        ("^Y", "copy the resume command"),
        ("←→  Home End", "move in the search line · ^← ^→ by word"),
        ("⌫  Del  ^W  ^U", "delete char · ^⌫ ^Del by word · ^W word · ^U all"),
        ("esc", "clear the search, then twice to exit · ^C exits now"),
        ("F2", "settings"),
        ("F1  ?", "this help (? when the search is empty)"),
    ];

    static readonly (string Syntax, string What)[] HelpSearch =
    [
        ("words", "all must match a project, title or message (prefixes count)"),
        ("\"exact phrase\"", "words next to each other"),
        ("-word", "leave out sessions mentioning it"),
        ("p:name", "only projects whose folder contains name"),
        ("after:7d", "active in the last 7 days (d w mo y, or 2026-09-01)"),
        ("before:2026-09", "started before a date"),
    ];

    static List<Row> HelpLines(int w)
    {
        var lines = new List<Row> { new Row(w).Add(" keys", Ansi.Bold) };
        foreach (var (key, what) in HelpKeys)
            lines.Add(new Row(w).Add("   ").Add(Text.PadRight(key, 18), Ansi.Accent).Add(what, Ansi.Dim));
        lines.Add(new Row(w));
        lines.Add(new Row(w).Add(" search", Ansi.Bold));
        foreach (var (syntax, what) in HelpSearch)
            lines.Add(new Row(w).Add("   ").Add(Text.PadRight(syntax, 18), Ansi.Accent).Add(what, Ansi.Dim));
        return lines;
    }

    List<Row> SettingsLines(int w)
    {
        var lines = new List<Row>
        {
            new Row(w).Add(" settings", Ansi.Bold),
            new Row(w).Add(" titles and chat text are always searched", Ansi.Dim),
            new Row(w),
        };
        var options = Options;
        for (int i = 0; i < options.Length; i++)
        {
            bool sel = i == settingsSel;
            var o = options[i];
            var row = new Row(w).Add(sel ? "▌ " : "  ", Ansi.Accent);
            if (o.Toggle is { } on)
                row.Add(on() ? "[x] " : "[ ] ", on() ? Ansi.Accent : Ansi.Dim).Add(o.Label, sel ? Ansi.Bold : "");
            else
                row.Add("    ").Add(o.Label, sel ? Ansi.Bold : "").Add("  ‹ ", Ansi.Dim).Add(o.Value(), Ansi.Accent).Add(" ›", Ansi.Dim);
            lines.Add(row);
        }
        lines.Add(new Row(w));
        lines.Add(new Row(w).Add(" space toggle · ←→ change value · esc close", Ansi.Dim));
        return lines;
    }

    Row BottomBar(int w)
    {
        var row = new Row(w).Add(" ");
        (string Key, string Label)[] keys = settingsOpen
            ? [("↑↓", "move"), ("space", "toggle"), ("←→", "change"), ("esc", "close")]
            : helpOpen ? [("any key", "close")]
            : [("↑↓", "move"), ("enter", settings.Enter switch { EnterMode.Copy => "copy", EnterMode.NewTab => "new tab", _ => "resume" }), ("^Y", "copy"), ("F2", "settings"), ("F1", "help"), ("esc", query.Length > 0 ? "clear" : "quit")];
        foreach (var (key, label) in keys) row.Add(key, Ansi.Bold).Add(" " + label + "   ", Ansi.Dim);

        if (toast != null)
        {
            var t = toast + " ";
            row.Pad(w - Text.Width(t)).Add(t, toastError ? Ansi.Red : Ansi.Green);
        }
        return row;
    }
}
