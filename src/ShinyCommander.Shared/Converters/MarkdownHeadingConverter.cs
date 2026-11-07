using System.Globalization;
using System.Text.RegularExpressions;

namespace ShinyCommander.Converters;

/// <summary>
/// Shiny.Maui.Controls.Markdown 2.4.0 renders heading text at the body size and weight (its spans override the
/// heading label's font). Wrapping each heading's text in ** keeps headings bold until the fixed package ships.
/// </summary>
public partial class MarkdownHeadingConverter : IValueConverter
{
    [GeneratedRegex(@"^(?<hashes>#{1,6})[ \t]+(?<text>(?!\*\*).+?)[ \t]*#*[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex Heading();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string md ? Heading().Replace(md, m => $"{m.Groups["hashes"].Value} **{m.Groups["text"].Value}**") : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
