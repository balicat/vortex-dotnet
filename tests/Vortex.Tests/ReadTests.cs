using System.Runtime.InteropServices;
using Apache.Arrow;
using Parquet;
using Parquet.Data;
using Xunit;

namespace Vortex.Tests;

public class ReadTests
{
    private static readonly Lazy<bool> NativeAvailable = new(() =>
    {
        string? explicitPath = Environment.GetEnvironmentVariable("VORTEX_FFI_PATH");
        if (!string.IsNullOrEmpty(explicitPath))
            return File.Exists(explicitPath);
        return NativeLibrary.TryLoad("vortex_ffi", typeof(VortexFile).Assembly, null, out _);
    });

    private static void RequireNative() =>
        Skip.IfNot(NativeAvailable.Value,
            "vortex_ffi native library not found. Build it with 'cargo build --release -p vortex-ffi' " +
            "in a vortex checkout and set VORTEX_FFI_PATH to the produced library.");

    private static string TestData(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "testdata")))
            dir = dir.Parent;
        if (dir == null)
            throw new FileNotFoundException($"testdata directory not found above {AppContext.BaseDirectory}");
        return Path.Combine(dir.FullName, "testdata", name);
    }

    [SkippableFact]
    public void SmallFile_SchemaIsExpected()
    {
        RequireNative();
        using var file = VortexFile.Open(TestData("pet_rwtc_2024.vortex"));

        Schema schema = file.Schema;
        Assert.Equal(new[] { "series_id", "period", "value" }, schema.FieldsList.Select(f => f.Name));
    }

    [SkippableTheory]
    [InlineData("pet_rwtc_2024", 252)]
    [InlineData("pet_monthly_10y", 65_026)]
    [InlineData("elec_slice", 2_969_849)]
    [InlineData("elec_slice_date", 2_969_849)]
    public void VortexFile_MatchesParquetTwin(string name, int expectedRows)
    {
        RequireNative();
        var vortexRows = ReadVortexRows(TestData($"{name}.vortex"));
        var parquetRows = ReadParquetRows(TestData($"{name}.parquet")).GetAwaiter().GetResult();

        Assert.Equal(expectedRows, parquetRows.Count);
        AssertRowsEqual(parquetRows, vortexRows);
    }

    private static void AssertRowsEqual(
        List<(string SeriesId, string Period, double? Value)> expected,
        List<(string SeriesId, string Period, double? Value)> actual)
    {
        Assert.Equal(expected.Count, actual.Count);

        // Both files were written from the same sorted frame. Sort defensively
        // anyway so the comparison does not depend on scan partition order.
        Comparison<(string SeriesId, string Period, double? Value)> byKey = (a, b) =>
        {
            int c = string.CompareOrdinal(a.SeriesId, b.SeriesId);
            return c != 0 ? c : string.CompareOrdinal(a.Period, b.Period);
        };
        expected.Sort(byKey);
        actual.Sort(byKey);

        for (int i = 0; i < expected.Count; i++)
        {
            if (expected[i] != actual[i])
                Assert.Fail($"row {i}: expected {expected[i]}, got {actual[i]}");
        }
    }

    private static List<(string, string, double?)> ReadVortexRows(string path)
    {
        var rows = new List<(string, string, double?)>();
        using var file = VortexFile.Open(path);
        foreach (RecordBatch batch in file.ReadAll(ordered: true))
        {
            using (batch)
            {
                IArrowArray seriesIds = batch.Column("series_id");
                IArrowArray periods = batch.Column("period");
                var values = (DoubleArray)batch.Column("value");

                for (int i = 0; i < batch.Length; i++)
                    rows.Add((GetString(seriesIds, i)!, GetPeriodString(periods, i)!, values.GetValue(i)));
            }
        }

        return rows;
    }

    private static string? GetString(IArrowArray array, int index) => array switch
    {
        StringArray s => s.GetString(index),
        StringViewArray sv => sv.GetString(index),
        LargeStringArray ls => ls.GetString(index),
        _ => throw new NotSupportedException($"unexpected string array type {array.GetType().Name}"),
    };

    private static string? GetPeriodString(IArrowArray array, int index) => array switch
    {
        Date32Array d => d.GetDateOnly(index)?.ToString("yyyy-MM-dd"),
        _ => GetString(array, index),
    };

    private static async Task<List<(string, string, double?)>> ReadParquetRows(string path)
    {
        var rows = new List<(string, string, double?)>();
        using ParquetReader reader = await ParquetReader.CreateAsync(path);
        var fields = reader.Schema.GetDataFields();

        for (int g = 0; g < reader.RowGroupCount; g++)
        {
            using ParquetRowGroupReader rg = reader.OpenRowGroupReader(g);
            DataColumn seriesCol = await rg.ReadColumnAsync(fields.Single(f => f.Name == "series_id"));
            DataColumn periodCol = await rg.ReadColumnAsync(fields.Single(f => f.Name == "period"));
            DataColumn valueCol = await rg.ReadColumnAsync(fields.Single(f => f.Name == "value"));

            var seriesIds = (string?[])seriesCol.Data;
            System.Array periods = periodCol.Data;
            var values = (double?[])valueCol.Data;

            for (int i = 0; i < seriesIds.Length; i++)
                rows.Add((seriesIds[i]!, PeriodToString(periods.GetValue(i))!, values[i]));
        }

        return rows;
    }

    private static string? PeriodToString(object? value) => value switch
    {
        null => null,
        string s => s,
        DateTime dt => dt.ToString("yyyy-MM-dd"),
        DateOnly d => d.ToString("yyyy-MM-dd"),
        _ => throw new NotSupportedException($"unexpected period type {value.GetType().Name}"),
    };
}
