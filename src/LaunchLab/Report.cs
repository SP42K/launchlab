// Turns runs.csv / modules.csv into a report a human can act on.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

static class Report
{
    // Diagnostic components. They overlap (disk service time happens inside wait time),
    // so they rank causes, they do not add up to startup_ms. The report says so out loud.
    static readonly string[] Components = { "cpu_ms", "ready_ms", "wait_ms", "disk_service_ms", "hardfault_io_ms" };

    public static int Run(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: launchlab report <resultsDir>");
            return 1;
        }

        string dir = Path.GetFullPath(args[0]);
        List<Dictionary<string, string>> rows = ReadCsv(Path.Combine(dir, "runs.csv"));
        if (rows.Count == 0)
        {
            Console.Error.WriteLine("runs.csv is empty - run 'launchlab analyze' first");
            return 1;
        }

        string attribPath = Path.Combine(dir, "attrib.csv");
        List<Dictionary<string, string>> attribRows = File.Exists(attribPath) ? ReadCsv(attribPath) : new();

        string[] configs = rows.Select(r => r["config"]).Distinct().ToArray();
        string baseline = configs[0];

        var sb = new StringBuilder();
        var summary = new Dictionary<string, object>();

        sb.AppendLine("# LaunchLab report");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} from `runs.csv` ({rows.Count} runs, {configs.Length} configs).");
        sb.AppendLine();

        string envPath = Path.Combine(dir, "env.json");
        if (File.Exists(envPath))
        {
            sb.AppendLine("## Measurement environment");
            sb.AppendLine();
            using JsonDocument env = JsonDocument.Parse(File.ReadAllText(envPath));
            foreach (JsonProperty p in env.RootElement.EnumerateObject())
                sb.AppendLine($"- **{p.Name}**: {p.Value}");
            sb.AppendLine();
            summary["environment"] = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(envPath));
        }

        // --- Startup distribution per config -------------------------------------------------
        sb.AppendLine("## Startup time per config");
        sb.AppendLine();
        sb.AppendLine("`startup_ms` = process create -> process exit, taken from the kernel process events in the trace.");
        sb.AppendLine();
        sb.AppendLine("| config | n | median ms | MAD ms | p95 ms | min ms | max ms | CV % | flaky runs |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---|");

        var startupByConfig = new Dictionary<string, List<double>>();
        var runIdByConfig = new Dictionary<string, List<string>>();
        var configSummaries = new List<Dictionary<string, object>>();

        foreach (string cfg in configs)
        {
            List<Dictionary<string, string>> cfgRows = rows.Where(r => r["config"] == cfg).ToList();
            List<double> startup = cfgRows.Select(r => Num(r, "startup_ms")).ToList();
            startupByConfig[cfg] = startup;
            runIdByConfig[cfg] = cfgRows.Select(r => r["run"]).ToList();

            List<int> flaky = Stats.Outliers(startup);
            string flakyText = flaky.Count == 0 ? "-" : string.Join(", ", flaky.Select(i => "run " + runIdByConfig[cfg][i]));

            sb.AppendLine($"| {cfg} | {startup.Count} | {N(Stats.Median(startup))} | {N(Stats.Mad(startup))} | " +
                          $"{N(Stats.Percentile(startup, 0.95))} | {N(startup.Min())} | {N(startup.Max())} | " +
                          $"{N(Stats.CvPercent(startup))} | {flakyText} |");

            configSummaries.Add(new Dictionary<string, object>
            {
                ["config"] = cfg,
                ["n"] = startup.Count,
                ["median_ms"] = Round(Stats.Median(startup)),
                ["mad_ms"] = Round(Stats.Mad(startup)),
                ["p95_ms"] = Round(Stats.Percentile(startup, 0.95)),
                ["cv_percent"] = Round(Stats.CvPercent(startup)),
                ["flaky_runs"] = flaky.Select(i => runIdByConfig[cfg][i]).ToArray(),
                ["components_median_ms"] = Components.ToDictionary(
                    c => c, c => Round(Stats.Median(rows.Where(r => r["config"] == cfg).Select(r => Num(r, c))))),
            });
        }
        sb.AppendLine();
        summary["configs"] = configSummaries;

        // Wall clock from the harness vs the trace: an independent clock catching analyzer bugs.
        sb.AppendLine("Cross-check against the harness stopwatch (an independent clock; a large gap means the analyzer is measuring the wrong thing):");
        sb.AppendLine();
        sb.AppendLine("| config | median startup_ms (trace) | median wall_ms (stopwatch) |");
        sb.AppendLine("|---|---:|---:|");
        foreach (string cfg in configs)
        {
            double wall = Stats.Median(rows.Where(r => r["config"] == cfg).Select(r => Num(r, "wall_ms")));
            sb.AppendLine($"| {cfg} | {N(Stats.Median(startupByConfig[cfg]))} | {N(wall)} |");
        }
        sb.AppendLine();

        // --- Component medians ---------------------------------------------------------------
        sb.AppendLine("## Where the time goes");
        sb.AppendLine();
        sb.AppendLine("Medians per config. **These components overlap** (disk service time is served while the thread waits), " +
                      "so they rank contributors - they are not an additive budget of `startup_ms`.");
        sb.AppendLine();
        sb.Append("| config |");
        foreach (string c in Components) sb.Append($" {c} |");
        sb.AppendLine(" disk_ios | hardfaults | images_loaded |");
        sb.Append("|---|");
        foreach (string _ in Components) sb.Append("---:|");
        sb.AppendLine("---:|---:|---:|");
        foreach (string cfg in configs)
        {
            IEnumerable<Dictionary<string, string>> cfgRows = rows.Where(r => r["config"] == cfg);
            sb.Append($"| {cfg} |");
            foreach (string c in Components) sb.Append($" {N(Stats.Median(cfgRows.Select(r => Num(r, c))))} |");
            sb.AppendLine($" {N(Stats.Median(cfgRows.Select(r => Num(r, "disk_ios"))))} |" +
                          $" {N(Stats.Median(cfgRows.Select(r => Num(r, "hardfaults"))))} |" +
                          $" {N(Stats.Median(cfgRows.Select(r => Num(r, "images_loaded"))))} |");
        }
        sb.AppendLine();

        // --- Comparisons against the baseline config ------------------------------------------
        var comparisons = new List<Dictionary<string, object>>();
        if (configs.Length > 1)
        {
            sb.AppendLine($"## Comparison against `{baseline}`");
            sb.AppendLine();
            sb.AppendLine("| config | Δ median ms | ratio | Hodges-Lehmann shift ms | biggest component mover |");
            sb.AppendLine("|---|---:|---:|---:|---|");

            foreach (string cfg in configs.Skip(1))
            {
                double baseMed = Stats.Median(startupByConfig[baseline]);
                double cfgMed = Stats.Median(startupByConfig[cfg]);
                double hl = Stats.HodgesLehmann(startupByConfig[baseline], startupByConfig[cfg]);

                var deltas = Components.ToDictionary(
                    c => c,
                    c => Stats.Median(rows.Where(r => r["config"] == cfg).Select(r => Num(r, c)))
                       - Stats.Median(rows.Where(r => r["config"] == baseline).Select(r => Num(r, c))));
                KeyValuePair<string, double> top = deltas.OrderByDescending(kv => Math.Abs(kv.Value)).First();

                sb.AppendLine($"| {cfg} | {N(cfgMed - baseMed)} | {N(baseMed == 0 ? 0 : cfgMed / baseMed)}x | {N(hl)} | " +
                              $"`{top.Key}` {(top.Value >= 0 ? "+" : "")}{N(top.Value)} ms |");

                comparisons.Add(new Dictionary<string, object>
                {
                    ["baseline"] = baseline,
                    ["config"] = cfg,
                    ["delta_median_ms"] = Round(cfgMed - baseMed),
                    ["ratio"] = Round(baseMed == 0 ? 0 : cfgMed / baseMed),
                    ["hodges_lehmann_ms"] = Round(hl),
                    ["component_deltas_ms"] = deltas.ToDictionary(kv => kv.Key, kv => Round(kv.Value)),
                });
            }
            sb.AppendLine();
        }
        summary["comparisons"] = comparisons;

        // --- Flaky run attribution -------------------------------------------------------------
        sb.AppendLine("## Flaky runs");
        sb.AppendLine();
        sb.AppendLine("A run is flagged when it sits further than 3 MAD from its config median. " +
                      "The attribution is that run's component deltas against the same config's medians.");
        sb.AppendLine();

        var flakyList = new List<Dictionary<string, object>>();
        bool anyFlaky = false;
        foreach (string cfg in configs)
        {
            List<Dictionary<string, string>> cfgRows = rows.Where(r => r["config"] == cfg).ToList();
            List<double> startup = startupByConfig[cfg];
            foreach (int i in Stats.Outliers(startup))
            {
                anyFlaky = true;
                double delta = startup[i] - Stats.Median(startup);
                var deltas = Components.ToDictionary(
                    c => c, c => Num(cfgRows[i], c) - Stats.Median(cfgRows.Select(r => Num(r, c))));
                KeyValuePair<string, double> top = deltas.OrderByDescending(kv => Math.Abs(kv.Value)).First();
                double share = delta == 0 ? 0 : top.Value / delta * 100.0;

                sb.AppendLine($"- **{cfg} run {cfgRows[i]["run"]}**: {N(startup[i])} ms, {(delta >= 0 ? "+" : "")}{N(delta)} ms vs median. " +
                              $"Largest mover `{top.Key}` {(top.Value >= 0 ? "+" : "")}{N(top.Value)} ms (~{N(share)}% of the delta).");

                flakyList.Add(new Dictionary<string, object>
                {
                    ["config"] = cfg,
                    ["run"] = cfgRows[i]["run"],
                    ["startup_ms"] = Round(startup[i]),
                    ["delta_vs_median_ms"] = Round(delta),
                    ["top_component"] = top.Key,
                    ["top_component_delta_ms"] = Round(top.Value),
                });
            }
        }
        if (!anyFlaky) sb.AppendLine("- None. Every run sits within 3 MAD of its config median.");
        sb.AppendLine();
        summary["flaky_runs"] = flakyList;

        // --- Attribution: which module, which file, which process ------------------------------
        if (attribRows.Count > 0)
        {
            sb.AppendLine("## Attribution");
            sb.AppendLine();
            sb.AppendLine("Medians across the runs of each config. A key missing from a run counts as zero for that run, " +
                          "so a cost that only appears occasionally does not masquerade as a typical one.");
            sb.AppendLine();

            var attribution = new Dictionary<string, object>();
            var runCount = configs.ToDictionary(c => c, c => rows.Count(r => r["config"] == c));

            foreach ((string kind, string title, string unit, bool movers) in new[]
            {
                ("module", "CPU by module", "median cpu ms", true),
                ("disk_read_file", "Disk read service time by file", "median service ms", true),
                ("disk_write_file", "Disk write service time by file", "median service ms", false),
                ("hardfault_file", "Hard fault I/O by file", "median io ms", false),
                ("readying", "Wait time by readying process", "median wait ms", false),
            })
            {
                List<Dictionary<string, string>> kindRows = attribRows.Where(r => r["kind"] == kind).ToList();
                if (kindRows.Count == 0) continue;

                sb.AppendLine($"### {title}");
                sb.AppendLine();

                var medianByConfigKey = new Dictionary<string, Dictionary<string, double>>();
                foreach (string cfg in configs)
                {
                    var perKey = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    foreach (IGrouping<string, Dictionary<string, string>> g in kindRows.Where(r => r["config"] == cfg).GroupBy(r => r["key"]))
                    {
                        List<double> values = g.Select(r => Num(r, "ms")).ToList();
                        // Pad with zeros for the runs this key never showed up in.
                        while (values.Count < runCount[cfg]) values.Add(0);
                        perKey[g.Key] = Stats.Median(values);
                    }
                    medianByConfigKey[cfg] = perKey;

                    if (perKey.Count == 0) continue;
                    sb.AppendLine($"**{cfg}** - top 10:");
                    sb.AppendLine();
                    sb.AppendLine($"| key | {unit} |");
                    sb.AppendLine("|---|---:|");
                    foreach (KeyValuePair<string, double> kv in perKey.OrderByDescending(kv => kv.Value).Take(10))
                        sb.AppendLine($"| `{kv.Key}` | {N(kv.Value)} |");
                    sb.AppendLine();
                }

                attribution[kind] = medianByConfigKey.ToDictionary(
                    kv => kv.Key,
                    kv => (object)kv.Value.OrderByDescending(x => x.Value).Take(10).ToDictionary(x => x.Key, x => Round(x.Value)));

                if (movers && configs.Length > 1)
                {
                    foreach (string cfg in configs.Skip(1))
                    {
                        IEnumerable<string> allKeys = medianByConfigKey[cfg].Keys
                            .Union(medianByConfigKey[baseline].Keys, StringComparer.OrdinalIgnoreCase);
                        var moved = allKeys
                            .Select(k => (Key: k, Delta: Get(medianByConfigKey[cfg], k) - Get(medianByConfigKey[baseline], k)))
                            .Where(x => Math.Abs(x.Delta) > 0.0001)
                            .OrderByDescending(x => Math.Abs(x.Delta))
                            .Take(10)
                            .ToList();
                        if (moved.Count == 0) continue;

                        sb.AppendLine($"**`{cfg}` vs `{baseline}`** - biggest movers:");
                        sb.AppendLine();
                        sb.AppendLine($"| key | Δ {unit} |");
                        sb.AppendLine("|---|---:|");
                        foreach ((string key, double d) in moved)
                            sb.AppendLine($"| `{key}` | {(d >= 0 ? "+" : "")}{N(d)} |");
                        sb.AppendLine();

                        Dictionary<string, object> c = comparisons.FirstOrDefault(x => (string)x["config"] == cfg);
                        if (c != null) c["top_" + kind + "_movers"] = moved.ToDictionary(x => x.Key, x => Round(x.Delta));
                    }
                }
            }

            summary["attribution"] = attribution;
        }

        string reportPath = Path.Combine(dir, "report.md");
        File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));

        string summaryPath = Path.Combine(dir, "summary.json");
        File.WriteAllText(summaryPath,
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        Console.WriteLine($"wrote {reportPath} and {summaryPath}");
        return 0;
    }

    static double Get(Dictionary<string, double> d, string key) => d.TryGetValue(key, out double v) ? v : 0;

    static double Num(Dictionary<string, string> row, string col) =>
        row.TryGetValue(col, out string s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    static string N(double v) => double.IsNaN(v) ? "-" : v.ToString("F1", CultureInfo.InvariantCulture);

    static double Round(double v) => double.IsNaN(v) ? 0 : Math.Round(v, 2);

    static List<Dictionary<string, string>> ReadCsv(string path)
    {
        var result = new List<Dictionary<string, string>>();
        if (!File.Exists(path)) return result;
        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 2) return result;
        string[] header = SplitCsv(lines[0]);
        foreach (string line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            string[] cells = SplitCsv(line);
            var row = new Dictionary<string, string>();
            for (int i = 0; i < header.Length && i < cells.Length; i++) row[header[i]] = cells[i];
            result.Add(row);
        }
        return result;
    }

    static string[] SplitCsv(string line)
    {
        var cells = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cur.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { cells.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        cells.Add(cur.ToString());
        return cells.ToArray();
    }
}
