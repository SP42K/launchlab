// Negative control. Injects a known amount of work INSIDE the measured process, so the
// analyzer's attribution can be checked against a cost whose size is known in advance.
//
// The injection has to happen in the workload itself: work done by the runner would be
// charged to the runner's process, which the analyzer correctly ignores, and the test
// would pass by measuring nothing.
using System;
using System.Diagnostics;
using System.IO;

static class Spike
{
    public static int Run(string[] args)
    {
        double spinMs = 0;
        string readPath = null;
        string writePath = null;
        string makePath = null;
        int mb = 64;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--spin-ms": spinMs = double.Parse(args[++i]); break;
                case "--read": readPath = args[++i]; break;
                case "--write": writePath = args[++i]; break;
                case "--make": makePath = args[++i]; break;
                case "--mb": mb = int.Parse(args[++i]); break;
                default:
                    Console.Error.WriteLine("""
                        Usage: launchlab spike [--spin-ms N] [--read <file>] [--write <file> --mb N]
                               launchlab spike --make <file> [--mb 64]

                        --spin-ms  burn N ms of CPU in this process
                        --read     read a file end to end in this process
                        --write    write --mb megabytes and flush them to the device, which
                                   produces disk service time of a known size
                        --make     create a file of incompressible bytes to read later

                        The paths can be combined, which is the interesting case: it checks
                        that the analyzer separates two simultaneous costs rather than only
                        attributing one at a time.
                        """);
                    return 2;
            }
        }

        if (makePath != null)
        {
            WriteMegabytes(makePath, mb);
            Console.WriteLine($"wrote {mb} MB to {makePath}");
            return 0;
        }

        long sink = 0;

        // Write first: flushing to the device is the part with a known, real disk cost.
        if (writePath != null) WriteMegabytes(writePath, mb);

        if (readPath != null)
        {
            var buffer = new byte[64 * 1024];
            using FileStream fs = new(readPath, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length,
                                      FileOptions.SequentialScan);
            int n;
            while ((n = fs.Read(buffer, 0, buffer.Length)) > 0) sink += buffer[n - 1];
        }

        if (spinMs > 0)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalMilliseconds < spinMs) sink += sw.ElapsedTicks & 1;
        }

        // Consume the sink so neither loop can be optimised away.
        if (sink == long.MinValue) Console.WriteLine(sink);
        return 0;
    }

    static void WriteMegabytes(string path, int megabytes)
    {
        var rng = new Random(1);
        var buffer = new byte[1024 * 1024];
        using FileStream fs = File.Create(path);
        for (int i = 0; i < megabytes; i++)
        {
            rng.NextBytes(buffer); // incompressible, so no filesystem compression shortcut
            fs.Write(buffer, 0, buffer.Length);
        }
        fs.Flush(true); // to the device, not just the cache: otherwise there is no disk cost to find
    }
}
