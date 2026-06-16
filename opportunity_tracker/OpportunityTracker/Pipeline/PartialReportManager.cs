using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpportunityTracker.Pipeline;

/// <summary>
/// Manages mid-week partial report creation, merging, and lifecycle.
/// Ported from manage_reports.sh, _merge_partial.py, and add_week_dividers.py.
/// </summary>
public class PartialReportManager
{
    // ── Regex patterns ──────────────────────────────────────────────────────

    private static readonly Regex FilenameDateRe = new(
        @"opportunities_(\d+days)_(\d{1,2})-(\d{1,2})-(\d{2,4})_enriched\.html$",
        RegexOptions.IgnoreCase);

    private static readonly Regex PartialDateRe = new(
        @"opportunities_(\d+days)_(\d{1,2})-(\d{1,2})-(\d{2,4})_partial\.html$",
        RegexOptions.IgnoreCase);

    private static readonly Regex TbodyRe = new(
        @"<tbody>(.*?)</tbody>",
        RegexOptions.Singleline);

    private static readonly Regex RowRe = new(
        @"<tr(?:\s[^>]*)?>.*?</tr>",
        RegexOptions.Singleline);

    private static readonly Regex TitleLinkRe = new(
        @"<a[^>]*>([^<]+)</a>");

    private static readonly Regex DedupeDateRe = new(
        @"([A-Z][a-z]{2}\s+\d+,\s+\d{4}\s*\(\d+\))");

    private static readonly Regex UpdatedDateRe = new(
        @"([A-Z][a-z]{2})\s+(\d{1,2}),\s+(\d{4})");

    private static readonly Regex FriendlyDateRe = new(
        @"([A-Z][a-z]{2}\s+\d{1,2},\s+\d{4})");

    private static readonly Regex CellRe = new(
        @"<td[^>]*>(.*?)</td>",
        RegexOptions.Singleline);

    private static readonly Regex HeaderRe = new(
        @"<thead>(.*?)</thead>",
        RegexOptions.Singleline);

    private static readonly Regex ThCountRe = new(@"<th");

    private static readonly Regex ReportDateFromNameRe = new(
        @"opportunities_\d+days_(\d{1,2})-(\d{1,2})-(\d{2,4})");

    private const string DividerCss =
        ".week-divider td{background-color:#1a1a2e;border:1px solid #555;" +
        "color:#7b8cde;font-weight:bold;font-size:14px;padding:8px 12px;" +
        "text-align:center;letter-spacing:0.5px;}";

    private static readonly Dictionary<string, int> MonthMap = new()
    {
        ["Jan"] = 1, ["Feb"] = 2, ["Mar"] = 3, ["Apr"] = 4,
        ["May"] = 5, ["Jun"] = 6, ["Jul"] = 7, ["Aug"] = 8,
        ["Sep"] = 9, ["Oct"] = 10, ["Nov"] = 11, ["Dec"] = 12
    };

    // ── Public entry point ──────────────────────────────────────────────────

    /// <summary>
    /// Processes a newly arrived enriched report file and decides whether to
    /// keep it as-is, create a new partial, or merge it into an existing partial.
    /// </summary>
    /// <param name="docsDir">Path to the docs/ directory containing published reports.</param>
    /// <param name="tempDocDir">Path to temp_doc/ for archiving originals.</param>
    /// <param name="filePath">Full path to the new enriched HTML file in docsDir.</param>
    public void ProcessNewReport(string docsDir, string tempDocDir, string filePath)
    {
        string fileName = Path.GetFileName(filePath);

        // Skip partials and non-opportunity files
        if (fileName.Contains("_partial.html", StringComparison.OrdinalIgnoreCase))
            return;
        if (!fileName.StartsWith("opportunities_", StringComparison.OrdinalIgnoreCase))
            return;

        // Parse date from filename
        var (fileDate, window) = ParseFilenameDate(fileName);
        if (fileDate == null)
            return;

        DateTime date = fileDate.Value;
        DayOfWeek dow = date.DayOfWeek;
        string fileDateStr = FormatDateMDYY(date);

        if (dow == DayOfWeek.Monday)
        {
            // ── Monday report: this is a real weekly report ──
            string partialForThisWeek = $"opportunities_{window}_{fileDateStr}_partial.html";
            string partialPath = Path.Combine(docsDir, partialForThisWeek);

            if (File.Exists(partialPath))
            {
                // Full Monday report replaces partial
                Directory.CreateDirectory(tempDocDir);
                string destPath = Path.Combine(tempDocDir, partialForThisWeek);
                if (File.Exists(destPath))
                    File.Delete(destPath);
                File.Move(partialPath, destPath);
            }

            // Clear state if active partial matches
            var state = ReadState(tempDocDir);
            if (state != null && state.VirtualReport == partialForThisWeek)
            {
                ClearState(tempDocDir);
            }

            // Keep Monday report in docs/ — no further action needed
        }
        else
        {
            // ── Non-Monday report ──

            // Skip if older than 14 days
            if (!IsWithinDays(date, 14))
                return;

            // Check if a Monday report exists for this week
            if (!MondayReportExists(docsDir, date))
            {
                // No Monday report — treat as late Monday, keep in docs/
                return;
            }

            // If only a partial exists (no full enriched), this is the real weekly report arriving late
            if (OnlyPartialExistsForWeek(docsDir, date))
            {
                DateTime prevMondayDate = SnapToMonday(date);
                string prevMonStr = FormatDateMDYY(prevMondayDate);
                var dir = new DirectoryInfo(docsDir);
                var oldPartial = dir.GetFiles($"opportunities_*_{prevMonStr}_partial.html").FirstOrDefault();
                if (oldPartial != null)
                {
                    Directory.CreateDirectory(tempDocDir);
                    string archivePath = Path.Combine(tempDocDir, oldPartial.Name);
                    if (File.Exists(archivePath)) File.Delete(archivePath);
                    File.Move(oldPartial.FullName, archivePath);
                    ClearState(tempDocDir);
                }
                return;
            }

            // Full Monday report exists — process as mid-week update
            Directory.CreateDirectory(tempDocDir);

            DateTime nextMon = GetNextMonday(date);
            DateTime prevMon = SnapToMonday(date);
            string nextMonStr = FormatDateMDYY(nextMon);
            string virtualName = $"opportunities_{window}_{nextMonStr}_partial.html";
            string virtualPath = Path.Combine(docsDir, virtualName);

            var state = ReadState(tempDocDir);

            // Recover state if the partial exists on disk but state was lost
            if (File.Exists(virtualPath) && (state == null || state.VirtualReport != virtualName))
            {
                state = new VirtualState
                {
                    VirtualReport = virtualName,
                    LastProcessedFile = "",
                    WeekOf = FormatDateMDYY(prevMon)
                };
            }

            if (state == null || state.VirtualReport != virtualName)
            {
                // ── First mid-week report for this upcoming week ──
                // Copy the file as the partial report foundation
                File.Copy(filePath, virtualPath, overwrite: true);

                // Move original to temp_doc
                string tempDest = Path.Combine(tempDocDir, fileName);
                if (File.Exists(tempDest))
                    File.Delete(tempDest);
                File.Move(filePath, tempDest);

                WriteState(tempDocDir, virtualName, fileName, FormatDateMDYY(prevMon));
            }
            else
            {
                // ── Subsequent mid-week report: merge into existing partial ──
                MergeIntoPartial(virtualPath, filePath);

                // Re-apply week dividers after merge
                ApplyWeekDividers(virtualPath);

                // Move original to temp_doc
                string tempDest = Path.Combine(tempDocDir, fileName);
                if (File.Exists(tempDest))
                    File.Delete(tempDest);
                File.Move(filePath, tempDest);

                WriteState(tempDocDir, virtualName, fileName, state.WeekOf);
            }
        }
    }

    // ── Merge logic ─────────────────────────────────────────────────────────

    /// <summary>
    /// Merges all rows from a new enriched file into an existing partial report.
    /// Strips week dividers, deduplicates by (title, date), sorts by Updated Date
    /// descending, and replaces the tbody content in the partial file.
    /// </summary>
    public void MergeIntoPartial(string partialPath, string newFilePath)
    {
        string partialHtml = File.ReadAllText(partialPath, System.Text.Encoding.UTF8);
        string newHtml = File.ReadAllText(newFilePath, System.Text.Encoding.UTF8);

        var partialRows = ExtractRows(partialHtml);
        var newRows = ExtractRows(newHtml);

        // Combine all rows, stripping week dividers
        var candidateRows = partialRows.Concat(newRows)
            .Where(r => !r.Contains("class=\"week-divider\""))
            .ToList();

        // Deduplicate by (title text, full date string including bracket number)
        var seen = new HashSet<(string title, string date)>();
        var dataRows = new List<string>();

        foreach (string row in candidateRows)
        {
            var key = DedupeKey(row);
            if (seen.Contains(key))
                continue;
            seen.Add(key);
            dataRows.Add(row);
        }

        // Sort by Updated Date descending (most recent first)
        dataRows.Sort((a, b) =>
        {
            DateTime dateA = ParseUpdatedDate(a);
            DateTime dateB = ParseUpdatedDate(b);
            return dateB.CompareTo(dateA);
        });

        string combined = string.Join("\n", dataRows);
        string newTbody = $"<tbody>\n{combined}\n</tbody>";

        string result = TbodyRe.Replace(partialHtml, newTbody, count: 1);
        File.WriteAllText(partialPath, result, System.Text.Encoding.UTF8);
    }

    // ── Week divider insertion ──────────────────────────────────────────────

    /// <summary>
    /// Inserts week divider rows at week boundaries within the tbody of the file.
    /// Idempotent: skips if dividers already exist.
    /// </summary>
    public void ApplyWeekDividers(string filePath)
    {
        string html = File.ReadAllText(filePath, System.Text.Encoding.UTF8);

        // If dividers already present, strip them first (we re-generate)
        // We always regenerate after a merge to handle reordering
        var tbodyMatch = TbodyRe.Match(html);
        if (!tbodyMatch.Success)
            return;

        int numCols = CountColumns(html);

        // Inject CSS if not already present
        if (!html.Contains(DividerCss))
        {
            html = html.Replace("</style>", DividerCss + "\n</style>");
        }

        // Re-match tbody after CSS injection may have shifted offsets
        tbodyMatch = TbodyRe.Match(html);
        if (!tbodyMatch.Success)
            return;

        string tbodyContent = tbodyMatch.Groups[1].Value;
        var rows = RowRe.Matches(tbodyContent)
            .Select(m => m.Value)
            .Where(r => !r.Contains("class=\"week-divider\""))
            .ToList();

        if (rows.Count == 0)
            return;

        var newRows = new List<string>();
        string? prevLabel = null;

        foreach (string row in rows)
        {
            DateTime? dateDt = ParseFriendlyDateFromRow(row);

            if (dateDt != null)
            {
                string label = WeekLabel(dateDt.Value);
                if (label != prevLabel)
                {
                    string divider =
                        $"<tr class=\"week-divider\">" +
                        $"<td colspan=\"{numCols}\">Week of {label}</td></tr>";
                    newRows.Add(divider);
                    prevLabel = label;
                }
            }

            newRows.Add(row);
        }

        string newTbody = "<tbody>\n" + string.Join("\n", newRows) + "\n</tbody>";
        html = html[..tbodyMatch.Index] + newTbody + html[(tbodyMatch.Index + tbodyMatch.Length)..];

        File.WriteAllText(filePath, html, System.Text.Encoding.UTF8);
    }

    // ── Helper: date parsing from filename ──────────────────────────────────

    /// <summary>
    /// Parses date and window from a filename like opportunities_14days_4-6-26_enriched.html.
    /// Returns (DateTime, window) or (null, "") if parsing fails.
    /// </summary>
    public static (DateTime? date, string window) ParseFilenameDate(string fileName)
    {
        var match = FilenameDateRe.Match(fileName);
        if (!match.Success)
            return (null, "");

        string window = match.Groups[1].Value;
        int mm = int.Parse(match.Groups[2].Value);
        int dd = int.Parse(match.Groups[3].Value);
        int yy = int.Parse(match.Groups[4].Value);
        if (yy < 100) yy += 2000;

        try
        {
            return (new DateTime(yy, mm, dd), window);
        }
        catch (ArgumentOutOfRangeException)
        {
            return (null, "");
        }
    }

    /// <summary>
    /// Snaps a date to the most recent Monday on or before it.
    /// </summary>
    public static DateTime SnapToMonday(DateTime dt)
    {
        int daysSinceMonday = ((int)dt.DayOfWeek - 1 + 7) % 7;
        return dt.AddDays(-daysSinceMonday).Date;
    }

    /// <summary>
    /// Gets the next Monday strictly after the given date.
    /// </summary>
    public static DateTime GetNextMonday(DateTime dt)
    {
        int daysUntilMonday = ((8 - (int)dt.DayOfWeek) % 7);
        if (daysUntilMonday == 0) daysUntilMonday = 7;
        return dt.AddDays(daysUntilMonday).Date;
    }

    /// <summary>
    /// Formats a DateTime as M-D-YY (no leading zeros, 2-digit year).
    /// </summary>
    public static string FormatDateMDYY(DateTime dt)
    {
        return $"{dt.Month}-{dt.Day}-{dt.Year % 100}";
    }

    /// <summary>
    /// Generates a week label like "Jun 1, 2026 — Jun 7, 2026".
    /// </summary>
    public static string WeekLabel(DateTime entryDate)
    {
        DateTime monday = SnapToMonday(entryDate);
        DateTime sunday = monday.AddDays(6);
        return $"{FormatFriendlyDate(monday)} — {FormatFriendlyDate(sunday)}";
    }

    private static string FormatFriendlyDate(DateTime dt)
    {
        string month = dt.ToString("MMM", CultureInfo.InvariantCulture);
        return $"{month} {dt.Day}, {dt.Year}";
    }

    // ── Helper: row extraction and parsing ──────────────────────────────────

    private static List<string> ExtractRows(string html)
    {
        var match = TbodyRe.Match(html);
        if (!match.Success)
            return new List<string>();

        string body = match.Groups[1].Value;
        return RowRe.Matches(body).Select(m => m.Value).ToList();
    }

    private static (string title, string date) DedupeKey(string row)
    {
        var titleMatch = TitleLinkRe.Match(row);
        var dateMatch = DedupeDateRe.Match(row);

        string title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : row;
        string date = dateMatch.Success
            ? Regex.Replace(dateMatch.Groups[1].Value, @"\s+", " ")
            : "";

        return (title, date);
    }

    private static DateTime ParseUpdatedDate(string row)
    {
        foreach (Match m in UpdatedDateRe.Matches(row))
        {
            if (MonthMap.TryGetValue(m.Groups[1].Value, out int month))
            {
                try
                {
                    int day = int.Parse(m.Groups[2].Value);
                    int year = int.Parse(m.Groups[3].Value);
                    return new DateTime(year, month, day);
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }
            }
        }
        return new DateTime(1900, 1, 1);
    }

    private static DateTime? ParseFriendlyDateFromRow(string row)
    {
        var cells = CellRe.Matches(row);
        foreach (Match cell in cells)
        {
            string cellContent = cell.Groups[1].Value;
            var m = FriendlyDateRe.Match(cellContent);
            if (m.Success)
            {
                string normalized = Regex.Replace(m.Groups[1].Value, @"\s+", " ");
                if (DateTime.TryParseExact(normalized, "MMM d, yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result))
                {
                    return result;
                }
            }
        }
        return null;
    }

    private static int CountColumns(string html)
    {
        var headerMatch = HeaderRe.Match(html);
        if (!headerMatch.Success)
            return 17;
        return ThCountRe.Matches(headerMatch.Groups[1].Value).Count;
    }

    private static bool IsWithinDays(DateTime fileDate, int maxDays)
    {
        int diffDays = (int)(DateTime.Today - fileDate.Date).TotalDays;
        // Allow future dates and up to maxDays in the past
        return diffDays <= maxDays;
    }

    private bool MondayReportExists(string docsDir, DateTime date)
    {
        return FullMondayReportExists(docsDir, date) || OnlyPartialExistsForWeek(docsDir, date);
    }

    private bool FullMondayReportExists(string docsDir, DateTime date)
    {
        DateTime prevMonday = SnapToMonday(date);
        string mondayStr = FormatDateMDYY(prevMonday);
        var dir = new DirectoryInfo(docsDir);
        if (!dir.Exists) return false;
        return dir.GetFiles($"opportunities_*_{mondayStr}_enriched.html").Any();
    }

    private bool OnlyPartialExistsForWeek(string docsDir, DateTime date)
    {
        DateTime prevMonday = SnapToMonday(date);
        string mondayStr = FormatDateMDYY(prevMonday);
        var dir = new DirectoryInfo(docsDir);
        if (!dir.Exists) return false;
        if (dir.GetFiles($"opportunities_*_{mondayStr}_enriched.html").Any()) return false;
        return dir.GetFiles($"opportunities_*_{mondayStr}_partial.html").Any();
    }

    // ── State file management ───────────────────────────────────────────────

    private static string GetStateFilePath(string tempDocDir)
    {
        return Path.Combine(tempDocDir, ".virtual_state.json");
    }

    private static VirtualState? ReadState(string tempDocDir)
    {
        string stateFile = GetStateFilePath(tempDocDir);
        if (!File.Exists(stateFile))
            return null;

        try
        {
            string json = File.ReadAllText(stateFile, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<VirtualState>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteState(string tempDocDir, string virtualName, string lastProcessed, string weekOf)
    {
        Directory.CreateDirectory(tempDocDir);
        string stateFile = GetStateFilePath(tempDocDir);

        var state = new VirtualState
        {
            VirtualReport = virtualName,
            LastProcessedFile = lastProcessed,
            WeekOf = weekOf
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(state, options);
        File.WriteAllText(stateFile, json, System.Text.Encoding.UTF8);
    }

    private static void ClearState(string tempDocDir)
    {
        string stateFile = GetStateFilePath(tempDocDir);
        if (File.Exists(stateFile))
            File.Delete(stateFile);
    }
}

/// <summary>
/// Tracks the current partial report state between runs.
/// Serialized as JSON at tempDocDir/.virtual_state.json.
/// </summary>
public class VirtualState
{
    [System.Text.Json.Serialization.JsonPropertyName("virtual_report")]
    public string VirtualReport { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("last_processed_file")]
    public string LastProcessedFile { get; set; } = "";

    [System.Text.Json.Serialization.JsonPropertyName("week_of")]
    public string WeekOf { get; set; } = "";
}
