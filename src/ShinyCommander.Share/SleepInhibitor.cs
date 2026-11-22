using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShinyCommander.Share;

/// <summary>
/// Keeps the computer from going to sleep on its own (the screen may still turn off): an IOKit power assertion on
/// macOS, <c>SetThreadExecutionState</c> on Windows, <c>systemd-inhibit</c> on Linux. <see cref="Set"/> is cheap to
/// call repeatedly - it only acts when the wanted state changes.
/// </summary>
public sealed partial class SleepInhibitor : IDisposable
{
    readonly Lock gate = new();
    bool held;

    // macOS
    uint assertionId;

    // Windows: the execution state belongs to the thread that set it, so one thread holds it
    Thread? holder;
    ManualResetEventSlim? release;

    // Linux
    Process? inhibit;

    public bool IsHeld => this.held;

    public void Set(bool keepAwake, string reason)
    {
        lock (this.gate)
        {
            if (keepAwake == this.held)
                return;

            try
            {
                if (keepAwake)
                    this.Acquire(reason);
                else
                    this.Release();

                this.held = keepAwake;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // no way to hold it on this machine - sharing still works, the computer may just sleep
                Debug.WriteLine($"SleepInhibitor: {ex.Message}");
            }
        }
    }

    public void Dispose() => this.Set(false, "");

    void Acquire(string reason)
    {
        if (OperatingSystem.IsMacOS())
        {
            var type = CFStringCreateWithCString(IntPtr.Zero, "PreventUserIdleSystemSleep", 0x08000100);
            var name = CFStringCreateWithCString(IntPtr.Zero, reason, 0x08000100);
            try
            {
                if (IOPMAssertionCreateWithName(type, 255, name, out this.assertionId) != 0)
                    throw new InvalidOperationException("IOPMAssertionCreateWithName failed");
            }
            finally
            {
                CFRelease(type);
                CFRelease(name);
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            var release = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                SetThreadExecutionState(EsContinuous | EsSystemRequired);
                release.Wait();
                SetThreadExecutionState(EsContinuous);
            })
            {
                IsBackground = true,
                Name = "ShinyCommander keep awake"
            };
            thread.Start();
            this.holder = thread;
            this.release = release;
        }
        else if (OperatingSystem.IsLinux())
        {
            this.inhibit = Process.Start(new ProcessStartInfo("systemd-inhibit")
            {
                ArgumentList = { "--what=idle:sleep", "--who=ShinyCommander", $"--why={reason}", "--mode=block", "sleep", "infinity" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
        }
    }

    void Release()
    {
        if (OperatingSystem.IsMacOS())
        {
            if (this.assertionId != 0)
                IOPMAssertionRelease(this.assertionId);
            this.assertionId = 0;
        }
        else if (OperatingSystem.IsWindows())
        {
            this.release?.Set();
            this.holder = null;
            this.release = null;
        }
        else if (OperatingSystem.IsLinux() && this.inhibit is { } p)
        {
            this.inhibit = null;
            try
            {
                p.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }
            p.Dispose();
        }
    }

    const uint EsContinuous = 0x80000000;
    const uint EsSystemRequired = 0x00000001;

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);

    [LibraryImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static partial int IOPMAssertionCreateWithName(IntPtr type, uint level, IntPtr name, out uint id);

    [LibraryImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static partial int IOPMAssertionRelease(uint id);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(IntPtr value);
}
