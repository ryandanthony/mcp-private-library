using System.Diagnostics;

namespace McpPrivateLibrary.Services;

public sealed record CloneResult(string LocalPath, string? Branch, string? CommitSha);

/// <summary>Clones a GitHub repo using the system git binary (shallow, single-branch).</summary>
public sealed class GitCloneService
{
    private readonly ILogger<GitCloneService> _logger;

    public GitCloneService(ILogger<GitCloneService> logger) => _logger = logger;

    public async Task<CloneResult> CloneAsync(string cloneUrl, string destination, CancellationToken ct = default)
    {
        if (Directory.Exists(destination))
            DeleteDirectory(destination);
        Directory.CreateDirectory(destination);

        await RunGitAsync(new[] { "clone", "--depth", "1", "--single-branch", cloneUrl, destination }, workingDir: null, ct);

        var branch = (await RunGitAsync(new[] { "rev-parse", "--abbrev-ref", "HEAD" }, destination, ct)).Trim();
        var sha = (await RunGitAsync(new[] { "rev-parse", "HEAD" }, destination, ct)).Trim();
        _logger.LogInformation("Cloned {Url} @ {Sha} (branch {Branch})", cloneUrl, sha, branch);
        return new CloneResult(destination, branch, sha);
    }

    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        // Git objects are often read-only on Windows; clear attributes first to be safe.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best effort */ }
        }
        Directory.Delete(path, recursive: true);
    }

    private async Task<string> RunGitAsync(string[] args, string? workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workingDir is not null) psi.WorkingDirectory = workingDir;
        // Never prompt for credentials; fail fast on private repos without a token.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_ASKPASS"] = "echo";

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {proc.ExitCode}): {stderr.Trim()}");

        return stdout;
    }
}
