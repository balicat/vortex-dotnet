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

    /// <summary>
    /// Reads one series, optionally restricted to a period range. The filter is
    /// pushed down into the Vortex scan, so only matching chunks are decoded.
    /// </summary>
    public IEnumerable<RecordBatch> ReadSeries(
        string seriesId, DateOnly? start = null, DateOnly? end = null,
        string seriesColumn = "series_id", string periodColumn = "period")
        => ReadSeries(new[] { seriesId }, start, end, seriesColumn, periodColumn);

    /// <summary>
    /// Reads a set of series, optionally restricted to a period range. The series
    /// list is pushed down into the Vortex scan (chunk pruning), while date bounds
    /// are applied as zero-copy slices of the returned batches — the 0.76 FFI has
    /// no way to express a date-typed literal for an extension column yet.
    /// </summary>
    public IEnumerable<RecordBatch> ReadSeries(
        IReadOnlyCollection<string> seriesIds, DateOnly? start = null, DateOnly? end = null,
        string seriesColumn = "series_id", string periodColumn = "period")
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(seriesIds);
        if (seriesIds.Count == 0)
            throw new ArgumentException("at least one series id is required", nameof(seriesIds));

        IntPtr scan;
        using (var builder = new FilterBuilder())
        {
            IntPtr filter = builder.SeriesPredicate(seriesIds, seriesColumn);
            VxScanOptions options = new() { Filter = filter, Ordered = 1 };
            IntPtr err = IntPtr.Zero;
            scan = NativeMethods.vx_data_source_scan(_dataSource, &options, IntPtr.Zero, &err);
            if (scan == IntPtr.Zero)
                throw VortexException.FromNative(err);
            // The scan clones the filter, so the builder can free everything now.
        }

        IEnumerable<RecordBatch> batches = EnumerateBatches(scan);
        return start == null && end == null
            ? batches
            : FilterByPeriod(batches, periodColumn, start, end);
    }

    /// <summary>
    /// Keeps only rows whose period falls inside [start, end]. Rows are copied
    /// into fresh managed batches: the imported batches own FFI memory, so
    /// zero-copy slices could not outlive them safely. After the series pushdown
    /// the surviving row counts are small, so the copy is cheap. String-view
    /// columns are normalized to utf8 in the output.
    /// </summary>
    private static IEnumerable<RecordBatch> FilterByPeriod(
        IEnumerable<RecordBatch> batches, string periodColumn, DateOnly? start, DateOnly? end)
    {
        foreach (RecordBatch batch in batches)
        {
            using (batch)
            {
                if (batch.Column(periodColumn) is not Date32Array periods)
                    throw new NotSupportedException(
                        $"period filtering requires a date32 column, but '{periodColumn}' is " +
                        $"{batch.Column(periodColumn).GetType().Name}");

                var keep = new List<int>();
                for (int i = 0; i < batch.Length; i++)
                {
                    DateOnly? d = periods.GetDateOnly(i);
                    if (d != null && (start == null || d >= start) && (end == null || d <= end))
                        keep.Add(i);
                }

                if (keep.Count > 0)
                    yield return CopyRows(batch, keep);
            }
        }
    }

    private static RecordBatch CopyRows(RecordBatch batch, List<int> rows)
    {
        var fields = new List<Field>();
        var columns = new List<IArrowArray>();
        foreach (Field field in batch.Schema.FieldsList)
        {
            IArrowArray copied = CopyColumn(batch.Column(field.Name), rows);
            fields.Add(new Field(field.Name, copied.Data.DataType, field.IsNullable));
            columns.Add(copied);
        }

        return new RecordBatch(new Schema(fields, batch.Schema.Metadata), columns, rows.Count);
    }

    private static IArrowArray CopyColumn(IArrowArray source, List<int> rows)
    {
        switch (source)
        {
            case StringArray s:
                return CopyStrings(i => s.GetString(i), rows);
            case StringViewArray sv:
                return CopyStrings(i => sv.GetString(i), rows);
            case LargeStringArray ls:
                return CopyStrings(i => ls.GetString(i), rows);
            case Date32Array d:
            {
                var b = new Date32Array.Builder();
                foreach (int i in rows)
                {
                    DateOnly? v = d.GetDateOnly(i);
                    if (v == null) b.AppendNull(); else b.Append(v.Value);
                }
                return b.Build();
            }
            case DoubleArray f64:
            {
                var b = new DoubleArray.Builder();
                foreach (int i in rows)
                {
                    double? v = f64.GetValue(i);
                    if (v == null) b.AppendNull(); else b.Append(v.Value);
                }
                return b.Build();
            }
            case Int64Array i64:
            {
                var b = new Int64Array.Builder();
                foreach (int i in rows)
                {
                    long? v = i64.GetValue(i);
                    if (v == null) b.AppendNull(); else b.Append(v.Value);
                }
                return b.Build();
            }
            case Int32Array i32:
            {
                var b = new Int32Array.Builder();
                foreach (int i in rows)
                {
                    int? v = i32.GetValue(i);
                    if (v == null) b.AppendNull(); else b.Append(v.Value);
                }
                return b.Build();
            }
            default:
                throw new NotSupportedException(
                    $"period-filtered copy does not support {source.GetType().Name} columns");
        }
    }

    private static StringArray CopyStrings(Func<int, string?> get, List<int> rows)
    {
        var b = new StringArray.Builder();
        foreach (int i in rows)
        {
            string? v = get(i);
            if (v == null) b.AppendNull(); else b.Append(v);
        }
        return b.Build();
    }

    /// <summary>
    /// Builds vx_expression predicate trees, tracking every native handle it
    /// creates so Dispose can free them. The scan clones the filter expression,
    /// so handles only need to live until the scan is created.
    /// </summary>
    private sealed class FilterBuilder : IDisposable
    {
        private readonly List<IntPtr> _expressions = new();
        private readonly List<IntPtr> _scalars = new();
        private readonly List<IntPtr> _dtypes = new();

        public IntPtr SeriesPredicate(IReadOnlyCollection<string> seriesIds, string seriesColumn)
        {
            IntPtr column = Column(seriesColumn);
            if (seriesIds.Count == 1)
                return Compare(0 /* EQ */, column, Utf8Literal(seriesIds.First()));

            var elements = new List<IntPtr>();
            foreach (string id in seriesIds)
                elements.Add(Utf8Scalar(id));

            IntPtr utf8Dtype = NativeMethods.vx_dtype_new_utf8(true);
            _dtypes.Add(utf8Dtype);

            IntPtr err = IntPtr.Zero;
            IntPtr listScalar;
            fixed (IntPtr* ptr = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(elements))
            {
                listScalar = NativeMethods.vx_scalar_new_list(utf8Dtype, ptr, (nuint)elements.Count, false, &err);
            }

            if (listScalar == IntPtr.Zero)
                throw VortexException.FromNative(err);
            _scalars.Add(listScalar);

            IntPtr listLiteral = NativeMethods.vx_expression_literal(listScalar, &err);
            if (listLiteral == IntPtr.Zero)
                throw VortexException.FromNative(err);
            _expressions.Add(listLiteral);

            IntPtr contains = NativeMethods.vx_expression_list_contains(listLiteral, column);
            return TrackExpression(contains, "failed to build list-contains expression");
        }

        private IntPtr Column(string name)
        {
            IntPtr root = TrackExpression(NativeMethods.vx_expression_root(), "failed to create root expression");
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
            fixed (byte* p = bytes)
            {
                VxView view = new() { Ptr = p, Len = (nuint)bytes.Length };
                return TrackExpression(NativeMethods.vx_expression_get_item(view, root),
                    $"failed to reference column '{name}'");
            }
        }

        private IntPtr Compare(int op, IntPtr lhs, IntPtr rhs)
            => TrackExpression(NativeMethods.vx_expression_binary(op, lhs, rhs), "failed to build comparison");

        private IntPtr Utf8Literal(string value) => Literal(Utf8Scalar(value));

        private IntPtr Utf8Scalar(string value)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
            IntPtr err = IntPtr.Zero;
            IntPtr scalar;
            fixed (byte* p = bytes)
            {
                VxView view = new() { Ptr = p, Len = (nuint)bytes.Length };
                scalar = NativeMethods.vx_scalar_new_utf8(view, false, &err);
            }

            if (scalar == IntPtr.Zero)
                throw VortexException.FromNative(err);
            _scalars.Add(scalar);
            return scalar;
        }

        private IntPtr Literal(IntPtr scalar)
        {
            IntPtr err = IntPtr.Zero;
            IntPtr literal = NativeMethods.vx_expression_literal(scalar, &err);
            if (literal == IntPtr.Zero)
                throw VortexException.FromNative(err);
            _expressions.Add(literal);
            return literal;
        }

        private IntPtr TrackExpression(IntPtr expression, string errorMessage)
        {
            if (expression == IntPtr.Zero)
                throw new VortexException(errorMessage);
            _expressions.Add(expression);
            return expression;
        }

        public void Dispose()
        {
            foreach (IntPtr e in _expressions)
                NativeMethods.vx_expression_free(e);
            foreach (IntPtr s in _scalars)
                NativeMethods.vx_scalar_free(s);
            foreach (IntPtr d in _dtypes)
                NativeMethods.vx_dtype_free(d);
        }
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
