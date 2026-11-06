namespace ShinyCommander.Core.Models;

public static class ArchiveFormats
{
    // System.IO.Compression handles zip natively; the zip family below are all plain zip containers
    static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".jar", ".nupkg", ".snupkg", ".apk", ".aab", ".epub", ".vsix", ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".ipa", ".whl"
    };

    public static bool IsSupported(string fileName) => Supported.Contains(Path.GetExtension(fileName));
}
