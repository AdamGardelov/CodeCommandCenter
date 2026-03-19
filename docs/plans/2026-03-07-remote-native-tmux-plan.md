# Remote-Native Tmux Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make remote sessions live in tmux *on the remote machine* so they survive closing your laptop.

**Architecture:** A new `BackendRouter : ISessionBackend` wraps a local `TmuxBackend` plus one `RemoteTmuxBackend` per configured remote host. All tmux commands for remote sessions run as `ssh -S <socket> host tmux ...` via a persistent ControlMaster connection. `App.cs` and all handlers are unchanged — they still receive a single `ISessionBackend`, which is now the router.

**Tech Stack:** .NET 10, Spectre.Console, SSH ControlMaster (OpenSSH built-in), no new NuGet packages.

---

## Task 0: Create worktree and branch

**REQUIRED SUB-SKILL:** Use superpowers:using-git-worktrees before starting.

Create branch `feature/remote-native-tmux` as a git worktree so work is isolated from `main`. All subsequent tasks run inside that worktree.

---

## Task 1: Add `IsOffline` to Session and cache model to config

**Files:**
- Modify: `Models/Session.cs`
- Modify: `Models/CccConfig.cs`
- Create: `Models/CachedRemoteSession.cs`

**Step 1: Add `IsOffline` to `Session`**

In `Models/Session.cs`, add after `SkipPermissions`:

```csharp
public bool IsOffline { get; set; }
```

**Step 2: Create `CachedRemoteSession`**

Create `Models/CachedRemoteSession.cs`:

```csharp
namespace CodeCommandCenter.Models;

public class CachedRemoteSession
{
    public required string Name { get; set; }
    public string? Path { get; set; }
    public DateTime Created { get; set; }
}
```

**Step 3: Add cache dictionary to `CccConfig`**

In `Models/CccConfig.cs`, add after `SessionRemoteHosts`:

```csharp
public Dictionary<string, List<CachedRemoteSession>> CachedRemoteSessions { get; set; } = new();
```

**Step 4: Build**

```bash
dotnet build
```

Expected: Build succeeded, 0 errors.

**Step 5: Commit**

```bash
git add Models/Session.cs Models/CccConfig.cs Models/CachedRemoteSession.cs
git commit -m "feat: add IsOffline to Session and cached remote session model"
```

---

## Task 2: `SshControlMasterService`

**Files:**
- Create: `Services/SshControlMasterService.cs`

This service manages one persistent SSH ControlMaster socket per remote host. All `RemoteTmuxBackend` instances share it. The socket lives at `~/.ccc/ssh/<safename>.sock`.

**Step 1: Create the service**

Create `Services/SshControlMasterService.cs`:

```csharp
using System.Diagnostics;

namespace CodeCommandCenter.Services;

public static class SshControlMasterService
{
    private static readonly string _socketDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ccc", "ssh");

    // Tracks last failed connection attempt per host to throttle retries (30s cooldown)
    private static readonly Dictionary<string, DateTime> _lastFailure = new();
    private static readonly TimeSpan _retryCooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ensures a ControlMaster socket exists for the host.
    /// Returns true if the socket is alive (or was successfully started).
    /// Uses a 30s cooldown after failure to avoid hammering unreachable hosts.
    /// </summary>
    public static bool EnsureConnected(string host)
    {
        // Throttle: don't retry a failed host within cooldown period
        if (_lastFailure.TryGetValue(host, out var lastFail)
            && DateTime.UtcNow - lastFail < _retryCooldown)
            return false;

        if (IsAlive(host))
            return true;

        return StartControlMaster(host);
    }

    /// <summary>
    /// Runs a tmux command on the remote host via the ControlMaster socket.
    /// Returns (success, stdout) or (false, null) if the host is offline.
    /// </summary>
    public static (bool Success, string? Output) RunTmuxCommand(string host, params string[] tmuxArgs)
    {
        if (!EnsureConnected(host))
            return (false, null);

        var socketPath = SocketPath(host);
        var startInfo = new ProcessStartInfo
        {
            FileName = "ssh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-S");
        startInfo.ArgumentList.Add(socketPath);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("BatchMode=yes");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add("ConnectTimeout=5");
        startInfo.ArgumentList.Add(host);
        startInfo.ArgumentList.Add("tmux");
        foreach (var arg in tmuxArgs)
            startInfo.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(startInfo);
            if (process == null)
                return (false, null);

            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return process.ExitCode == 0 ? (true, stdout.TrimEnd()) : (false, null);
        }
        catch
        {
            return (false, null);
        }
    }

    /// <summary>
    /// Disconnects the ControlMaster for this host (sends -O exit).
    /// Safe to call even if the socket doesn't exist.
    /// </summary>
    public static void Disconnect(string host)
    {
        try
        {
            var socketPath = SocketPath(host);
            if (!File.Exists(socketPath))
                return;

            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-S");
            startInfo.ArgumentList.Add(socketPath);
            startInfo.ArgumentList.Add("-O");
            startInfo.ArgumentList.Add("exit");
            startInfo.ArgumentList.Add(host);

            using var process = Process.Start(startInfo);
            process?.WaitForExit(3000);
        }
        catch
        {
            // Best-effort
        }
    }

    private static bool IsAlive(string host)
    {
        try
        {
            var socketPath = SocketPath(host);
            if (!File.Exists(socketPath))
                return false;

            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-S");
            startInfo.ArgumentList.Add(socketPath);
            startInfo.ArgumentList.Add("-O");
            startInfo.ArgumentList.Add("check");
            startInfo.ArgumentList.Add(host);

            using var process = Process.Start(startInfo);
            if (process == null) return false;
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool StartControlMaster(string host)
    {
        try
        {
            Directory.CreateDirectory(_socketDir);
            // Restrict socket dir to owner only
            if (!OperatingSystem.IsWindows())
                Process.Start("chmod", $"700 {_socketDir}")?.WaitForExit(1000);

            var socketPath = SocketPath(host);
            var startInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // -M = master mode, -N = no command, -f = daemonize after auth
            startInfo.ArgumentList.Add("-M");
            startInfo.ArgumentList.Add("-N");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("-S");
            startInfo.ArgumentList.Add(socketPath);
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("BatchMode=yes");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ConnectTimeout=10");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("ControlPersist=no");
            startInfo.ArgumentList.Add(host);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _lastFailure[host] = DateTime.UtcNow;
                return false;
            }

            process.WaitForExit(12000);
            var connected = process.ExitCode == 0;

            if (!connected)
                _lastFailure[host] = DateTime.UtcNow;
            else
                _lastFailure.Remove(host);

            return connected;
        }
        catch
        {
            _lastFailure[host] = DateTime.UtcNow;
            return false;
        }
    }

    private static string SocketPath(string host)
    {
        // Replace characters that are invalid in socket filenames
        var safeName = string.Concat(host.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '.' ? c : '_'));
        return Path.Combine(_socketDir, $"{safeName}.sock");
    }
}
```

**Step 2: Build**

```bash
dotnet build
```

Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add Services/SshControlMasterService.cs
git commit -m "feat: add SshControlMasterService for persistent SSH ControlMaster connections"
```

---

## Task 3: `RemoteTmuxBackend`

**Files:**
- Create: `Services/RemoteTmuxBackend.cs`

This mirrors `TmuxBackend` exactly but routes every `tmux` command through `SshControlMasterService.RunTmuxCommand`. One instance per configured remote host.

**Step 1: Create `RemoteTmuxBackend`**

Create `Services/RemoteTmuxBackend.cs`:

```csharp
using System.Diagnostics;
using CodeCommandCenter.Models;

namespace CodeCommandCenter.Services;

public class RemoteTmuxBackend(RemoteHost remoteHost) : ISessionBackend
{
    private readonly string _host = remoteHost.Host;

    /// <summary>
    /// True when the last ListSessions() call failed (host unreachable).
    /// Caller should fall back to cached sessions.
    /// </summary>
    public bool IsOffline { get; private set; }

    public List<Session> ListSessions()
    {
        var (success, output) = Run("list-sessions", "-F",
            "#{session_name}\t#{session_created}\t#{session_attached}\t#{session_windows}\t#{pane_current_path}\t#{pane_dead}");

        if (!success || output == null)
        {
            IsOffline = true;
            return [];
        }

        IsOffline = false;
        var sessions = new List<Session>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 4)
                continue;

            var session = new Session
            {
                Name = parts[0],
                IsAttached = parts[2] != "0",
                WindowCount = int.TryParse(parts[3], out var wc) ? wc : 0,
                CurrentPath = parts.Length > 4 ? parts[4] : null,
                IsDead = parts.Length > 5 && parts[5] == "1",
                RemoteHostName = remoteHost.Name,
            };

            if (long.TryParse(parts[1], out var epoch))
                session.Created = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;

            sessions.Add(session);
        }

        return sessions.OrderBy(s => s.Created).ThenBy(s => s.Name).ToList();
    }

    public string? CreateSession(string name, string workingDirectory, string? claudeConfigDir = null,
        string? remoteHost = null, bool dangerouslySkipPermissions = false)
    {
        var claudeCmd = dangerouslySkipPermissions ? "claude --dangerously-skip-permissions" : "claude";
        // Wrap in exec $SHELL -lc so the remote gets a login shell (loads PATH etc.)
        var shellCmd = $"exec \"$SHELL\" -lc '{claudeCmd.Replace("'", "'\\''")}'";
        var fullCmd = $"cd {SshService.EscapePath(workingDirectory)} && {shellCmd}";

        var (success, error) = RunWithError(
            "new-session", "-d", "-s", name, "-n", name,
            "-e", $"CCC_SESSION_NAME={name}",
            fullCmd);

        if (!success)
            return error ?? "Failed to create remote tmux session";

        // Disable auto-rename so our session name sticks
        Run("set-option", "-t", name, "automatic-rename", "off");
        return null;
    }

    public string? KillSession(string name)
    {
        var (success, error) = RunWithError("kill-session", "-t", name);
        return success ? null : error ?? "Failed to kill remote session";
    }

    public string? RenameSession(string oldName, string newName)
    {
        var (success, error) = RunWithError("rename-session", "-t", oldName, newName);
        return success ? null : error ?? "Failed to rename remote session";
    }

    /// <summary>
    /// Attaches to the remote tmux session with a real interactive TTY.
    /// This opens a full ssh + tmux attach terminal, suspending the CCC UI.
    /// </summary>
    public void AttachSession(string name)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ssh",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(_host);
        startInfo.ArgumentList.Add($"tmux attach-session -t {name}");

        try
        {
            var process = Process.Start(startInfo);
            process?.WaitForExit();
        }
        catch
        {
            // attach failed silently
        }
    }

    public void DetachSession()
    {
        // No-op — tmux handles detach via prefix+d on the remote side
    }

    public string? SendKeys(string sessionName, string text)
    {
        var (success, error) = RunWithError("send-keys", "-t", sessionName, "-l", text);
        if (!success)
            return error ?? "Failed to send keys to remote session";

        Run("send-keys", "-t", sessionName, "Enter");
        return null;
    }

    public void ForwardKey(string sessionName, ConsoleKeyInfo key)
    {
        if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key >= ConsoleKey.A && key.Key <= ConsoleKey.Z)
        {
            Run("send-keys", "-t", sessionName, $"C-{(char)('a' + key.Key - ConsoleKey.A)}");
            return;
        }

        var tmuxKey = key.Key switch
        {
            ConsoleKey.Enter => "Enter",
            ConsoleKey.Backspace => "BSpace",
            ConsoleKey.Delete => "DC",
            ConsoleKey.Tab => "Tab",
            ConsoleKey.Escape => "Escape",
            ConsoleKey.UpArrow => "Up",
            ConsoleKey.DownArrow => "Down",
            ConsoleKey.LeftArrow => "Left",
            ConsoleKey.RightArrow => "Right",
            ConsoleKey.Home => "Home",
            ConsoleKey.End => "End",
            ConsoleKey.PageUp => "PPage",
            ConsoleKey.PageDown => "NPage",
            ConsoleKey.Insert => "IC",
            ConsoleKey.F1 => "F1",
            ConsoleKey.F2 => "F2",
            ConsoleKey.F3 => "F3",
            ConsoleKey.F4 => "F4",
            ConsoleKey.F5 => "F5",
            ConsoleKey.F6 => "F6",
            ConsoleKey.F7 => "F7",
            ConsoleKey.F8 => "F8",
            ConsoleKey.F9 => "F9",
            ConsoleKey.F10 => "F10",
            ConsoleKey.F11 => "F11",
            ConsoleKey.F12 => "F12",
            _ => null,
        };

        if (tmuxKey != null)
        {
            Run("send-keys", "-t", sessionName, tmuxKey);
            return;
        }

        if (key.KeyChar != '\0')
            Run("send-keys", "-t", sessionName, "-l", key.KeyChar.ToString());
    }

    public void ForwardLiteralBatch(string sessionName, string text)
    {
        if (text.Length > 0)
            Run("send-keys", "-t", sessionName, "-l", text);
    }

    public string? CapturePaneContent(string sessionName, int lines = 500)
    {
        var (_, output) = Run("capture-pane", "-t", sessionName, "-p", "-e", "-S", $"-{lines}");
        return output;
    }

    public void ResizeWindow(string sessionName, int width, int height) =>
        Run("resize-window", "-t", sessionName, "-x", width.ToString(), "-y", height.ToString());

    public void ResetWindowSize(string sessionName) =>
        Run("set-option", "-u", "-t", sessionName, "window-size");

    public void ApplyStatusColor(string sessionName, string? spectreColor)
    {
        if (string.IsNullOrWhiteSpace(spectreColor))
            return;

        try
        {
            var color = Spectre.Console.Style.Parse(spectreColor).Foreground;
            var hex = $"#{color.R:x2}{color.G:x2}{color.B:x2}";
            Run("set-option", "-t", sessionName, "status-style", $"bg={hex},fg=white");
        }
        catch { }
    }

    private const int StableThreshold = 4;

    public void DetectWaitingForInputBatch(List<Session> sessions)
    {
        // No hook state available for remote sessions — always use pane content stability
        foreach (var session in sessions)
        {
            if (session.IsDead || session.IsOffline)
            {
                session.IsWaitingForInput = false;
                session.IsIdle = false;
                continue;
            }

            var (_, output) = Run("capture-pane", "-t", session.Name, "-p", "-S", "-20");
            if (output == null)
            {
                session.IsWaitingForInput = true;
                continue;
            }

            var content = SessionContentAnalyzer.GetContentAboveStatusBar(output);

            if (content == session.PreviousContent)
                session.StableContentCount++;
            else
            {
                session.StableContentCount = 0;
                session.PreviousContent = content;
            }

            var isStable = session.StableContentCount >= StableThreshold;
            session.IsIdle = isStable && SessionContentAnalyzer.IsIdlePrompt(content);
            session.IsWaitingForInput = isStable && !session.IsIdle;
        }
    }

    public bool IsAvailable() => true; // Assume SSH is available if we got this far

    public bool IsInsideHost() => false; // CCC runs locally, not inside the remote

    public bool HasClaude() => true; // Assume claude is installed on configured remote hosts

    public void Dispose()
    {
        SshControlMasterService.Disconnect(_host);
    }

    private (bool Success, string? Output) Run(params string[] args) =>
        SshControlMasterService.RunTmuxCommand(_host, args);

    private (bool Success, string? Error) RunWithError(params string[] args)
    {
        var (success, output) = SshControlMasterService.RunTmuxCommand(_host, args);
        return (success, success ? null : "Remote tmux command failed");
    }
}
```

**Step 2: Build**

```bash
dotnet build
```

Expected: Build succeeded, 0 errors.

**Step 3: Commit**

```bash
git add Services/RemoteTmuxBackend.cs
git commit -m "feat: add RemoteTmuxBackend — tmux sessions managed on remote machine via SSH"
```

---

## Task 4: `BackendRouter`

**Files:**
- Create: `Services/BackendRouter.cs`

The router is the single `ISessionBackend` passed to `App` and handlers. It aggregates sessions from all backends, routes operations to the right one, and handles offline fallback via config cache.

**Step 1: Add cache persistence to `ConfigService`**

In `Services/ConfigService.cs`, add a new static method after the existing `Save*` methods:

```csharp
public static void SaveRemoteSessionCache(CccConfig config, string hostName, List<Session> sessions)
{
    config.CachedRemoteSessions[hostName] = sessions
        .Select(s => new CachedRemoteSession
        {
            Name = s.Name,
            Path = s.CurrentPath,
            Created = s.Created ?? DateTime.UtcNow,
        })
        .ToList();
    Save(config);
}
```

**Step 2: Create `BackendRouter`**

Create `Services/BackendRouter.cs`:

```csharp
using CodeCommandCenter.Models;

namespace CodeCommandCenter.Services;

/// <summary>
/// Routes ISessionBackend calls to the correct local or remote backend.
/// This is the single backend instance seen by App and all handlers.
/// </summary>
public class BackendRouter(ISessionBackend local, Dictionary<string, RemoteTmuxBackend> remotes, CccConfig config) : ISessionBackend
{
    // Maps session name → remote host name (null = local). Rebuilt on each ListSessions().
    private Dictionary<string, string?> _sessionHosts = new();

    public List<Session> ListSessions()
    {
        var all = new List<Session>();

        // Local sessions
        all.AddRange(local.ListSessions());

        // Remote sessions
        foreach (var (hostName, remoteBackend) in remotes)
        {
            var remoteSessions = remoteBackend.ListSessions();

            if (remoteBackend.IsOffline)
            {
                // Use cached sessions, marked as offline
                var cached = config.CachedRemoteSessions.GetValueOrDefault(hostName) ?? [];
                var offlineSessions = cached.Select(c => new Session
                {
                    Name = c.Name,
                    CurrentPath = c.Path,
                    Created = c.Created,
                    RemoteHostName = hostName,
                    IsOffline = true,
                }).ToList();
                all.AddRange(offlineSessions);
            }
            else
            {
                // Update cache with fresh data
                ConfigService.SaveRemoteSessionCache(config, hostName, remoteSessions);
                all.AddRange(remoteSessions);
            }
        }

        // Rebuild routing map
        _sessionHosts = all.ToDictionary(
            s => s.Name,
            s => s.RemoteHostName);

        return all;
    }

    public string? CreateSession(string name, string workingDirectory, string? claudeConfigDir = null,
        string? remoteHost = null, bool dangerouslySkipPermissions = false)
    {
        if (remoteHost != null && remotes.TryGetValue(remoteHost, out var remoteBackend))
            return remoteBackend.CreateSession(name, workingDirectory, claudeConfigDir, null, dangerouslySkipPermissions);

        return local.CreateSession(name, workingDirectory, claudeConfigDir, null, dangerouslySkipPermissions);
    }

    public string? KillSession(string name) => BackendFor(name).KillSession(name);

    public string? RenameSession(string oldName, string newName)
    {
        var result = BackendFor(oldName).RenameSession(oldName, newName);
        if (result == null)
        {
            // Update routing map: old name is gone, new name takes its host
            if (_sessionHosts.TryGetValue(oldName, out var host))
            {
                _sessionHosts.Remove(oldName);
                _sessionHosts[newName] = host;
            }
        }
        return result;
    }

    public void AttachSession(string name) => BackendFor(name).AttachSession(name);

    public void DetachSession() => local.DetachSession();

    public string? SendKeys(string sessionName, string text) =>
        BackendFor(sessionName).SendKeys(sessionName, text);

    public void ForwardKey(string sessionName, ConsoleKeyInfo key) =>
        BackendFor(sessionName).ForwardKey(sessionName, key);

    public void ForwardLiteralBatch(string sessionName, string text) =>
        BackendFor(sessionName).ForwardLiteralBatch(sessionName, text);

    public string? CapturePaneContent(string sessionName, int lines = 500) =>
        BackendFor(sessionName).CapturePaneContent(sessionName, lines);

    public void ResizeWindow(string sessionName, int width, int height) =>
        BackendFor(sessionName).ResizeWindow(sessionName, width, height);

    public void ResetWindowSize(string sessionName) =>
        BackendFor(sessionName).ResetWindowSize(sessionName);

    public void ApplyStatusColor(string sessionName, string? spectreColor) =>
        BackendFor(sessionName).ApplyStatusColor(sessionName, spectreColor);

    public void DetectWaitingForInputBatch(List<Session> sessions)
    {
        // Split sessions by backend and dispatch to each
        var localSessions = sessions.Where(s => s.RemoteHostName == null).ToList();
        if (localSessions.Count > 0)
            local.DetectWaitingForInputBatch(localSessions);

        foreach (var (hostName, remoteBackend) in remotes)
        {
            var remoteSessions = sessions
                .Where(s => s.RemoteHostName == hostName && !s.IsOffline)
                .ToList();
            if (remoteSessions.Count > 0)
                remoteBackend.DetectWaitingForInputBatch(remoteSessions);
        }
    }

    public bool IsAvailable() => local.IsAvailable();

    public bool IsInsideHost() => local.IsInsideHost();

    public bool HasClaude() => local.HasClaude();

    public void Dispose()
    {
        local.Dispose();
        foreach (var remote in remotes.Values)
            remote.Dispose();
    }

    private ISessionBackend BackendFor(string sessionName)
    {
        if (_sessionHosts.TryGetValue(sessionName, out var hostName)
            && hostName != null
            && remotes.TryGetValue(hostName, out var remoteBackend))
            return remoteBackend;

        return local;
    }
}
```

**Step 3: Build**

```bash
dotnet build
```

Expected: Build succeeded, 0 errors.

**Step 4: Commit**

```bash
git add Services/BackendRouter.cs Services/ConfigService.cs
git commit -m "feat: add BackendRouter — routes session operations to local or remote backend"
```

---

## Task 5: Wire `BackendRouter` into `Program.cs`

**Files:**
- Modify: `Program.cs`

**Step 1: Locate where `TmuxBackend` is constructed**

Read `Program.cs`. Find the line(s) that create `new TmuxBackend()` (or `new ConPtyBackend()`) and `new App(backend)`.

**Step 2: Build the router and pass it to App**

Replace the backend construction with:

```csharp
// Build local backend
ISessionBackend localBackend = OperatingSystem.IsWindows()
    ? new ConPtyBackend()
    : new TmuxBackend();

// Load config to discover remote hosts
var config = ConfigService.Load();

// Build one RemoteTmuxBackend per configured remote host
var remotes = config.RemoteHosts.ToDictionary(
    h => h.Name,
    h => new RemoteTmuxBackend(h));

// Kick off ControlMaster connections in background (non-blocking)
foreach (var host in config.RemoteHosts)
    Task.Run(() => SshControlMasterService.EnsureConnected(host.Host));

// Router is the single ISessionBackend used by App and handlers
var backend = new BackendRouter(localBackend, remotes, config);

var app = new App(backend, mobileMode);
```

> **Note:** `Program.cs` may already have the config load + platform check. Integrate the above without duplicating — wrap existing code rather than rewriting. The key change is: old code passes raw `TmuxBackend` to `App`; new code passes `BackendRouter`.

**Step 3: Add missing using statements**

Ensure `Program.cs` has:

```csharp
using CodeCommandCenter.Services;
```

**Step 4: Build**

```bash
dotnet build
```

Expected: Build succeeded, 0 errors.

**Step 5: Commit**

```bash
git add Program.cs
git commit -m "feat: wire BackendRouter in Program.cs — remote sessions now use remote tmux"
```

---

## Task 6: Render offline sessions greyed out

**Files:**
- Modify: `UI/Renderer.cs`

**Step 1: Find `BuildSessionRow`**

Open `UI/Renderer.cs` and find the `BuildSessionRow` method (around line 128). It currently handles `IsExcluded` and color logic.

**Step 2: Add offline rendering**

After the `isSelected` check at the top of `BuildSessionRow`, add an early branch for offline sessions:

```csharp
// Offline remote sessions — show greyed out, no interaction indicators
if (session.IsOffline)
{
    var prefix = indented ? "  " : "";
    var name = Markup.Escape(session.Name);
    var hostInfo = session.RemoteHostName != null ? $" [grey35]({Markup.Escape(session.RemoteHostName)})[/]" : "";
    var row = $"[grey35]{prefix}✗ {name}[/]{hostInfo}";

    return isSelected
        ? new Markup($"[on grey15]{row}[/]")
        : new Markup(row);
}
```

Place this block **before** the existing color/status logic so offline sessions get a distinct fast-path render.

**Step 3: Disable offline sessions in preview**

In `BuildPreviewPanel` (or wherever `session.IsWaitingForInput` drives preview content), add a guard:

```csharp
if (session?.IsOffline == true)
{
    // Show a simple offline message instead of trying to capture pane
    return new Panel(new Markup("[grey35]Session is offline — host unreachable[/]"))
        .BorderColor(Color.Grey35)
        .Header("[grey35]Offline[/]");
}
```

Find where `CapturePaneContent` is invoked in preview rendering and add this check before it.

**Step 4: Build**

```bash
dotnet build
```

Expected: Build succeeded, 0 errors.

**Step 5: Commit**

```bash
git add UI/Renderer.cs
git commit -m "feat: render offline remote sessions greyed out with ✗ prefix"
```

---

## Task 7: Smoke test

No automated tests exist — verify manually.

**Step 1: Build release binary**

```bash
dotnet build
```

**Step 2: Test with no remote hosts configured**

Run CCC normally. Verify:
- [ ] All existing local sessions appear as before
- [ ] No errors or crashes
- [ ] `BackendRouter` is transparent when no remote hosts exist

**Step 3: Add a real remote host to `~/.ccc/config.json`**

```json
{
  "remoteHosts": [
    {
      "name": "MY-SERVER",
      "host": "user@your-server.example.com",
      "worktreeBasePath": "~/worktrees",
      "favoriteFolders": []
    }
  ]
}
```

**Step 4: Test online remote host**

Run CCC and verify:
- [ ] Remote sessions from that host appear in the session list alongside local ones
- [ ] `RemoteHostName` tag appears on remote sessions (check git branch line or preview)
- [ ] Pane preview renders remote session content
- [ ] Enter/attach opens `ssh -t host tmux attach-session` and returns to CCC on exit

**Step 5: Test offline remote host**

Stop the remote SSH server (or use an unreachable hostname). Run CCC and verify:
- [ ] Remote sessions appear greyed out with `✗` prefix
- [ ] Preview panel shows "Session is offline — host unreachable"
- [ ] Local sessions work normally
- [ ] After 30s, CCC retries (visible when you restore the server — sessions come back)

**Step 6: Test session persistence**

On a real remote host:
1. Create a remote session via CCC (`n` → pick remote host)
2. Close CCC (quit with `q`)
3. SSH manually to the remote: `ssh user@host 'tmux list-sessions'`
4. Verify the session still exists
5. Reopen CCC — session reappears and is controllable

---

## Task 8: Update README

**Files:**
- Modify: `README.md`

Add a section on remote hosts configuration:
- How to add `remoteHosts` to `~/.ccc/config.json`
- That sessions persist when laptop is closed (tmux lives on remote)
- Offline indicator behavior
- SSH key requirement (ControlMaster needs key-based auth — passwords not supported in BatchMode)
- Attach behavior (opens full SSH terminal, press prefix+d to return to CCC)

Commit:

```bash
git add README.md
git commit -m "docs: document remote-native tmux sessions in README"
```
