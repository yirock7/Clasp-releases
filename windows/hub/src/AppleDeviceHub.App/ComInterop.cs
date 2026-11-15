using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AppleDeviceHub.App;

/// <summary>
/// Helpers for the source-generated COM interfaces (<c>[GeneratedComInterface]</c>) used by the audio, connection
/// and media code. Native AOT has no built-in COM support, so wrappers for Windows COM objects come from
/// <see cref="StrategyBasedComWrappers"/>, the same wrappers the generated marshalling code uses.
/// <see cref="ComObject.FinalRelease"/> only releases a wrapper created as a unique instance, so every wrapper
/// that is released early must be unique: <see cref="Wrap{T}"/> creates them that way, and each interface Windows
/// hands back through a generated COM method is marked <c>[MarshalUsing(typeof(UniqueComInterfaceMarshaller&lt;T&gt;))]</c>.
/// </summary>
internal static class ComInterop
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>
    /// Wraps a raw interface pointer from Windows and takes over its reference. Throws
    /// <see cref="InvalidCastException"/> (after releasing the object) when it does not implement <typeparamref name="T"/>.
    /// </summary>
    internal static T Wrap<T>(IntPtr pointer) where T : class
    {
        if (pointer == IntPtr.Zero) throw new ArgumentNullException(nameof(pointer));
        object wrapper;
        try { wrapper = Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance); }
        finally { Marshal.Release(pointer); }
        if (wrapper is T typed) return typed;
        Release(wrapper);
        throw new InvalidCastException($"The COM object does not implement {typeof(T).Name}.");
    }

    /// <summary>Releases the COM object behind a wrapper now rather than at garbage collection.</summary>
    internal static void Release(object? value)
    {
        if (value is ComObject comObject) comObject.FinalRelease();
    }
}
