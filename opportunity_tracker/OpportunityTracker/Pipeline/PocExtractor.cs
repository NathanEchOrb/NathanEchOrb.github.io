using System.Text.RegularExpressions;

namespace OpportunityTracker.Pipeline;

/// <summary>
/// Extracts and categorizes point-of-contact information from opportunity data.
/// Identifies technical POCs vs contracting officers based on title keywords.
/// </summary>
public static class PocExtractor
{
    /// <summary>Keywords that indicate a technical POC.</summary>
    private static readonly string[] TechnicalKeywords =
    [
        "technical",
        "program",
        "engineer",
        "scientist",
        "project",
        "subject matter",
        "sme",
        "technical representative",
        "cor",
        "cotr",
        "tpoc",
    ];

    /// <summary>Keywords that indicate a contracting POC.</summary>
    private static readonly string[] ContractingKeywords =
    [
        "contracting",
        "contract",
        "procurement",
        "acquisition",
        "buyer",
        "co ",
        "contracting officer",
        "specialist",
    ];

    /// <summary>
    /// Regex to find email addresses, optionally preceded by a "First Last" name.
    /// Group 1 = name (may be empty), Group 2 = email address.
    /// </summary>
    private static readonly Regex EmailPattern = new(
        @"(?:([A-Z][a-z]+ [A-Z][a-z]+)[,:\s]+)?([a-zA-Z0-9_.+\-]+@[a-zA-Z0-9\-]+\.[a-zA-Z0-9\-.]+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Regex to strip all characters except digits, '+', and 'x' from a phone number.
    /// </summary>
    private static readonly Regex PhoneStripPattern = new(
        @"[^\d+x]",
        RegexOptions.Compiled);

    /// <summary>
    /// Regex to strip everything except digits.
    /// </summary>
    private static readonly Regex DigitsOnlyPattern = new(
        @"[^\d]",
        RegexOptions.Compiled);

    /// <summary>
    /// Enrich an <see cref="Opportunity"/> with POC fields extracted from its
    /// description and any structured POC data. For bookmarklet-ingested data
    /// POC info is typically in a "See solicitation" note, so this may often
    /// leave the fields empty -- that is expected.
    /// </summary>
    public static void EnrichOpportunity(Opportunity opp)
    {
        // The Opportunity model does not carry structured POC sub-objects the
        // way the SAM.gov JSON does. The bookmarklet-sourced data typically
        // stores free-text that may contain emails/names/phones. We try to
        // extract from any text field that might hold POC info.

        var pocs = ExtractFromText(BuildSearchableText(opp));

        // Classify each extracted POC
        foreach (var poc in pocs)
        {
            poc.ContactType = ClassifyPoc(poc);
        }

        // Pick the best technical POC
        var techPoc = pocs.FirstOrDefault(p => p.ContactType == "technical")
                     ?? pocs.FirstOrDefault(p => p.ContactType == "primary")
                     ?? (pocs.Count > 0 ? pocs[0] : null);

        if (techPoc != null)
        {
            opp.TechnicalPocName = techPoc.Name;
            opp.TechnicalPocEmail = techPoc.Email ?? "";
            opp.TechnicalPocPhone = techPoc.Phone ?? "";
            opp.TechnicalPocTitle = techPoc.Title ?? "";
        }
        else
        {
            opp.TechnicalPocName = "See solicitation";
            opp.TechnicalPocEmail = "";
            opp.TechnicalPocPhone = "";
            opp.TechnicalPocTitle = "";
        }
    }

    /// <summary>
    /// Build a single searchable text block from all opportunity fields that
    /// might contain POC information.
    /// </summary>
    private static string BuildSearchableText(Opportunity opp)
    {
        // Concatenate fields that could carry POC information.
        return string.Join(" ",
            opp.Title ?? "",
            opp.Office ?? "",
            opp.DepartmentName ?? "",
            opp.MatchedOrgs ?? "");
    }

    /// <summary>
    /// Extract POC information from unstructured text by matching email
    /// patterns and optional preceding names.
    /// </summary>
    private static List<PocInfo> ExtractFromText(string text)
    {
        var pocs = new List<PocInfo>();
        if (string.IsNullOrWhiteSpace(text))
            return pocs;

        var matches = EmailPattern.Matches(text);
        foreach (Match match in matches)
        {
            string name = match.Groups[1].Value.Trim();
            string email = match.Groups[2].Value;

            // Skip example addresses
            if (email.Contains("example", StringComparison.OrdinalIgnoreCase))
                continue;

            pocs.Add(new PocInfo
            {
                Name = string.IsNullOrWhiteSpace(name) ? "See solicitation" : name,
                Title = null,
                Email = email,
                Phone = null,
                ContactType = "extracted",
            });
        }

        return pocs;
    }

    /// <summary>
    /// Classify a POC as "technical", "contracting", or "primary" based on
    /// title and name keyword matching.
    /// </summary>
    private static string ClassifyPoc(PocInfo poc)
    {
        string titleLower = (poc.Title ?? "").ToLowerInvariant();
        string nameLower = poc.Name.ToLowerInvariant();
        string combined = $"{titleLower} {nameLower}";

        foreach (var keyword in TechnicalKeywords)
        {
            if (combined.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return "technical";
        }

        foreach (var keyword in ContractingKeywords)
        {
            if (combined.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return "contracting";
        }

        return "primary";
    }

    /// <summary>
    /// Clean and format a phone number string.  Attempts to produce
    /// (XXX) XXX-XXXX formatting for 10- or 11-digit US numbers.
    /// </summary>
    internal static string CleanPhone(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return phone;

        string cleaned = PhoneStripPattern.Replace(phone.ToLowerInvariant(), "");
        string digits = DigitsOnlyPattern.Replace(cleaned, "");

        if (digits.Length == 10)
            return $"({digits[..3]}) {digits[3..6]}-{digits[6..]}";

        if (digits.Length == 11 && digits[0] == '1')
            return $"({digits[1..4]}) {digits[4..7]}-{digits[7..]}";

        return phone; // Return original if we cannot format
    }

    /// <summary>
    /// Internal data class used during POC extraction/classification.
    /// </summary>
    private sealed class PocInfo
    {
        public string Name { get; set; } = "";
        public string? Title { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string ContactType { get; set; } = "primary";
    }
}
