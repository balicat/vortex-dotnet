using System.Text;

namespace Vortex;

/// <summary>
/// Error surfaced by the native Vortex library.
/// </summary>
public sealed class VortexException : Exception
{
    public VortexException(string message) : base(message)
    {
    }

    /// <summary>
    /// Builds an exception from an owned vx_error pointer and frees it.
    /// </summary>
    internal static unsafe VortexException FromNative(IntPtr error)
    {
        if (error == IntPtr.Zero)
            return new VortexException("unknown Vortex error (no error object returned)");

        VxView view = NativeMethods.vx_error_message(error);
        string message = view.Ptr == null
            ? "unknown Vortex error (empty message)"
            : Encoding.UTF8.GetString(view.Ptr, checked((int)view.Len));
        NativeMethods.vx_error_free(error);
        return new VortexException(message);
    }
}
