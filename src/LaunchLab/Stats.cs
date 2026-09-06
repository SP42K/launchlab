// Robust statistics. Medians and MAD, not mean and sigma: one 3x outlier run
// poisons a standard deviation, and startup measurements always have some.
using System;
using System.Collections.Generic;
using System.Linq;

static class Stats
{
    public static double Median(IEnumerable<double> values)
    {
        double[] v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0) return double.NaN;
        int mid = v.Length / 2;
        return v.Length % 2 == 1 ? v[mid] : (v[mid - 1] + v[mid]) / 2.0;
    }

    /// <summary>Median absolute deviation: the spread measure that survives outliers.</summary>
    public static double Mad(IEnumerable<double> values)
    {
        double[] v = values.ToArray();
        if (v.Length == 0) return double.NaN;
        double med = Median(v);
        return Median(v.Select(x => Math.Abs(x - med)));
    }

    /// <summary>Nearest-rank percentile, p in [0,1].</summary>
    public static double Percentile(IEnumerable<double> values, double p)
    {
        double[] v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0) return double.NaN;
        int rank = (int)Math.Ceiling(p * v.Length);
        return v[Math.Clamp(rank - 1, 0, v.Length - 1)];
    }

    public static double Mean(IEnumerable<double> values)
    {
        double[] v = values.ToArray();
        return v.Length == 0 ? double.NaN : v.Average();
    }

    public static double StdDev(IEnumerable<double> values)
    {
        double[] v = values.ToArray();
        if (v.Length < 2) return 0;
        double mean = v.Average();
        return Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Length - 1));
    }

    /// <summary>Coefficient of variation in percent. The run-to-run noise floor of the harness.</summary>
    public static double CvPercent(IEnumerable<double> values)
    {
        double[] v = values.ToArray();
        double mean = Mean(v);
        return mean == 0 ? 0 : StdDev(v) / mean * 100.0;
    }

    /// <summary>
    /// Hodges-Lehmann shift estimate: median of every pairwise (b - a).
    /// Distribution-free, so it does not assume the two arms are normal - they are not.
    /// ponytail: O(n*m), fine at n=20; sample the pairs if a config ever grows past a few thousand runs.
    /// </summary>
    public static double HodgesLehmann(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var diffs = new List<double>(a.Count * b.Count);
        foreach (double x in a)
            foreach (double y in b)
                diffs.Add(y - x);
        return Median(diffs);
    }

    /// <summary>Indices of runs further than 3 MAD from the median - the flaky ones.</summary>
    public static List<int> Outliers(IReadOnlyList<double> values, double madMultiple = 3.0)
    {
        var result = new List<int>();
        if (values.Count < 4) return result;
        double med = Median(values);
        double mad = Mad(values);
        if (mad <= 0) return result; // Degenerate spread: every deviation would look infinite.
        for (int i = 0; i < values.Count; i++)
            if (Math.Abs(values[i] - med) > madMultiple * mad)
                result.Add(i);
        return result;
    }

    public static int SelfTest()
    {
        Check(Median(new double[] { 3, 1, 2 }) == 2, "median odd");
        Check(Median(new double[] { 1, 2, 3, 4 }) == 2.5, "median even");
        Check(Mad(new double[] { 1, 2, 3, 4, 100 }) == 1, "mad ignores the outlier");
        Check(Percentile(Enumerable.Range(1, 100).Select(i => (double)i), 0.95) == 95, "p95 nearest rank");
        Check(HodgesLehmann(new double[] { 1, 2, 3 }, new double[] { 4, 5, 6 }) == 3, "hodges-lehmann shift");
        Check(Math.Abs(CvPercent(new double[] { 10, 10, 10 })) < 1e-9, "cv of a constant is zero");

        // One run 5x the others must be flagged; a tight set must not be.
        var flaky = Outliers(new double[] { 100, 101, 99, 100, 500 });
        Check(flaky.Count == 1 && flaky[0] == 4, "outlier detected at the right index");
        Check(Outliers(new double[] { 100, 101, 99, 100, 102 }).Count == 0, "no false positive on a tight set");

        Console.WriteLine("selftest: all checks passed");
        return 0;
    }

    static void Check(bool condition, string what)
    {
        if (!condition) throw new Exception($"selftest failed: {what}");
    }
}
