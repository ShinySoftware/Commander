using System.Globalization;

namespace ShinyCommander;

/// <summary>
/// The app's UI text: <c>Strings.resx</c> (English) with <c>Strings.fr.resx</c> and <c>Strings.es.resx</c> beside it.
/// Shiny.Extensions.Localization.Generator turns every key into a property (or a <c>…Format</c> method when the text
/// has <c>{0}</c> placeholders) on <see cref="StringsLocalized"/>. Code uses it through <see cref="L"/> (imported
/// everywhere with <c>global using static</c>), XAML through <c>{l:Tr Key}</c>.
/// <para>
/// The language is picked once at startup (<see cref="ApplyLanguage"/>) - the pages read their text when they are
/// built, so switching it in Settings takes effect on the next launch.
/// </para>
/// </summary>
public partial class Strings
{
    static StringsLocalized? current;

    /// <summary>The strongly typed strings - set from DI when the app starts.</summary>
    public static StringsLocalized L => current ?? throw new InvalidOperationException("Strings.Initialize hasn't run yet");

    public static void Initialize(StringsLocalized strings) => current = strings;

    /// <summary>The languages Settings offers, by culture name, each named in its own language.</summary>
    public static IReadOnlyList<(string Culture, string Name)> Languages { get; } =
    [
        ("en", "English"),
        ("fr", "Français"),
        ("es", "Español")
    ];

    /// <summary>
    /// "System" (or nothing) follows the OS language; a culture name ("fr") overrides it. Only the UI language moves -
    /// dates and numbers keep following the OS region settings.
    /// </summary>
    public static void ApplyLanguage(string? language)
    {
        if (String.IsNullOrWhiteSpace(language) || language == "System")
            return;

        // the process-wide default, not CurrentUICulture: that one is scoped to the async flow that sets it, and once set
        // it pins the thread, so a later change (the UI tests switch languages live) wouldn't be seen
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(language);
    }

    /// <summary>A string by key - for XAML (<see cref="TrExtension"/>) and generated tables keyed by name.</summary>
    public static string Get(string key)
    {
        var value = L.Localizer[key];
#if DEBUG
        // a typo in {l:Tr ...} would otherwise just show the key - fail loudly where the UI tests open every screen
        if (value.ResourceNotFound)
            throw new KeyNotFoundException($"No string '{key}' in Strings.resx");
#endif
        return value.Value;
    }
}

/// <summary>
/// <c>Text="{l:Tr Settings_ShowHidden}"</c> - a <see cref="Strings"/> entry in the current language.
/// <c>{l:Tr Cmd_Copy, Suffix=' (F5)'}</c> appends text that isn't translated, like a shortcut.
/// </summary>
[ContentProperty(nameof(Key))]
[AcceptEmptyServiceProvider]
public class TrExtension : IMarkupExtension<string>
{
    public string Key { get; set; } = String.Empty;
    public string? Suffix { get; set; }

    public string ProvideValue(IServiceProvider serviceProvider) => Strings.Get(this.Key) + this.Suffix;

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => this.ProvideValue(serviceProvider);
}
