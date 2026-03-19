namespace CodeCommandCenter.Models;

public class CachedRemoteSession
{
    public required string Name { get; set; }
    public string? Path { get; set; }
    public DateTime Created { get; set; }
}
