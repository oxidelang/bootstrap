using ClosedXML.Excel;

namespace Oxide.Compiler.Utils;

public static class SheetUtils
{
    public static void SetText(this IXLCell cell, string text)
    {
        cell.Value = $"'{text}";
        // cell.RichText.AddText(text);
    }
}