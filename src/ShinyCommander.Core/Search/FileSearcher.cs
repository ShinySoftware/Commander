using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using ShinyCommander.Core.Models;

namespace ShinyCommander.Core.Search;

public sealed record SearchOptions
{
    public required string StartPath { get; init; }
    public string NameMask { get; init; } = "*";
    public string? ContainingText { get; init; }
    public bool CaseSensitive { get; init; }
    public bool Recursive { get; init; } = true;
    public int MaxDepth { get; init; } = Int32.MaxValue;
    public bool IncludeHidden { get; init; }
    public bool DirectoriesOnly { get; init; }
    public bool FilesOnly { get; init; }
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public DateTime? ModifiedAfter { get; init; }
    public DateTime? ModifiedBefore { get; init; }
}

public readonly record struct SearchStatus(string CurrentDirectory, int Scanned, int Found);

/// <summary>Alt+F7 "Find Files".</summary>
public static class FileSearcher
{
    public static async IAsyncEnumerable<FileEntry> SearchAsync(
        SearchOptions options,
        IProgress<SearchStatus>? progress = null,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        var channel = Channel.CreateBounded<FileEntry>(new BoundedChannelOptions(256) { SingleWriter = true });
        var producer = Task.Run(async () =>
        {
            try
            {
                await Produce(options, channel.Writer, progress, ct).ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex is OperationCanceledException ? null : ex);
            }
        }, ct);

        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return item;

        await producer.ConfigureAwait(false);
    }

    static async Task Produce(SearchOptions o, ChannelWriter<FileEntry> writer, IProgress<SearchStatus>? progress, CancellationToken ct)
    {
        var mask = new WildcardMask(o.NameMask);
        var scanned = 0;
        var found = 0;
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((o.StartPath, 0));
        var enumeration = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
        var lastReport = Environment.TickCount64;

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = pending.Pop();
            IEnumerable<FileSystemInfo> infos;
            try
            {
                infos = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", enumeration).ToList();
            }
            catch
            {
                continue;
            }

            foreach (var info in infos)
            {
                ct.ThrowIfCancellationRequested();
                scanned++;
                var entry = FileEntry.FromInfo(info);
                if (!o.IncludeHidden && entry.IsHidden)
                    continue;

                if (entry.IsDirectory)
                {
                    if (mask.IsDirectoryExcluded(entry.Name))
                        continue;

                    // don't walk into symlinked folders - avoids loops
                    if (o.Recursive && depth < o.MaxDepth && !entry.IsSymlink)
                        pending.Push((entry.FullPath, depth + 1));
                }

                if (await Matches(entry, mask, o, ct).ConfigureAwait(false))
                {
                    found++;
                    await writer.WriteAsync(entry, ct).ConfigureAwait(false);
                }

                if (Environment.TickCount64 - lastReport > 100)
                {
                    lastReport = Environment.TickCount64;
                    progress?.Report(new SearchStatus(dir, scanned, found));
                }
            }
        }
        progress?.Report(new SearchStatus(String.Empty, scanned, found));
    }

    static async Task<bool> Matches(FileEntry e, WildcardMask mask, SearchOptions o, CancellationToken ct)
    {
        if (o.DirectoriesOnly && !e.IsDirectory)
            return false;

        if (o.FilesOnly && e.IsDirectory)
            return false;

        if (!mask.IsMatch(e.Name))
            return false;

        if (!e.IsDirectory)
        {
            if (o.MinSize is { } min && e.Size < min)
                return false;

            if (o.MaxSize is { } max && e.Size > max)
                return false;
        }

        if (o.ModifiedAfter is { } after && e.Modified < after)
            return false;

        if (o.ModifiedBefore is { } before && e.Modified > before)
            return false;

        if (!String.IsNullOrEmpty(o.ContainingText))
        {
            if (e.IsDirectory)
                return false;

            return await ContainsTextAsync(e.FullPath, o.ContainingText, o.CaseSensitive, ct).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// Streams the file looking for the text, carrying an overlap between chunks so matches spanning a
    /// buffer boundary are found. Tries UTF-8 (which also covers ASCII/most source code).
    /// </summary>
    public static async Task<bool> ContainsTextAsync(string path, string text, bool caseSensitive, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var buffer = new char[64 * 1024];
            var carry = String.Empty;
            int read;
            while ((read = await reader.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                var chunk = carry + new string(buffer, 0, read);
                if (chunk.Contains(text, comparison))
                    return true;

                carry = chunk.Length >= text.Length ? chunk[^(text.Length - 1)..] : chunk;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // unreadable - treat as no match
        }
        return false;
    }
}
