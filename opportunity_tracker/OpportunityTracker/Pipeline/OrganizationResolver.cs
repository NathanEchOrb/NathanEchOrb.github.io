using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpportunityTracker.Pipeline;

/// <summary>
/// Enriched organization information resolved from reference data and live budgets.
/// </summary>
public class OrganizationInfo
{
    public string CanonicalName { get; set; } = "";
    public string Acronym { get; set; } = "";
    public string ParentAgency { get; set; } = "";
    public string Mission { get; set; } = "";
    public string FundingProfile { get; set; } = "";
    public double? FyBudgetMillions { get; set; }
    public List<string> NdaaReferences { get; set; } = new();
    public List<string> FocusAreas { get; set; } = new();
    public string? Website { get; set; }
}

/// <summary>
/// Resolves and enriches organization information from SAM.gov opportunity data.
///
/// Resolution strategy chain:
///   1. Solicitation number prefix patterns (FA8650 -> afrl, HR0011 -> darpa, etc.)
///   2. Department/agency name against known aliases
///   3. Office name against known aliases
///   4. Keyword match (word-boundary) in title
///
/// Enrichment pulls canonical org details from an embedded organizations.json resource,
/// optionally augmented by live budget data via a BudgetFetcher instance.
/// </summary>
public class OrganizationResolver
{
    private readonly Dictionary<string, JsonElement> _orgData;
    private readonly Dictionary<string, string> _aliasMap;
    private readonly DoDBudgetFetcher? _budgetFetcher;
    private readonly int _fiscalYear;

    /// <summary>
    /// Initialise the resolver by loading organizations.json from the embedded resources
    /// and optionally wiring up a <see cref="DoDBudgetFetcher"/> for live DoD budget data.
    /// </summary>
    /// <param name="fiscalYear">Fiscal year for budget look-ups (default 2026).</param>
    /// <param name="fetchLiveBudgets">
    /// When true, a <see cref="DoDBudgetFetcher"/> is created for live data.
    /// </param>
    public OrganizationResolver(int fiscalYear = 2026, bool fetchLiveBudgets = true)
    {
        _fiscalYear = fiscalYear;

        // --- Load organisations.json from embedded resource ---
        _orgData = LoadOrganizations();

        // --- Build alias look-up table (lowered alias -> org key) ---
        _aliasMap = BuildAliasMap(_orgData);

        // --- Budget fetcher (optional) ---
        if (fetchLiveBudgets)
        {
            try
            {
                _budgetFetcher = new DoDBudgetFetcher(fiscalYear);
            }
            catch
            {
                _budgetFetcher = null;
            }
        }
    }

    // ------------------------------------------------------------------
    //  Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Determine which organisation posted an opportunity.
    /// Returns the organisation key (e.g. "darpa", "afrl") or null if unknown.
    /// </summary>
    public string? ResolveOrganization(Opportunity opportunity)
    {
        // 1. Solicitation number prefix patterns
        string? fromSol = ParseSolicitationNumber(opportunity.SolicitationNumber ?? "");
        if (fromSol is not null)
            return fromSol;

        // 2 & 3. Department / office fields against aliases
        string dept = (opportunity.DepartmentName ?? "").ToLowerInvariant();
        string office = (opportunity.Office ?? "").ToLowerInvariant();

        foreach (string field in new[] { dept, office })
        {
            if (string.IsNullOrEmpty(field))
                continue;

            foreach (var kvp in _aliasMap)
            {
                if (field.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
        }

        // 4. Keyword / word-boundary match in title
        string title = (opportunity.Title ?? "").ToLowerInvariant();
        if (!string.IsNullOrEmpty(title))
        {
            foreach (var kvp in _aliasMap)
            {
                string pattern = @"\b" + Regex.Escape(kvp.Key) + @"\b";
                if (Regex.IsMatch(title, pattern, RegexOptions.IgnoreCase))
                    return kvp.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Get full organisation information by key.
    /// Combines static reference data with live budget data when available.
    /// </summary>
    public OrganizationInfo? GetOrganizationInfo(string orgKey)
    {
        if (!_orgData.TryGetValue(orgKey, out JsonElement data))
            return null;

        string canonicalName = GetString(data, "canonical_name", orgKey.ToUpperInvariant());
        string acronym = GetString(data, "acronym", orgKey.ToUpperInvariant());
        string parentAgency = GetString(data, "parent_agency", "Department of Defense");
        string mission = GetString(data, "mission", "").Trim();
        string? website = data.TryGetProperty("website", out JsonElement ws)
            ? (ws.ValueKind == JsonValueKind.Null ? null : ws.GetString())
            : null;

        List<string> ndaaRefs = GetStringList(data, "ndaa_references");
        List<string> focusAreas = GetStringList(data, "focus_areas");

        // --- Funding profile ---
        string fundingProfile;
        double? budgetMillions;

        // Try live budget data first
        var liveBudget = GetLiveBudget(orgKey);
        if (liveBudget is not null && _budgetFetcher is not null)
        {
            fundingProfile = _budgetFetcher.FormatFundingProfile(liveBudget);
            budgetMillions = liveBudget.TotalMillions;
        }
        else
        {
            // Fall back to static config data
            var fundingNames = new List<string>();
            if (data.TryGetProperty("funding_sources", out JsonElement sources)
                && sources.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement src in sources.EnumerateArray())
                {
                    string name = GetString(src, "name", "");
                    if (!string.IsNullOrEmpty(name))
                        fundingNames.Add(name);
                }
            }

            budgetMillions = data.TryGetProperty("fy2024_budget_millions", out JsonElement bm)
                && bm.ValueKind == JsonValueKind.Number
                ? bm.GetDouble()
                : null;

            if (budgetMillions.HasValue)
            {
                fundingProfile = $"{string.Join(", ", fundingNames)} (FY24: ${budgetMillions.Value:N0}M)";
            }
            else if (fundingNames.Count > 0)
            {
                fundingProfile = string.Join(", ", fundingNames);
            }
            else
            {
                fundingProfile = "See agency budget documents";
            }
        }

        return new OrganizationInfo
        {
            CanonicalName = canonicalName,
            Acronym = acronym,
            ParentAgency = parentAgency,
            Mission = mission,
            FundingProfile = fundingProfile,
            FyBudgetMillions = budgetMillions,
            NdaaReferences = ndaaRefs,
            FocusAreas = focusAreas,
            Website = website,
        };
    }

    /// <summary>
    /// Add organisation enrichment data to an opportunity in-place.
    /// Sets: ParentAgency, FundingProfile, MatchedPrograms, Mission,
    ///       FocusAreas, NdaaReferences, OrgWebsite.
    /// </summary>
    public void EnrichOpportunity(Opportunity opportunity)
    {
        string? orgKey = ResolveOrganization(opportunity);

        if (orgKey is not null)
        {
            OrganizationInfo? info = GetOrganizationInfo(orgKey);
            if (info is not null)
            {
                opportunity.MatchedOrgs = info.CanonicalName;
                opportunity.ParentAgency = info.ParentAgency;
                opportunity.FundingProfile = info.FundingProfile;
                opportunity.Mission = info.Mission;
                opportunity.FocusAreas = string.Join(", ", info.FocusAreas);
                opportunity.NdaaReferences = string.Join("; ", info.NdaaReferences);
                opportunity.OrgWebsite = info.Website ?? "";

                // Program-level matching via BudgetFetcher
                string programs = MatchPrograms(opportunity, orgKey);
                if (!string.IsNullOrEmpty(programs))
                    opportunity.MatchedPrograms = programs;
            }
            else
            {
                opportunity.MatchedOrgs = orgKey.ToUpperInvariant();
            }
        }
        else
        {
            // Fallback to raw SAM.gov fields
            opportunity.MatchedOrgs = opportunity.DepartmentName ?? "Unknown";
        }
    }

    // ------------------------------------------------------------------
    //  Private helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Parse organisation from solicitation number prefix patterns.
    /// </summary>
    private static string? ParseSolicitationNumber(string solNumber)
    {
        string upper = solNumber.ToUpperInvariant();

        if (upper.StartsWith("HR0011"))
            return "darpa";

        if (upper.StartsWith("FA8650") || upper.StartsWith("FA8750") ||
            upper.StartsWith("FA9550") || upper.StartsWith("FA9451"))
            return "afrl";

        if (upper.StartsWith("HQ0034"))
            return "mda";

        if (upper.Contains("DIU"))
            return "diu";

        if (upper.StartsWith("FA2550") || upper.StartsWith("FA8810"))
            return "ssc";

        if (upper.StartsWith("HM"))
            return "nga";

        return null;
    }

    /// <summary>
    /// Match opportunity to specific budget programmes via DoDBudgetFetcher.
    /// </summary>
    private string MatchPrograms(Opportunity opportunity, string orgKey)
    {
        if (_budgetFetcher is null)
            return "";

        var budget = GetLiveBudget(orgKey);
        if (budget is null || budget.ProgramElements is null || budget.ProgramElements.Count == 0)
            return "";

        string title = opportunity.Title ?? "";
        if (string.IsNullOrEmpty(title))
            return "";

        var matches = _budgetFetcher.MatchProgramsToOpportunity(
            opportunityTitle: title,
            budget: budget,
            minScore: 0.25,
            maxMatches: 3);

        if (matches is null || matches.Count == 0)
            return "";

        return _budgetFetcher.FormatProgramMatches(matches, includeScore: false);
    }

    /// <summary>
    /// Fetch (and cache) live budget data for an organisation key.
    /// Uses Task.Run to bridge the async DoDBudgetFetcher API.
    /// </summary>
    private readonly Dictionary<string, BudgetData?> _budgetCache = new();

    private BudgetData? GetLiveBudget(string orgKey)
    {
        if (_budgetFetcher is null)
            return null;

        if (_budgetCache.TryGetValue(orgKey, out var cached))
            return cached;

        try
        {
            var budget = Task.Run(() => _budgetFetcher.GetOrganizationBudgetAsync(orgKey))
                .GetAwaiter().GetResult();
            _budgetCache[orgKey] = budget;
            return budget;
        }
        catch
        {
            _budgetCache[orgKey] = null;
            return null;
        }
    }

    // ------------------------------------------------------------------
    //  JSON / embedded-resource helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Load the organisations dictionary from the embedded organizations.json resource.
    /// </summary>
    private static Dictionary<string, JsonElement> LoadOrganizations()
    {
        const string resourceName = "OpportunityTracker.Config.organizations.json";

        using Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(resourceName);

        if (stream is null)
            throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found. " +
                "Ensure Config/organizations.json is marked as EmbeddedResource in the .csproj.");

        using var doc = JsonDocument.Parse(stream);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("organizations", out JsonElement orgsElement))
            throw new InvalidOperationException(
                "organizations.json does not contain an 'organizations' property at root.");

        // Clone elements so they outlive the JsonDocument
        var result = new Dictionary<string, JsonElement>();
        foreach (JsonProperty prop in orgsElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.Clone();
        }

        return result;
    }

    /// <summary>
    /// Build a dictionary mapping every alias (lower-cased) to its organisation key.
    /// The org key itself is also mapped.
    /// </summary>
    private static Dictionary<string, string> BuildAliasMap(
        Dictionary<string, JsonElement> orgData)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (orgKey, data) in orgData)
        {
            // Map the key itself
            map[orgKey.ToLowerInvariant()] = orgKey;

            // Map all aliases
            if (data.TryGetProperty("aliases", out JsonElement aliases)
                && aliases.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement alias in aliases.EnumerateArray())
                {
                    string? val = alias.GetString();
                    if (!string.IsNullOrEmpty(val))
                        map[val.ToLowerInvariant()] = orgKey;
                }
            }
        }

        return map;
    }

    private static string GetString(JsonElement element, string property, string defaultValue)
    {
        if (element.TryGetProperty(property, out JsonElement val)
            && val.ValueKind == JsonValueKind.String)
        {
            return val.GetString() ?? defaultValue;
        }

        return defaultValue;
    }

    private static List<string> GetStringList(JsonElement element, string property)
    {
        var list = new List<string>();

        if (element.TryGetProperty(property, out JsonElement arr)
            && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in arr.EnumerateArray())
            {
                string? s = item.GetString();
                if (!string.IsNullOrEmpty(s))
                    list.Add(s);
            }
        }

        return list;
    }
}
