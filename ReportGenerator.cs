using System.Globalization;
using System.Net;
using System.Text;

namespace ChestsCounter;

public sealed record RewardTier(string Category, double OneInN);

public sealed record DailySummary(
    DateTime Day,
    int Matches,
    double Hours,
    int OwnerOpened,
    int TeammatesOpened
);

public sealed record PlayerSummary(
    string Name,
    bool IsOwner,
    int Matches,
    int Chests
);

public static class ReportGenerator
{
    public static readonly RewardTier[] SeasonRewards =
    {
        new("Gizmos (Utility)", 127),
        new("Weekly Loading Screens", 272),
        new("Weekly Jam Tracks", 391),
        new("Weekly Pickaxes / Back Blings", 568),
        new("Weekly Vehicle & Decals", 7813),
        new("Pirate Shop Access Token", 137500),
        new("Weekly Gold Outfits", 1562500)
    };

    public static void PrintSummary(List<MatchResult> results, DateTime startDate, string htmlPath)
    {
        var okResults = results.Where(r => r.Status == "OK").ToList();
        int totalMatches = results.Count;
        int okMatches = okResults.Count;
        int timeouts = results.Count(r => r.Status == "TIMEOUT");
        int errors = results.Count(r => r.Status != "OK" && r.Status != "TIMEOUT");

        double totalHours = results.Sum(r => r.DurationMs) / 3600000.0;
        int ownerOpened = okResults.Sum(r => r.OwnerOpened);
        int teammatesOpened = okResults.Sum(r => r.TeammatesOpened);
        int teamTotalOpened = ownerOpened + teammatesOpened;

        var daily = BuildDaily(results);
        int activeDays = daily.Count;
        int calendarDays = Math.Max(1, (DateTime.Today - startDate.Date).Days + 1);

        double avgOwnerPerMatch = okMatches > 0 ? (double)ownerOpened / okMatches : 0;
        double avgTeamPerMatch = okMatches > 0 ? (double)teamTotalOpened / okMatches : 0;
        double avgOwnerPerActiveDay = activeDays > 0 ? (double)ownerOpened / activeDays : 0;
        double avgOwnerPerCalendarDay = (double)ownerOpened / calendarDays;
        double avgHoursPerActiveDay = activeDays > 0 ? totalHours / activeDays : 0;
        double avgHoursPerCalendarDay = totalHours / calendarDays;

        Console.WriteLine();
        Console.WriteLine("==================== FORTNITE CHESTS REPORT ====================");
        Console.WriteLine($"Period: {startDate:yyyy-MM-dd} to {DateTime.Today:yyyy-MM-dd} ({calendarDays} calendar days, {activeDays} active days)");
        Console.WriteLine($"Matches analyzed: {totalMatches} total | {okMatches} full parse | {timeouts} timeout (duration only) | {errors} errors");
        Console.WriteLine($"Total play time: {totalHours:F2} h ({totalHours * 60:F0} min)");
        Console.WriteLine($"Average play time: {avgHoursPerActiveDay:F2} h / active day | {avgHoursPerCalendarDay:F2} h / calendar day");
        Console.WriteLine();
        Console.WriteLine("--- CHESTS OPENED ---");
        Console.WriteLine($"You (solo):       {ownerOpened}");
        Console.WriteLine($"Teammates only:   {teammatesOpened}");
        Console.WriteLine($"Team total:       {teamTotalOpened}");
        Console.WriteLine($"Avg per match:    {avgOwnerPerMatch:F2} (you) | {avgTeamPerMatch:F2} (team total)");
        Console.WriteLine($"Avg per day:      {avgOwnerPerActiveDay:F2} / active day | {avgOwnerPerCalendarDay:F2} / calendar day (you)");
        Console.WriteLine();
        Console.WriteLine("--- TREASURE HUNTERS MINI-SEASON REWARDS ---");
        Console.WriteLine("Note: Epic Games stores cosmetic drops server-side, not in .replay files.");
        foreach (var tier in SeasonRewards)
        {
            double p = 1.0 / tier.OneInN;
            double probTeam = (1.0 - Math.Pow(1.0 - p, teamTotalOpened)) * 100.0;
            double expTeam = teamTotalOpened * p;
            Console.WriteLine($"  {tier.Category,-32} | 1 in {tier.OneInN,-9:N0} | Exp: {expTeam:F2} | P(>=1): {probTeam:F1}%");
        }
        Console.WriteLine();
        Console.WriteLine("--- DAILY BREAKDOWN ---");
        foreach (var d in daily)
        {
            Console.WriteLine(
                $"  {d.Day:yyyy-MM-dd} | Matches: {d.Matches,2} | Hours: {d.Hours,5:F2} | You: {d.OwnerOpened,4} | Teammates: {d.TeammatesOpened,4} | Team: {d.OwnerOpened + d.TeammatesOpened,4}");
        }
        var players = BuildPlayerStats(okResults);
        Console.WriteLine();
        Console.WriteLine("--- TEAM PLAYER BREAKDOWN ---");
        foreach (var p in players)
        {
            string role = p.IsOwner ? "You" : "Teammate";
            double share = teamTotalOpened > 0 ? p.Chests * 100.0 / teamTotalOpened : 0;
            Console.WriteLine($"  {p.Name,-24} | {role,-8} | Matches: {p.Matches,3} | Chests: {p.Chests,5} | {share,5:F1}%");
        }
        Console.WriteLine($"  {"Total",-24} | {"Team",-8} | Matches: {okMatches,3} | Chests: {teamTotalOpened,5} | {100.0,5:F1}%");
        Console.WriteLine("================================================================");
        Console.WriteLine($"Chart report saved to: {htmlPath}");
    }

    public static List<PlayerSummary> BuildPlayerStats(List<MatchResult> okResults)
    {
        return okResults
            .SelectMany(r => r.Players)
            .GroupBy(p => p.Key)
            .Select(g => new PlayerSummary(
                g.Last().Name,
                g.Any(x => x.IsOwner),
                g.Count(),
                g.Sum(x => x.Chests)
            ))
            .OrderByDescending(p => p.IsOwner)
            .ThenByDescending(p => p.Chests)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<DailySummary> BuildDaily(List<MatchResult> results)
    {
        return results
            .GroupBy(r => r.Day.Date)
            .OrderBy(g => g.Key)
            .Select(g => new DailySummary(
                g.Key,
                g.Count(),
                g.Sum(x => x.DurationMs) / 3600000.0,
                g.Where(x => x.Status == "OK").Sum(x => x.OwnerOpened),
                g.Where(x => x.Status == "OK").Sum(x => x.TeammatesOpened)
            ))
            .ToList();
    }

    public static void GenerateHtmlReport(List<MatchResult> results, DateTime startDate, string outputPath)
    {
        var okResults = results.Where(r => r.Status == "OK").ToList();
        var daily = BuildDaily(results);

        int totalMatches = results.Count;
        int okMatches = okResults.Count;
        double totalHours = results.Sum(r => r.DurationMs) / 3600000.0;
        int ownerOpened = okResults.Sum(r => r.OwnerOpened);
        int teammatesOpened = okResults.Sum(r => r.TeammatesOpened);
        int teamTotalOpened = ownerOpened + teammatesOpened;
        int activeDays = Math.Max(1, daily.Count);
        int calendarDays = Math.Max(1, (DateTime.Today - startDate.Date).Days + 1);

        var chestSvg = BuildBarChartSvg(
            daily.Select(d => d.Day.ToString("MM-dd", CultureInfo.InvariantCulture)).ToArray(),
            daily.Select(d => (double)d.OwnerOpened).ToArray(),
            daily.Select(d => (double)d.TeammatesOpened).ToArray(),
            "Daily Chests Opened (You vs Teammates)",
            "#38bdf8",
            "#a855f7",
            "You",
            "Teammates"
        );

        var hoursSvg = BuildDualAxisBarChartSvg(
            daily.Select(d => d.Day.ToString("MM-dd", CultureInfo.InvariantCulture)).ToArray(),
            daily.Select(d => d.Hours).ToArray(),
            daily.Select(d => d.Matches).ToArray(),
            "Daily Play Time (Hours) & Matches",
            "#22c55e",
            "#f59e0b",
            "Hours",
            "Matches"
        );

        var rewardRows = new StringBuilder();
        foreach (var tier in SeasonRewards)
        {
            double p = 1.0 / tier.OneInN;
            double probTeam = (1.0 - Math.Pow(1.0 - p, teamTotalOpened)) * 100.0;
            double expTeam = teamTotalOpened * p;
            rewardRows.AppendLine(
                $"<tr><td>{WebUtility.HtmlEncode(tier.Category)}</td><td>1 in {tier.OneInN:N0}</td><td>{expTeam:F2}</td><td>{probTeam:F1}%</td></tr>");
        }

        var dailyRows = new StringBuilder();
        foreach (var d in daily)
        {
            dailyRows.AppendLine(
                $"<tr><td>{d.Day:yyyy-MM-dd}</td><td>{d.Matches}</td><td>{d.Hours:F2} h</td><td>{d.OwnerOpened}</td><td>{d.TeammatesOpened}</td><td>{d.OwnerOpened + d.TeammatesOpened}</td></tr>");
        }

        var players = BuildPlayerStats(okResults);
        var playerRows = new StringBuilder();
        foreach (var p in players)
        {
            string role = p.IsOwner ? "You" : "Teammate";
            double share = teamTotalOpened > 0 ? p.Chests * 100.0 / teamTotalOpened : 0;
            playerRows.AppendLine(
                $"<tr><td>{WebUtility.HtmlEncode(p.Name)}</td><td>{role}</td><td>{p.Matches}</td><td>{p.Chests}</td><td>{share:F1}%</td></tr>");
        }

        string html = $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>Fortnite Chests Counter Report</title>
<style>
  body { font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif; background: #0f172a; color: #e2e8f0; margin: 0; padding: 24px; }
  .wrap { max-width: 1120px; margin: 0 auto; }
  h1 { margin: 0 0 6px; font-size: 28px; color: #f8fafc; }
  .sub { color: #94a3b8; margin-bottom: 24px; font-size: 14px; }
  .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 14px; margin-bottom: 24px; }
  .card { background: #1e293b; border: 1px solid #334155; border-radius: 12px; padding: 16px; }
  .label { font-size: 12px; color: #94a3b8; text-transform: uppercase; letter-spacing: 0.05em; }
  .val { font-size: 26px; font-weight: 700; margin-top: 6px; color: #38bdf8; }
  .hint { font-size: 12px; color: #cbd5e1; margin-top: 4px; }
  .panel { background: #1e293b; border: 1px solid #334155; border-radius: 12px; padding: 20px; margin-bottom: 20px; }
  h2 { margin: 0 0 14px; font-size: 18px; color: #f1f5f9; }
  table { width: 100%; border-collapse: collapse; font-size: 14px; }
  th, td { padding: 10px 12px; border-bottom: 1px solid #334155; text-align: left; }
  th { color: #94a3b8; font-weight: 600; }
  svg { width: 100%; height: auto; display: block; }
</style>
</head>
<body>
<div class="wrap">
  <h1>Fortnite Treasure Hunters - Chests &amp; Playtime Report</h1>
  <div class="sub">Generated {{DateTime.Now:yyyy-MM-dd HH:mm:ss}} | Period: {{startDate:yyyy-MM-dd}} to {{DateTime.Today:yyyy-MM-dd}} | Replays parsed: {{okMatches}}/{{totalMatches}}</div>

  <div class="grid">
    <div class="card">
      <div class="label">Chests Opened (You)</div>
      <div class="val">{{ownerOpened}}</div>
      <div class="hint">{{(okMatches > 0 ? (double)ownerOpened / okMatches : 0):F2}} / match | {{(double)ownerOpened / activeDays:F1}} / active day</div>
    </div>
    <div class="card">
      <div class="label">Chests Opened (Team Total)</div>
      <div class="val" style="color:#a855f7">{{teamTotalOpened}}</div>
      <div class="hint">Teammates: {{teammatesOpened}} | {{(okMatches > 0 ? (double)teamTotalOpened / okMatches : 0):F2}} / match</div>
    </div>
    <div class="card">
      <div class="label">Matches &amp; Play Time</div>
      <div class="val" style="color:#22c55e">{{totalMatches}} / {{totalHours:F2}}h</div>
      <div class="hint">{{totalHours / activeDays:F2}}h / active day | {{totalHours / calendarDays:F2}}h / calendar day</div>
    </div>
  </div>

  <div class="panel">
    {{chestSvg}}
  </div>

  <div class="panel">
    {{hoursSvg}}
  </div>

  <div class="panel">
    <h2>Treasure Hunters Mini-Season Rewards</h2>
    <table>
      <thead><tr><th>Category</th><th>Drop Rate</th><th>Expected Drops</th><th>P(&ge;1 Drop)</th></tr></thead>
      <tbody>{{rewardRows}}</tbody>
    </table>
  </div>

  <div class="panel">
    <h2>Daily Breakdown</h2>
    <table>
      <thead><tr><th>Date</th><th>Matches</th><th>Hours</th><th>You</th><th>Teammates</th><th>Team Total</th></tr></thead>
      <tbody>{{dailyRows}}</tbody>
    </table>
  </div>

  <div class="panel">
    <h2>Team Player Breakdown</h2>
    <table>
      <thead><tr><th>Player</th><th>Role</th><th>Matches</th><th>Chests Opened</th><th>Team Share</th></tr></thead>
      <tbody>{{playerRows}}</tbody>
      <tfoot><tr style="font-weight:700;border-top:2px solid #475569"><td>Total</td><td>Team</td><td>{{okMatches}}</td><td>{{teamTotalOpened}}</td><td>100.0%</td></tr></tfoot>
    </table>
  </div>
</div>
</body>
</html>
""";

        File.WriteAllText(outputPath, html, Encoding.UTF8);
    }

    private static string F(double v, int decimals = 1) =>
        v.ToString($"F{decimals}", CultureInfo.InvariantCulture);

    private static string BuildBarChartSvg(
        string[] labels,
        double[] seriesA,
        double[] seriesB,
        string title,
        string colorA,
        string colorB,
        string nameA,
        string nameB)
    {
        const int width = 960;
        const int height = 320;
        const int padLeft = 52;
        const int padBottom = 42;
        const int padTop = 56;
        const int padRight = 20;

        int n = Math.Max(1, labels.Length);
        double maxVal = 1;
        for (int i = 0; i < labels.Length; i++)
        {
            maxVal = Math.Max(maxVal, seriesA[i] + seriesB[i]);
        }

        double plotW = width - padLeft - padRight;
        double plotH = height - padTop - padBottom;
        double slotW = plotW / n;
        double barW = Math.Max(10, Math.Min(54, slotW * 0.62));

        var sb = new StringBuilder();
        sb.AppendLine($"<svg viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\">");
        sb.AppendLine($"<text x=\"{padLeft}\" y=\"22\" fill=\"#f1f5f9\" font-size=\"15\" font-weight=\"600\">{WebUtility.HtmlEncode(title)}</text>");
        sb.AppendLine($"<rect x=\"{width - 210}\" y=\"10\" width=\"12\" height=\"12\" fill=\"{colorA}\" rx=\"2\"/><text x=\"{width - 192}\" y=\"21\" fill=\"#cbd5e1\" font-size=\"12\">{nameA}</text>");
        sb.AppendLine($"<rect x=\"{width - 120}\" y=\"10\" width=\"12\" height=\"12\" fill=\"{colorB}\" rx=\"2\"/><text x=\"{width - 102}\" y=\"21\" fill=\"#cbd5e1\" font-size=\"12\">{nameB}</text>");

        for (int g = 0; g <= 4; g++)
        {
            double y = padTop + plotH - (plotH * g / 4.0);
            double v = maxVal * g / 4.0;
            sb.AppendLine($"<line x1=\"{padLeft}\" y1=\"{F(y)}\" x2=\"{width - padRight}\" y2=\"{F(y)}\" stroke=\"#334155\" stroke-dasharray=\"3,3\"/>");
            sb.AppendLine($"<text x=\"{padLeft - 8}\" y=\"{F(y + 4)}\" fill=\"#94a3b8\" font-size=\"11\" text-anchor=\"end\">{F(v, 0)}</text>");
        }

        for (int i = 0; i < labels.Length; i++)
        {
            double cx = padLeft + slotW * (i + 0.5);
            double x = cx - barW / 2.0;
            double hA = (seriesA[i] / maxVal) * plotH;
            double hB = (seriesB[i] / maxVal) * plotH;
            double yA = padTop + plotH - hA;
            double yB = yA - hB;

            if (hA > 0)
            {
                sb.AppendLine($"<rect x=\"{F(x)}\" y=\"{F(yA)}\" width=\"{F(barW)}\" height=\"{F(hA)}\" fill=\"{colorA}\" rx=\"2\"/>");
            }
            if (hB > 0)
            {
                sb.AppendLine($"<rect x=\"{F(x)}\" y=\"{F(yB)}\" width=\"{F(barW)}\" height=\"{F(hB)}\" fill=\"{colorB}\" rx=\"2\"/>");
            }
            sb.AppendLine($"<text x=\"{F(cx)}\" y=\"{F(yB - 5)}\" fill=\"#e2e8f0\" font-size=\"11\" text-anchor=\"middle\">{F(seriesA[i] + seriesB[i], 0)}</text>");
            sb.AppendLine($"<text x=\"{F(cx)}\" y=\"{height - 16}\" fill=\"#94a3b8\" font-size=\"11\" text-anchor=\"middle\">{WebUtility.HtmlEncode(labels[i])}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string BuildDualAxisBarChartSvg(
        string[] labels,
        double[] hours,
        int[] matches,
        string title,
        string colorA,
        string colorB,
        string nameA,
        string nameB)
    {
        const int width = 960;
        const int height = 260;
        const int padLeft = 52;
        const int padBottom = 40;
        const int padTop = 56;
        const int padRight = 48;

        int n = Math.Max(1, labels.Length);
        double maxHours = Math.Max(1, hours.Length > 0 ? hours.Max() : 1);
        double maxMatches = Math.Max(1, matches.Length > 0 ? matches.Max() : 1);
        double plotW = width - padLeft - padRight;
        double plotH = height - padTop - padBottom;
        double slotW = plotW / n;
        const double gap = 4;
        double barW = Math.Max(8, Math.Min(34, (slotW * 0.72 - gap) / 2.0));

        var sb = new StringBuilder();
        sb.AppendLine($"<svg viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\">");
        sb.AppendLine($"<text x=\"{padLeft}\" y=\"22\" fill=\"#f1f5f9\" font-size=\"15\" font-weight=\"600\">{WebUtility.HtmlEncode(title)}</text>");
        sb.AppendLine($"<rect x=\"{width - 210}\" y=\"10\" width=\"12\" height=\"12\" fill=\"{colorA}\" rx=\"2\"/><text x=\"{width - 192}\" y=\"21\" fill=\"#cbd5e1\" font-size=\"12\">{nameA}</text>");
        sb.AppendLine($"<rect x=\"{width - 120}\" y=\"10\" width=\"12\" height=\"12\" fill=\"{colorB}\" rx=\"2\"/><text x=\"{width - 102}\" y=\"21\" fill=\"#cbd5e1\" font-size=\"12\">{nameB}</text>");

        for (int g = 0; g <= 4; g++)
        {
            double y = padTop + plotH - (plotH * g / 4.0);
            double vA = maxHours * g / 4.0;
            double vB = maxMatches * g / 4.0;
            sb.AppendLine($"<line x1=\"{padLeft}\" y1=\"{F(y)}\" x2=\"{width - padRight}\" y2=\"{F(y)}\" stroke=\"#334155\" stroke-dasharray=\"3,3\"/>");
            sb.AppendLine($"<text x=\"{padLeft - 8}\" y=\"{F(y + 4)}\" fill=\"{colorA}\" font-size=\"11\" text-anchor=\"end\">{F(vA, 1)}h</text>");
            sb.AppendLine($"<text x=\"{width - padRight + 8}\" y=\"{F(y + 4)}\" fill=\"{colorB}\" font-size=\"11\" text-anchor=\"start\">{F(vB, 0)}</text>");
        }

        for (int i = 0; i < labels.Length; i++)
        {
            double cx = padLeft + slotW * (i + 0.5);
            double xA = cx - barW - gap / 2.0;
            double xB = cx + gap / 2.0;

            double hA = (hours[i] / maxHours) * plotH;
            double yA = padTop + plotH - hA;
            if (hA > 0)
            {
                sb.AppendLine($"<rect x=\"{F(xA)}\" y=\"{F(yA)}\" width=\"{F(barW)}\" height=\"{F(hA)}\" fill=\"{colorA}\" rx=\"3\"/>");
            }
            sb.AppendLine($"<text x=\"{F(xA + barW / 2.0)}\" y=\"{F(yA - 5)}\" fill=\"#e2e8f0\" font-size=\"11\" text-anchor=\"middle\">{F(hours[i], 2)}h</text>");

            double hB = (matches[i] / maxMatches) * plotH;
            double yB = padTop + plotH - hB;
            if (hB > 0)
            {
                sb.AppendLine($"<rect x=\"{F(xB)}\" y=\"{F(yB)}\" width=\"{F(barW)}\" height=\"{F(hB)}\" fill=\"{colorB}\" rx=\"3\"/>");
            }
            sb.AppendLine($"<text x=\"{F(xB + barW / 2.0)}\" y=\"{F(yB - 5)}\" fill=\"#e2e8f0\" font-size=\"11\" text-anchor=\"middle\">{matches[i]}</text>");

            sb.AppendLine($"<text x=\"{F(cx)}\" y=\"{height - 14}\" fill=\"#94a3b8\" font-size=\"11\" text-anchor=\"middle\">{WebUtility.HtmlEncode(labels[i])}</text>");
        }

        sb.AppendLine("</svg>");
        return sb.ToString();
    }
}
