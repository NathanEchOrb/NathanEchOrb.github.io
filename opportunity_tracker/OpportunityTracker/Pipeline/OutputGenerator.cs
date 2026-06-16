using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpportunityTracker.Pipeline;

/// <summary>
/// Generates enriched HTML opportunity reports.
/// Ported from pipeline/output_generator.py (to_html_with_enriched_columns).
/// </summary>
public static class OutputGenerator
{
    // ── Regex for parsing report date from filename ────────────────────────

    private static readonly Regex ReportDateRe = new(
        @"opportunities_\d+days_(\d{1,2})-(\d{1,2})-(\d{2,4})",
        RegexOptions.IgnoreCase);

    private static readonly Regex TzAbbrevRe = new(
        @"\s+(?:UTC|GMT|Z|EST|EDT|CST|CDT|MST|MDT|PST|PDT|AKST|AKDT|HST|AST|ADT|NST|NDT|BST|CET|CEST|EET|EEST|IST|JST|KST|AEST|AEDT|ACST|ACDT|AWST|NZST|NZDT)\b",
        RegexOptions.IgnoreCase);

    // ── Column definition ──────────────────────────────────────────────────

    /// <summary>
    /// Defines one column in the HTML table.
    /// </summary>
    private readonly record struct ColumnDef(
        string PropertyName,
        string DisplayName,
        bool IncludeLink,
        bool IsCollapsible);

    private static readonly ColumnDef[] ColumnOrder =
    {
        new("Title",            "Opportunity (Linked)", true,  false),
        new("MatchedOrgs",      "Organization",         false, false),
        // --- Enriched columns start ---
        new("ParentAgency",     "Parent Agency",        false, true),
        new("FundingProfile",   "Funding Profile",      false, false),
        new("MatchedPrograms",  "Matched Programs",     false, true),
        new("Mission",          "Mission",              false, true),
        new("FocusAreas",       "Focus Areas",          false, true),
        new("NdaaReferences",   "NDAA References",      false, true),
        new("TechnicalPocName", "Technical POC",        false, true),
        new("TechnicalPocEmail","POC Email",            false, true),
        new("TechnicalPocPhone","POC Phone",            false, true),
        new("TechnicalPocTitle","POC Title",            false, true),
        new("OrgWebsite",       "Org Website",          false, true),
        // --- Enriched columns end ---
        new("NoticeTypeDisplay","Notice Type",          false, false),
        new("PostedDate",       "Updated Date",         false, false),
        new("ResponseDeadline", "Response Deadline",    false, false),
        new("Office",           "Office",               false, false),
    };

    private static readonly HashSet<string> EnrichedCols = new()
    {
        "ParentAgency", "FundingProfile", "MatchedPrograms", "Mission",
        "FocusAreas", "NdaaReferences", "TechnicalPocName",
        "TechnicalPocEmail", "TechnicalPocPhone", "TechnicalPocTitle",
        "OrgWebsite"
    };

    // ── Public entry point ─────────────────────────────────────────────────

    /// <summary>
    /// Generates an enriched HTML file matching the dark-theme layout produced
    /// by the Python pipeline's <c>to_html_with_enriched_columns</c> method.
    /// </summary>
    /// <param name="opportunities">List of enriched opportunities to render.</param>
    /// <param name="outputPath">Full path for the output HTML file.</param>
    /// <returns>The path to the created HTML file.</returns>
    public static string GenerateEnrichedHtml(List<Opportunity> opportunities, string outputPath)
    {
        if (opportunities.Count == 0)
            return outputPath;

        // Ensure output directory exists
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var html = new StringBuilder();

        // ── Head: CSS ──────────────────────────────────────────────────────
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html>");
        html.AppendLine("<head>");
        html.AppendLine("<style>");
        html.AppendLine("body{background-color:#1e1e1e;color:#e0e0e0;font-family:Arial,sans-serif;margin:20px;}");
        html.AppendLine("h2{color:#4CAF50;margin-bottom:15px;font-size:20px;}");
        html.AppendLine("table{border-collapse:collapse;width:100%;font-size:13px;}");
        html.AppendLine("th,td{border:1px solid #444;padding:4px 8px;text-align:left;}");
        html.AppendLine("th{background-color:#2d2d2d;color:#4CAF50;font-weight:bold;}");
        html.AppendLine("tr:nth-child(even){background-color:#252525;}");
        html.AppendLine("tr:nth-child(odd){background-color:#2a2a2a;}");
        html.AppendLine("tr:hover{background-color:#333;}");
        html.AppendLine("a{color:#64b5f6;text-decoration:none;}");
        html.AppendLine("a:hover{text-decoration:underline;color:#90caf9;}");
        html.AppendLine(".enriched-col{background-color:#1a2a1a;}");
        html.AppendLine("th.enriched-col{background-color:#2d3d2d;color:#81C784;}");
        html.AppendLine(".collapsible{display:none;}");
        html.AppendLine(".expanded .collapsible{display:table-cell;}");
        html.AppendLine(".toggle-btn{");
        html.AppendLine("  background-color:#4CAF50;");
        html.AppendLine("  color:white;");
        html.AppendLine("  border:none;");
        html.AppendLine("  padding:6px 12px;");
        html.AppendLine("  cursor:pointer;");
        html.AppendLine("  font-size:12px;");
        html.AppendLine("  border-radius:4px;");
        html.AppendLine("  margin-bottom:10px;");
        html.AppendLine("}");
        html.AppendLine(".toggle-btn:hover{background-color:#45a049;}");
        html.AppendLine(".button-row{margin-bottom:10px;}");
        html.AppendLine(".week-divider td{background-color:#1a1a2e;border:1px solid #555;color:#7b8cde;font-weight:bold;font-size:14px;padding:8px 12px;text-align:center;letter-spacing:0.5px;}");
        html.AppendLine("</style>");
        html.AppendLine("</head>");

        // ── Body: toggle button, table ─────────────────────────────────────
        html.AppendLine("<body>");
        html.AppendLine("<h2>Enriched Opportunity Data</h2>");
        html.AppendLine("<div class=\"button-row\">");
        html.AppendLine("<button class=\"toggle-btn\" id=\"toggleBtn\" onclick=\"toggleDetails()\">Expand Details</button>");
        html.AppendLine("</div>");
        html.AppendLine("<table id=\"dataTable\">");
        html.AppendLine("<thead>");
        html.AppendLine("<tr>");

        // ── Header row ─────────────────────────────────────────────────────
        foreach (var col in ColumnOrder)
        {
            string classAttr = BuildClassAttr(col.PropertyName, col.IsCollapsible);
            html.AppendLine($"<th{classAttr}>{col.DisplayName}</th>");
        }

        html.AppendLine("</tr>");
        html.AppendLine("</thead>");
        html.AppendLine("<tbody>");

        // ── Week divider state ─────────────────────────────────────────────
        DateTime? reportMonday = GetReportMonday(outputPath);
        int numCols = ColumnOrder.Length;
        string? prevWeekLabel = null;

        // ── Data rows ──────────────────────────────────────────────────────
        foreach (var opp in opportunities)
        {
            // Insert week divider when the week changes
            if (reportMonday != null)
            {
                string rawDate = opp.PostedDate ?? "";
                if (!string.IsNullOrEmpty(rawDate))
                {
                    DateTime? entryDt = TryParseDate(rawDate);
                    if (entryDt != null)
                    {
                        string weekLabel = GetWeekLabel(entryDt.Value);
                        if (weekLabel != prevWeekLabel)
                        {
                            html.AppendLine(
                                $"<tr class=\"week-divider\">" +
                                $"<td colspan=\"{numCols}\">Week of {weekLabel}</td></tr>");
                            prevWeekLabel = weekLabel;
                        }
                    }
                }
            }

            html.AppendLine("<tr>");

            foreach (var col in ColumnOrder)
            {
                string value = GetPropertyValue(opp, col.PropertyName);
                string classAttr = BuildClassAttr(col.PropertyName, col.IsCollapsible);

                // Format date columns
                if (col.PropertyName is "PostedDate" or "ResponseDeadline")
                {
                    value = FormatDateFriendly(value);
                }

                // Opportunity title with link
                if (col.PropertyName == "Title" && col.IncludeLink)
                {
                    string link = opp.UiLink ?? "";
                    if (!string.IsNullOrEmpty(link))
                    {
                        html.AppendLine(
                            $"<td{classAttr}><a href=\"{link}\" target=\"_blank\">{value}</a></td>");
                    }
                    else
                    {
                        html.AppendLine($"<td{classAttr}>{value}</td>");
                    }
                }
                else
                {
                    // Truncate long mission text
                    if (col.PropertyName == "Mission" && value.Length > 300)
                    {
                        value = value[..300] + "...";
                    }

                    html.AppendLine($"<td{classAttr}>{value}</td>");
                }
            }

            html.AppendLine("</tr>");
        }

        // ── Close table, add JavaScript, close body ────────────────────────
        html.AppendLine("</tbody>");
        html.AppendLine("</table>");
        html.AppendLine("<script>");
        html.AppendLine("function toggleDetails() {");
        html.AppendLine("  var table = document.getElementById(\"dataTable\");");
        html.AppendLine("  var btn = document.getElementById(\"toggleBtn\");");
        html.AppendLine("  if (table.classList.contains(\"expanded\")) {");
        html.AppendLine("    table.classList.remove(\"expanded\");");
        html.AppendLine("    btn.textContent = \"Expand Details\";");
        html.AppendLine("  } else {");
        html.AppendLine("    table.classList.add(\"expanded\");");
        html.AppendLine("    btn.textContent = \"Collapse Details\";");
        html.AppendLine("  }");
        html.AppendLine("}");
        html.AppendLine("</script>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");

        // ── Write file ─────────────────────────────────────────────────────
        File.WriteAllText(outputPath, html.ToString(), Encoding.UTF8);

        return outputPath;
    }

    // ── Private helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Builds the CSS class attribute string for a column cell.
    /// </summary>
    private static string BuildClassAttr(string propertyName, bool isCollapsible)
    {
        var classes = new List<string>(2);
        if (EnrichedCols.Contains(propertyName))
            classes.Add("enriched-col");
        if (isCollapsible)
            classes.Add("collapsible");

        return classes.Count > 0
            ? $" class=\"{string.Join(" ", classes)}\""
            : "";
    }

    /// <summary>
    /// Gets a property value from an Opportunity by property name.
    /// </summary>
    private static string GetPropertyValue(Opportunity opp, string propertyName)
    {
        return propertyName switch
        {
            "Title"            => opp.Title ?? "",
            "UiLink"           => opp.UiLink ?? "",
            "MatchedOrgs"      => opp.MatchedOrgs ?? "",
            "ParentAgency"     => opp.ParentAgency ?? "",
            "FundingProfile"   => opp.FundingProfile ?? "",
            "MatchedPrograms"  => opp.MatchedPrograms ?? "",
            "Mission"          => opp.Mission ?? "",
            "FocusAreas"       => opp.FocusAreas ?? "",
            "NdaaReferences"   => opp.NdaaReferences ?? "",
            "TechnicalPocName" => opp.TechnicalPocName ?? "",
            "TechnicalPocEmail"=> opp.TechnicalPocEmail ?? "",
            "TechnicalPocPhone"=> opp.TechnicalPocPhone ?? "",
            "TechnicalPocTitle"=> opp.TechnicalPocTitle ?? "",
            "OrgWebsite"       => opp.OrgWebsite ?? "",
            "NoticeTypeDisplay"=> opp.NoticeTypeDisplay ?? "",
            "PostedDate"       => opp.PostedDate ?? "",
            "ResponseDeadline" => opp.ResponseDeadline ?? "",
            "Office"           => opp.Office ?? "",
            _                  => ""
        };
    }

    /// <summary>
    /// Converts a date string like "2026-04-06" to "Apr 6, 2026".
    /// Returns the original string if parsing fails.
    /// </summary>
    private static string FormatDateFriendly(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        // Strip trailing timezone abbreviations
        string cleaned = TzAbbrevRe.Replace(value, "");

        if (DateTime.TryParse(cleaned, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime dt))
        {
            string month = dt.ToString("MMM", CultureInfo.InvariantCulture);
            return $"{month} {dt.Day}, {dt.Year}";
        }

        return value;
    }

    /// <summary>
    /// Tries to parse a date string (ISO or other common formats).
    /// </summary>
    private static DateTime? TryParseDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string cleaned = TzAbbrevRe.Replace(value, "");

        if (DateTime.TryParse(cleaned, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTime dt))
        {
            return dt;
        }

        return null;
    }

    /// <summary>
    /// Extracts the report date from the output filename and snaps to Monday.
    /// Matches patterns like "opportunities_14days_4-6-26".
    /// </summary>
    private static DateTime? GetReportMonday(string outputPath)
    {
        var m = ReportDateRe.Match(outputPath);
        if (!m.Success)
            return null;

        int mm = int.Parse(m.Groups[1].Value);
        int dd = int.Parse(m.Groups[2].Value);
        int yy = int.Parse(m.Groups[3].Value);
        if (yy < 100) yy += 2000;

        try
        {
            return SnapToMonday(new DateTime(yy, mm, dd));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Snaps a date to the most recent Monday on or before it.
    /// </summary>
    private static DateTime SnapToMonday(DateTime dt)
    {
        int daysSinceMonday = ((int)dt.DayOfWeek - 1 + 7) % 7;
        return dt.AddDays(-daysSinceMonday).Date;
    }

    /// <summary>
    /// Generates a week label like "Jun 1, 2026 — Jun 7, 2026".
    /// </summary>
    private static string GetWeekLabel(DateTime entryDate)
    {
        DateTime monday = SnapToMonday(entryDate);
        DateTime sunday = monday.AddDays(6);

        static string fmt(DateTime d)
        {
            string month = d.ToString("MMM", CultureInfo.InvariantCulture);
            return $"{month} {d.Day}, {d.Year}";
        }

        return $"{fmt(monday)} — {fmt(sunday)}";
    }
}
