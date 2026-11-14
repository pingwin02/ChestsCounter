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
        if (Directory.Exists(demosDir))
        {
            Directory.CreateDirectory(replaysDir);
            int copied = 0;
            foreach (var src in Directory.GetFiles(demosDir, "*.replay"))
            {
                if (ReplayAnalyzer.ParseFileDate(src).Date < startDate)
                {
                    continue;
                }
                string dest = Path.Combine(replaysDir, Path.GetFileName(src));
                try
                {
                    var srcInfo = new FileInfo(src);
                    if (!File.Exists(dest) || new FileInfo(dest).Length != srcInfo.Length)
                    {
                        File.Copy(src, dest, overwrite: true);
                        copied++;
                    }
                }
                catch
                {
                }
            }
            if (copied > 0)
            {
                Console.WriteLine($"Copied {copied} new/updated replay(s) to {replaysDir}");
            }
        }
        else if (!Directory.Exists(replaysDir))
        {
            Console.WriteLine($"Demos directory not found: {demosDir}");
            return;
        }

        var files = Directory.GetFiles(replaysDir, "*.replay")
            .Where(f => ReplayAnalyzer.ParseFileDate(f).Date >= startDate)
            .OrderBy(f => f)
            .ToArray();

        if (files.Length == 0)
        {
            Console.WriteLine($"No replay files found since {startDate:yyyy-MM-dd} in {replaysDir}");
            return;
        }

        Console.WriteLine($"Found {files.Length} replay files since {startDate:yyyy-MM-dd}. Starting analysis...");
        var sw = Stopwatch.StartNew();
        var queue = new ConcurrentQueue<string>(files);
        var results = new ConcurrentBag<MatchResult>();
        string exePath = Environment.ProcessPath!;
        int completed = 0;
        int total = files.Length;
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

                results.Add(res);
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

        var ordered = results.OrderBy(r => r.Day).ThenBy(r => r.FileName).ToList();
        string reportsDir = Path.Combine(Directory.GetCurrentDirectory(), "reports");
        Directory.CreateDirectory(reportsDir);
        string reportName = $"chests_report_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.html";
        string htmlPath = Path.Combine(reportsDir, reportName);
        ReportGenerator.GenerateHtmlReport(ordered, startDate, htmlPath);
        ReportGenerator.PrintSummary(ordered, startDate, htmlPath);
    }
}
