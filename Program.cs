using System.Diagnostics;
using System.Text;
using ClaudeHistory;

Console.OutputEncoding = new UTF8Encoding(false);

var queryParts = new List<string>();
bool print = false, reindex = false;
string? frame = null; // hidden: --frame WxH[:down][:settings|help] prints one rendered frame
int limit = 25;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-p" or "--print": print = true; break;
        case "--reindex": reindex = true; break;
        case "--frame" when i + 1 < args.Length: frame = args[++i]; break;
        case "-n" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n): limit = n; i++; break;
        case "-h" or "--help" or "/?":
            Console.WriteLine("""
                ccr — browse and resume Claude Code conversations

                  ccr                   open the browser, newest first
                  ccr <text>            open it with a search already typed
                  ccr -p <text>         print matches instead (automatic when output is piped)
                  ccr -n <count>        how many matches to print (default 25)
                  ccr --reindex         rebuild the index from scratch

                search: words · "exact phrase" · -exclude · p:project · after:7d · before:2026-09-01
                keys: ↑↓ move · enter resume · ^Y copy resume command · F2 settings · F1 help · esc clear/quit
                index: %LOCALAPPDATA%\ccr
                """);
            return 0;
        default: queryParts.Add(args[i]); break;
    }
}

var query = string.Join(' ', queryParts);
bool interactive = !print && !Console.IsOutputRedirected && !Console.IsInputRedirected;

if (interactive) Native.EnableVirtualTerminal();

using var store = new Store(reindex);
var jobs = Indexer.Plan(store);
if (jobs.Count > 0) Indexer.Run(store, jobs, interactive ? ProgressBar() : null);
if (interactive && jobs.Count > 0) Console.Write("\r\e[2K");

var settings = Settings.Load();
var search = new Search(store, store.LoadSessions(), settings);

if (frame != null)
{
    var parts = frame.Split(':');
    var size = parts[0].Split('x');
    Console.WriteLine(new Tui(search, settings, query).Snapshot(int.Parse(size[0]), int.Parse(size[1]),
        parts.Length > 1 ? int.Parse(parts[1]) : 0, parts.Length > 2 ? parts[2] : null));
    return 0;
}

if (!interactive)
{
    var hits = search.Run(query).Take(limit).ToList();
    int projectW = Math.Min(20, hits.Select(h => Text.Width(h.Session.Project)).DefaultIfEmpty(0).Max());
    foreach (var h in hits)
    {
        var s = h.Session;
        var project = Text.Truncate(s.Project, projectW);
        project += new string(' ', projectW - Text.Width(project));
        var title = Text.Truncate(Text.Flatten(s.Title), 70);
        title += new string(' ', Math.Max(0, 70 - Text.Width(title)));
        Console.WriteLine($"{Text.PadLeft(Text.Age(s.LastTs), 4)}  {project}  {title}  {s.Id}");
    }
    return hits.Count > 0 ? 0 : 1;
}

var command = new Tui(search, settings, query).Run();
if (command != null && !Native.InjectInput(command))
{
    Native.CopyToClipboard(command);
    Console.WriteLine(command);
    Console.WriteLine("(couldn't type it into the shell — copied to clipboard instead)");
}
return 0;

static Action<long, long> ProgressBar()
{
    var clock = Stopwatch.StartNew();
    long lastDraw = -1000;
    return (done, total) =>
    {
        if (clock.ElapsedMilliseconds - lastDraw < 50 && done < total) return;
        lastDraw = clock.ElapsedMilliseconds;
        const int width = 30;
        double f = total == 0 ? 1 : Math.Clamp((double)done / total, 0, 1);
        int full = (int)(f * width);
        Console.Write($"\r  \e[2mindexing\e[0m \e[36m{new string('█', full)}\e[2m{new string('░', width - full)}\e[0m {f * 100,3:0}%");
    };
}
