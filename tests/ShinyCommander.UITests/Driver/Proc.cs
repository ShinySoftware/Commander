using System.Diagnostics;
using System.Text;

namespace ShinyCommander.UITests.Driver;

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => this.ExitCode == 0;
    public string Combined => String.IsNullOrWhiteSpace(this.StdErr) ? this.StdOut : $"{this.StdOut}\n{this.StdErr}";
}

/// <summary>Minimal process runner with a hard timeout.</summary>
public static class Proc
{
    public static async Task<ProcResult> Run(string fileName, IEnumerable<string> args, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = TestConfig.RepoRoot
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw new TimeoutException($"'{fileName} {String.Join(' ', args)}' did not finish in time.");
        }
        return new ProcResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
