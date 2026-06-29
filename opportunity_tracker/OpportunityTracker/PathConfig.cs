using System.Text.Json;

namespace OpportunityTracker;

public static class PathConfig
{
    public static string DownloadsDir { get; }
    public static string PagesRepoDir { get; }
    public static string RawDocsDir { get; }
    public static string EnrichedDocsDir { get; }
    public static string PublishDocsDir { get; }
    public static string TempDocDir { get; }

    static PathConfig()
    {
        DownloadsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string settingsPath = Path.Combine(exeDir, "paths.json");

        string pagesRepo = exeDir;
        string oppRepo = Path.Combine(
            Directory.GetParent(exeDir)?.FullName ?? exeDir, "Opportunity");

        if (File.Exists(settingsPath))
        {
            try
            {
                var json = JsonDocument.Parse(File.ReadAllText(settingsPath));
                var root = json.RootElement;
                if (root.TryGetProperty("pages_repo", out var p) && p.GetString() is string pr)
                    pagesRepo = pr;
                if (root.TryGetProperty("opportunity_repo", out var o) && o.GetString() is string or2)
                    oppRepo = or2;
            }
            catch { }
        }

        PagesRepoDir = pagesRepo;
        RawDocsDir = Path.Combine(oppRepo, "raw docs");
        EnrichedDocsDir = Path.Combine(oppRepo, "enriched docs");
        PublishDocsDir = Path.Combine(pagesRepo, "docs");
        TempDocDir = Path.Combine(pagesRepo, "temp_doc");
    }
}
