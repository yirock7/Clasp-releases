using System.Runtime.InteropServices;
using AppleDeviceHub.Core.Presentation;
using Microsoft.Win32;

namespace AppleDeviceHub.App.Shell;

/// <summary>
/// Draws the tray silhouette into a small HICON using plain GDI (no System.Drawing, no extra
/// packages). The device silhouette comes from a pre-rendered 8-bit alpha mask
/// (<see cref="TrayGlyphMasks"/>) and is filled with one solid colour: the taskbar foreground, or a
/// status colour for the battery tint. No digits are drawn; a dimmed icon uses half the alpha.
/// The caller owns the returned handle (<see cref="TrayIcon.SetIcon"/> takes it over).
/// </summary>
internal static class TrayIconRenderer
{
    private const double DimAlpha = 0.5;

    /// <summary>
    /// Renders the silhouette for <paramref name="glyph"/> at the size the shell wants for a small icon at
    /// <paramref name="dpi"/>. Returns 0 when there is no silhouette (the caller shows the packaged icon) or on any GDI failure.
    /// </summary>
    public static nint Render(TrayGlyph glyph, TrayTint tint, uint dpi)
    {
        if (glyph.Group is not { } group) return 0;
        int size = SnapSize(GetSystemMetricsForDpi(SM_CXSMICON, dpi == 0 ? 96 : dpi));
        var mask = TrayGlyphMasks.Get(group, size);
        int pixels = size * size;
        if (mask.Length != pixels) return 0;
        var (r, g, b) = ColourFor(tint, TaskbarUsesLightTheme());
        double alphaScale = (glyph.Dimmed ? DimAlpha : 1.0);

        nint screenDc = GetDC(0);
        nint memDc = 0, colorBmp = 0, maskBmp = 0, oldBmp = 0;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = size,
                    biHeight = -size, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                },
            };
            colorBmp = CreateDIBSection(memDc, ref bmi, 0, out nint bits, 0, 0);
            if (colorBmp == 0 || bits == 0) return 0;
            oldBmp = SelectObject(memDc, colorBmp);

            var buffer = new byte[pixels * 4];
            for (int i = 0; i < pixels; i++)
            {
                int o = i * 4;
                buffer[o] = b; // straight (non-premultiplied) alpha, BGRA
                buffer[o + 1] = g;
                buffer[o + 2] = r;
                buffer[o + 3] = (byte)Math.Round(mask[i] * alphaScale);
            }
            Marshal.Copy(buffer, 0, bits, buffer.Length);
            GdiFlush();

            // With an alpha colour bitmap the mask is ignored, but CreateIconIndirect still needs one.
            var maskBits = new byte[((size + 15) / 16) * 2 * size];
            maskBmp = CreateBitmap(size, size, 1, 1, maskBits);
            if (maskBmp == 0) return 0;

            SelectObject(memDc, oldBmp); // free the section before it is copied into the icon
            oldBmp = 0;
            var info = new ICONINFO { fIcon = true, hbmMask = maskBmp, hbmColor = colorBmp };
            return CreateIconIndirect(ref info);
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (oldBmp != 0) SelectObject(memDc, oldBmp);
            if (colorBmp != 0) DeleteObject(colorBmp);
            if (maskBmp != 0) DeleteObject(maskBmp);
            if (memDc != 0) DeleteDC(memDc);
            if (screenDc != 0) _ = ReleaseDC(0, screenDc);
        }
    }

    /// <summary>The fill colour: the taskbar foreground, or the Windows status colour for the tint.</summary>
    internal static (byte R, byte G, byte B) ColourFor(TrayTint tint, bool lightTaskbar) => tint switch
    {
        TrayTint.Low => lightTaskbar ? ((byte)0x9D, (byte)0x5D, (byte)0x00) : ((byte)0xFF, (byte)0xC8, (byte)0x3D),
        TrayTint.Critical => lightTaskbar ? ((byte)0xC4, (byte)0x2B, (byte)0x1C) : ((byte)0xFF, (byte)0x6B, (byte)0x6B),
        TrayTint.Charging => lightTaskbar ? ((byte)0x0F, (byte)0x7B, (byte)0x0F) : ((byte)0x6C, (byte)0xCB, (byte)0x5F),
        _ => lightTaskbar ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255),
    };

    /// <summary>Pixel size to draw at: the shell's small-icon size, snapped to 16/20/24/32.</summary>
    internal static int SnapSize(int metric) => metric switch
    {
        <= 16 => 16,
        <= 20 => 20,
        <= 24 => 24,
        _ => 32,
    };

    /// <summary>Read-only check of the taskbar theme; defaults to dark (the Windows 11 default).</summary>
    internal static bool TaskbarUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    // ---- GDI interop (private so the shared NativeMethods file stays untouched) -------------

    private const int SM_CXSMICON = 49;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors; // unused for 32-bit
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public uint xHotspot, yHotspot;
        public nint hbmMask, hbmColor;
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDc);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern nint CreateIconIndirect(ref ICONINFO info);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO bmi, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint CreateBitmap(int width, int height, uint planes, uint bitCount, byte[] bits);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

}
