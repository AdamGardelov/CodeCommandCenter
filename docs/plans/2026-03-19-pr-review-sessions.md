# PR Review Sessions Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a dedicated "Review PR" action that picks a git repo, lists open PRs via `gh`, creates a worktree on the PR branch, and launches a Claude session with a language-specific review prompt.

**Architecture:** New `review-pr` keybinding triggers `SessionHandler.ReviewPr()`. The flow picks a git favorite, runs `gh pr list` to show open PRs, creates a worktree at `{worktreeBasePath}/reviews/{branch}/{repo}/`, and calls `backend.CreateSession()` with a new `initialPrompt` parameter. A `PrReviewLanguage` config setting controls the prompt language (Swedish/English).

**Tech Stack:** C# / .NET 10, Spectre.Console, `gh` CLI for PR listing, tmux/ConPTY backends.

---

### Task 1: Add `PrReviewLanguage` to CccConfig

**Files:**
- Modify: `Models/CccConfig.cs:20` (add property before closing brace)

**Step 1: Add the property**

In `Models/CccConfig.cs`, add after `SkipPermissionsSessions`:

```csharp
public string PrReviewLanguage { get; set; } = "en";
```

**Step 2: Build to verify**

Run: `dotnet build`
Expected: Build succeeded.

---

### Task 2: Add PR Review Language to Settings UI

**Files:**
- Modify: `UI/SettingsDefinition.cs:43-59` (add item to BuildGeneralItems)

**Step 1: Add setting item**

In `BuildGeneralItems`, add after the Worktree Base Path item:

```csharp
new()
{
    Label = "PR Review Language",
    Type = SettingsItemType.Text,
    GetValue = c => c.PrReviewLanguage,
    SetValue = (c, v) =>
    {
        var normalized = v.Trim().ToLowerInvariant();
        if (normalized is "en" or "sv")
            c.PrReviewLanguage = normalized;
    },
},
```

**Step 2: Build to verify**

Run: `dotnet build`
Expected: Build succeeded.

---

### Task 3: Add `initialPrompt` parameter to `ISessionBackend.CreateSession`

**Files:**
- Modify: `Services/ISessionBackend.cs:9`

**Step 1: Add parameter**

Change the `CreateSession` signature to:

```csharp
string? CreateSession(string name, string workingDirectory, string? claudeConfigDir = null, string? remoteHost = null, bool dangerouslySkipPermissions = false, string? initialPrompt = null);
```

**Step 2: Build (will fail — implementations need updating)**

Run: `dotnet build`
Expected: Build errors in TmuxBackend and ConPtyBackend (expected, fixed in next tasks).

---

### Task 4: Update `SshService.BuildSessionCommand` to support initial prompt

**Files:**
- Modify: `Services/SshService.cs:60-74`

**Step 1: Add initialPrompt parameter**

Change `BuildSessionCommand` signature and implementation:

```csharp
public static (string FileName, List<string> Args) BuildSessionCommand(
    string? remoteHost, string workingDirectory, bool dangerouslySkipPermissions = false, string? initialPrompt = null)
{
    var claudeCmd = dangerouslySkipPermissions ? "claude --dangerously-skip-permissions" : "claude";
    if (initialPrompt != null)
    {
        // Shell-escape the prompt: replace single quotes for safe embedding in shell string
        var escaped = initialPrompt.Replace("'", "'\\''");
        claudeCmd += $" '{escaped}'";
    }

    if (remoteHost == null)
    {
        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
        return (shell, ["-lc", claudeCmd]);
    }

    return ("ssh", ["-t", remoteHost, $"cd {EscapePath(workingDirectory)} && exec \"$SHELL\" -lc {EscapeSegment(claudeCmd)}"]);
}
```

**Step 2: Update all callers**

The only callers are `TmuxBackend.CreateSession` and `ConPtyBackend.StartProcess`. They'll be updated in the next tasks.

---

### Task 5: Update `TmuxBackend.CreateSession` to pass through `initialPrompt`

**Files:**
- Modify: `Services/TmuxBackend.cs:43-74`

**Step 1: Add parameter and pass through**

```csharp
public string? CreateSession(string name, string workingDirectory, string? claudeConfigDir = null, string? remoteHost = null, bool dangerouslySkipPermissions = false, string? initialPrompt = null)
{
    var envArgs = new List<string> { "-e", $"CCC_SESSION_NAME={name}" };
    if (!string.IsNullOrEmpty(claudeConfigDir))
    {
        envArgs.Add("-e");
        envArgs.Add($"CLAUDE_CONFIG_DIR={claudeConfigDir}");
    }

    var (cmdFile, cmdArgs) = SshService.BuildSessionCommand(remoteHost, workingDirectory, dangerouslySkipPermissions, initialPrompt);
    // ... rest unchanged
```

**Step 2: Build to verify**

Run: `dotnet build`
Expected: Build errors only in ConPtyBackend (fixed next task).

---

### Task 6: Update `ConPtyBackend.StartProcess` to pass through `initialPrompt`

**Files:**
- Modify: `Services/ConPty/ConPtyBackend.cs:54` and `:348`

**Step 1: Update CreateSession signature**

```csharp
public string? CreateSession(string name, string workingDirectory, string? claudeConfigDir = null, string? remoteHost = null, bool dangerouslySkipPermissions = false, string? initialPrompt = null)
```

Pass `initialPrompt` through to `StartProcess`.

**Step 2: Update StartProcess**

```csharp
private static ConPtySession StartProcess(string name, string workingDirectory, string? claudeConfigDir, string? remoteHost, bool dangerouslySkipPermissions = false, string? initialPrompt = null)
```

In the local command path (line ~427), update:

```csharp
else
{
    var claudeCmd = dangerouslySkipPermissions ? "claude --dangerously-skip-permissions" : "claude";
    if (initialPrompt != null)
    {
        // On Windows, quote the prompt for cmd.exe
        var escaped = initialPrompt.Replace("\"", "\\\"");
        claudeCmd += $" \"{escaped}\"";
    }
    commandLine = claudeCmd;
```

For the remote path, pass `initialPrompt` to `SshService.BuildSessionCommand`.

**Step 3: Build to verify**

Run: `dotnet build`
Expected: Build succeeded (all implementations now match interface).

---

### Task 7: Add `PickGitFavorite` helper to FlowHelper

**Files:**
- Modify: `Handlers/FlowHelper.cs`

**Step 1: Add method**

Add a new method to `FlowHelper` that shows only git repo favorites (no worktree creation, no custom path):

```csharp
public FavoriteFolder? PickGitFavorite()
{
    var gitFavorites = config.FavoriteFolders
        .Where(f => GitService.IsGitRepo(ConfigService.ExpandPath(f.Path)))
        .ToList();

    if (gitFavorites.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]No git repos found in favorites[/]");
        AnsiConsole.MarkupLine("[grey](Press any key)[/]");
        Console.ReadKey(true);
        return null;
    }

    var prompt = new SelectionPrompt<string>()
        .Title("[grey70]Pick a repo[/]")
        .PageSize(15)
        .HighlightStyle(new Style(Color.White, Color.Grey70));

    foreach (var fav in gitFavorites)
        prompt.AddChoice($"{fav.Name}  [grey50]{fav.Path}[/]");
    prompt.AddChoice(CancelChoice);

    var selected = AnsiConsole.Prompt(prompt);
    if (selected == CancelChoice)
        return null;

    var selectedName = selected.Split("  ")[0];
    return gitFavorites.FirstOrDefault(f => f.Name == selectedName);
}
```

**Step 2: Build to verify**

Run: `dotnet build`
Expected: Build succeeded.

---

### Task 8: Add `PickPullRequest` helper to FlowHelper

**Files:**
- Modify: `Handlers/FlowHelper.cs`

**Step 1: Add PR model record**

Add at the bottom of FlowHelper.cs (or a new file `Models/PullRequest.cs` — but since it's only used in the flow, a local record is fine):

Actually, add a new file `Models/PullRequest.cs`:

```csharp
namespace ClaudeCommandCenter.Models;

public record PullRequest(int Number, string Title, string HeadBranch, string Author);
```

**Step 2: Add PickPullRequest method to FlowHelper**

```csharp
public PullRequest? PickPullRequest(string repoPath)
{
    List<PullRequest>? prs = null;
    string? error = null;

    AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .SpinnerStyle(new Style(Color.Grey70))
        .Start("[grey70]Fetching open PRs...[/]", _ =>
        {
            (prs, error) = GitService.ListPullRequests(repoPath);
        });

    if (error != null)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
        AnsiConsole.MarkupLine("[grey](Press any key)[/]");
        Console.ReadKey(true);
        return null;
    }

    if (prs == null || prs.Count == 0)
    {
        AnsiConsole.MarkupLine("[yellow]No open PRs found[/]");
        AnsiConsole.MarkupLine("[grey](Press any key)[/]");
        Console.ReadKey(true);
        return null;
    }

    var prompt = new SelectionPrompt<string>()
        .Title("[grey70]Pick a PR to review[/]")
        .PageSize(15)
        .HighlightStyle(new Style(Color.White, Color.Grey70));

    foreach (var pr in prs)
        prompt.AddChoice($"[white]#{pr.Number}[/]  {Markup.Escape(pr.Title)}  [grey50]{Markup.Escape(pr.Author)} \u2192 {Markup.Escape(pr.HeadBranch)}[/]");
    prompt.AddChoice(CancelChoice);

    var selected = AnsiConsole.Prompt(prompt);
    if (selected == CancelChoice)
        return null;

    // Parse PR number from selection: "#123  title..."
    var raw = Markup.Remove(selected);
    var numberStr = raw.Split(' ', 2)[0].TrimStart('#');
    if (int.TryParse(numberStr, out var num))
        return prs.FirstOrDefault(p => p.Number == num);

    return null;
}
```

**Step 3: Build to verify**

Run: `dotnet build`
Expected: Build error — `GitService.ListPullRequests` doesn't exist yet. Fixed in next task.

---

### Task 9: Add `ListPullRequests` to GitService

**Files:**
- Modify: `Services/GitService.cs`

**Step 1: Add method**

```csharp
public static (List<PullRequest>? Prs, string? Error) ListPullRequests(string repoPath)
{
    try
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gh",
            WorkingDirectory = repoPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("pr");
        startInfo.ArgumentList.Add("list");
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("number,title,headRefName,author");
        startInfo.ArgumentList.Add("--limit");
        startInfo.ArgumentList.Add("30");

        using var process = Process.Start(startInfo);
        if (process == null)
            return (null, "Failed to start gh");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            return (null, string.IsNullOrWhiteSpace(stderr) ? "gh pr list failed" : stderr.Trim());

        var prs = new List<PullRequest>();
        using var doc = System.Text.Json.JsonDocument.Parse(stdout);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var number = el.GetProperty("number").GetInt32();
            var title = el.GetProperty("title").GetString() ?? "";
            var branch = el.GetProperty("headRefName").GetString() ?? "";
            var author = el.GetProperty("author").TryGetProperty("login", out var login)
                ? login.GetString() ?? ""
                : "";
            prs.Add(new PullRequest(number, title, branch, author));
        }

        return (prs, null);
    }
    catch (Exception ex)
    {
        return (null, $"gh not available: {ex.Message}");
    }
}
```

Add `using ClaudeCommandCenter.Models;` at the top if not already present.

**Step 2: Build to verify**

Run: `dotnet build`
Expected: Build succeeded.

---

### Task 10: Add review prompt constants

**Files:**
- Create: `Services/PrReviewPrompts.cs`

**Step 1: Create the file**

```csharp
namespace ClaudeCommandCenter.Services;

public static class PrReviewPrompts
{
    public static string GetPrompt(string language) => language switch
    {
        "sv" => "Granska alla ändringar i denna PR. Ge mig en kort lista med saker som bör ändras innan merge. Fokusera på: buggar, säkerhetsproblem, prestandaproblem, och avvikelser från C#/.NET best practices. Skippa småsaker som namngivning och formatering. Viktigt att inte bara kolla på ändrad kod utan också vad som eventuellt saknas. Svara på svenska",
        _ => "Review all changes in this PR. Give me a short list of things that should be changed before merge. Focus on: bugs, security issues, performance problems, and deviations from C#/.NET best practices. Skip minor stuff like naming and formatting. Important to not only review the changed code but also consider what might be missing.",
    };
}
```

**Step 2: Build to verify**

Run: `dotnet build`
Expected: Build succeeded.

---

### Task 11: Add `ReviewPr` method to SessionHandler

**Files:**
- Modify: `Handlers/SessionHandler.cs`

**Step 1: Add the method**

Add after the `Create` method:

```csharp
public void ReviewPr(bool claudeAvailable)
{
    if (!claudeAvailable)
    {
        state.SetStatus("'claude' not found in PATH — install Claude Code first");
        return;
    }

    FlowHelper.RunFlow("Review PR", () =>
    {
        var totalSteps = 2;
        var step = 0;

        // Step 1: Pick repo
        FlowHelper.PrintStep(++step, totalSteps, "Repository");
        var favorite = flow.PickGitFavorite()
                       ?? throw new FlowCancelledException();

        var repoPath = ConfigService.ExpandPath(favorite.Path);
        var repoName = favorite.Name;

        // Step 2: Pick PR
        FlowHelper.PrintStep(++step, totalSteps, "Pull Request");
        var pr = flow.PickPullRequest(repoPath)
                 ?? throw new FlowCancelledException();

        // Create worktree for the PR branch
        var basePath = ConfigService.ExpandPath(config.WorktreeBasePath);
        var worktreeDest = Path.Combine(basePath, "reviews", pr.HeadBranch, repoName);

        string? worktreeError = null;
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(new Style(Color.Grey70))
            .Start($"[grey70]Creating worktree for [white]{Markup.Escape(pr.HeadBranch)}[/]...[/]", _ =>
            {
                GitService.FetchPrune(repoPath);

                if (Directory.Exists(worktreeDest))
                {
                    // Worktree already exists — just check out the branch
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(worktreeDest)!);

                // For PRs we don't create a new branch — we check out the existing remote branch
                var (checkSuccess, checkOutput) = RunGitWorktreeAddExisting(repoPath, worktreeDest, pr.HeadBranch);
                if (!checkSuccess)
                    worktreeError = checkOutput;
            });

        if (worktreeError != null)
            throw new FlowCancelledException($"Worktree failed: {worktreeError}");

        // Build session
        var sessionName = FlowHelper.SanitizeSessionName($"review-{pr.Number}");
        var existingNames = new HashSet<string>(state.Sessions.Select(s => s.Name));
        sessionName = FlowHelper.UniqueSessionName(sessionName, existingNames, "-");

        var prompt = PrReviewPrompts.GetPrompt(config.PrReviewLanguage);
        var claudeConfigDir = ConfigService.ResolveClaudeConfigDir(config, worktreeDest);
        var error = backend.CreateSession(sessionName, worktreeDest, claudeConfigDir, initialPrompt: prompt);
        if (error != null)
            throw new FlowCancelledException(error);

        // Auto-assign color and save metadata
        var color = FlowHelper.PickRandomUnusedColor(config);
        if (color != null)
            ConfigService.SaveColor(config, sessionName, color);
        ConfigService.SaveDescription(config, sessionName, $"PR #{pr.Number}: {pr.Title}");
        backend.ApplyStatusColor(sessionName, color ?? "grey42");
        backend.AttachSession(sessionName);
        loadSessions();
        resetPaneCache();
    }, state);
}
```

Note: This requires two helper methods — `RunGitWorktreeAddExisting` on `GitService` and `PickRandomUnusedColor` on `FlowHelper`. Added in next tasks.

---

### Task 12: Add `CreateWorktreeFromExisting` to GitService

**Files:**
- Modify: `Services/GitService.cs`

**Step 1: Add method**

For PR review, we check out an existing remote branch (not create a new one):

```csharp
/// <summary>
/// Creates a worktree that checks out an existing remote branch (for PR review).
/// Fetches the branch from origin first, then creates the worktree.
/// </summary>
public static (bool Success, string? Output) CreateWorktreeFromExisting(string repoPath, string worktreeDest, string branchName)
{
    // Fetch the specific branch
    var (fetchOk, fetchErr) = RunGit(repoPath, "fetch", "origin", $"{branchName}:{branchName}");
    if (!fetchOk)
    {
        // Branch might already exist locally, try worktree add anyway
    }

    Directory.CreateDirectory(Path.GetDirectoryName(worktreeDest)!);
    return RunGit(repoPath, "worktree", "add", worktreeDest, branchName);
}
```

**Step 2: Build to verify**

Run: `dotnet build`
Expected: Build succeeded (or errors from Task 11 — those get resolved as we complete all tasks).

---

### Task 13: Add `PickRandomUnusedColor` to FlowHelper

**Files:**
- Modify: `Handlers/FlowHelper.cs`

**Step 1: Add static method**

```csharp
public static string? PickRandomUnusedColor(CccConfig config)
{
    var usedColors = new HashSet<string>(config.SessionColors.Values, StringComparer.OrdinalIgnoreCase);
    var unused = _colorPalette.Where(c => !usedColors.Contains(c.SpectreColor)).ToArray();
    return unused.Length > 0 ? unused[Random.Shared.Next(unused.Length)].SpectreColor : null;
}
```

**Step 2: Build to verify**

Run: `dotnet build`
Expected: Build succeeded.

---

### Task 14: Add `review-pr` keybinding

**Files:**
- Modify: `Services/KeyBindingService.cs`

**Step 1: Add default keybinding**

In the `_defaults` list, add in the CRUD group (after `adopt-remote`):

```csharp
new()
{
    ActionId = "review-pr",
    Key = "p",
    Label = "review",
    CanDisable = true,
    StatusBarOrder = 27
},
```

**Step 2: Build to verify**

Run: `dotnet build`
Expected: Build succeeded.

---

### Task 15: Wire `review-pr` action in App.DispatchAction

**Files:**
- Modify: `App.cs:~694` (in the switch statement)

**Step 1: Add case**

After the `"move-to-group"` case:

```csharp
case "review-pr":
    _sessionHandler.ReviewPr(_claudeAvailable);
    break;
```

**Step 2: Build to verify**

Run: `dotnet build`
Expected: Build succeeded.

---

### Task 16: Update SessionHandler.ReviewPr to use GitService.CreateWorktreeFromExisting

**Files:**
- Modify: `Handlers/SessionHandler.cs`

**Step 1: Replace placeholder call**

In the `ReviewPr` method, replace the `RunGitWorktreeAddExisting` call with:

```csharp
var (checkSuccess, checkOutput) = GitService.CreateWorktreeFromExisting(repoPath, worktreeDest, pr.HeadBranch);
if (!checkSuccess)
    worktreeError = checkOutput;
```

**Step 2: Full build**

Run: `dotnet build`
Expected: Build succeeded — all pieces connected.

---

### Task 17: Update README.md

**Files:**
- Modify: `README.md`

**Step 1: Add keybinding to table**

Add `p` / `review` entry in the keybindings table.

**Step 2: Add config option**

Add `prReviewLanguage` to the config section with description: `"en"` or `"sv"` — controls the language of the PR review prompt.

**Step 3: Add feature description**

Add a section about the PR review feature explaining the flow.

---

### Task 18: Final verification

**Step 1: Build**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings, 0 errors.

**Step 2: Manual test**

Run: `dotnet run`

1. Press `p` to trigger Review PR
2. Pick a repo from favorites
3. Verify PRs are listed from `gh pr list`
4. Pick a PR
5. Verify worktree is created at `~/Dev/Wint/worktrees/reviews/{branch}/{repo}/`
6. Verify Claude launches with the review prompt
7. Open settings (`s`) and verify "PR Review Language" appears under General
8. Change it to `sv`, press `p` again, verify Swedish prompt is used
