using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShinyCommander.Share;

/// <summary>What the computer is running on right now.</summary>
/// <param name="HasBattery">It has a battery (or a UPS) it could run from - false on most desktops.</param>
/// <param name="OnBattery">It is running from that battery rather than the mains.</param>
public readonly record struct PowerState(bool HasBattery, bool OnBattery);

/// <summary>
/// Reads the power source: IOKit's power source API on macOS, <c>GetSystemPowerStatus</c> on Windows,
/// <c>/sys/class/power_supply</c> on Linux. Anything it can't read counts as plugged in with no battery.
/// </summary>
public static partial class PowerSource
{
    public static PowerState Read()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return ReadMac();
            if (OperatingSystem.IsWindows())
                return ReadWindows();
            if (OperatingSystem.IsLinux())
                return ReadLinux();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"PowerSource: {ex.Message}");
        }
        return new PowerState(false, false);
    }

    // ------------------------------------------------------------------------------------------- macOS

    // kIOPSTimeRemainingUnlimited - the machine is on external power
    const double TimeRemainingUnlimited = -2.0;

    static PowerState ReadMac()
    {
        var hasBattery = false;
        var snapshot = IOPSCopyPowerSourcesInfo();
        if (snapshot != IntPtr.Zero)
        {
            var list = IOPSCopyPowerSourcesList(snapshot);
            if (list != IntPtr.Zero)
            {
                hasBattery = CFArrayGetCount(list) > 0;
                CFRelease(list);
            }
            CFRelease(snapshot);
        }
        var onBattery = hasBattery && IOPSGetTimeRemainingEstimate() != TimeRemainingUnlimited;
        return new PowerState(hasBattery, onBattery);
    }

    const string IoKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(IoKit)]
    private static partial IntPtr IOPSCopyPowerSourcesInfo();

    [LibraryImport(IoKit)]
    private static partial IntPtr IOPSCopyPowerSourcesList(IntPtr snapshot);

    [LibraryImport(IoKit)]
    private static partial double IOPSGetTimeRemainingEstimate();

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetCount(IntPtr array);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr value);

    // ------------------------------------------------------------------------------------------- Windows

    [StructLayout(LayoutKind.Sequential)]
    struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    const byte AcOffline = 0;
    const byte BatteryFlagNoBattery = 128;
    const byte BatteryFlagUnknown = 255;

    static PowerState ReadWindows()
    {
        if (!GetSystemPowerStatus(out var status))
            return new PowerState(false, false);

        var hasBattery = status.BatteryFlag != BatteryFlagUnknown && (status.BatteryFlag & BatteryFlagNoBattery) == 0;
        return new PowerState(hasBattery, hasBattery && status.AcLineStatus == AcOffline);
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    // ------------------------------------------------------------------------------------------- Linux

    const string PowerSupplyRoot = "/sys/class/power_supply";

    static PowerState ReadLinux()
    {
        if (!Directory.Exists(PowerSupplyRoot))
            return new PowerState(false, false);

        var supplies = Directory.GetDirectories(PowerSupplyRoot)
            .Select(x => (Type: ReadValue(x, "type"), Online: ReadValue(x, "online"), Scope: ReadValue(x, "scope")))
            .ToList();

        // a peripheral's battery (a mouse, a headset) says scope=Device - those don't power the machine
        var hasBattery = supplies.Any(x => x.Type == "Battery" && x.Scope != "Device");
        var mainsOnline = supplies.Any(x => x.Type is "Mains" or "USB" && x.Online == "1");
        var hasMains = supplies.Any(x => x.Type == "Mains");

        // no mains supply listed at all: nothing says it's unplugged
        return new PowerState(hasBattery, hasBattery && hasMains && !mainsOnline);
    }

    static string? ReadValue(string supply, string name)
    {
        var path = Path.Combine(supply, name);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }
}
