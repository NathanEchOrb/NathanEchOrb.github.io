// Budget Data Fetcher
//
// Pulls official budget data from DoD Comptroller spreadsheets.
// Source: https://comptroller.war.gov/Budget-Materials/
//
// R-1 = RDT&E (Research, Development, Test & Evaluation)
// P-1 = Procurement
// O-1 = Operations & Maintenance
//
// Ported from pipeline/budget_fetcher.py

using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace OpportunityTracker.Pipeline;

/// <summary>
/// A matched program element from the budget.
/// </summary>
public class ProgramMatch
{
    public string PeCode { get; set; } = "";          // Program Element code (e.g., 0603892C)
    public string Name { get; set; } = "";             // Program name (e.g., "AEGIS BMD")
    public string BudgetType { get; set; } = "";       // rdte, procurement, or om
    public double AmountMillions { get; set; }
    public int FiscalYear { get; set; }
    public double MatchScore { get; set; }             // Confidence score 0-1
    public List<string> MatchedKeywords { get; set; } = [];
}

/// <summary>
/// Budget allocation data for an organization.
/// </summary>
public class BudgetData
{
    public int FiscalYear { get; set; }
    public string Organization { get; set; } = "";
    public double? RdteMillions { get; set; }          // R-1 data
    public double? ProcurementMillions { get; set; }   // P-1 data
    public double? OmMillions { get; set; }            // O-1 data
    public double? TotalMillions { get; set; }
    public List<Dictionary<string, object>> ProgramElements { get; set; } = [];
}

/// <summary>
/// Fetches and parses official DoD budget spreadsheets.
/// Data source: DoD Comptroller Budget Materials
/// https://comptroller.war.gov/Budget-Materials/
/// </summary>
public class DoDBudgetFetcher
{
    private const string BaseUrl = "https://comptroller.war.gov/Portals/45/Documents/defbudget";

    // Map organization keys to Organization column codes in DoD spreadsheets.
    // The "Organization" column uses short codes like DARPA, MDA, F (Air Force), etc.
    private static readonly Dictionary<string, string[]> OrgCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["darpa"] = ["DARPA"],
        ["mda"] = ["MDA"],
        ["diu"] = ["DIU"],
        ["dtra"] = ["DTRA"],
        ["socom"] = ["SOCOM"],
        ["dha"] = ["DHA"],
        ["disa"] = ["DISA"],
        ["afrl"] = ["F"],        // Air Force - AFRL is part of AF budget
        ["ussf"] = [],           // Space Force has separate account title matching
        ["sda"] = [],            // SDA - search by account title
    };

    // For orgs that need account title matching instead of org code.
    private static readonly Dictionary<string, string[]> OrgAccountTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ussf"] = ["Space Force"],
        ["sda"] = ["Space Development Agency"],
        ["afwerx"] = ["Air Force"],      // Part of AF budget
        ["spacewerx"] = ["Space Force"], // Part of SF budget
    };

    // Fallback text search patterns (searches all text columns).
    private static readonly Dictionary<string, string[]> OrgTextPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nga"] = ["NATIONAL GEOSPATIAL", "NGA"],
        ["nro"] = ["NATIONAL RECONNAISSANCE", "NRO"],
        ["nsa"] = ["NATIONAL SECURITY AGENCY", "NSA"],
        ["ssc"] = ["SPACE SYSTEMS COMMAND", "SSC"],
        ["spoc"] = ["SPACE OPERATIONS COMMAND", "SPOC"],
    };

    // Keywords to exclude from matching (common but non-discriminating).
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "will", "are",
        "was", "were", "been", "being", "have", "has", "had", "having",
        "does", "did", "doing", "would", "could", "should", "may", "might",
        "must", "shall", "can", "need", "dare", "ought", "used", "program",
        "system", "systems", "advanced", "development", "research", "support",
        "technology", "technologies", "engineering", "services", "contract",
        "acquisition", "office", "agency", "department", "defense", "military",
        "integration", "operations", "operational", "capability", "capabilities",
    };

    // Domain-specific keywords that boost match confidence.
    private static readonly Dictionary<string, string[]> DomainKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Missile defense
        ["aegis"] = ["aegis", "bmd", "ballistic missile"],
        ["thaad"] = ["thaad", "terminal high altitude"],
        ["patriot"] = ["patriot", "pac-3"],
        ["gmds"] = ["ground-based midcourse", "gmd", "gmds"],
        ["hypersonic"] = ["hypersonic", "hgv", "hypersonics", "glide"],

        // Space
        ["satellite"] = ["satellite", "sat", "satcom", "gps"],
        ["launch"] = ["launch", "rocket", "slv", "orbital"],
        ["space"] = ["space", "orbital", "leo", "geo", "meo"],
        ["gps"] = ["gps", "navigation", "pnt", "positioning"],
        ["isr"] = ["isr", "reconnaissance", "surveillance", "imaging"],

        // Aircraft/Autonomy
        ["uav"] = ["uav", "uas", "unmanned", "drone", "autonomous"],
        ["fighter"] = ["fighter", "f-35", "f-22", "ngad", "tactical"],

        // Cyber/Electronic
        ["cyber"] = ["cyber", "cybersecurity", "information warfare"],
        ["electronic"] = ["electronic warfare", "ew", "jamming", "spectrum"],

        // Other
        ["ai"] = ["artificial intelligence", "machine learning", "ai/ml", "autonomy"],
        ["quantum"] = ["quantum", "qis", "qubit"],
        ["directed_energy"] = ["directed energy", "laser", "hel", "microwave"],
    };

    private readonly int _fiscalYear;
    private readonly HttpClient _httpClient;

    // Cached raw Excel bytes per doc type (r1, p1, o1).
    private readonly Dictionary<string, byte[]> _cachedBytes = new(StringComparer.OrdinalIgnoreCase);

    // Cached parsed sheet data: list of rows, each row is a dict of (columnName -> cellValue).
    private readonly Dictionary<string, SheetData?> _cachedSheets = new(StringComparer.OrdinalIgnoreCase);

    public DoDBudgetFetcher(int fiscalYear = 2026, HttpClient? httpClient = null)
    {
        _fiscalYear = fiscalYear;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    // -----------------------------------------------------------------------
    // Spreadsheet URL and fetching
    // -----------------------------------------------------------------------

    private string GetSpreadsheetUrl(string docType)
        => $"{BaseUrl}/FY{_fiscalYear}/{docType}_display.xlsx";

    /// <summary>
    /// Fetch and parse a budget spreadsheet into a SheetData structure.
    /// Results are cached in memory after the first download.
    /// </summary>
    private async Task<SheetData?> FetchSpreadsheetAsync(string docType)
    {
        if (_cachedSheets.TryGetValue(docType, out var cached))
            return cached;

        var url = GetSpreadsheetUrl(docType);

        try
        {
            byte[] bytes;

            if (_cachedBytes.TryGetValue(docType, out var rawBytes))
            {
                bytes = rawBytes;
            }
            else
            {
                bytes = await _httpClient.GetByteArrayAsync(url).ConfigureAwait(false);
                _cachedBytes[docType] = bytes;
            }

            var sheetData = ParseExcelBytes(bytes);
            _cachedSheets[docType] = sheetData;
            return sheetData;
        }
        catch (HttpRequestException)
        {
            _cachedSheets[docType] = null;
            return null;
        }
        catch (Exception)
        {
            _cachedSheets[docType] = null;
            return null;
        }
    }

    /// <summary>
    /// Parse raw Excel bytes into a SheetData using ClosedXML.
    /// The DoD budget spreadsheets have a header row (row 2, 1-indexed) with column names,
    /// and row 1 typically contains totals/metadata. Data starts from row 3.
    /// This mirrors the Python code using header=1 (0-indexed row 1 = Excel row 2).
    /// </summary>
    private static SheetData ParseExcelBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheets.First();

        var usedRange = worksheet.RangeUsed();
        if (usedRange is null)
            return new SheetData([], []);

        int firstCol = usedRange.FirstColumn().ColumnNumber();
        int lastCol = usedRange.LastColumn().ColumnNumber();
        int lastRow = usedRange.LastRow().RowNumber();

        // Row 2 (1-indexed) contains column headers (matches Python header=1 which is 0-indexed).
        const int headerRowNumber = 2;

        if (lastRow < headerRowNumber)
            return new SheetData([], []);

        // Read column names from header row.
        var columnNames = new List<string>();
        var colNumberToIndex = new Dictionary<int, int>(); // Excel col number -> list index

        for (int col = firstCol; col <= lastCol; col++)
        {
            var cell = worksheet.Cell(headerRowNumber, col);
            var name = CellToString(cell).Trim();
            // If column name is blank, give it a placeholder so indices stay consistent.
            if (string.IsNullOrWhiteSpace(name))
                name = $"__col_{col}";
            columnNames.Add(name);
            colNumberToIndex[col] = columnNames.Count - 1;
        }

        // Read data rows starting from row 3.
        var rows = new List<Dictionary<string, object?>>();
        for (int row = headerRowNumber + 1; row <= lastRow; row++)
        {
            var rowDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            bool hasAnyValue = false;

            for (int col = firstCol; col <= lastCol; col++)
            {
                int idx = colNumberToIndex[col];
                var colName = columnNames[idx];
                var cell = worksheet.Cell(row, col);

                if (cell.IsEmpty())
                {
                    rowDict[colName] = null;
                    continue;
                }

                hasAnyValue = true;

                // Try to get numeric value; fall back to string.
                if (cell.DataType == XLDataType.Number)
                {
                    rowDict[colName] = cell.GetDouble();
                }
                else
                {
                    rowDict[colName] = CellToString(cell);
                }
            }

            if (hasAnyValue)
                rows.Add(rowDict);
        }

        return new SheetData(columnNames, rows);
    }

    private static string CellToString(IXLCell cell)
    {
        if (cell.IsEmpty()) return "";
        try
        {
            return cell.GetFormattedString();
        }
        catch
        {
            return cell.Value.ToString() ?? "";
        }
    }

    // -----------------------------------------------------------------------
    // Data loading helpers
    // -----------------------------------------------------------------------

    public Task<SheetData?> LoadR1DataAsync() => FetchSpreadsheetAsync("r1");
    public Task<SheetData?> LoadP1DataAsync() => FetchSpreadsheetAsync("p1");
    public Task<SheetData?> LoadO1DataAsync() => FetchSpreadsheetAsync("o1");

    // -----------------------------------------------------------------------
    // Organization budget retrieval
    // -----------------------------------------------------------------------

    /// <summary>
    /// Get budget data for a specific organization (synchronous wrapper).
    /// </summary>
    public BudgetData? GetOrganizationBudget(string orgKey)
        => GetOrganizationBudgetAsync(orgKey).GetAwaiter().GetResult();

    /// <summary>
    /// Get budget data for a specific organization.
    /// </summary>
    public async Task<BudgetData?> GetOrganizationBudgetAsync(string orgKey)
    {
        var orgKeyLower = orgKey.ToLowerInvariant();

        double rdteTotal = 0;
        double procurementTotal = 0;
        double omTotal = 0;
        var programElements = new List<Dictionary<string, object>>();

        // Search R-1 data (RDT&E)
        var r1 = await LoadR1DataAsync().ConfigureAwait(false);
        if (r1 is not null)
        {
            var (total, pes) = SearchBudgetSheet(r1, orgKeyLower, "rdte");
            rdteTotal = total;
            programElements.AddRange(pes);
        }

        // Search P-1 data (Procurement)
        var p1 = await LoadP1DataAsync().ConfigureAwait(false);
        if (p1 is not null)
        {
            var (total, pes) = SearchBudgetSheet(p1, orgKeyLower, "procurement");
            procurementTotal = total;
            programElements.AddRange(pes);
        }

        // Search O-1 data (O&M)
        var o1 = await LoadO1DataAsync().ConfigureAwait(false);
        if (o1 is not null)
        {
            var (total, pes) = SearchBudgetSheet(o1, orgKeyLower, "om");
            omTotal = total;
            programElements.AddRange(pes);
        }

        double grandTotal = rdteTotal + procurementTotal + omTotal;

        if (grandTotal == 0)
            return null;

        return new BudgetData
        {
            FiscalYear = _fiscalYear,
            Organization = orgKey,
            RdteMillions = rdteTotal > 0 ? rdteTotal : null,
            ProcurementMillions = procurementTotal > 0 ? procurementTotal : null,
            OmMillions = omTotal > 0 ? omTotal : null,
            TotalMillions = grandTotal,
            ProgramElements = programElements,
        };
    }

    /// <summary>
    /// Get budget data for multiple organizations.
    /// </summary>
    public async Task<Dictionary<string, BudgetData>> GetAllBudgetsAsync(IEnumerable<string> orgKeys)
    {
        // Pre-load all spreadsheets in parallel.
        await Task.WhenAll(LoadR1DataAsync(), LoadP1DataAsync(), LoadO1DataAsync()).ConfigureAwait(false);

        var results = new Dictionary<string, BudgetData>(StringComparer.OrdinalIgnoreCase);

        foreach (var orgKey in orgKeys)
        {
            var budget = await GetOrganizationBudgetAsync(orgKey).ConfigureAwait(false);
            if (budget is not null)
                results[orgKey] = budget;
        }

        return results;
    }

    // -----------------------------------------------------------------------
    // Column detection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Find the FY Total column name for the current fiscal year.
    /// Priority: "FY XXXX Total Amount" > "FY XXXX Total" > any col with year + "TOTAL".
    /// </summary>
    private string? GetFyTotalColumn(SheetData sheet)
    {
        string fyPattern = $"FY {_fiscalYear}";
        string fyPatternCompact = $"FY{_fiscalYear}";
        var candidates = new List<string>();

        foreach (var col in sheet.ColumnNames)
        {
            var colUpper = col.ToUpperInvariant();

            if (colUpper.Contains(fyPattern, StringComparison.OrdinalIgnoreCase)
                || colUpper.Contains(fyPatternCompact, StringComparison.OrdinalIgnoreCase))
            {
                if (colUpper.Contains("TOTAL") && colUpper.Contains("AMOUNT"))
                    return col; // Best match
                if (colUpper.Contains("TOTAL"))
                    candidates.Add(col);
            }
        }

        if (candidates.Count > 0)
            return candidates[0];

        // Fallback: any column with the year and "TOTAL"
        foreach (var col in sheet.ColumnNames)
        {
            var colUpper = col.ToUpperInvariant();
            if (colUpper.Contains(_fiscalYear.ToString()) && colUpper.Contains("TOTAL"))
                return col;
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Multi-strategy budget search
    // -----------------------------------------------------------------------

    /// <summary>
    /// Search a budget sheet for organization entries.
    /// Uses multiple strategies:
    /// 1. Match on "Organization" column code (e.g., DARPA, MDA)
    /// 2. Match on "Account Title" column (e.g., "Space Force")
    /// 3. Fallback text search across all text columns
    /// </summary>
    private (double total, List<Dictionary<string, object>> elements) SearchBudgetSheet(
        SheetData sheet,
        string orgKey,
        string budgetType)
    {
        double total = 0;
        var elements = new List<Dictionary<string, object>>();

        var fyCol = GetFyTotalColumn(sheet);
        if (fyCol is null)
            return (total, elements);

        // Determine which rows match via multi-strategy approach.
        var matchingRows = new List<Dictionary<string, object?>>();

        // Find actual column names (case-insensitive) for Organization and Account Title.
        string? orgColName = FindColumnName(sheet, "Organization");
        string? accountTitleColName = FindColumnName(sheet, "Account Title");

        foreach (var row in sheet.Rows)
        {
            bool matched = false;

            // Strategy 1: Match on Organization column code.
            if (!matched && OrgCodes.TryGetValue(orgKey, out var codes) && codes.Length > 0 && orgColName is not null)
            {
                var orgValue = GetStringValue(row, orgColName).Trim().ToUpperInvariant();
                foreach (var code in codes)
                {
                    if (string.Equals(orgValue, code, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = true;
                        break;
                    }
                }
            }

            // Strategy 2: Match on Account Title.
            if (!matched && OrgAccountTitles.TryGetValue(orgKey, out var titles) && titles.Length > 0 && accountTitleColName is not null)
            {
                var titleValue = GetStringValue(row, accountTitleColName);
                foreach (var title in titles)
                {
                    if (titleValue.Contains(title, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = true;
                        break;
                    }
                }
            }

            // Strategy 3: Fallback text patterns across all text columns.
            if (!matched && OrgTextPatterns.TryGetValue(orgKey, out var patterns) && patterns.Length > 0)
            {
                foreach (var pattern in patterns)
                {
                    foreach (var kvp in row)
                    {
                        if (kvp.Value is string strVal
                            && strVal.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            matched = true;
                            break;
                        }
                    }
                    if (matched) break;
                }
            }

            if (matched)
                matchingRows.Add(row);
        }

        // Process matching rows.
        foreach (var row in matchingRows)
        {
            double amount = GetNumericValue(row, fyCol);
            if (double.IsNaN(amount) || amount <= 0)
                continue;

            // Amounts are in thousands, convert to millions.
            double amountMillions = amount / 1000.0;
            total += amountMillions;

            // Get program element name.
            string peName = "";
            string? peNameCol = FindColumnName(sheet, "Program Element/Budget Line Item (BLI) Title");
            if (peNameCol is not null)
            {
                peName = GetStringValue(row, peNameCol);
            }
            if (string.IsNullOrWhiteSpace(peName) && accountTitleColName is not null)
            {
                peName = GetStringValue(row, accountTitleColName);
            }
            if (peName.Length > 100)
                peName = peName[..100];

            // Try to get PE code from various column names.
            string peCode = "";
            string[] peCodeCandidates =
            [
                "Program Element (PE) Number",
                "PE Number",
                "Budget Line Item (BLI) Number",
                "Line Number",
            ];
            foreach (var candidate in peCodeCandidates)
            {
                var colName = FindColumnName(sheet, candidate);
                if (colName is not null)
                {
                    var val = GetStringValue(row, colName).Trim();
                    if (!string.IsNullOrEmpty(val))
                    {
                        peCode = val;
                        break;
                    }
                }
            }

            elements.Add(new Dictionary<string, object>
            {
                ["type"] = budgetType,
                ["name"] = peName,
                ["pe_code"] = peCode,
                ["amount_millions"] = amountMillions,
                ["fiscal_year"] = _fiscalYear,
            });
        }

        return (total, elements);
    }

    // -----------------------------------------------------------------------
    // Sheet data helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Find a column name in the sheet using case-insensitive match.
    /// </summary>
    private static string? FindColumnName(SheetData sheet, string targetName)
    {
        foreach (var col in sheet.ColumnNames)
        {
            if (string.Equals(col, targetName, StringComparison.OrdinalIgnoreCase))
                return col;
        }
        return null;
    }

    /// <summary>
    /// Get a string value from a row dictionary for a given column name (case-insensitive).
    /// </summary>
    private static string GetStringValue(Dictionary<string, object?> row, string columnName)
    {
        if (row.TryGetValue(columnName, out var val) && val is not null)
            return val.ToString() ?? "";
        return "";
    }

    /// <summary>
    /// Get a numeric value from a row dictionary for a given column name (case-insensitive).
    /// Returns NaN if not found or not numeric.
    /// </summary>
    private static double GetNumericValue(Dictionary<string, object?> row, string columnName)
    {
        if (!row.TryGetValue(columnName, out var val) || val is null)
            return double.NaN;

        if (val is double d) return d;
        if (val is float f) return f;
        if (val is int i) return i;
        if (val is long l) return l;
        if (val is decimal dec) return (double)dec;

        // Try parsing string.
        if (val is string s && double.TryParse(s.Replace(",", ""), out var parsed))
            return parsed;

        return double.NaN;
    }

    // -----------------------------------------------------------------------
    // Formatting
    // -----------------------------------------------------------------------

    /// <summary>
    /// Format budget data as a human-readable funding profile string.
    /// Example: "RDT&amp;E: $4,369M, Procurement: $500M (FY2026 Total: $4,869M)"
    /// </summary>
    public string FormatFundingProfile(BudgetData budget)
    {
        var parts = new List<string>();

        if (budget.RdteMillions.HasValue)
            parts.Add($"RDT&E: ${budget.RdteMillions.Value:N0}M");
        if (budget.ProcurementMillions.HasValue)
            parts.Add($"Procurement: ${budget.ProcurementMillions.Value:N0}M");
        if (budget.OmMillions.HasValue)
            parts.Add($"O&M: ${budget.OmMillions.Value:N0}M");

        if (parts.Count > 0)
        {
            var profile = string.Join(", ", parts);
            profile += $" (FY{budget.FiscalYear} Total: ${budget.TotalMillions!.Value:N0}M)";
            return profile;
        }

        return $"FY{budget.FiscalYear}: ${budget.TotalMillions!.Value:N0}M";
    }

    /// <summary>
    /// Format matched programs as a human-readable string.
    /// Example line: "* AF Multi-Domain... (228) - $3.0M RDTE"
    /// </summary>
    public string FormatProgramMatches(List<ProgramMatch> matches, bool includeScore = false)
    {
        if (matches.Count == 0)
            return "No specific program matches found";

        var lines = new List<string>();

        foreach (var m in matches)
        {
            var line = $"• {m.Name}";
            if (!string.IsNullOrEmpty(m.PeCode))
                line += $" ({m.PeCode})";
            line += $" - ${m.AmountMillions:N1}M {m.BudgetType.ToUpperInvariant()}";
            if (includeScore)
                line += $" [score: {m.MatchScore:P0}]";
            lines.Add(line);
        }

        return string.Join("\n", lines);
    }

    // -----------------------------------------------------------------------
    // Keyword Extraction
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extract meaningful keywords from opportunity title or description.
    /// </summary>
    public static List<string> ExtractKeywords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var normalized = text.ToLowerInvariant();

        // Remove common patterns like solicitation numbers.
        normalized = Regex.Replace(normalized, @"[A-Z]{2}\d{4}[-\w]*", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\d{4}-\d{2}-[A-Z]-\d+", "", RegexOptions.IgnoreCase);

        // Extract words (alphanumeric sequences starting with a letter).
        var wordMatches = Regex.Matches(normalized, @"[a-z][a-z0-9]+");
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match wm in wordMatches)
        {
            var w = wm.Value;
            if (w.Length >= 3 && !Stopwords.Contains(w))
                keywords.Add(w);
        }

        // Also extract multi-word domain keyword phrases if found in text.
        foreach (var domainPhrases in DomainKeywords.Values)
        {
            foreach (var kw in domainPhrases)
            {
                if (kw.Contains(' ') && normalized.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    keywords.Add(kw.Replace(' ', '_'));
            }
        }

        return [.. keywords];
    }

    // -----------------------------------------------------------------------
    // Program-Level Matching
    // -----------------------------------------------------------------------

    /// <summary>
    /// Match an opportunity to relevant program elements.
    /// </summary>
    public List<ProgramMatch> MatchProgramsToOpportunity(
        string opportunityTitle,
        BudgetData? budget,
        double minScore = 0.3,
        int maxMatches = 5)
    {
        if (budget is null || budget.ProgramElements.Count == 0)
            return [];

        var keywords = ExtractKeywords(opportunityTitle);
        if (keywords.Count == 0)
            return [];

        var matches = new List<ProgramMatch>();

        foreach (var pe in budget.ProgramElements)
        {
            var peName = pe.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
            var peCode = pe.TryGetValue("pe_code", out var c) ? c?.ToString() ?? "" : "";
            var peAmount = pe.TryGetValue("amount_millions", out var a) ? Convert.ToDouble(a) : 0.0;
            var peType = pe.TryGetValue("type", out var t) ? t?.ToString() ?? "rdte" : "rdte";

            if (string.IsNullOrWhiteSpace(peName) || peAmount <= 0)
                continue;

            var (score, matchedKw) = CalculateMatchScore(keywords, peName);

            if (score >= minScore)
            {
                matches.Add(new ProgramMatch
                {
                    PeCode = peCode,
                    Name = peName,
                    BudgetType = peType,
                    AmountMillions = peAmount,
                    FiscalYear = _fiscalYear,
                    MatchScore = score,
                    MatchedKeywords = matchedKw,
                });
            }
        }

        // Sort by score descending, then by amount descending.
        matches.Sort((a, b) =>
        {
            int cmp = b.MatchScore.CompareTo(a.MatchScore);
            return cmp != 0 ? cmp : b.AmountMillions.CompareTo(a.AmountMillions);
        });

        return matches.Count > maxMatches ? matches[..maxMatches] : matches;
    }

    /// <summary>
    /// Calculate match score between keywords and a program name.
    /// Returns (score 0-1, list of matched keywords).
    /// </summary>
    private static (double score, List<string> matchedKeywords) CalculateMatchScore(
        List<string> keywords,
        string programName)
    {
        if (keywords.Count == 0 || string.IsNullOrWhiteSpace(programName))
            return (0.0, []);

        var programLower = programName.ToLowerInvariant();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        double score = 0.0;

        foreach (var kw in keywords)
        {
            var kwNormalized = kw.Replace('_', ' ');

            // Direct keyword match.
            if (programLower.Contains(kwNormalized, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(kw);
                score += 0.25;
            }

            // Check domain keyword mappings.
            foreach (var (domain, related) in DomainKeywords)
            {
                if (related.Contains(kwNormalized, StringComparer.OrdinalIgnoreCase)
                    || string.Equals(kw, domain, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var rel in related)
                    {
                        if (programLower.Contains(rel, StringComparison.OrdinalIgnoreCase))
                        {
                            matched.Add($"{kw}->{rel}");
                            score += 0.35; // Higher score for domain match
                            break;
                        }
                    }
                }
            }
        }

        // Bonus for multiple matches.
        if (matched.Count >= 3)
            score += 0.2;
        else if (matched.Count >= 2)
            score += 0.1;

        // Normalize score to 0-1.
        score = Math.Min(1.0, score);

        return (score, [.. matched]);
    }

    // -----------------------------------------------------------------------
    // Internal data structures
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parsed spreadsheet data: column names + list of row dictionaries.
    /// </summary>
    public class SheetData(List<string> columnNames, List<Dictionary<string, object?>> rows)
    {
        public List<string> ColumnNames { get; } = columnNames;
        public List<Dictionary<string, object?>> Rows { get; } = rows;
    }
}

/// <summary>
/// Convenience alias so callers can use <c>new BudgetFetcher(fy)</c>.
/// </summary>
public class BudgetFetcher : DoDBudgetFetcher
{
    public BudgetFetcher(int fiscalYear = 2026, HttpClient? httpClient = null)
        : base(fiscalYear, httpClient)
    {
    }
}
