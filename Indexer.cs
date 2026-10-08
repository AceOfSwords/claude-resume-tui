using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClaudeHistory;

/// <summary>Keeps the SQLite index in sync with the JSONL transcripts, re-reading only what changed.</summary>
static class Indexer
{
    const int MaxPrompt = 600;
    const int MaxToolInput = 1000;
    const int MaxToolOutput = 2000;

    public sealed record Job(string Path, string ProjectDir, string SessionId, bool IsSub, long Size, long Mtime, long Offset, long? FileId);

    sealed record Known(long Id, long Size, long Mtime, long Offset);

    public static List<Job> Plan(Store store)
    {
        var known = new Dictionary<string, Known>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = store.Db.CreateCommand())
        {
            cmd.CommandText = "SELECT id, path, size, mtime, offset FROM files";
            using var r = cmd.ExecuteReader();
            while (r.Read()) known[r.GetString(1)] = new(r.GetInt64(0), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
        }

        var jobs = new List<Job>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(Paths.ProjectsDir))
        {
            foreach (var projectDir in Directory.EnumerateDirectories(Paths.ProjectsDir))
            {
                var projectName = Path.GetFileName(projectDir);
                foreach (var file in Directory.EnumerateFiles(projectDir, "*.jsonl"))
                    Consider(file, projectName, Path.GetFileNameWithoutExtension(file), false);

                foreach (var sessionDir in Directory.EnumerateDirectories(projectDir))
                {
                    var subDir = Path.Combine(sessionDir, "subagents");
                    if (!Directory.Exists(subDir)) continue;
                    foreach (var file in Directory.EnumerateFiles(subDir, "*.jsonl"))
                        Consider(file, projectName, Path.GetFileName(sessionDir), true);
                }
            }
        }

        // Transcripts that disappeared from disk.
        foreach (var (path, k) in known)
        {
            if (seen.Contains(path)) continue;
            using var tx = store.Db.BeginTransaction();
            Exec(store, "DELETE FROM entries WHERE file_id = $id", ("$id", k.Id));
            Exec(store, "DELETE FROM sessions WHERE id = (SELECT session_id FROM files WHERE id = $id AND is_sub = 0)", ("$id", k.Id));
            Exec(store, "DELETE FROM files WHERE id = $id", ("$id", k.Id));
            tx.Commit();
        }

        return jobs;

        void Consider(string file, string projectName, string sessionId, bool isSub)
        {
            seen.Add(file);
            var info = new FileInfo(file);
            long size = info.Length, mtime = info.LastWriteTimeUtc.Ticks;

            if (known.TryGetValue(file, out var k))
            {
                if (k.Size == size && k.Mtime == mtime) return;
                // Transcripts are append-only: continue from where we stopped unless the file shrank or was rewritten.
                var offset = size > k.Size ? k.Offset : 0;
                jobs.Add(new(file, projectName, sessionId, isSub, size, mtime, offset, k.Id));
            }
            else
            {
                jobs.Add(new(file, projectName, sessionId, isSub, size, mtime, 0, null));
            }
        }
    }

    public static void Run(Store store, List<Job> jobs, Action<long, long>? progress)
    {
        long total = jobs.Sum(j => j.Size - j.Offset), done = 0;
        progress?.Invoke(0, total);

        foreach (var job in jobs)
        {
            using var tx = store.Db.BeginTransaction();

            long fileId;
            if (job.FileId is long id)
            {
                fileId = id;
                if (job.Offset == 0)
                {
                    Exec(store, "DELETE FROM entries WHERE file_id = $id", ("$id", fileId));
                    if (!job.IsSub) Exec(store, "DELETE FROM sessions WHERE id = $s", ("$s", job.SessionId));
                }
            }
            else
            {
                using var cmd = store.Db.CreateCommand();
                cmd.CommandText = "INSERT INTO files(path, session_id, is_sub, size, mtime, offset) VALUES ($p, $s, $sub, 0, 0, 0) RETURNING id";
                cmd.Parameters.AddWithValue("$p", job.Path);
                cmd.Parameters.AddWithValue("$s", job.SessionId);
                cmd.Parameters.AddWithValue("$sub", job.IsSub ? 1 : 0);
                fileId = Convert.ToInt64(cmd.ExecuteScalar());
            }

            var session = job.IsSub ? null
                : (job.Offset > 0 ? store.LoadSession(job.SessionId) : null) ?? new Session { Id = job.SessionId, ProjectDir = job.ProjectDir };

            var parser = new Parser(store, fileId, job, session);
            long end = job.Offset, lastReported = 0;
            try
            {
                foreach (var (line, lineEnd) in Lines(job.Path, job.Offset))
                {
                    parser.Line(line);
                    end = lineEnd;
                    if (progress != null && end - job.Offset - lastReported > 1 << 20)
                    {
                        lastReported = end - job.Offset;
                        progress(done + lastReported, total);
                    }
                }
            }
            catch (IOException) { } // locked or vanished mid-read: keep what we have, retry next run

            if (session != null) store.SaveSession(session);
            Exec(store, "UPDATE files SET size = $size, mtime = $mtime, offset = $off WHERE id = $id",
                ("$size", job.Size), ("$mtime", job.Mtime), ("$off", end), ("$id", fileId));
            tx.Commit();

            done += job.Size - job.Offset;
            progress?.Invoke(done, total);
        }
    }

    static void Exec(Store store, string sql, params (string, object)[] args)
    {
        using var cmd = store.Db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Complete lines from <paramref name="start"/>; a trailing line without a newline is still being written and is skipped.</summary>
    static IEnumerable<(ReadOnlyMemory<byte> Line, long End)> Lines(string path, long start)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        fs.Position = start;
        var buf = new byte[1 << 20];
        int len = 0;
        long bufStart = start;

        while (true)
        {
            int n = fs.Read(buf, len, buf.Length - len);
            if (n == 0) yield break;
            len += n;

            int pos = 0;
            while (true)
            {
                int nl = IndexOfNewline(buf, pos, len - pos);
                if (nl < 0) break;
                yield return (new ReadOnlyMemory<byte>(buf, pos, nl - pos), bufStart + nl + 1);
                pos = nl + 1;
            }

            Buffer.BlockCopy(buf, pos, buf, 0, len - pos);
            len -= pos;
            bufStart += pos;
            if (len == buf.Length) Array.Resize(ref buf, buf.Length * 2);
        }
    }

    static int IndexOfNewline(byte[] buf, int start, int count)
    {
        int i = buf.AsSpan(start, count).IndexOf((byte)'\n');
        return i < 0 ? -1 : start + i;
    }

    sealed class Parser(Store store, long fileId, Job job, Session? session)
    {
        readonly SqliteCommand insert = CreateInsert(store);

        static SqliteCommand CreateInsert(Store store)
        {
            var cmd = store.Db.CreateCommand();
            cmd.CommandText = "INSERT INTO entries(file_id, session_id, kind, sub, ts, text) VALUES ($f, $s, $k, $sub, $ts, $t)";
            foreach (var p in new[] { "$f", "$s", "$k", "$sub", "$ts", "$t" }) cmd.Parameters.Add(new SqliteParameter(p, null));
            return cmd;
        }

        public void Line(ReadOnlyMemory<byte> line)
        {
            if (line.Length < 2) return;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { return; }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;

                var type = Str(root, "type");
                long ts = Timestamp(root);

                if (session != null)
                {
                    if (session.Cwd == null && Str(root, "cwd") is { Length: > 0 } cwd) session.Cwd = cwd;
                    if (Str(root, "gitBranch") is { Length: > 0 } branch) session.Branch = branch;
                    if (ts > 0)
                    {
                        if (session.FirstTs == 0 || ts < session.FirstTs) session.FirstTs = ts;
                        if (ts > session.LastTs) session.LastTs = ts;
                    }
                }

                switch (type)
                {
                    case "ai-title" when session != null:
                        session.AiTitle = Clean(Str(root, "aiTitle"), 200) ?? session.AiTitle;
                        break;
                    case "custom-title" when session != null:
                        session.CustomTitle = Clean(Str(root, "customTitle"), 200) ?? session.CustomTitle;
                        break;
                    case "user":
                        User(root, ts);
                        break;
                    case "assistant":
                        Assistant(root, ts);
                        break;
                }
            }
        }

        void User(JsonElement root, long ts)
        {
            if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)) return;

            if (content.ValueKind == JsonValueKind.Array)
            {
                bool hadToolResult = false;
                foreach (var block in content.EnumerateArray())
                {
                    if (Str(block, "type") != "tool_result") continue;
                    hadToolResult = true;
                    if (block.TryGetProperty("content", out var rc) && Clean(BlockText(rc), MaxToolOutput) is { } output)
                        Add(Kind.ToolOutput, ts, output);
                }
                if (hadToolResult) return;
            }

            if (!IsHuman(root)) return;

            var text = Clean(BlockText(content), null);
            if (text == null) return;
            text = SlashCommand(text) ?? text;
            if (text.StartsWith('<') || text.StartsWith("[Request interrupted")) return;

            Add(Kind.User, ts, text);
            if (session != null)
            {
                session.Prompts++;
                session.Msgs++;
                var short_ = Clean(text, MaxPrompt);
                session.FirstPrompt ??= short_;
                session.LastPrompt = short_;
            }
        }

        static bool IsHuman(JsonElement root)
        {
            if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return false;
            if (root.TryGetProperty("isCompactSummary", out var cs) && cs.ValueKind == JsonValueKind.True) return false;
            if (root.TryGetProperty("origin", out var origin)) return Str(origin, "kind") == "human";
            return true;
        }

        /// <summary>"&lt;command-name&gt;/foo&lt;/command-name&gt;…&lt;command-args&gt;bar&lt;/command-args&gt;" → "/foo bar".</summary>
        static string? SlashCommand(string text)
        {
            var name = Between(text, "<command-name>", "</command-name>");
            if (name == null) return null;
            var args = Between(text, "<command-args>", "</command-args>");
            return string.IsNullOrWhiteSpace(args) ? name.Trim() : $"{name.Trim()} {args.Trim()}";
        }

        static string? Between(string s, string open, string close)
        {
            int a = s.IndexOf(open, StringComparison.Ordinal);
            if (a < 0) return null;
            a += open.Length;
            int b = s.IndexOf(close, a, StringComparison.Ordinal);
            return b < 0 ? null : s[a..b];
        }

        void Assistant(JsonElement root, long ts)
        {
            if (!root.TryGetProperty("message", out var msg)) return;
            if (session != null && Str(msg, "model") is { Length: > 0 } model && model != "<synthetic>") session.Model = model;
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;

            bool hadText = false;
            foreach (var block in content.EnumerateArray())
            {
                switch (Str(block, "type"))
                {
                    case "text":
                        if (Clean(Str(block, "text"), null) is { } text) { Add(Kind.Assistant, ts, text); hadText = true; }
                        break;
                    case "thinking":
                        if (Clean(Str(block, "thinking"), null) is { } thinking) Add(Kind.Thinking, ts, thinking);
                        break;
                    case "tool_use":
                        if (ToolInput(block) is { } input) Add(Kind.ToolInput, ts, input);
                        break;
                }
            }
            if (hadText && session != null) session.Msgs++;
        }

        static readonly string[] InterestingInputs = ["command", "file_path", "path", "pattern", "query", "url", "description", "prompt", "skill"];

        static string? ToolInput(JsonElement block)
        {
            var name = Str(block, "name") ?? "tool";
            if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return name;

            var sb = new StringBuilder(name);
            foreach (var key in InterestingInputs)
                if (Str(input, key) is { Length: > 0 } v) sb.Append(' ').Append(v);
            if (sb.Length == name.Length) sb.Append(' ').Append(input.GetRawText());
            return Clean(sb.ToString(), MaxToolInput);
        }

        void Add(Kind kind, long ts, string text)
        {
            insert.Parameters[0].Value = fileId;
            insert.Parameters[1].Value = job.SessionId;
            insert.Parameters[2].Value = (int)kind;
            insert.Parameters[3].Value = job.IsSub ? 1 : 0;
            insert.Parameters[4].Value = ts;
            insert.Parameters[5].Value = text;
            insert.ExecuteNonQuery();
        }

        static string BlockText(JsonElement content)
        {
            if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
            if (content.ValueKind != JsonValueKind.Array) return "";
            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (Str(block, "type") != "text") continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(Str(block, "text"));
            }
            return sb.ToString();
        }

        static string? Clean(string? s, int? max)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim();
            if (max is int m && s.Length > m) s = s[..m] + "…";
            return s;
        }

        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        static long Timestamp(JsonElement root) =>
            Str(root, "timestamp") is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
                ? dto.ToUnixTimeMilliseconds()
                : 0;
    }
}
