using System.Diagnostics;
using System.Text.Json;
using Microsoft.Maui.DevFlow.Driver;

namespace ShinyCommander.UITests.Driver;

/// <summary>
/// Owns the app process and the DevFlow connection for the whole run.
/// <para>
/// <b>Attach, don't relaunch.</b> When a previous run left the test app running (it does by default, see
/// <see cref="TestConfig.KeepApp"/>) and the binary hasn't been rebuilt since, the fixture attaches to it in well
/// under a second. That is what makes "run just the test for the thing I'm fixing" fast. Each test then puts the
/// app into a known state through the app's own <c>reset</c> tool, rather than restarting it.
/// </para>
/// <para>
/// <b>One client, one lease.</b> The agent grants a mutation lease to one driver at a time. Every <c>maui devflow</c>
/// CLI process is a new holder, so back-to-back CLI calls fail with "Another DevFlow session is driving this app".
/// The suite talks to the agent over HTTP through a single <see cref="AgentClient"/>, claiming the lease once with
/// force (a crashed earlier run may still hold one).
/// </para>
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    public AgentClient Client { get; private set; } = null!;
    public Commander App { get; private set; } = null!;
    public int ProcessId { get; private set; }
    public int AgentPort { get; private set; }
    public bool Attached { get; private set; }

    Process? launched;

    sealed record RunningApp(int Pid, int Port, DateTime LaunchedUtc, string Executable);

    public async ValueTask InitializeAsync()
    {
        if (Directory.Exists(TestConfig.WorkDir))
        {
            try { Directory.Delete(TestConfig.WorkDir, true); } catch { }
        }
        Directory.CreateDirectory(TestConfig.WorkDir);

        if (TestConfig.BuildBeforeRun)
            await BuildApp();

        if (!File.Exists(TestConfig.AppExecutable))
            throw new InvalidOperationException($"No app at {TestConfig.AppBundle}. Run tests/ShinyCommander.UITests/run.sh (builds first), or set SC_UITEST_BUILD=1.");

        if (TestConfig.ReuseApp && await this.TryAttach())
            return;

        await this.Launch();
    }

    async Task<bool> TryAttach()
    {
        if (!File.Exists(TestConfig.StateFile))
            return false;

        try
        {
            var running = JsonSerializer.Deserialize<RunningApp>(await File.ReadAllTextAsync(TestConfig.StateFile))!;
            if (running.Executable != TestConfig.AppExecutable || !IsAlive(running.Pid))
                return false;

            // rebuilt since it started? then it's testing yesterday's code - start over
            if (File.GetLastWriteTimeUtc(TestConfig.AppExecutable) > running.LaunchedUtc)
            {
                Kill(running.Pid);
                return false;
            }

            await this.Connect(running.Port);
            await this.App.State();
            this.ProcessId = running.Pid;
            this.Attached = true;
            Console.WriteLine($"[uitests] attached to running app pid {running.Pid} on :{running.Port}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[uitests] could not attach ({ex.Message}) - launching");
            return false;
        }
    }

    async Task Launch()
    {
        // a stale test app (from a previous fixture) would only confuse the broker lookup
        if (File.Exists(TestConfig.StateFile))
        {
            try
            {
                var old = JsonSerializer.Deserialize<RunningApp>(await File.ReadAllTextAsync(TestConfig.StateFile));
                if (old != null && IsAlive(old.Pid))
                    Kill(old.Pid);
            }
            catch { }
        }

        var home = Path.Combine(TestConfig.WorkDir, "start");
        Directory.CreateDirectory(home);

        var psi = new ProcessStartInfo(TestConfig.AppExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = home
        };
        psi.Environment["SHINYCOMMANDER_UITEST"] = "1";
        psi.Environment["SHINYCOMMANDER_LEFT"] = home;
        psi.Environment["SHINYCOMMANDER_RIGHT"] = home;
        psi.Environment["SHINYCOMMANDER_TRASH"] = TestConfig.TrashDir;

        var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + TestConfig.AppExecutable);
        var log = Path.Combine(TestConfig.ArtifactDir, "app.log");
        Directory.CreateDirectory(TestConfig.ArtifactDir);
        var writer = new StreamWriter(log, false) { AutoFlush = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (writer) writer.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (writer) writer.WriteLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        this.launched = p;
        this.ProcessId = p.Id;
        var started = DateTime.UtcNow;

        var port = await FindAgentPort(p.Id, p, TimeSpan.FromSeconds(60));
        await this.Connect(port);
        await this.WaitForMainPage();

        await File.WriteAllTextAsync(TestConfig.StateFile, JsonSerializer.Serialize(new RunningApp(p.Id, port, started, TestConfig.AppExecutable)));
        Console.WriteLine($"[uitests] launched app pid {p.Id} on :{port}");
    }

    async Task Connect(int port)
    {
        this.AgentPort = port;
        this.Client = new AgentClient("127.0.0.1", port) { MutationLeaseLabel = "ShinyCommander.UITests" };
        this.App = new Commander(this.Client);

        // the agent registers with the broker a moment before its HTTP server listens
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                // take over from whatever held it before (a crashed run, a CLI session)
                await this.Client.ControlMutationLeaseAsync("claim", force: true, this.Client.MutationLeaseId, this.Client.MutationLeaseHolderKind, this.Client.MutationLeaseLabel);
                return;
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }
        }
    }

    async Task WaitForMainPage()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var state = await this.App.State();
                if (state.Page == "MainPage" && !String.IsNullOrEmpty(state.Left.Path))
                    return;
            }
            catch (Exception ex)
            {
                last = ex;
            }
            await Task.Delay(300);
        }
        throw new InvalidOperationException($"The app never reached its main page. Last error: {last?.Message}. App log: {Path.Combine(TestConfig.ArtifactDir, "app.log")}");
    }

    /// <summary>The broker assigns the agent port per launch; find ours by process id (`maui devflow list`).</summary>
    static async Task<int> FindAgentPort(int pid, Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException($"The app exited during startup ({process.ExitCode}). See {Path.Combine(TestConfig.ArtifactDir, "app.log")}");

            var r = await Proc.Run("maui", ["devflow", "list", "--json"], TimeSpan.FromSeconds(20));
            try
            {
                using var doc = JsonDocument.Parse(r.StdOut.Trim() is { Length: > 0 } s ? s : "[]");
                foreach (var a in doc.RootElement.EnumerateArray())
                {
                    if (a.TryGetProperty("processId", out var p) && p.ValueKind == JsonValueKind.Number && p.GetInt32() == pid)
                        return a.GetProperty("port").GetInt32();
                }
            }
            catch (JsonException)
            {
                // broker still starting
            }
            await Task.Delay(500);
        }
        throw new TimeoutException($"The app (pid {pid}) never registered with the DevFlow broker. Is the agent added in Debug (MauiProgram)?");
    }

    public static async Task BuildApp()
    {
        // own output/intermediate folders so the suite never fights over src/ShinyCommander.MacOS/bin|obj with a dev build
        var result = await Proc.Run("dotnet",
            [
                "build", TestConfig.AppProject, "-c", "Debug", "-nologo", "-v:q",
                "-p:OutputPath=" + Path.Combine(TestConfig.SuiteDir, ".app-build", "bin") + "/",
                "-p:IntermediateOutputPath=" + Path.Combine(TestConfig.SuiteDir, ".app-build", "obj") + "/"
            ],
            TimeSpan.FromMinutes(15));
        if (!result.Ok)
            throw new InvalidOperationException("Building the app failed:\n" + result.Combined);
    }

    static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    static void Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(true);
            p.WaitForExit(5000);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await this.Client.ControlMutationLeaseAsync("release", force: false, this.Client.MutationLeaseId, this.Client.MutationLeaseHolderKind, this.Client.MutationLeaseLabel);
        }
        catch { }

        if (!TestConfig.KeepApp)
        {
            Kill(this.ProcessId);
            try { File.Delete(TestConfig.StateFile); } catch { }
        }
        else if (this.launched != null)
        {
            // keep it running for the next run; stop collecting its output
            this.launched.CancelOutputRead();
            this.launched.CancelErrorRead();
        }
        this.Client?.Dispose();
    }
}

[CollectionDefinition(Name)]
public sealed class AppCollection : ICollectionFixture<AppFixture>
{
    public const string Name = "app";
}
