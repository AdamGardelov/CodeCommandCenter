namespace ClaudeCommandCenter.Services;

public static class PrReviewPrompts
{
    public static string GetPrompt(string language) => language switch
    {
        "sv" => "Granska alla ändringar i denna PR. Ge mig en kort lista med saker som bör ändras innan merge. Fokusera på: buggar, säkerhetsproblem, prestandaproblem, och avvikelser från C#/.NET best practices. Skippa småsaker som namngivning och formatering. Viktigt att inte bara kolla på ändrad kod utan också vad som eventuellt saknas. Svara på svenska",
        _ => "Review all changes in this PR. Give me a short list of things that should be changed before merge. Focus on: bugs, security issues, performance problems, and deviations from C#/.NET best practices. Skip minor stuff like naming and formatting. Important to not only review the changed code but also consider what might be missing.",
    };
}
