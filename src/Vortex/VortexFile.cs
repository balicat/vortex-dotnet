using System.Text;
using Apache.Arrow;
using Apache.Arrow.C;
using Apache.Arrow.Ipc;

namespace Vortex;

/// <summary>
/// Read-only view over one or more Vortex files, exposing their contents as
/// Apache Arrow record batches via the Arrow C Data Interface (zero-copy).
/// </summary>
public sealed unsafe class VortexFile : IDisposable
{
    private IntPtr _session;
    private IntPtr _dataSource;

    private VortexFile(IntPtr session, IntPtr dataSource)
    {
        _session = session;
        _dataSource = dataSource;
    }

    /// <summary>
    /// Opens a Vortex file. The path may also be a glob pattern like "*.vortex".
    /// </summary>
    public static VortexFile Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // The FFI converts bare paths to file:// URLs, where Windows
        // backslashes end up percent-encoded and unusable.
        path = Path.GetFullPath(path).Replace('\\', '/');

        IntPtr session = NativeMethods.vx_session_new();
        if (session == IntPtr.Zero)
            throw new VortexException("failed to create Vortex session");

        byte[] pathBytes = Encoding.UTF8.GetBytes(path);
        IntPtr err = IntPtr.Zero;
        IntPtr dataSource;
        fixed (byte* pathPtr = pathBytes)
        {
            VxView view = new() { Ptr = pathPtr, Len = (nuint)pathBytes.Length };
            VxDataSourceOptions options = new() { Paths = &view, PathsLen = 1 };
            dataSource = NativeMethods.vx_data_source_new(session, &options, &err);
        }

        if (dataSource == IntPtr.Zero)
        {
            NativeMethods.vx_session_free(session);
            throw VortexException.FromNative(err);
        }

        return new VortexFile(session, dataSource);
    }

    /// <summary>
    /// The Arrow schema of the data source.
    /// </summary>
    public Schema Schema
    {
        get
        {
            ThrowIfDisposed();
            IntPtr dtype = NativeMethods.vx_data_source_dtype(_dataSource);
            if (dtype == IntPtr.Zero)
                throw new VortexException("failed to read data source dtype");

            try
            {
                CArrowSchema* cSchema = CArrowSchema.Create();
                try
                {
                    IntPtr err = IntPtr.Zero;
                    if (NativeMethods.vx_dtype_to_arrow_schema(dtype, cSchema, &err) != 0)
                        throw VortexException.FromNative(err);
                    return CArrowSchemaImporter.ImportSchema(cSchema);
                }
                finally
                {
                    CArrowSchema.Free(cSchema);
                }
            }
            finally
            {
                NativeMethods.vx_dtype_free(dtype);
            }
        }
    }

    /// <summary>
    /// Reads the whole data source as a sequence of Arrow record batches.
    /// Each call starts a fresh scan. Pass ordered=true to get rows in
    /// storage order.
    /// </summary>
    public IEnumerable<RecordBatch> ReadAll(bool ordered = false)
    {
        ThrowIfDisposed();

        VxScanOptions options = new() { Ordered = ordered ? (byte)1 : (byte)0 };
        IntPtr err = IntPtr.Zero;
        IntPtr scan = NativeMethods.vx_data_source_scan(_dataSource, &options, IntPtr.Zero, &err);
        if (scan == IntPtr.Zero)
            throw VortexException.FromNative(err);

        return EnumerateBatches(scan);
    }

    private IEnumerable<RecordBatch> EnumerateBatches(IntPtr scan)
    {
        try
        {
            while (true)
            {
                IntPtr partition = NextPartition(scan);
                if (partition == IntPtr.Zero)
                    yield break;

                using IArrowArrayStream stream = ImportPartitionStream(partition);
                while (true)
                {
                    RecordBatch? batch = stream.ReadNextRecordBatchAsync().AsTask().GetAwaiter().GetResult();
                    if (batch == null)
                        break;
                    yield return batch;
                }
            }
        }
        finally
        {
            NativeMethods.vx_scan_free(scan);
        }
    }

    private IntPtr NextPartition(IntPtr scan)
    {
        IntPtr err = IntPtr.Zero;
        IntPtr partition = NativeMethods.vx_scan_next_partition(scan, &err);
        if (partition == IntPtr.Zero && err != IntPtr.Zero)
            throw VortexException.FromNative(err);
        return partition;
    }

    private IArrowArrayStream ImportPartitionStream(IntPtr partition)
    {
        // vx_partition_scan_arrow consumes the partition in every outcome:
        // on success the stream owns it, on failure the callee frees it.
        CArrowArrayStream* cStream = CArrowArrayStream.Create();
        IntPtr err = IntPtr.Zero;
        if (NativeMethods.vx_partition_scan_arrow(_session, partition, cStream, &err) != 0)
        {
            CArrowArrayStream.Free(cStream);
            throw VortexException.FromNative(err);
        }

        return CArrowArrayStreamImporter.ImportArrayStream(cStream);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_dataSource == IntPtr.Zero, this);
    }

    public void Dispose()
    {
        if (_dataSource != IntPtr.Zero)
        {
            NativeMethods.vx_data_source_free(_dataSource);
            _dataSource = IntPtr.Zero;
        }

        if (_session != IntPtr.Zero)
        {
            NativeMethods.vx_session_free(_session);
            _session = IntPtr.Zero;
        }
    }
}
