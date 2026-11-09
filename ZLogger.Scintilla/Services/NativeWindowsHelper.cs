using System.Runtime.InteropServices;
namespace ZLogger.Scintilla.Services;

// Partial for LibraryImport's generated half, which net48 does not have.
// ReSharper disable once PartialTypeWithSinglePart
internal static partial class NativeWindowsHelper
{
    private const string UxThemeDll = "uxtheme.dll";
    private const string User32Dll = "user32.dll";
    private const string DwmApiDll = "dwmapi.dll";

#if NET8_0_OR_GREATER
    [LibraryImport(User32Dll, EntryPoint = "GetParent")]
    internal static partial IntPtr GetParent(IntPtr hWnd);

    [LibraryImport(User32Dll, EntryPoint = "SendMessageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr SendMessage(IntPtr hWnd, int msg, nint wParam, string lParam);

    [LibraryImport(DwmApiDll, EntryPoint = "DwmSetWindowAttribute")]
    internal static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport(UxThemeDll, EntryPoint = "SetWindowTheme", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

    [LibraryImport(UxThemeDll, EntryPoint = "#135", SetLastError = true)]
    private static partial int SetPreferredAppMode([MarshalAs(UnmanagedType.Bool)] bool mode);

    [LibraryImport(UxThemeDll, EntryPoint = "#136", SetLastError = true)]
    private static partial void FlushMenuThemes();
#else
    [DllImport(User32Dll)]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport(User32Dll, EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, nint wParam, string lParam);

    [DllImport(DwmApiDll)]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport(UxThemeDll, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

    [DllImport(UxThemeDll, EntryPoint = "#135", SetLastError = true)]
    private static extern int SetPreferredAppMode(bool mode);

    [DllImport(UxThemeDll, EntryPoint = "#136", SetLastError = true)]
    private static extern void FlushMenuThemes();
#endif

    internal static void ApplyScrollbarTheme(IntPtr hwnd, bool isDark)
    {
        if (hwnd == IntPtr.Zero)
            return;
        _ = SetWindowTheme(hwnd, isDark ? "DarkMode_Explorer" : "Explorer", null);
    }

    internal static void ApplyContextMenuTheme(bool isDark)
    {
        try
        {
            _ = SetPreferredAppMode(isDark);
            FlushMenuThemes();
        }
        catch (Exception)
        {
            // #135 and #136 are undocumented ordinals that older Windows builds lack. Menus then keep the
            // system theme, which is only cosmetic.
        }
    }
}
