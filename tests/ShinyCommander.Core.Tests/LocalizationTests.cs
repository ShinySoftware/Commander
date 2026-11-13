using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ShinyCommander.Core.Tests;

/// <summary>
/// The UI strings live in the shared MAUI project, which these tests can't load - so check the .resx files as files:
/// every language has every key, nothing extra, and the same {0} placeholders (a missing one throws in string.Format).
/// Also every {l:Tr Key} in the XAML names a real key.
/// </summary>
public partial class LocalizationTests
{
    static readonly string StringsDir = Path.Combine(RepoRoot(), "src", "ShinyCommander.Shared", "Resources", "Strings");

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ShinyCommander.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    static Dictionary<string, string> Load(string file) => XDocument.Load(Path.Combine(StringsDir, file))
        .Root!.Elements("data")
        .ToDictionary(x => (string)x.Attribute("name")!, x => (string?)x.Element("value") ?? String.Empty);

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    static string[] Placeholders(string value) => Placeholder().Matches(value).Select(m => m.Value).Distinct().Order().ToArray();

    [Theory]
    [InlineData("Strings.fr.resx")]
    [InlineData("Strings.es.resx")]
    public void Every_language_has_every_key_with_the_same_placeholders(string file)
    {
        var english = Load("Strings.resx");
        var translated = Load(file);

        english.Keys.Except(translated.Keys).ShouldBeEmpty($"missing from {file}");
        translated.Keys.Except(english.Keys).ShouldBeEmpty($"in {file} but not Strings.resx");
        foreach (var (key, value) in english)
        {
            Placeholders(translated[key]).ShouldBe(Placeholders(value), $"{file}: {key}");
            translated[key].ShouldNotBeNullOrWhiteSpace($"{file}: {key}");
        }
    }

    [GeneratedRegex(@"\{l:Tr\s+([A-Za-z0-9_]+)")]
    private static partial Regex TrKey();

    [Fact]
    public void Every_key_the_xaml_uses_exists()
    {
        var keys = Load("Strings.resx").Keys.ToHashSet();
        var missing = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "ShinyCommander.Shared"), "*.xaml", SearchOption.AllDirectories)
            .SelectMany(f => TrKey().Matches(File.ReadAllText(f)).Select(m => $"{Path.GetFileName(f)}: {m.Groups[1].Value}"))
            .Where(x => !keys.Contains(x[(x.IndexOf(": ") + 2)..]))
            .ToList();
        missing.ShouldBeEmpty();
    }
}
