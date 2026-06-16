using PuppeteerSharp;

namespace OpportunityTracker.Pipeline;

public static class SamGovScraper
{
    private const string SearchUrl =
        "https://sam.gov/search/?page=1&pageSize=50&sort=-modifiedDate"
        + "&sfm%5BsimpleSearch%5D%5BkeywordRadio%5D=ALL"
        + "&sfm%5BsimpleSearch%5D%5BkeywordEditorTextarea%5D="
        + "(darpa OR afrl OR afwerx OR spacewerx OR mda OR isr OR diu OR ussf"
        + " OR ssdp OR rco OR sdacp OR nga OR nro OR nsa OR SpOC OR ssc OR starcom"
        + " OR otti) AND (satellite OR spacecraft OR vleo OR orbital OR orbit"
        + " OR space OR vehicle) OR apfit OR sda OR stratfi OR (space_force)"
        + " OR (Open_Topic)"
        + "&sfm%5Bstatus%5D%5Bis_active%5D=true";

    private const string ScrapeJs = """
        () => {
            var orgs = ['AFRL','RD','RQR','RS','RV','RVB','RVOP','RVSU','RVSV','RVSW',
                'RX','RY','SB','ARMY','DEVCOM','DARPA','DSO','TTO','DIA','DIU',
                'FTS International','In-Q-Tel','MDA','DT','DV','NASA','Ames','Goddard',
                'JPL','NGA','NOAA','NRO','OSC','OSD','OUSD','I&S','R-E','SCO',
                'SpaceWERX','STRIKEWERX','USAF','AFMC','AFLCMC/LPA','HAF','AF','NASIC',
                'SAF','USN','ONR','USSF','HQ','NSIC','SDA','SFC','SPACEFOR-INDOPAC',
                'SpOC','SSDP','SpRCO','SSC','AATS','BC','COMSO','SSIO','SZ','TIDP',
                'STARCOM','TAP Lab','USSOCOM','1st JSOAC','NSW','USSPACECOM','afwerx',
                'rco','nsa','otti'];

            var days = 14;
            var cutoffDate = new Date();
            cutoffDate.setDate(cutoffDate.getDate() - (days + 1));
            cutoffDate.setHours(0, 0, 0, 0);

            var rows = [];
            rows.push(['Opportunity (Linked)', 'Organization', 'Notice Type',
                       'Updated Date', 'Current Response Date', 'Office']);

            document.querySelectorAll('app-opportunity-result').forEach(container => {
                var linkEl = container.querySelector(
                    'a.usa-link[href*="/workspace/contract/opp/"]');
                if (!linkEl) return;
                var url = linkEl.href || '';
                var title = linkEl.textContent.trim() || '';
                var noticeType = '', updatedDate = '', responseDate = '', office = '';
                container.querySelectorAll('.sds-field__name').forEach(el => {
                    var label = el.textContent.trim();
                    var valueEl = el.nextElementSibling;
                    var value = valueEl ? valueEl.textContent.trim() : '';
                    if (label === 'Notice Type') noticeType = value;
                    if (label === 'Updated Date') updatedDate = value;
                    if (label.includes('Response Date')) responseDate = value;
                    if (label === 'Office') office = value;
                });
                var matchedOrgs = [];
                if (office) {
                    var officeLower = office.toLowerCase();
                    orgs.forEach(function(org) {
                        if (officeLower.includes(org.toLowerCase()))
                            matchedOrgs.push(org);
                    });
                }
                var matchedOrgsStr = matchedOrgs.join(', ');
                if (updatedDate) {
                    var itemDate = new Date(updatedDate);
                    itemDate.setHours(0, 0, 0, 0);
                    if (!isNaN(itemDate.getTime()) && itemDate < cutoffDate) return;
                }
                rows.push([url, title, matchedOrgsStr, noticeType,
                           updatedDate, responseDate, office]);
            });

            if (rows.length <= 1) return null;

            var today = new Date();
            var dateStr = (today.getMonth()+1) + '-' + today.getDate() + '-'
                          + String(today.getFullYear()).slice(-2);

            var html = '<!DOCTYPE html><html><head><style>'
                + 'body{background-color:#1e1e1e;color:#e0e0e0;font-family:Arial,'
                + 'sans-serif;margin:20px;}'
                + 'h2{color:#4CAF50;margin-bottom:15px;font-size:20px;}'
                + 'table{border-collapse:collapse;width:100%;font-size:13px;}'
                + 'th,td{border:1px solid #444;padding:4px 8px;text-align:left;}'
                + 'th{background-color:#2d2d2d;color:#4CAF50;font-weight:bold;}'
                + 'tr:nth-child(even){background-color:#252525;}'
                + 'tr:nth-child(odd){background-color:#2a2a2a;}'
                + 'tr:hover{background-color:#333;}'
                + 'a{color:#64b5f6;text-decoration:none;}'
                + 'a:hover{text-decoration:underline;color:#90caf9;}'
                + '</style></head><body>'
                + '<h2>Opportunity Data (Last 14 Days)</h2>'
                + '<table><thead><tr>';

            rows[0].forEach(h => html += '<th>' + h + '</th>');
            html += '</tr></thead><tbody>';
            for (var i = 1; i < rows.length; i++) {
                html += '<tr>'
                    + '<td><a href="' + rows[i][0] + '" target="_blank">'
                    + rows[i][1] + '</a></td>'
                    + '<td>' + rows[i][2] + '</td>'
                    + '<td>' + rows[i][3] + '</td>'
                    + '<td>' + rows[i][4] + '</td>'
                    + '<td>' + rows[i][5] + '</td>'
                    + '<td>' + rows[i][6] + '</td></tr>';
            }
            html += '</tbody></table></body></html>';
            return html;
        }
        """;

    public static async Task<string> FetchAsync(Action<string> log)
    {
        var today = DateTime.Today;
        string dateStr = $"{today.Month}-{today.Day}-{today.Year % 100}";
        string fileName = $"opportunities_14days_{dateStr}.html";
        string downloadsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        string outputPath = Path.Combine(downloadsDir, fileName);

        log("Checking for Chrome/Edge...");
        string? browserPath = FindBrowser();
        if (browserPath == null)
        {
            throw new InvalidOperationException(
                "Could not find Chrome or Edge. Please install one of them.");
        }
        log($"Using browser: {Path.GetFileName(browserPath)}");

        log("Launching headless browser...");
        var launchOptions = new LaunchOptions
        {
            Headless = true,
            ExecutablePath = browserPath,
            Args = ["--no-sandbox", "--disable-gpu"],
        };

        await using var browser = await Puppeteer.LaunchAsync(launchOptions);
        await using var page = await browser.NewPageAsync();

        log("Navigating to SAM.gov search...");
        await page.GoToAsync(SearchUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = 60_000,
        });

        log("Waiting for results to render...");
        try
        {
            await page.WaitForSelectorAsync("app-opportunity-result",
                new WaitForSelectorOptions { Timeout = 60_000 });
            await Task.Delay(3000);
        }
        catch (WaitTaskTimeoutException)
        {
            throw new TimeoutException(
                "Timed out waiting for SAM.gov results. "
                + "The page may require login or have changed layout.");
        }

        log("Scraping opportunity data...");
        var html = await page.EvaluateFunctionAsync<string?>(ScrapeJs);

        if (string.IsNullOrEmpty(html))
        {
            throw new InvalidOperationException(
                "No opportunities matched the 14-day filter.");
        }

        await File.WriteAllTextAsync(outputPath, html);
        log($"Saved: {fileName}");

        return outputPath;
    }

    private static string? FindBrowser()
    {
        string[] candidates =
        [
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }
}
