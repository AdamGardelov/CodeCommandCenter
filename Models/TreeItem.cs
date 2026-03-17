namespace ClaudeCommandCenter.Models;

public abstract record TreeItem
{
    public record SessionItem(Session Session, string? GroupName) : TreeItem;
    public record GroupHeader(SessionGroup Group, bool IsExpanded) : TreeItem;
    public record RepoItem(string RepoName, string RepoPath, string GroupName) : TreeItem;
}
