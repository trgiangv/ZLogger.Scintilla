using System;
using System.Runtime.InteropServices;
namespace ZLogger.Scintilla.Services;
// ReSharper disable once InconsistentNaming
// ReSharper disable once PartialTypeWithSinglePart
internal static partial class NativeWindowsHelper
{
    private const string UXTHEME_DLL = "uxtheme.dll";

#if NET8_0_OR_GREATER
    [LibraryImport(UXTHEME_DLL, EntryPoint = "SetWindowTheme", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);
    
    [LibraryImport(UXTHEME_DLL, EntryPoint = "#135", SetLastError = true)]
    private static partial int SetPreferredAppMode([MarshalAs(UnmanagedType.Bool)] bool mode);
    
    [LibraryImport(UXTHEME_DLL, EntryPoint = "#136", SetLastError = true)]
    private static partial void FlushMenuThemes();
#else
    [DllImport(UXTHEME_DLL, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);
    
    [DllImport(UXTHEME_DLL, EntryPoint = "#135", SetLastError = true)]
    private static extern int SetPreferredAppMode(bool mode);

    [DllImport(UXTHEME_DLL, EntryPoint = "#136", SetLastError = true)]
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
            // ignored
        }
    }
}
