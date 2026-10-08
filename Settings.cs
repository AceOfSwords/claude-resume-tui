using System.Text.Json;

namespace ClaudeHistory;

static class Paths
{
    public static string DataDir { get; } = Directory.CreateDirectory(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ccr")).FullName;

    public static string DbFile => Path.Combine(DataDir, "index.db");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static string ProjectsDir { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "projects");
}

enum EnterMode { Resume, NewTab, Copy }

sealed class Settings
{
    public bool IncludeThinking { get; set; }
    public bool IncludeToolInput { get; set; }
    public bool IncludeToolOutput { get; set; }
    public bool IncludeSubagents { get; set; }
    public bool HideMissingFolders { get; set; }
    public EnterMode Enter { get; set; }
    public bool ForkSession { get; set; }
    public int TypingDelayMs { get; set; } = 150;

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<Kind> Kinds
    {
        get
        {
            var kinds = new List<Kind> { Kind.User, Kind.Assistant };
            if (IncludeThinking) kinds.Add(Kind.Thinking);
            if (IncludeToolInput) kinds.Add(Kind.ToolInput);
            if (IncludeToolOutput) kinds.Add(Kind.ToolOutput);
            return kinds;
        }
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Paths.SettingsFile))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}
