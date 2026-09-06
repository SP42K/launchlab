// Regression gate. Not "compare these two arms I just ran", but "is this run a regression
// against a stored baseline, or is it noise?" - and it answers with an exit code, so it
// can sit in a pipeline rather than in a human's eyeballs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

static class Compare
{
    public static int Run(string[] args)
    {
        string metric = "startup_ms";
        double threshold = 3.0;
        string config = null;
        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--metric": metric = args[++i]; break;
                case "--threshold": threshold = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--config": config = args[++i]; break;
                default: positional.Add(args[i]); break;
            }
        }

        if (positional.Count != 2)
        {
            Console.Error.WriteLine("""
                Usage: launchlab compare <baseline-runs.csv> <candidate-runs.csv>
                                         [--metric startup_ms] [--threshold 3.0] [--config <name>]

                Exit code 0 = PASS (within the baseline's own noise), 1 = FAIL (regression).
                """);
            return 2;
        }

        List<double> baseline = Load(positional[0], metric, config);
        List<double> candidate = Load(positional[1], metric, config);
        if (baseline.Count < 4 || candidate.Count < 4)
        {
            Console.Error.WriteLine($"need at least 4 runs on each side (got {baseline.Count} and {candidate.Count})");
            return 2;
        }

        double baseMed = Stats.Median(baseline);
        double candMed = Stats.Median(candidate);
        double shift = Stats.HodgesLehmann(baseline, candidate);

        // Scale the shift by the baseline's OWN run-to-run noise, measured with MAD rather than
        // a standard deviation: the outlier runs a startup benchmark always produces would
        // inflate sigma and hide exactly the regressions this gate exists to catch.
        // 1.4826 x MAD estimates sigma for a normal distribution, which keeps the threshold
        // readable in familiar units without assuming normality for the shift itself.
        double noise = 1.4826 * Stats.Mad(baseline);
        bool degenerate = noise <= 0;
        if (degenerate) noise = Math.Max(baseMed * 0.02, 1e-9); // fall back to 2% of the median

        double effect = shift / noise;
        bool fail = Math.Abs(effect) > threshold;

        Console.WriteLine($"metric        {metric}{(config == null ? "" : $"  (config {config})")}");
        Console.WriteLine($"baseline      n={baseline.Count,-4} median {baseMed,9:F1} ms   MAD {Stats.Mad(baseline),6:F1} ms");
        Console.WriteLine($"candidate     n={candidate.Count,-4} median {candMed,9:F1} ms   MAD {Stats.Mad(candidate),6:F1} ms");
        Console.WriteLine($"noise band    {noise,9:F1} ms  (1.4826 x baseline MAD{(degenerate ? ", fell back to 2% of median" : "")})");
        Console.WriteLine($"shift         {shift,9:F1} ms  (Hodges-Lehmann)");
        Console.WriteLine($"effect        {effect,9:F2} x noise   threshold {threshold:F1}");
        Console.WriteLine();

        string direction = shift >= 0 ? "slower" : "faster";
        Console.WriteLine(fail
            ? $"FAIL: median {(candMed - baseMed >= 0 ? "+" : "")}{candMed - baseMed:F0} ms " +
              $"(shift {shift:+0;-0} ms), {Math.Abs(effect):F1}x the baseline noise band - {direction} beyond noise."
            : $"PASS: shift {shift:+0;-0} ms is {Math.Abs(effect):F1}x the baseline noise band, within the {threshold:F1}x threshold.");

        return fail ? 1 : 0;
    }

    static List<double> Load(string path, string metric, string config)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"not found: {path}");
        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 2) throw new Exception($"no data rows in {path}");

        string[] header = lines[0].Split(',');
        int metricIdx = Array.IndexOf(header, metric);
        if (metricIdx < 0) throw new Exception($"no column '{metric}' in {path}");
        int configIdx = Array.IndexOf(header, "config");

        var values = new List<double>();
        var configs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            string[] cells = line.Split(',');
            if (cells.Length <= metricIdx) continue;
            string rowConfig = configIdx >= 0 && configIdx < cells.Length ? cells[configIdx] : "";
            configs.Add(rowConfig);
            if (config != null && !string.Equals(rowConfig, config, StringComparison.OrdinalIgnoreCase)) continue;
            if (double.TryParse(cells[metricIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                values.Add(v);
        }

        if (config == null && configs.Count > 1)
            throw new Exception($"{path} holds {configs.Count} configs ({string.Join(", ", configs)}); " +
                                "pass --config to pick one, otherwise the comparison mixes them.");
        return values;
    }
}
