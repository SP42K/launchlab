// Extracts one metrics row per ETL trace, attributed to a single process id,
// plus a long-form attribution table naming the modules, files and processes involved.
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
        var attrib = new List<string>();

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
                r.ImagesLoaded.ToString(CultureInfo.InvariantCulture),
                F(Stats.Median(r.QueueDepths))));

            // Keep the top contributors of each kind. A full dump would be mostly noise.
            foreach (IGrouping<string, KeyValuePair<(string Kind, string Key), Bucket>> byKind
                     in r.Attribution.GroupBy(kv => kv.Key.Kind))
            {
                foreach (KeyValuePair<(string Kind, string Key), Bucket> kv
                         in byKind.OrderByDescending(kv => kv.Value.Ms).ThenByDescending(kv => kv.Value.Count).Take(15))
                {
                    attrib.Add(string.Join(",",
                        Csv(config), run.ToString(CultureInfo.InvariantCulture),
                        Csv(kv.Key.Kind), Csv(kv.Key.Key),
                        kv.Value.Count.ToString(CultureInfo.InvariantCulture),
                        F(kv.Value.Bytes), F(kv.Value.Ms)));
                }
            }
        }

        if (runs.Count == 0)
        {
            Console.Error.WriteLine("no runs analyzed");
            return 1;
        }

        string runsCsv = Path.Combine(dir, "runs.csv");
        string attribCsv = Path.Combine(dir, "attrib.csv");
        WriteLines(runsCsv, "config,run,etl,pid,image,startup_ms,wall_ms,cpu_ms,ready_ms,wait_ms,disk_service_ms,disk_ios,disk_mb,hardfaults,hardfault_io_ms,images_loaded,disk_qd_median", runs);
        WriteLines(attribCsv, "config,run,kind,key,count,bytes,ms", attrib);
        Console.WriteLine($"wrote {runsCsv} ({runs.Count} runs) and {attribCsv} ({attrib.Count} rows)");
        return 0;
    }

    sealed class Bucket
    {
        public long Count;
        public double Bytes;
        public double Ms;
    }

    sealed class RunMetrics
    {
        public string Image;
        public double StartupMs, CpuMs, ReadyMs, WaitMs, DiskServiceMs, DiskMb, HardFaultIoMs;
        public int DiskIos, HardFaults, ImagesLoaded;
        public List<double> QueueDepths = new();
        public Dictionary<(string Kind, string Key), Bucket> Attribution = new();

        public void Add(string kind, string key, double ms = 0, double bytes = 0, long count = 1)
        {
            var k = (kind, string.IsNullOrEmpty(key) ? "<unknown>" : key);
            if (!Attribution.TryGetValue(k, out Bucket b)) Attribution[k] = b = new Bucket();
            b.Count += count;
            b.Bytes += bytes;
            b.Ms += ms;
        }
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
                r.Add("module", s.Image?.FileName, ms);
            }
        }

        if (pScheduling.HasResult)
        {
            foreach (ICpuThreadActivity a in pScheduling.Result.ThreadActivity)
            {
                if (a.Process == null || a.Process.Id != pid) continue;
                double waitMs = Ms(a.WaitingDuration);
                r.ReadyMs += Ms(a.ReadyDuration);
                r.WaitMs += waitMs;
                // Who unblocked this thread? This is WPA's Wait Analysis workflow, done in code.
                if (waitMs > 0) r.Add("readying", a.ReadyingProcess?.ImageName, waitMs);
            }
        }

        if (pDisk.HasResult)
        {
            foreach (IDiskActivity d in pDisk.Result.Activity)
            {
                if (d.IssuingProcess == null || d.IssuingProcess.Id != pid) continue;
                double ms = Ms(d.DiskServiceDuration);
                double bytes = Bytes(d.Size);
                r.DiskIos++;
                r.DiskServiceMs += ms;
                r.DiskMb += bytes / (1024.0 * 1024.0);
                r.QueueDepths.Add(Convert.ToDouble(d.QueueDepthAtInitializeTime));
                // Naming the file is what turns "disk cost 200 ms" into something an owner can act on.
                r.Add($"disk_{Str(d.IOType).ToLowerInvariant()}_file", d.Path ?? d.FileName, ms, bytes);
            }
        }

        if (pFaults.HasResult)
        {
            foreach (IHardFault f in pFaults.Result.Faults)
            {
                if (f.FaultingProcess == null || f.FaultingProcess.Id != pid) continue;
                double ms = Ms(f.IODuration);
                r.HardFaults++;
                r.HardFaultIoMs += ms;
                r.Add("hardfault_file", f.Path ?? f.FileName, ms, Bytes(f.Size));
            }
        }

        return r;
    }

    // The SDK exposes durations/timestamps as several shapes (value or nullable, trace-relative,
    // raw, or plain TimeSpan). Overloads let every call site read the same, whichever shape it is.
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

    static string Str(object o) => o?.ToString() ?? "unknown";

    static string F(double v) => double.IsNaN(v) ? "0.000" : v.ToString("F3", CultureInfo.InvariantCulture);

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
