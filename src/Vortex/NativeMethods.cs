using System.Reflection;
using System.Runtime.InteropServices;
using Apache.Arrow.C;

namespace Vortex;

/// <summary>
/// A non-owning view over a byte range (vx_view).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VxView
{
    public byte* Ptr;
    public nuint Len;
}

/// <summary>
/// Options for creating a data source (vx_data_source_options).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VxDataSourceOptions
{
    public VxView* Paths;
    public nuint PathsLen;
}

/// <summary>
/// Scan row selection (vx_scan_selection). Zero-initialized means include all rows.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VxScanSelection
{
    public ulong* Idx;
    public nuint IdxLen;
    public int Include; // vx_scan_selection_include: 0 = ALL, 1 = INCLUDE_RANGE, 2 = EXCLUDE_RANGE
}

/// <summary>
/// Scan options (vx_scan_options). Zero-initialized returns everything.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VxScanOptions
{
    public IntPtr Projection; // const vx_expression*
    public IntPtr Filter;     // const vx_expression*
    public ulong RowRangeBegin;
    public ulong RowRangeEnd;
    public VxScanSelection Selection;
    public ulong Limit;
    public byte Ordered;      // C bool
}

/// <summary>
/// P/Invoke surface over the vortex-ffi cdylib. Pinned against vortex 0.76.x —
/// the FFI is pre-1.0 and churns, so any upgrade must re-verify every signature
/// against cinclude/vortex.h.
/// </summary>
internal static unsafe partial class NativeMethods
{
    private const string LibName = "vortex_ffi";

    static NativeMethods()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
    }

    /// <summary>
    /// Allows VORTEX_FFI_PATH to point at the native library explicitly
    /// (e.g. the cargo target dir during development) before default probing.
    /// </summary>
    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != LibName)
            return IntPtr.Zero;

        string? explicitPath = Environment.GetEnvironmentVariable("VORTEX_FFI_PATH");
        if (!string.IsNullOrEmpty(explicitPath) && NativeLibrary.TryLoad(explicitPath, out IntPtr handle))
            return handle;

        return IntPtr.Zero; // fall through to default probing (app dir, PATH)
    }

    // --- session ---

    [LibraryImport(LibName)]
    internal static partial IntPtr vx_session_new();

    [LibraryImport(LibName)]
    internal static partial void vx_session_free(IntPtr session);

    // --- data source ---

    [LibraryImport(LibName)]
    internal static partial IntPtr vx_data_source_new(IntPtr session, VxDataSourceOptions* options, IntPtr* err);

    [LibraryImport(LibName)]
    internal static partial void vx_data_source_free(IntPtr dataSource);

    [LibraryImport(LibName)]
    internal static partial IntPtr vx_data_source_dtype(IntPtr dataSource);

    // --- scan ---

    /// <remarks>options and estimate may be null; a scan may be consumed only once.</remarks>
    [LibraryImport(LibName)]
    internal static partial IntPtr vx_data_source_scan(IntPtr dataSource, VxScanOptions* options, IntPtr estimate, IntPtr* err);

    [LibraryImport(LibName)]
    internal static partial void vx_scan_free(IntPtr scan);

    /// <remarks>Returns null on exhaustion without setting err; null with err set on failure.</remarks>
    [LibraryImport(LibName)]
    internal static partial IntPtr vx_scan_next_partition(IntPtr scan, IntPtr* err);

    /// <remarks>
    /// Consumes the partition: it must not be freed or used afterwards.
    /// On error (return 1) the partition is freed by the callee.
    /// </remarks>
    [LibraryImport(LibName)]
    internal static partial int vx_partition_scan_arrow(IntPtr session, IntPtr partition, CArrowArrayStream* stream, IntPtr* err);

    [LibraryImport(LibName)]
    internal static partial void vx_partition_free(IntPtr partition);

    // --- dtype ---

    [LibraryImport(LibName)]
    internal static partial void vx_dtype_free(IntPtr dtype);

    [LibraryImport(LibName)]
    internal static partial int vx_dtype_to_arrow_schema(IntPtr dtype, CArrowSchema* schema, IntPtr* err);

    // --- error ---

    [LibraryImport(LibName)]
    internal static partial VxView vx_error_message(IntPtr error);

    [LibraryImport(LibName)]
    internal static partial void vx_error_free(IntPtr error);
}
