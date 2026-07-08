using System.Diagnostics;
using Apache.Arrow;
using Parquet;
using Vortex;

// Reads each corpus file fully and reports the median wall time, comparing
// the Vortex FFI path against Parquet.Net on the twin file.

string root = FindTestData();
var datasets = new[] { "pet_rwtc_2024", "elec_slice" };
const int iterations = 5;

foreach (string name in datasets)
{
    string vortexPath = Path.Combine(root, $"{name}.vortex");
    string parquetPath = Path.Combine(root, $"{name}.parquet");

    (double vortexMs, long vortexRows) = Median(() => ReadVortex(vortexPath));
    (double parquetMs, long parquetRows) = Median(() => ReadParquet(parquetPath).GetAwaiter().GetResult());

    if (vortexRows != parquetRows)
        throw new InvalidOperationException($"{name}: row mismatch {vortexRows} vs {parquetRows}");

    Console.WriteLine($"{name}: rows={vortexRows:N0} " +
                      $"vortex={vortexMs:F1}ms ({SizeMb(vortexPath):F1} MB) " +
                      $"parquet={parquetMs:F1}ms ({SizeMb(parquetPath):F1} MB)");
}

return;

(double medianMs, long rows) Median(Func<long> read)
{
    read(); // warmup
    var times = new List<double>();
    long rows = 0;
    for (int i = 0; i < iterations; i++)
    {
        var sw = Stopwatch.StartNew();
        rows = read();
        sw.Stop();
        times.Add(sw.Elapsed.TotalMilliseconds);
    }

    times.Sort();
    return (times[times.Count / 2], rows);
}

static long ReadVortex(string path)
{
    long rows = 0;
    using var file = VortexFile.Open(path);
    foreach (RecordBatch batch in file.ReadAll())
    {
        rows += batch.Length;
        batch.Dispose();
    }

    return rows;
}

static async Task<long> ReadParquet(string path)
{
    long rows = 0;
    using ParquetReader reader = await ParquetReader.CreateAsync(path);
    var fields = reader.Schema.GetDataFields();
    for (int g = 0; g < reader.RowGroupCount; g++)
    {
        using ParquetRowGroupReader rg = reader.OpenRowGroupReader(g);
        foreach (var field in fields)
        {
            var column = await rg.ReadColumnAsync(field);
            if (field.Name == "series_id")
                rows += column.Data.Length;
        }
    }

    return rows;
}

static double SizeMb(string path) => new FileInfo(path).Length / 1_000_000.0;

static string FindTestData()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "testdata")))
        dir = dir.Parent;
    if (dir == null)
        throw new DirectoryNotFoundException("testdata not found above " + AppContext.BaseDirectory);
    return Path.Combine(dir.FullName, "testdata");
}
