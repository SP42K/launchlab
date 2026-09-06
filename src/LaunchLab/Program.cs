// LaunchLab - process startup responsiveness measurement over ETW.
using System;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 1;
        }

        try
        {
            switch (args[0])
            {
                case "analyze": return Analyze.Run(args[1..]);
                case "report": return Report.Run(args[1..]);
                case "compare": return Compare.Run(args[1..]);
                case "spike": return Spike.Run(args[1..]);
                case "selftest": return Stats.SelfTest();
                default: Usage(); return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    static void Usage()
    {
        Console.Error.WriteLine("""
            Usage:
              launchlab analyze <resultsDir>   Extract per-run metrics from every <resultsDir>/*.etl
                                               that has a matching .etl.json sidecar written by run.ps1.
                                               Writes runs.csv and modules.csv.
              launchlab report  <resultsDir>   Turn runs.csv/attrib.csv into report.md + summary.json.
              launchlab compare <baseline.csv> <candidate.csv> [--metric startup_ms]
                                               [--threshold 3.0] [--config <name>]
                                               Regression gate. Exit code 0 = pass, 1 = regression.
              launchlab spike   [--spin-ms N] [--read <file>] | --make <file> [--mb 64]
                                               Negative control workload: inject a known cost.
              launchlab selftest               Run the statistics self-check.
            """);
    }
}
