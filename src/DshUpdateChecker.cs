using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DshBar;

/// <summary>
/// dsh 更新检查：本地版本走 `dsh --version`，最新版本走 npm 注册表
/// packument 的 dist-tags.latest，比较用完整的 semver 预发布规则。
/// 更新动作是 `npm install -g @deepseek-ai/dsh@latest`（本机为全局安装）。
/// </summary>
public sealed class DshUpdateChecker
{
    private const string PackumentUrl = "https://registry.npmjs.org/@deepseek-ai%2fdsh";
    private static readonly TimeSpan LocalVersionTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NpmInstallTimeout = TimeSpan.FromMinutes(5);

    private static readonly Regex VersionPattern =
        new(@"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z\-.]+))?$", RegexOptions.Compiled);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public sealed record CheckResult(string Local, string Latest, bool UpdateAvailable);

    /// <summary>并行读本地版本和注册表最新版，返回是否需要更新。</summary>
    public async Task<CheckResult> CheckAsync(CancellationToken ct)
    {
        var localTask = GetLocalVersionAsync(ct);
        var latestTask = GetLatestVersionAsync(ct);
        await Task.WhenAll(localTask, latestTask);

        var local = localTask.Result;
        var latest = latestTask.Result;
        return new CheckResult(local, latest, CompareVersions(latest, local) > 0);
    }

    /// <summary>全局更新 dsh 到 latest；失败抛异常（带 npm 输出尾部）。</summary>
    public async Task UpdateAsync(IProgress<string> status, CancellationToken ct)
    {
        status.Report("正在通过 npm 全局更新 @deepseek-ai/dsh …");
        var (code, stdout, stderr) = await RunProcessAsync(
            "cmd.exe", "/c npm install -g @deepseek-ai/dsh@latest", NpmInstallTimeout, ct);
        if (code != 0)
            throw new InvalidOperationException(
                $"npm 更新失败（退出码 {code}）：\n{Tail(stderr + "\n" + stdout, 1200)}");
        status.Report("npm 更新完成。");
    }

    private async Task<string> GetLocalVersionAsync(CancellationToken ct)
    {
        var (code, stdout, stderr) = await RunProcessAsync(
            "cmd.exe", "/c dsh --version", LocalVersionTimeout, ct);
        if (code != 0)
            throw new InvalidOperationException($"dsh --version 执行失败：{stderr.Trim()}");

        var version = stdout.Trim();
        if (!VersionPattern.IsMatch(version))
            throw new InvalidOperationException($"无法解析 dsh 版本号：{version}");
        return version;
    }

    private async Task<string> GetLatestVersionAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(PackumentUrl, ct));
        var latest = doc.RootElement.GetProperty("dist-tags").GetProperty("latest").GetString();
        return latest ?? throw new InvalidOperationException("注册表响应里没有 dist-tags.latest");
    }

    /// <summary>
    /// semver 比较：a &gt; b 返回正数。支持预发布标签（如 rc.6），
    /// 规则：同主版本下正式版 &gt; 预发布；标识符数字按数值、字母按字典序，数字 &lt; 字母。
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        var ma = VersionPattern.Match(a.Trim());
        var mb = VersionPattern.Match(b.Trim());
        if (!ma.Success || !mb.Success)
            return string.CompareOrdinal(a, b);

        for (var i = 1; i <= 3; i++)
        {
            var cmp = int.Parse(ma.Groups[i].Value).CompareTo(int.Parse(mb.Groups[i].Value));
            if (cmp != 0) return cmp;
        }

        var preA = ma.Groups[4].Value;
        var preB = mb.Groups[4].Value;
        if (preA.Length == 0 && preB.Length == 0) return 0;
        if (preA.Length == 0) return 1;
        if (preB.Length == 0) return -1;

        var idsA = preA.Split('.');
        var idsB = preB.Split('.');
        for (var i = 0; i < Math.Max(idsA.Length, idsB.Length); i++)
        {
            if (i >= idsA.Length) return -1;
            if (i >= idsB.Length) return 1;

            var numA = int.TryParse(idsA[i], out var ia);
            var numB = int.TryParse(idsB[i], out var ib);
            var cmp = (numA, numB) switch
            {
                (true, true) => ia.CompareTo(ib),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(idsA[i], idsB[i]),
            };
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 尽力而为 */ }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"命令执行超时（{timeout.TotalSeconds:F0} 秒）：{fileName} {arguments}");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string Tail(string text, int maxChars) =>
        text.Length <= maxChars ? text : "…" + text[^maxChars..];
}
