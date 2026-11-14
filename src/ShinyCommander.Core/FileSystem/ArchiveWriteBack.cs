using ShinyCommander.Core.Models;

namespace ShinyCommander.Core.FileSystem;

/// <summary>
/// A file opened out of an archive is a copy in a temporary folder - the editor, or whatever app opened it, saves
/// there. This watches those copies and writes each save back into its archive, so editing a file inside an archive
/// works like editing it in a folder. Files opened from a server are watched the same way and uploaded on each save.
/// </summary>
public sealed class ArchiveWriteBack : IDisposable
{
    readonly List<Session> sessions = [];
    readonly object gate = new();

    /// <summary>A save went back into its archive.</summary>
    public event EventHandler<ArchiveWriteBackEventArgs>? Saved;

    /// <summary>A save couldn't be written back - the copy is still there, and the next save tries again.</summary>
    public event EventHandler<ArchiveWriteBackEventArgs>? Failed;

    /// <summary>Starts watching <paramref name="copy"/>, the temporary copy of <paramref name="key"/> in <paramref name="archiveFile"/>.</summary>
    public void Watch(string copy, string archiveFile, string key, UnixFileMode? mode = null)
    {
        if (!ArchiveFormats.IsWritable(archiveFile))
            return;

        lock (this.gate)
        {
            if (this.sessions.Any(x => x.Copy == copy))
                return;
            this.sessions.Add(new Session(this, copy, archiveFile, key, mode, null));
        }
    }

    /// <summary>
    /// Starts watching <paramref name="copy"/>, a download of <paramref name="key"/> (its name) from the folder
    /// <paramref name="target"/>; <paramref name="save"/> uploads the copy at the given path.
    /// </summary>
    public void Watch(string copy, string target, string key, Action<string> save)
    {
        lock (this.gate)
        {
            if (this.sessions.Any(x => x.Copy == copy))
                return;
            this.sessions.Add(new Session(this, copy, target, key, null, save));
        }
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            foreach (var s in this.sessions)
                s.Dispose();
            this.sessions.Clear();
        }
    }

    void WriteBack(Session session)
    {
        // one archive rewrite at a time - two saves racing would each rewrite from the same original
        lock (this.gate)
        {
            try
            {
                var info = new FileInfo(session.Copy);
                if (!info.Exists || (info.LastWriteTimeUtc, info.Length) == session.Written)
                    return;

                if (session.Save != null)
                {
                    session.Save(session.Copy);
                }
                else
                {
                    var changes = new ArchiveChanges();
                    changes.Adds.Add(new ArchiveAdd(session.Key, false, () => File.OpenRead(session.Copy), info.LastWriteTime, session.Mode));
                    ArchiveWriter.Apply(session.ArchiveFile, changes);
                }
                session.Written = (info.LastWriteTimeUtc, info.Length);
                this.Saved?.Invoke(this, new ArchiveWriteBackEventArgs(session.ArchiveFile, session.Key, null));
            }
            catch (Exception ex)
            {
                this.Failed?.Invoke(this, new ArchiveWriteBackEventArgs(session.ArchiveFile, session.Key, ex));
            }
        }
    }

    sealed class Session : IDisposable
    {
        readonly FileSystemWatcher watcher;
        readonly Timer settle;

        public Session(ArchiveWriteBack owner, string copy, string archiveFile, string key, UnixFileMode? mode, Action<string>? save)
        {
            this.Save = save;
            this.Copy = copy;
            this.ArchiveFile = archiveFile;
            this.Key = key;
            this.Mode = mode;
            var info = new FileInfo(copy);
            this.Written = (info.LastWriteTimeUtc, info.Length);

            // saves come as several events (truncate, write, close) - wait for them to settle
            this.settle = new Timer(_ => owner.WriteBack(this));
            // watch the folder for the name: many editors save by writing a new file and renaming it over the old
            this.watcher = new FileSystemWatcher(Path.GetDirectoryName(copy)!, Path.GetFileName(copy))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            this.watcher.Changed += (_, _) => this.Poke();
            this.watcher.Created += (_, _) => this.Poke();
            this.watcher.Renamed += (_, _) => this.Poke();
            this.watcher.EnableRaisingEvents = true;
        }

        public string Copy { get; }
        public string ArchiveFile { get; }
        public string Key { get; }
        public UnixFileMode? Mode { get; }
        public Action<string>? Save { get; }
        public (DateTime, long) Written { get; set; }

        void Poke() => this.settle.Change(500, Timeout.Infinite);

        public void Dispose()
        {
            this.watcher.Dispose();
            this.settle.Dispose();
        }
    }
}

/// <summary>Which archive entry a save was for, and what went wrong if it didn't go back.</summary>
public sealed record ArchiveWriteBackEventArgs(string ArchiveFile, string Key, Exception? Error);
