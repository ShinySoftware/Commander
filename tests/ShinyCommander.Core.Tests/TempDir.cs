namespace ShinyCommander.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cmdr-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(this.Path);

    public string File(string relative, string content = "hello")
    {
        var full = System.IO.Path.Combine(this.Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public string Dir(string relative)
    {
        var full = System.IO.Path.Combine(this.Path, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(this.Path, true); } catch { }
    }
}
