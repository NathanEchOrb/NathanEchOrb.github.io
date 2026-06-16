namespace OpportunityTracker.Pipeline;

public class Opportunity
{
    public string Title { get; set; } = "";
    public string UiLink { get; set; } = "";
    public string MatchedOrgs { get; set; } = "";
    public string DepartmentName { get; set; } = "";
    public string Type { get; set; } = "";
    public string NoticeTypeDisplay { get; set; } = "";
    public string PostedDate { get; set; } = "";
    public string ResponseDeadline { get; set; } = "";
    public string Office { get; set; } = "";
    public string SolicitationNumber { get; set; } = "";

    // Enriched fields
    public string ParentAgency { get; set; } = "";
    public string FundingProfile { get; set; } = "";
    public string MatchedPrograms { get; set; } = "";
    public string Mission { get; set; } = "";
    public string FocusAreas { get; set; } = "";
    public string NdaaReferences { get; set; } = "";
    public string TechnicalPocName { get; set; } = "";
    public string TechnicalPocEmail { get; set; } = "";
    public string TechnicalPocPhone { get; set; } = "";
    public string TechnicalPocTitle { get; set; } = "";
    public string OrgWebsite { get; set; } = "";
}
