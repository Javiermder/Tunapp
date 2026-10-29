using System.Globalization;
using System.Text;

namespace Tunapp;

internal static class RecordSearch
{
    internal static bool Matches(string? query, IEnumerable<string?> values)
    {
        var normalized = Normalize(query?.Trim() ?? "");
        return normalized.Length == 0 || values.Any(value => Normalize(value ?? "").Contains(normalized, StringComparison.OrdinalIgnoreCase));
    }
    private static string Normalize(string text) => string.Concat(text.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark));
}
