using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ChestsCounter;

public static class Program
{
    private static Process StartWorker(string exePath)
    {
        var psi = new ProcessStartInfo(exePath, new[] { "--worker-loop" })
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        return Process.Start(psi)!;
    }

    private static void PrintProgress(int done, int total, double elapsedSec, string fileName, string status)
    {
        int pct = total > 0 ? (int)Math.Round(done * 100.0 / total) : 100;
        const int barWidth = 24;
        int filled = total > 0 ? (int)Math.Round(done * (double)barWidth / total) : barWidth;
        string bar = new string('#', filled) + new string('-', barWidth - filled);
        Console.WriteLine($"[{done,3}/{total,3}] {pct,3}% [{bar}] {elapsedSec,5:F1}s | {status,-7} | {fileName}");
        Console.Out.Flush();
    }

    public static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--worker-loop")
        {
            string? line;
            while ((line = Console.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                var res = ReplayAnalyzer.AnalyzeFile(line);
                Console.WriteLine(JsonSerializer.Serialize(res));
                Console.Out.Flush();
            }
            return;
        }

        DateTime startDate = new DateTime(2026, 11, 1);
        string demosDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FortniteGame",
            "Saved",
            "Demos"
        );

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--since" && DateTime.TryParseExact(args[i + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                startDate = parsedDate.Date;
            }
            else if (args[i] == "--demos")
            {
                demosDir = args[i + 1];
            }
        }

        string replaysDir = Path.Combine(Directory.GetCurrentDirectory(), "replays");
        Directory.CreateDirectory(replaysDir);

        var cachedByFile = new Dictionary<string, MatchResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var jsonFile in Directory.GetFiles(replaysDir, "*.json"))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<MatchResult>(File.ReadAllText(jsonFile));
                if (cached != null)
                {
                    cachedByFile[cached.FileName] = cached;
                }
            }
            catch
            {
            }
        }

        var toAnalyze = new List<string>();
        if (Directory.Exists(demosDir))
        {
            foreach (var src in Directory.GetFiles(demosDir, "*.replay").OrderBy(f => f))
            {
                if (ReplayAnalyzer.ParseFileDate(src).Date < startDate)
                {
                    continue;
                }
                string fileName = Path.GetFileName(src);
                long srcLen = 0;
                try { srcLen = new FileInfo(src).Length; } catch { }
                if (cachedByFile.TryGetValue(fileName, out var existing) &&
                    existing.Status == "OK" &&
                    (existing.FileLength == 0 || existing.FileLength == srcLen))
                {
                    continue;
                }
                toAnalyze.Add(src);
            }
        }
        else if (cachedByFile.Count == 0)
        {
            Console.WriteLine($"Demos directory not found: {demosDir}");
            return;
        }

        Console.WriteLine($"Cached replays: {cachedByFile.Count} | New/updated to analyze: {toAnalyze.Count}");

        if (toAnalyze.Count > 0)
        {
            var sw = Stopwatch.StartNew();
            var queue = new ConcurrentQueue<string>(toAnalyze);
            var freshResults = new ConcurrentBag<MatchResult>();
            string exePath = Environment.ProcessPath!;
            int completed = 0;
            int total = toAnalyze.Count;
            int workerCount = Math.Min(4, Math.Max(1, Environment.ProcessorCount / 2));

            var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(() =>
            {
                Process? proc = null;
                while (queue.TryDequeue(out var file))
                {
                    proc ??= StartWorker(exePath);
                    proc.StandardInput.WriteLine(file);
                    proc.StandardInput.Flush();

                    var readTask = proc.StandardOutput.ReadLineAsync();
                    MatchResult res;
                    if (readTask.Wait(TimeSpan.FromSeconds(8)) && readTask.Result != null)
                    {
                        res = JsonSerializer.Deserialize<MatchResult>(readTask.Result)
                            ?? new MatchResult(Path.GetFileName(file), ReplayAnalyzer.ParseFileDate(file).Date, 0, 0, 0, "ERR", new List<PlayerChestStat>());
                    }
                    else
                    {
                        try { proc.Kill(true); } catch { }
                        proc.Dispose();
                        proc = null;
                        var fb = ReplayAnalyzer.ReadHeaderFallback(file);
                        res = new MatchResult(Path.GetFileName(file), fb.Day, fb.DurationMs, 0, 0, "TIMEOUT", new List<PlayerChestStat>());
                    }

                    freshResults.Add(res);
                    int done = Interlocked.Increment(ref completed);
                    lock (Console.Out)
                    {
                        PrintProgress(done, total, sw.Elapsed.TotalSeconds, res.FileName, res.Status);
                    }
                }

                if (proc != null)
                {
                    try { proc.Kill(true); } catch { }
                    proc.Dispose();
                }
            })).ToArray();

            Task.WaitAll(workers);

            foreach (var res in freshResults)
            {
                cachedByFile[res.FileName] = res;
                if (res.Status == "OK")
                {
                    string jsonPath = Path.Combine(replaysDir, Path.GetFileNameWithoutExtension(res.FileName) + ".json");
                    File.WriteAllText(jsonPath, JsonSerializer.Serialize(res));
                }
            }
        }

        var ordered = cachedByFile.Values
            .Where(r => r.Day.Date >= startDate)
            .OrderBy(r => r.Day)
            .ThenBy(r => r.FileName)
            .ToList();

        if (ordered.Count == 0)
        {
            Console.WriteLine($"No replay data found since {startDate:yyyy-MM-dd}.");
            return;
        }
        string reportsDir = Path.Combine(Directory.GetCurrentDirectory(), "reports");
        Directory.CreateDirectory(reportsDir);
        string reportName = $"chests_report_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.html";
        string htmlPath = Path.Combine(reportsDir, reportName);
        ReportGenerator.GenerateHtmlReport(ordered, startDate, htmlPath);
        ReportGenerator.PrintSummary(ordered, startDate, htmlPath);
    }
}
