# vortex-dotnet

A read-only .NET reader for [Vortex](https://github.com/spiraldb/vortex) files. It binds the
Vortex C FFI and hands data to .NET through the Arrow C Data Interface, so a Vortex file comes
back as ordinary `Apache.Arrow` record batches with zero copies along the way.

Vortex is a columnar file format incubating at the Linux Foundation. It has readers for Rust,
Python, Java and C. This project adds .NET to that list.

## Status

Early. The binding is pinned against vortex 0.76.x and the format is pre-1.0. The scope is
deliberately narrow: open a file, read the schema, scan everything to Arrow. Column projection
and predicate pushdown are possible through the same FFI surface and may come later.

## Usage

```csharp
using Vortex;

using var file = VortexFile.Open("data.vortex");

Console.WriteLine(file.Schema);

foreach (var batch in file.ReadAll())
{
    // batch is an Apache.Arrow.RecordBatch
}
```

The native `vortex_ffi` library must be on the default library search path, or pointed at
explicitly with the `VORTEX_FFI_PATH` environment variable. Build it from a vortex checkout:

```sh
cargo build --release -p vortex-ffi
```

## Benchmark

Full-file read to Arrow record batches, median of five warm runs on a Ryzen 7 7840HS.
The comparison is this reader (vortex-ffi 0.76 through the C Data Interface) against
Parquet.Net 5.6.1 reading a Parquet twin written from the same data.

| Dataset | Rows | Vortex size | Parquet size | Vortex read | Parquet.Net read |
|---|---|---|---|---|---|
| PET.RWTC.D 2024 | 252 | 7 KB | 3 KB | 0.5 ms | 0.2 ms |
| PET monthly workload, 849 series, 10 years | 65,026 | 320 KB | 143 KB | **1.3 ms** | 3.5 ms |
| ELEC slice, period as string | 2,969,849 | 16.3 MB | 9.3 MB | **39 ms** | 182 ms |
| ELEC slice, period as date32 | 2,969,849 | 18.2 MB | 9.3 MB | **38 ms** | 146 ms |

That is roughly 76 million rows per second through the binding. Retyping the period column
as date32 barely moves Vortex here: with only 432 distinct periods across three million rows
the string column dictionary-encodes almost to nothing, so there was little left to win.
Parquet.Net does speed up on dates since it no longer materializes three million strings.
Both ELEC files decode through thirteen distinct encodings, which is what makes them good
reader tests. Reproduce with:

```sh
dotnet run --project examples/Bench -c Release
```

## Tests

The test suite compares Vortex files against Parquet twins written from the same data. The
corpus is real energy-market time series with the schema `series_id | period | value`. One
file is small and friendly. The other packs 2.9 million rows across 38,370 series and exercises
thirteen distinct encodings, including ALP, FSST, dictionary, run-end and FastLanes bit-packing.
Tests skip with a clear message when the native library is absent.

```sh
dotnet test
```

## License

Apache-2.0
