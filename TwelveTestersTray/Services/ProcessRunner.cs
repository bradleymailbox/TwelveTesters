using System.Diagnostics;
using System.Text;

namespace TwelveTestersTray.Services;

public sealed record ProcessResult(int ExitCode, string Output, string Error);
public sealed record BinaryProcessResult(int ExitCode, byte[] Output, string Error);

/// <summary>Runs hidden processes on a worker thread, draining both pipes and killing the tree on timeout/cancel.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var result = await RunBinaryAsync(fileName, args, timeout, ct).ConfigureAwait(false);
        return new(result.ExitCode, Encoding.UTF8.GetString(result.Output), result.Error);
    }

    public static Task<BinaryProcessResult> RunBinaryAsync(string fileName, IEnumerable<string> args,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var arguments = args.ToArray();
        return Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(20));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            var token = linked.Token;
            var start = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new IOException($"Couldn't start {Path.GetFileName(fileName)}.");
            using var cancellation = token.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
                catch (System.ComponentModel.Win32Exception) { /* process is exiting */ }
            });
            using var output = new MemoryStream();
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(output, token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            try
            {
                // The same deadline covers process exit AND pipe draining.
                await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return new BinaryProcessResult(process.ExitCode, output.ToArray(), await stderr.ConfigureAwait(false));
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                throw new TimeoutException($"{Path.GetFileName(fileName)} timed out after {(timeout ?? TimeSpan.FromSeconds(20)).TotalSeconds:0.#} seconds.");
            }
        }, ct);
    }
}
