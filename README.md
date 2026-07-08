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
