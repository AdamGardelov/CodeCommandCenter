namespace ClaudeCommandCenter.Models;

public record PullRequest(int Number, string Title, string HeadBranch, string Author);
