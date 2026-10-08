# claude-resume-tui

Unofficial terminal UI for finding and resuming Claude Code sessions: full-text search across all your projects, then one key to jump back in.

**1. Search for anything you remember** (a project, a title, something you or Claude said):

```powershell
PS C:\> ccr webhook retry
```

**2. Pick the session:**

```
 > webhook retry                                                      4/83
──────────────────────────────────────────┬────────────────────────────────────────
▌ Payment webhook retries     billing  2d │ D:\src\billing
  Stripe signature mismatch       api  5d │ main · claude-opus-5-5
  Queue backoff tuning         worker 12d │ started  3 Oct 2026 09:12
  Retry policy review             api 1mo │ last     6 Oct 2026 17:40
                                          │ 41 prompts · 128 messages
                                          │ 3f2a9c1e-5b7d-4e08-9a61-c4d2e8b07f13
                                          │
                                          │ 3 matches
                                          │ » you · 6 Oct 2026 16:58
                                          │   add exponential backoff to the webhook
                                          │   retry and cap it at 5 attempts
                                          │ ● claude · 6 Oct 2026 17:02
                                          │   …the webhook handler now retries with…
──────────────────────────────────────────┴────────────────────────────────────────
 ↑↓ move   enter resume   ^Y copy   F2 settings   F1 help   esc clear
```

**3. Press Enter.** `ccr` closes and types the resume command into your shell, so you're back in the conversation, in the right folder:

```powershell
PS C:\> cd 'D:\src\billing'; claude --resume 3f2a9c1e-5b7d-4e08-9a61-c4d2e8b07f13
```

### At a glance

```powershell
ccr                        # browse every session, newest first
ccr billing                # a project, a title, or anything said in a session
ccr "rate limit" -test     # an exact phrase, leaving out sessions that mention "test"
ccr p:api after:7d         # only the api project, active in the last 7 days
ccr -p webhook             # print matches as plain text instead of opening the browser
```

Inside Claude Code, `!ccr webhook` prints the matching sessions with their IDs right in the conversation.

`/resume` only shows sessions for the folder you're in, and you have to know roughly what you're looking for. `ccr` searches every conversation you've had in any project, shows you where and when it happened, and puts you back in that session in the right folder.

> Not affiliated with or endorsed by Anthropic. It only reads the transcripts Claude Code already keeps on your machine.

## Features

- **One search box.** Type a project name, a word from a session title, or something you said weeks ago. Project matches come first, then titles, then message text, newest first within each.
- **Instant after the first run.** Transcripts are indexed into a local SQLite full-text index. Later launches only read what changed, so they open in a fraction of a second.
- **Resume in place.** Enter types `cd '<folder>'; claude --resume <id>` into your shell and runs it. It can open a new Windows Terminal tab instead, or just copy the command.
- **Preview.** The panel shows the folder, branch, model, start time, exact time of the last message, counts and the session ID, plus matching snippets with your search words highlighted.
- **Search syntax.** Exact phrases, exclusions, project filters and date ranges.
- **Keyboard and mouse.** Click to select, double-click to resume, wheel to scroll.
- **Scriptable.** When its output is piped, `ccr` prints a plain list, so `!ccr webhook` works from inside Claude Code.

## Requirements

- Windows 10/11. Windows Terminal is recommended; mouse support and the new-tab mode rely on it.
- [.NET 10 runtime](https://dotnet.microsoft.com/download) (the SDK to build).
- Claude Code, which writes the transcripts `ccr` reads.

## Install

```powershell
git clone https://github.com/AceOfSwords/claude-resume-tui
cd claude-resume-tui
dotnet publish -c Release -o publish
copy publish\ccr.exe $HOME\.local\bin\   # or any folder on your PATH
```

The result is a single ~3 MB `ccr.exe`. Nothing depends on its name, so rename it if `ccr` clashes with something on your machine.

## Usage

```
ccr                    open the browser, newest first
ccr <text>             open it with a search already typed
ccr -p <text>          print matches instead (automatic when output is piped)
ccr -n <count>         how many matches to print (default 25)
ccr --reindex          rebuild the index from scratch
```

The first run indexes your whole history and shows a progress bar. On about 800 MB of transcripts that takes around 10 seconds.

### Keys

| Key | Action |
| --- | --- |
| type | search as you type |
| `↑` `↓` `PgUp` `PgDn` | move through sessions |
| `Ctrl+Home` `Ctrl+End` | first / last session |
| `Enter` / double-click | resume (or open a new tab, or copy, see settings) |
| `Ctrl+Y` | copy the resume command |
| `←` `→` `Home` `End` | move in the search line; `Ctrl+←` `Ctrl+→` by word |
| `Backspace` `Del` `Ctrl+W` `Ctrl+U` | delete a character, a word, or everything |
| `Esc` | clear the search; when it's empty, press twice to exit |
| `Ctrl+C` | exit immediately |
| `F2` | settings |
| `F1` or `?` | help |

While `ccr` is open, Windows Terminal passes mouse drags to it. Hold `Shift` to select text.

### Search syntax

| Syntax | Meaning |
| --- | --- |
| `webhook retry` | every word must match a project, title or message; prefixes count |
| `"exact phrase"` | words next to each other |
| `-word` | leave out sessions that mention it |
| `p:billing` | only projects whose folder contains `billing` |
| `after:7d` | active in the last 7 days (`d`, `w`, `mo`, `y`, or a date like `2026-09-01`) |
| `before:2026-09` | started before a date |

Filters and words can be combined in any order: `p:api after:2w "rate limit" -test`.

### Settings (`F2`)

| Setting | Default |
| --- | --- |
| also search thinking / tool calls / tool output / subagents | off |
| hide sessions whose folder no longer exists | off |
| fork instead of resume (`--fork-session`) | off |
| what Enter does: resume here, open new tab, copy command | resume here |
| typing delay before searching (1–2 character searches wait twice as long) | 150 ms |

Changing what's searched takes effect immediately; it never requires a re-index.

## How it works

Claude Code stores every session as a JSONL file under `~/.claude/projects/` (or `$CLAUDE_CONFIG_DIR/projects/`). `ccr` reads those files and keeps an index in `%LOCALAPPDATA%\ccr\index.db`:

- Your prompts and Claude's replies are indexed with SQLite FTS5. Thinking, tool calls, tool output (capped at 2 KB each) and subagent transcripts are indexed too, but only searched when enabled in settings.
- Transcripts are append-only, so a session that grew is read from where the last run stopped. Deleted transcripts are dropped from the index.
- The index is roughly a tenth of the size of the transcripts. It's a cache; delete it at any time and it's rebuilt on the next run.

Everything stays on your machine. `ccr` makes no network requests and never modifies your transcripts.

## Uninstall

Delete `ccr.exe` and `%LOCALAPPDATA%\ccr`.

## License

[MIT](LICENSE)
