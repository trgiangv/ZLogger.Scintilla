using System.Runtime.InteropServices;
namespace ZLogger.Scintilla.Services;

// Partial for LibraryImport's generated half, which net48 does not have.
// ReSharper disable once PartialTypeWithSinglePart
internal static partial class NativeWindowsHelper
{
    private const string UxThemeDll = "uxtheme.dll";

#if NET8_0_OR_GREATER
    [LibraryImport(UxThemeDll, EntryPoint = "SetWindowTheme", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

    [LibraryImport(UxThemeDll, EntryPoint = "#135", SetLastError = true)]
    private static partial int SetPreferredAppMode([MarshalAs(UnmanagedType.Bool)] bool mode);

    [LibraryImport(UxThemeDll, EntryPoint = "#136", SetLastError = true)]
    private static partial void FlushMenuThemes();
#else
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
