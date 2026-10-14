using System.Globalization;

namespace ScanBack.Web.Services;

public sealed partial class UiText
{
    public bool IsGreek => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "el";
    public string this[string text] => IsGreek && Greek.TryGetValue(text, out var translated) ? translated : text;
}
