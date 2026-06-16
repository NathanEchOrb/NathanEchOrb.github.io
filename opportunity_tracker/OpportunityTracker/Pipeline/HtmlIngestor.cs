using System.Text.RegularExpressions;

namespace OpportunityTracker.Pipeline;

/// <summary>
/// Parses HTML table output from the SAM.gov bookmarklet and converts rows
/// into <see cref="Opportunity"/> objects.  The HTML is expected to contain a
/// single &lt;table&gt; with &lt;thead&gt;/&lt;tbody&gt; sections.
/// </summary>
public static class HtmlIngestor
{
    // ── Notice-type mapping (display text → SAM.gov code) ──────────────
    private static readonly (string Key, string Code)[] NoticeTypeMap =
    [
        ("presolicitation", "p"),
        ("solicitation", "o"),
        ("combined synopsis/solicitation", "k"),
        ("combined synopsis", "k"),
        ("sources sought", "r"),
        ("special notice", "s"),
        ("award notice", "a"),
        ("intent to bundle", "i"),
        ("sale of surplus", "g"),
    ];

    // ── Organization patterns for office-field extraction ──────────────
    private static readonly (string Org, string[] Patterns)[] OrgPatterns =
    [
        ("DARPA",     ["DARPA", "DSO", "TTO", "I2O", "MTO"]),
        ("AFRL",      ["AFRL", "AIR FORCE RESEARCH"]),
        ("MDA",       ["MDA", "MISSILE DEFENSE"]),
        ("DIU",       ["DIU", "DEFENSE INNOVATION"]),
        ("USSF",      ["SPACE FORCE", "USSF", "SPOC", "SSC", "SDA"]),
        ("NGA",       ["NGA", "GEOSPATIAL"]),
        ("NRO",       ["NRO", "RECONNAISSANCE"]),
        ("AFWERX",    ["AFWERX"]),
        ("SpaceWERX", ["SPACEWERX"]),
    ];

    // ── Solicitation-number regex patterns (tried in order) ────────────
    private static readonly Regex[] SolNumberPatterns =
    [
        new Regex(@"(HR\d{4}[\w-]+)",          RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(FA\d{4}-[\w-]+)",         RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(HQ\d{4}-[\w-]+)",         RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(N\d{5}-[\w-]+)",          RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(W\d{5}-[\w-]+)",          RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"(\w+-\d{2}-[A-Z]-\d+)",   RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private static readonly Regex OppIdFromUrl =
        new(@"/opp/([^/]+)", RegexOptions.Compiled);

    // ── Date formats (order matters: most specific first) ──────────────
    private static readonly string[] DateFormats =
    [
        "M/d/yyyy",
        "yyyy-MM-dd",
        "MMM d, yyyy",
        "MMMM d, yyyy",
        "M-d-yyyy",
    ];

    // Pre-compiled patterns for stripping HTML tags and extracting hrefs.
    private static readonly Regex TagStripper =
        new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex HrefExtractor =
        new(@"<a\b[^>]*\bhref\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Matches date strings that have a trailing parenthetical like "  (4)" after them.
    private static readonly Regex TrailingParen =
        new(@"\s*\(\d+\)\s*$", RegexOptions.Compiled);

    // ────────────────────────────────────────────────────────────────────
    //  Public entry point
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parse an HTML file exported by the SAM.gov bookmarklet.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the HTML file.</param>
    /// <returns>List of parsed <see cref="Opportunity"/> objects.</returns>
    public static List<Opportunity> ParseHtmlFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}", filePath);

        string html = File.ReadAllText(filePath);
        return ParseHtmlContent(html);
    }

    /// <summary>
    /// Parse raw HTML content containing a bookmarklet table.
    /// </summary>
    public static List<Opportunity> ParseHtmlContent(string html)
    {
        // ── Locate the <table> ─────────────────────────────────────────
        int tableStart = IndexOfTag(html, "table", 0);
        if (tableStart < 0)
            throw new InvalidOperationException("No <table> found in HTML content.");

        int tableEnd = html.IndexOf("</table>", tableStart, StringComparison.OrdinalIgnoreCase);
        if (tableEnd < 0)
            tableEnd = html.Length;
        else
            tableEnd += "</table>".Length;

        string tableHtml = html[tableStart..tableEnd];

        // ── Parse column headers ───────────────────────────────────────
        List<string> headers = ParseHeaders(tableHtml);

        // ── Parse body rows ────────────────────────────────────────────
        List<string> rows = ExtractBodyRows(tableHtml);

        var opportunities = new List<Opportunity>();
        foreach (string row in rows)
        {
            List<string> cells = ExtractCells(row);
            if (cells.Count < 2) continue;

            var opp = ParseRow(cells, row, headers);
            if (opp != null)
                opportunities.Add(opp);
        }

        return opportunities;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Header parsing
    // ────────────────────────────────────────────────────────────────────

    private static List<string> ParseHeaders(string tableHtml)
    {
        var headers = new List<string>();

        // Try <thead> first
        int theadStart = IndexOfTag(tableHtml, "thead", 0);
        if (theadStart >= 0)
        {
            int theadEnd = tableHtml.IndexOf("</thead>", theadStart, StringComparison.OrdinalIgnoreCase);
            if (theadEnd < 0) theadEnd = tableHtml.Length;
            string thead = tableHtml[theadStart..theadEnd];

            // Extract <th> cells
            headers = ExtractTagContents(thead, "th");
            if (headers.Count > 0) return headers;

            // Fall back to <td> inside thead
            headers = ExtractTagContents(thead, "td");
            if (headers.Count > 0) return headers;
        }

        // No <thead>: treat first <tr> as header row
        int firstTr = IndexOfTag(tableHtml, "tr", 0);
        if (firstTr >= 0)
        {
            int trEnd = tableHtml.IndexOf("</tr>", firstTr, StringComparison.OrdinalIgnoreCase);
            if (trEnd < 0) trEnd = tableHtml.Length;
            string firstRow = tableHtml[firstTr..(trEnd + "</tr>".Length)];

            headers = ExtractTagContents(firstRow, "th");
            if (headers.Count == 0)
                headers = ExtractTagContents(firstRow, "td");
        }

        return headers;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Row / cell extraction
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns raw &lt;tr&gt;...&lt;/tr&gt; strings from &lt;tbody&gt;, or
    /// all rows after the first one when no &lt;tbody&gt; exists.
    /// </summary>
    private static List<string> ExtractBodyRows(string tableHtml)
    {
        string searchArea;

        int tbodyStart = IndexOfTag(tableHtml, "tbody", 0);
        if (tbodyStart >= 0)
        {
            int tbodyEnd = tableHtml.IndexOf("</tbody>", tbodyStart, StringComparison.OrdinalIgnoreCase);
            if (tbodyEnd < 0) tbodyEnd = tableHtml.Length;
            searchArea = tableHtml[tbodyStart..tbodyEnd];
        }
        else
        {
            searchArea = tableHtml;
        }

        var rows = new List<string>();
        int pos = 0;
        while (true)
        {
            int trStart = IndexOfTag(searchArea, "tr", pos);
            if (trStart < 0) break;

            int trEnd = searchArea.IndexOf("</tr>", trStart, StringComparison.OrdinalIgnoreCase);
            if (trEnd < 0) break;

            trEnd += "</tr>".Length;
            rows.Add(searchArea[trStart..trEnd]);
            pos = trEnd;
        }

        // When there was no <tbody>, skip the first row (header).
        if (tbodyStart < 0 && rows.Count > 0)
            rows.RemoveAt(0);

        return rows;
    }

    /// <summary>
    /// Returns the raw inner-HTML of each &lt;td&gt; in <paramref name="trHtml"/>.
    /// </summary>
    private static List<string> ExtractCells(string trHtml)
    {
        var cells = new List<string>();
        int pos = 0;
        while (true)
        {
            int tdStart = IndexOfTag(trHtml, "td", pos);
            if (tdStart < 0) break;

            // Move past the opening tag
            int tagClose = trHtml.IndexOf('>', tdStart);
            if (tagClose < 0) break;
            int contentStart = tagClose + 1;

            int tdEnd = trHtml.IndexOf("</td>", contentStart, StringComparison.OrdinalIgnoreCase);
            if (tdEnd < 0) break;

            cells.Add(trHtml[contentStart..tdEnd]);
            pos = tdEnd + "</td>".Length;
        }

        return cells;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Row → Opportunity mapping
    // ────────────────────────────────────────────────────────────────────

    private static Opportunity? ParseRow(List<string> cellsInnerHtml, string rawTrHtml, List<string> headers)
    {
        try
        {
            // ── First cell: title + link ───────────────────────────────
            string firstCell = cellsInnerHtml[0];

            string url = "";
            var hrefMatch = HrefExtractor.Match(firstCell);
            if (hrefMatch.Success)
                url = hrefMatch.Groups[1].Value;

            string title = StripTags(firstCell).Trim();

            string solNumber = ExtractSolNumber(url, title);

            var opp = new Opportunity
            {
                Title = title,
                UiLink = url,
                SolicitationNumber = solNumber,
            };

            // ── Remaining cells mapped by header name ──────────────────
            for (int i = 1; i < cellsInnerHtml.Count; i++)
            {
                if (i >= headers.Count) break;

                string header = headers[i].ToLowerInvariant();
                string value = StripTags(cellsInnerHtml[i]).Trim();

                if (header.Contains("organization") || header.Contains("matched"))
                {
                    opp.MatchedOrgs = value;
                    if (!string.IsNullOrEmpty(value))
                        opp.DepartmentName = value.Split(',')[0].Trim();
                }
                else if (header.Contains("notice") && header.Contains("type"))
                {
                    opp.Type = MapNoticeType(value);
                    opp.NoticeTypeDisplay = value;
                }
                else if (header.Contains("updated"))
                {
                    opp.PostedDate = ParseDate(value);
                }
                else if (header.Contains("response"))
                {
                    opp.ResponseDeadline = ParseDate(value);
                }
                else if (header.Contains("office"))
                {
                    opp.Office = value;
                    if (string.IsNullOrEmpty(opp.DepartmentName) && !string.IsNullOrEmpty(value))
                        opp.DepartmentName = ExtractOrgFromOffice(value);
                }
            }

            return opp;
        }
        catch
        {
            return null;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Helper methods
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Map a human-readable notice type to its SAM.gov single-character code.
    /// </summary>
    internal static string MapNoticeType(string noticeType)
    {
        if (string.IsNullOrEmpty(noticeType))
            return "o";

        string lower = noticeType.ToLowerInvariant();
        foreach (var (key, code) in NoticeTypeMap)
        {
            if (lower.Contains(key))
                return code;
        }

        return noticeType[..1].ToLowerInvariant();
    }

    /// <summary>
    /// Parse a date string into ISO 8601 (yyyy-MM-dd).
    /// Handles trailing parenthetical counts like "Apr 6, 2026  (4)".
    /// </summary>
    internal static string ParseDate(string dateStr)
    {
        if (string.IsNullOrWhiteSpace(dateStr))
            return "";

        // Strip trailing " (4)" style annotations
        string cleaned = TrailingParen.Replace(dateStr, "").Trim();

        foreach (string fmt in DateFormats)
        {
            if (DateTime.TryParseExact(cleaned, fmt,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out DateTime dt))
            {
                return dt.ToString("yyyy-MM-dd");
            }
        }

        // Return the original string when no format matched.
        return dateStr.Trim();
    }

    /// <summary>
    /// Extract an organization abbreviation from the office field using
    /// known keyword patterns.
    /// </summary>
    internal static string ExtractOrgFromOffice(string office)
    {
        if (string.IsNullOrEmpty(office))
            return office;

        string upper = office.ToUpperInvariant();
        foreach (var (org, patterns) in OrgPatterns)
        {
            foreach (string pattern in patterns)
            {
                if (upper.Contains(pattern))
                    return org;
            }
        }

        return office;
    }

    /// <summary>
    /// Extract a solicitation number from the opportunity URL or title.
    /// </summary>
    private static string ExtractSolNumber(string url, string title)
    {
        // Try URL first: /opp/{id}/view
        if (!string.IsNullOrEmpty(url))
        {
            var m = OppIdFromUrl.Match(url);
            if (m.Success) return m.Groups[1].Value;
        }

        // Try known solicitation patterns in the title
        if (!string.IsNullOrEmpty(title))
        {
            foreach (var rx in SolNumberPatterns)
            {
                var m = rx.Match(title);
                if (m.Success) return m.Groups[1].Value;
            }
        }

        // Fallback
        return string.IsNullOrEmpty(title)
            ? "UNKNOWN"
            : title.Length <= 30 ? title : title[..30];
    }

    // ────────────────────────────────────────────────────────────────────
    //  Low-level HTML helpers (no external dependencies)
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Find the start of an opening tag, e.g. &lt;table ...&gt;.
    /// Returns the index of the '&lt;' or -1.
    /// </summary>
    private static int IndexOfTag(string html, string tagName, int startIndex)
    {
        // Match <tagName or <tagName> (with optional attributes).
        int pos = startIndex;
        while (pos < html.Length)
        {
            int idx = html.IndexOf('<' + tagName, pos, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;

            int after = idx + 1 + tagName.Length;
            if (after >= html.Length) return -1;

            char ch = html[after];
            // The char after the tag name must be whitespace, '>', or '/'.
            if (ch == '>' || ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r' || ch == '/')
                return idx;

            pos = after;
        }

        return -1;
    }

    /// <summary>
    /// Extract the text-only content of every instance of the given tag
    /// within the supplied HTML fragment.
    /// </summary>
    private static List<string> ExtractTagContents(string html, string tagName)
    {
        var results = new List<string>();
        int pos = 0;
        string closeTag = $"</{tagName}>";

        while (true)
        {
            int tagStart = IndexOfTag(html, tagName, pos);
            if (tagStart < 0) break;

            int tagClose = html.IndexOf('>', tagStart);
            if (tagClose < 0) break;

            int contentStart = tagClose + 1;
            int contentEnd = html.IndexOf(closeTag, contentStart, StringComparison.OrdinalIgnoreCase);
            if (contentEnd < 0) break;

            string inner = html[contentStart..contentEnd];
            results.Add(StripTags(inner).Trim());

            pos = contentEnd + closeTag.Length;
        }

        return results;
    }

    /// <summary>Remove all HTML tags, returning only text content.</summary>
    private static string StripTags(string html) =>
        TagStripper.Replace(html, "");
}
