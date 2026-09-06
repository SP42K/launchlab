// Extracts one metrics row per ETL trace, attributed to a single process id.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Disk;
using Microsoft.Windows.EventTracing.Memory;
using Microsoft.Windows.EventTracing.Processes;

static class Analyze
{
    public static int Run(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: launchlab analyze <resultsDir>");
            return 1;
        }

        string dir = Path.GetFullPath(args[0]);
        string[] etls = Directory.GetFiles(dir, "*.etl").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (etls.Length == 0)
        {
            Console.Error.WriteLine($"no .etl files in {dir}");
            return 1;
        }

        var runs = new List<string>();
        var modules = new List<string>();

        foreach (string etl in etls)
        {
            string sidecar = etl + ".json";
            if (!File.Exists(sidecar))
            {
                Console.Error.WriteLine($"skip (no sidecar): {Path.GetFileName(etl)}");
                continue;
            }

            using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(sidecar));
            JsonElement m = meta.RootElement;
            string config = m.GetProperty("config").GetString();
            int run = m.GetProperty("run").GetInt32();
            int pid = m.GetProperty("pid").GetInt32();
            double wallMs = m.TryGetProperty("wall_ms", out JsonElement w) ? w.GetDouble() : 0;

            Console.WriteLine($"analyzing {Path.GetFileName(etl)} (config={config} run={run} pid={pid}) ...");
            RunMetrics r = Measure(etl, pid);
            if (r == null)
            {
                Console.Error.WriteLine($"  pid {pid} not found in trace, skipped");
                continue;
            }

            runs.Add(string.Join(",",
                Csv(config), run.ToString(CultureInfo.InvariantCulture), Csv(Path.GetFileName(etl)),
                pid.ToString(CultureInfo.InvariantCulture), Csv(r.Image),
                F(r.StartupMs), F(wallMs), F(r.CpuMs), F(r.ReadyMs), F(r.WaitMs),
                F(r.DiskServiceMs), r.DiskIos.ToString(CultureInfo.InvariantCulture), F(r.DiskMb),
                r.HardFaults.ToString(CultureInfo.InvariantCulture), F(r.HardFaultIoMs),
                r.ImagesLoaded.ToString(CultureInfo.InvariantCulture)));

            foreach (KeyValuePair<string, double> kv in r.CpuMsByModule.OrderByDescending(kv => kv.Value).Take(15))
            {
                modules.Add(string.Join(",", Csv(config), run.ToString(CultureInfo.InvariantCulture), Csv(kv.Key), F(kv.Value)));
            }
        }

        if (runs.Count == 0)
        {
            Console.Error.WriteLine("no runs analyzed");
            return 1;
        }

        string runsCsv = Path.Combine(dir, "runs.csv");
        string modulesCsv = Path.Combine(dir, "modules.csv");
        WriteLines(runsCsv, "config,run,etl,pid,image,startup_ms,wall_ms,cpu_ms,ready_ms,wait_ms,disk_service_ms,disk_ios,disk_mb,hardfaults,hardfault_io_ms,images_loaded", runs);
        WriteLines(modulesCsv, "config,run,module,cpu_ms", modules);
        Console.WriteLine($"wrote {runsCsv} ({runs.Count} runs) and {modulesCsv}");
        return 0;
    }

    sealed class RunMetrics
    {
        public string Image;
        public double StartupMs, CpuMs, ReadyMs, WaitMs, DiskServiceMs, DiskMb, HardFaultIoMs;
        public int DiskIos, HardFaults, ImagesLoaded;
        public Dictionary<string, double> CpuMsByModule = new(StringComparer.OrdinalIgnoreCase);
    }

    static RunMetrics Measure(string etlPath, int pid)
    {
        var settings = new TraceProcessorSettings
        {
            // Real traces routinely report lost events; refusing to open them is not an option in a lab.
            AllowLostEvents = true,
            AllowTimeInversion = true,
        };

        using ITraceProcessor trace = TraceProcessor.Create(etlPath, settings);
        IPendingResult<IProcessDataSource> pProcesses = trace.UseProcesses();
        IPendingResult<ICpuSampleDataSource> pSamples = trace.UseCpuSamplingData();
        IPendingResult<ICpuSchedulingDataSource> pScheduling = trace.UseCpuSchedulingData();
        IPendingResult<IDiskActivityDataSource> pDisk = trace.UseDiskIOData();
        IPendingResult<IHardFaultDataSource> pFaults = trace.UseHardFaults();

        trace.Process();

        if (!pProcesses.HasResult) return null;

        IProcess process = pProcesses.Result.Processes
            .Where(p => p.Id == pid && p.CreateTime.HasValue)
            .OrderByDescending(p => Ms(p.CreateTime))
            .FirstOrDefault();
        if (process == null) return null;

        var r = new RunMetrics
        {
            Image = process.ImageName,
            ImagesLoaded = process.Images?.Count ?? 0,
            StartupMs = Ms(process.ExitTime) - Ms(process.CreateTime),
        };

        if (pSamples.HasResult)
        {
            foreach (ICpuSample s in pSamples.Result.Samples)
            {
                if (s.Process == null || s.Process.Id != pid) continue;
                double ms = Ms(s.Weight);
                r.CpuMs += ms;
                string module = s.Image?.FileName ?? "<unknown>";
                r.CpuMsByModule.TryGetValue(module, out double acc);
                r.CpuMsByModule[module] = acc + ms;
            }
        }

        if (pScheduling.HasResult)
        {
            foreach (ICpuThreadActivity a in pScheduling.Result.ThreadActivity)
            {
                if (a.Process == null || a.Process.Id != pid) continue;
                r.ReadyMs += Ms(a.ReadyDuration);
                r.WaitMs += Ms(a.WaitingDuration);
            }
        }

        if (pDisk.HasResult)
        {
            foreach (IDiskActivity d in pDisk.Result.Activity)
            {
                if (d.IssuingProcess == null || d.IssuingProcess.Id != pid) continue;
                r.DiskIos++;
                r.DiskServiceMs += Ms(d.DiskServiceDuration);
                r.DiskMb += Bytes(d.Size) / (1024.0 * 1024.0);
            }
        }

        if (pFaults.HasResult)
        {
            foreach (IHardFault f in pFaults.Result.Faults)
            {
                if (f.FaultingProcess == null || f.FaultingProcess.Id != pid) continue;
                r.HardFaults++;
                r.HardFaultIoMs += Ms(f.IODuration);
            }
        }

        return r;
    }

    // The SDK exposes durations/timestamps as four shapes (value or nullable, trace-relative or raw).
    // Overloads let every call site read the same, whichever shape a given property happens to be.
    static double Ms(Duration d) => (double)d.TotalMilliseconds;
    static double Ms(Duration? d) => d.HasValue ? (double)d.Value.TotalMilliseconds : 0;
    static double Ms(TraceDuration d) => (double)d.TotalMilliseconds;
    static double Ms(TraceDuration? d) => d.HasValue ? (double)d.Value.TotalMilliseconds : 0;
    static double Ms(Timestamp t) => (double)t.TotalMilliseconds;
    static double Ms(Timestamp? t) => t.HasValue ? (double)t.Value.TotalMilliseconds : 0;
    static double Ms(TraceTimestamp t) => (double)t.TotalMilliseconds;
    static double Ms(TraceTimestamp? t) => t.HasValue ? (double)t.Value.TotalMilliseconds : 0;
    static double Ms(TimeSpan t) => t.TotalMilliseconds;
    static double Ms(TimeSpan? t) => t?.TotalMilliseconds ?? 0;
    static double Bytes(DataSize s) => (double)s.Bytes;
    static double Bytes(DataSize? s) => s.HasValue ? (double)s.Value.Bytes : 0;

    static string F(double v) => v.ToString("F3", CultureInfo.InvariantCulture);

    static string Csv(string s)
    {
        s ??= "";
        return s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    static void WriteLines(string path, string header, List<string> rows)
    {
        var sb = new StringBuilder();
        sb.Append(header).Append('\n');
        foreach (string row in rows) sb.Append(row).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
