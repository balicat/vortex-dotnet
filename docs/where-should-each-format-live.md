# Where should each format live?

*Benchmark notes behind the vortex-dotnet release, 2026-07-08.*

> "Vortex is an extensible, state-of-the-art columnar format meant to replace Apache Parquet."

That's the opening line of the Vortex README.

I wrote a .NET reader so Excel could read Vortex files through an XLL, and compared it with
Parquet using the U.S. EIA Petroleum (PET) dataset — 10.3 million rows, 81,422 monthly series, 1920 through 2026.

I also added QlikView's QVD format. I reverse engineered that one a while back and wrote a
.NET [reader and writer](https://github.com/balicat/qvd-dotnet) for it, so all three formats
go through my own code.

| Workload                    |    Vortex |     QVD |     Parquet |
| --------------------------- | --------: | ------: | ----------: |
| One series (10 years)       |  **8 ms** |  226 ms |      463 ms |
| 849-series workbook refresh | **65 ms** |  233 ms |      921 ms |
| Full scan (10.3M rows)      | **75 ms** |  606 ms |      399 ms |
| File size                   |   45.3 MB | 66.9 MB | **20.3 MB** |

Parquet read through Parquet.Net, the standard managed library — the environment Excel
add-ins actually live in. A C++ engine with predicate pushdown closes much of the gap.
.NET does not have one. QVD queries use the format's honest path: resolve the series in the
symbol table once, then compare bit-packed indexes.

The benchmarks tell an interesting story.

Parquet is designed to scan.

Vortex is designed to seek.

QVD, despite being two decades older, sits between the two. Its symbol tables make scans
cheap, but there is no way to skip — one series costs the same as 849.

Without pushdown, Parquet's one-series query costs more than its own full scan. Every
question costs the whole file.

## The file as the data service

If your workload is "give me these 849 time series", a Vortex file on a LAN share can
become the data service.

For scale I ran the same queries through my Arrow Flight server, warm and batched:

| Source                                  | One series (10 years) | 849-series refresh |
| --------------------------------------- | --------------------: | -----------------: |
| Local Vortex file                        |               **8 ms** |          **65 ms** |
| Arrow Flight server, 2.5 GbE LAN         |                  27 ms |              70 ms |
| Arrow Flight server, internet, Cloudflare |                 262 ms |              2.5 s |

The local file ties the LAN server on the big pull, wins 3× on a single series, and needs
no server at all.

Every Excel user reads the same file directly. No database process. No API. No server
restart. Just random-access reads.

Move the same file to S3 and the advantage largely disappears because every seek becomes an
HTTP request. That's where Parquet belongs.

## So

I don't think the question is "Does Vortex replace Parquet?"

I think it's "Where should each format live?"

Parquet is an excellent storage format.

Vortex is the first columnar format I've used that makes a file feel like a database.

---

*Setup: Ryzen 7 7840HS laptop, NVMe, warm cache, medians. Vortex through this repository's
reader over the vortex C FFI (pinned commit past 0.76.0). Parquet through Parquet.Net 5.6.1.
QVD written and read with [qvd-dotnet](https://github.com/balicat/qvd-dotnet). Flight numbers
from a pyarrow 23 client against the production EnergyScope server, one batched call per
query. The 849-series watchlist is `testdata/pet_monthly_series.txt`. The 10.3M-row universe
file is not in the repository — regenerate it from the public EIA bulk data with any Vortex
writer, sorted by series then period.*
