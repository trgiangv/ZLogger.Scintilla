using System.Text;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Public;

namespace ZLogger.Scintilla.Wpf.Demo;

public sealed class DemoRevitLinkifier : ILinkifier
{
    public bool TryMatch(ReadOnlySpan<byte> utf8Token, RenderContext context, out Action? onClick)
    {
        onClick = null;
        if (utf8Token.IsEmpty)
            return false;

        var token = Encoding.UTF8.GetString(utf8Token).Trim();
        if (string.IsNullOrEmpty(token))
            return false;

        if (IsElementIdFromParameters(token, context))
        {
            var captured = token;
            onClick = () => ShowTokenInfo("ElementId", captured, $"revitlog://elementid/{captured}");
            return true;
        }

        if (LooksLikeUniqueId(token))
        {
            var captured = token;
            onClick = () => ShowTokenInfo("UniqueId", captured, $"revitlog://uniqueid/{Uri.EscapeDataString(captured)}");
            return true;
        }

        if (!LooksLikeIfcGuid(token))
            return false;

        onClick = () => ShowTokenInfo("IfcGuid", token, $"revitlog://ifcguid/{Uri.EscapeDataString(token)}");
        return true;
    }

    private static void ShowTokenInfo(string kind, string value, string uri)
    {
        System.Windows.MessageBox.Show(
            $"Token clicked:\nKind: {kind}\nValue: {value}\nURI: {uri}",
            "Revit Token",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private static bool LooksLikeUniqueId(string token)
        => token.Length == 45 && CountChar(token, '-') == 5;

    private static bool LooksLikeIfcGuid(string token)
    {
        if (token.Length != 22)
            return false;
        foreach (var c in token)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '$'))
                return false;
        }
        return true;
    }

    private static bool IsElementIdFromParameters(string token, RenderContext context)
    {
        return ParameterSpan.HasParameter(context, typeof(ElementId).FullName!, token);
    }

    private static int CountChar(string text, char value)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (c == value)
                count++;
        }
        return count;
    }
}
