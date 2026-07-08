# vortex-dotnet

A read-only .NET reader for [Vortex](https://github.com/spiraldb/vortex) files. It binds the
Vortex C FFI and hands data to .NET through the Arrow C Data Interface, so a Vortex file comes
back as ordinary `Apache.Arrow` record batches with zero copies along the way.

Vortex is a columnar file format incubating at the Linux Foundation. It ships readers for Rust,
Python, Java and C. This project adds .NET to that list. It is the sequel to
[qvd-dotnet](https://github.com/balicat/qvd-dotnet) — same instinct, friendlier target: where QVD
meant reverse engineering an undocumented binary layout, Vortex publishes its FFI surface and its
FlatBuffers schemas, and the work is binding them well.

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

`Open` also accepts glob patterns like `"*.vortex"`. `ReadAll(ordered: true)` returns rows in
storage order.

For time-series files shaped like `series_id | period | value`, point queries push the series
predicate down into the scan so only matching chunks are decoded:

```csharp
var batches = file.ReadSeries(
    ["PET.RWTC.D", "PET.RBRTE.D"],
    start: new DateOnly(2016, 7, 1));
```

Against a 45 MB file holding 81,422 series and 10.3 million rows, one series with ten years of
history returns in about 8 ms, a five-series pull in under 10 ms, and an 849-series watchlist
(the one in `testdata/pet_monthly_series.txt`) in 65 ms. Period bounds are applied after
decode — the current FFI cannot express a date-typed literal — but the series pushdown is what
prunes the work, so the range costs nothing measurable.

## Status

Early but real: it reads production files and the values check out. The binding is pinned to a
vortex commit just past the 0.76.0 tag — the FFI surface changed right after that release, and
the binding targets the newer shape. The format is pre-1.0, so treat any version bump as a
re-verification of the FFI signatures. The scope is deliberately narrow: open a file, read the schema, scan everything to
Arrow. The FFI also exposes column projection and predicate pushdown through the same scan
surface, so those can come later without redesign.

Tested on win-x64 and linux-x64. 64-bit only.

## The native library

The managed side is a thin binding over `vortex_ffi`, built from a vortex checkout:

```sh
git clone https://github.com/spiraldb/vortex
cd vortex
cargo build --release -p vortex-ffi
```

Put the resulting library (`vortex_ffi.dll` on Windows, `libvortex_ffi.so` on Linux) on the
default search path, or point the `VORTEX_FFI_PATH` environment variable at it.

## Why a .NET reader

The motivating consumer is Excel. [EnergyScope](https://energyscope.io)'s add-in already moves
market data as Arrow record batches over Arrow Flight, and this reader emits exactly the same
type from a local file. That makes a local-data mode a drop-in addition: the add-in can read
Vortex partitions straight from disk at roughly 80 million rows per second, with no server in the
loop. A real ten-year workload of 849 monthly series decodes in about a millisecond. Synced
partition files plus a local reader means the data layer keeps working on a plane, or at a client
site when the tunnel is down.

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
Parquet also compresses these datasets smaller — zstd works harder than Vortex's lightweight
encodings, and pays for it at decode time. Both ELEC files exercise thirteen distinct
encodings, which is what makes them good reader tests. Reproduce with:

```sh
dotnet run --project examples/Bench -c Release
```

## Tests

The test suite compares every Vortex file against a Parquet twin written from the same data. The
corpus is real energy-market time series with the schema `series_id | period | value`, from tiny
and friendly up to 2.9 million rows across 38,370 series. Encodings covered include ALP, FSST,
dictionary, run-end, FastLanes bit-packing and frame-of-reference, zigzag, constant and extension
dates. Tests skip with a clear message when the native library is absent.

```sh
dotnet test
```

## License

Apache-2.0
