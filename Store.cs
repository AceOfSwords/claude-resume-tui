using Microsoft.Data.Sqlite;

namespace ClaudeHistory;

enum Kind { User = 0, Assistant = 1, Thinking = 2, ToolInput = 3, ToolOutput = 4 }

sealed class Session
{
    public string Id = "";
    public string ProjectDir = "";
    public string? Cwd;
    public string? AiTitle;
    public string? CustomTitle;
    public string? FirstPrompt;
    public string? LastPrompt;
    public long FirstTs;
    public long LastTs;
    public int Prompts;
    public int Msgs;
    public string? Branch;
    public string? Model;

    public bool Exists;

    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
    public string Title => CustomTitle ?? AiTitle ?? FirstPrompt ?? "(untitled)";
    public string Folder => Cwd ?? ProjectDir;

    public string Project
    {
        get
        {
            if (Cwd != null && string.Equals(Cwd.TrimEnd('\\', '/'), Home, StringComparison.OrdinalIgnoreCase)) return "~";
            if (Cwd != null) return Path.GetFileName(Cwd.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Cwd;
            var i = ProjectDir.LastIndexOf('-');
            return i >= 0 ? ProjectDir[(i + 1)..] : ProjectDir;
        }
    }
}

sealed record Snippet(Kind Kind, long Ts, string Text);

sealed class Store : IDisposable
{
    const int SchemaVersion = 1;

    public SqliteConnection Db { get; }

    public Store(bool rebuild)
    {
        if (rebuild)
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { Paths.DbFile, Paths.DbFile + "-wal", Paths.DbFile + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }

        Db = new SqliteConnection($"Data Source={Paths.DbFile}");
        Db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");

        if (Scalar("PRAGMA user_version") != SchemaVersion)
        {
            Exec("""
                DROP TABLE IF EXISTS entries_fts;
                DROP TABLE IF EXISTS entries;
                DROP TABLE IF EXISTS sessions;
                DROP TABLE IF EXISTS files;

                CREATE TABLE files(
                    id INTEGER PRIMARY KEY,
                    path TEXT UNIQUE NOT NULL,
                    session_id TEXT NOT NULL,
                    is_sub INTEGER NOT NULL,
                    size INTEGER NOT NULL,
                    mtime INTEGER NOT NULL,
                    offset INTEGER NOT NULL);

                CREATE TABLE sessions(
                    id TEXT PRIMARY KEY,
                    project_dir TEXT NOT NULL,
                    cwd TEXT,
                    ai_title TEXT,
                    custom_title TEXT,
                    first_prompt TEXT,
                    last_prompt TEXT,
                    first_ts INTEGER NOT NULL,
                    last_ts INTEGER NOT NULL,
                    prompts INTEGER NOT NULL,
                    msgs INTEGER NOT NULL,
                    branch TEXT,
                    model TEXT);

                CREATE TABLE entries(
                    id INTEGER PRIMARY KEY,
                    file_id INTEGER NOT NULL,
                    session_id TEXT NOT NULL,
                    kind INTEGER NOT NULL,
                    sub INTEGER NOT NULL,
                    ts INTEGER NOT NULL,
                    text TEXT NOT NULL);
                CREATE INDEX entries_file ON entries(file_id);

                CREATE VIRTUAL TABLE entries_fts USING fts5(
                    text, content='entries', content_rowid='id',
                    tokenize='unicode61 remove_diacritics 2', prefix='2 3');

                CREATE TRIGGER entries_ai AFTER INSERT ON entries BEGIN
                    INSERT INTO entries_fts(rowid, text) VALUES (new.id, new.text);
                END;
                CREATE TRIGGER entries_ad AFTER DELETE ON entries BEGIN
                    INSERT INTO entries_fts(entries_fts, rowid, text) VALUES ('delete', old.id, old.text);
                END;
                """);
            Exec($"PRAGMA user_version={SchemaVersion}");
        }
    }

    public void Exec(string sql)
    {
        using var cmd = Db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    long Scalar(string sql)
    {
        using var cmd = Db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public List<Session> LoadSessions()
    {
        var list = new List<Session>();
        using var cmd = Db.CreateCommand();
        cmd.CommandText = "SELECT id, project_dir, cwd, ai_title, custom_title, first_prompt, last_prompt, first_ts, last_ts, prompts, msgs, branch, model FROM sessions WHERE msgs > prompts";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var s = ReadSession(r);
            s.Exists = s.Cwd != null && Directory.Exists(s.Cwd);
            list.Add(s);
        }
        return list;
    }

    public Session? LoadSession(string id)
    {
        using var cmd = Db.CreateCommand();
        cmd.CommandText = "SELECT id, project_dir, cwd, ai_title, custom_title, first_prompt, last_prompt, first_ts, last_ts, prompts, msgs, branch, model FROM sessions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadSession(r) : null;
    }

    static Session ReadSession(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        ProjectDir = r.GetString(1),
        Cwd = r.IsDBNull(2) ? null : r.GetString(2),
        AiTitle = r.IsDBNull(3) ? null : r.GetString(3),
        CustomTitle = r.IsDBNull(4) ? null : r.GetString(4),
        FirstPrompt = r.IsDBNull(5) ? null : r.GetString(5),
        LastPrompt = r.IsDBNull(6) ? null : r.GetString(6),
        FirstTs = r.GetInt64(7),
        LastTs = r.GetInt64(8),
        Prompts = r.GetInt32(9),
        Msgs = r.GetInt32(10),
        Branch = r.IsDBNull(11) ? null : r.GetString(11),
        Model = r.IsDBNull(12) ? null : r.GetString(12),
    };

    public void SaveSession(Session s)
    {
        using var cmd = Db.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO sessions(id, project_dir, cwd, ai_title, custom_title, first_prompt, last_prompt, first_ts, last_ts, prompts, msgs, branch, model)
            VALUES ($id, $pd, $cwd, $ai, $ct, $fp, $lp, $fts, $lts, $p, $m, $b, $mo)
            """;
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$pd", s.ProjectDir);
        cmd.Parameters.AddWithValue("$cwd", (object?)s.Cwd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ai", (object?)s.AiTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ct", (object?)s.CustomTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fp", (object?)s.FirstPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$lp", (object?)s.LastPrompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fts", s.FirstTs);
        cmd.Parameters.AddWithValue("$lts", s.LastTs);
        cmd.Parameters.AddWithValue("$p", s.Prompts);
        cmd.Parameters.AddWithValue("$m", s.Msgs);
        cmd.Parameters.AddWithValue("$b", (object?)s.Branch ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mo", (object?)s.Model ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    static string KindList(IReadOnlyList<Kind> kinds) => string.Join(",", kinds.Select(k => (int)k));

    /// <summary>Number of matching entries per session.</summary>
    public Dictionary<string, int> ContentHits(string ftsQuery, IReadOnlyList<Kind> kinds, bool includeSub)
    {
        var hits = new Dictionary<string, int>();
        using var cmd = Db.CreateCommand();
        cmd.CommandText = $"""
            SELECT e.session_id, count(*) FROM entries_fts JOIN entries e ON e.id = entries_fts.rowid
            WHERE entries_fts MATCH $q AND e.kind IN ({KindList(kinds)}) {(includeSub ? "" : "AND e.sub = 0")}
            GROUP BY e.session_id
            """;
        cmd.Parameters.AddWithValue("$q", ftsQuery);
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read()) hits[r.GetString(0)] = r.GetInt32(1);
        }
        catch (SqliteException) { } // malformed query: treat as no content hits
        return hits;
    }

    public List<Snippet> Snippets(string ftsQuery, string sessionId, IReadOnlyList<Kind> kinds, bool includeSub, int limit)
    {
        var list = new List<Snippet>();
        using var cmd = Db.CreateCommand();
        cmd.CommandText = $"""
            SELECT e.kind, e.ts, snippet(entries_fts, 0, char(1), char(2), '…', 24)
            FROM entries_fts JOIN entries e ON e.id = entries_fts.rowid
            WHERE entries_fts MATCH $q AND e.session_id = $s AND e.kind IN ({KindList(kinds)}) {(includeSub ? "" : "AND e.sub = 0")}
            ORDER BY e.ts DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$q", ftsQuery);
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$n", limit);
        try
        {
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new Snippet((Kind)r.GetInt32(0), r.GetInt64(1), r.GetString(2)));
        }
        catch (SqliteException) { }
        return list;
    }

    public void Dispose() => Db.Dispose();
}
