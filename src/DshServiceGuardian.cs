using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DshBar;

/// <summary>dsh web 的连接信息：BaseUrl 用于探测，LaunchUrl（带一次性令牌）用于导航。</summary>
public sealed record DshServiceInfo(Uri BaseUrl, Uri? LaunchUrl);

/// <summary>
/// dsh web 服务守护：保证 127.0.0.1 上有一个可用的 dsh web 实例。
/// 已在运行 → 直接返回其 URL；未运行 → 派生 `dsh web` 子进程并轮询到就绪。
/// 只管理自己拉起的进程；探测到的已有服务永远不被动它。
///
/// 新版本 dsh web（≥ 0.1.2-rc.1）有认证墙：首页需要 ?token=（启动令牌），
/// 换取签名 Cookie 后才能访问。令牌只出现在 dsh web 自己打印的 stdout URL 里，
/// 因此只有"我们拉起的"服务才能拿到 LaunchUrl；探测到的已有服务没有令牌，
/// 靠 WebView2 持久化目录里之前换得的 Cookie 通行（密钥持久于 ~/.dsh）。
/// </summary>
public sealed class DshServiceGuardian : IDisposable
{
    private static readonly Uri DefaultUrl = new("http://127.0.0.1:3080/");
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(120);

    /// <summary>首页 HTML 里的 DSH 特征，防止端口被无关程序占用时误连。</summary>
    private static readonly string[] PageMarkers = { "__DSH_BOOT__", "dsh", "deepseek" };

    private static readonly Regex UrlPattern =
        new(@"https?://[^\s""'<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    private Process? _ownedProcess;
    private readonly StringBuilder _processOutput = new();

    public DshServiceGuardian()
    {
        // 探测不跟随重定向：303/401 本身就是"服务活着"的判定依据
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(3),
        };
    }

    /// <summary>本进程是否拥有（拉起了）dsh 服务进程。</summary>
    public bool OwnsService => _ownedProcess is not null;

    /// <summary>
    /// 确保服务可用，返回探测/导航所需的 URL 信息。
    /// </summary>
    public async Task<DshServiceInfo> EnsureRunningAsync(IProgress<string> status, CancellationToken ct)
    {
        status.Report("正在探测本地 dsh web 服务（127.0.0.1:3080）…");
        if (await ProbeAsync(DefaultUrl, ct))
        {
            status.Report("检测到已在运行的 dsh web，直接连接。");
            return new DshServiceInfo(DefaultUrl, LaunchUrl: null);
        }

        status.Report("未检测到服务，正在启动 dsh web …");
        StartOwnedProcess();
        return await WaitUntilReadyAsync(status, ct);
    }

    /// <summary>
    /// 重启服务：杀掉当前占用 3080 的 dsh 进程（无论是不是我们拉起的），
    /// 再启动一个全新的 dsh web 并等待就绪。调用方负责随后刷新 WebView。
    /// 重启后服务由我们拉起，LaunchUrl 必带令牌。
    /// </summary>
    public async Task<DshServiceInfo> RestartAsync(IProgress<string> status, CancellationToken ct)
    {
        status.Report("正在停止当前 dsh 服务…");

        if (_ownedProcess is not null && !_ownedProcess.HasExited)
        {
            try { _ownedProcess.Kill(entireProcessTree: true); } catch { /* 进程已退出则忽略 */ }
        }
        else
        {
            // 服务不是我们拉起的：按端口找到宿主进程结束它
            KillPortOwner(DefaultUrl.Port);
        }

        _ownedProcess?.Dispose();
        _ownedProcess = null;
        _processOutput.Clear();

        // 给端口释放留一点时间
        await Task.Delay(1000, ct);

        status.Report("正在启动新的 dsh web …");
        StartOwnedProcess();
        return await WaitUntilReadyAsync(status, ct);
    }

    /// <summary>结束正在监听指定 TCP 端口的进程（netstat 反查 PID）。</summary>
    private static void KillPortOwner(int port)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netstat",
            Arguments = "-ano -p tcp",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var netstat = Process.Start(psi);
        if (netstat is null) return;

        var suffix = $":{port}";
        var killed = new HashSet<int>();
        while (netstat.StandardOutput.ReadLine() is { } line)
        {
            // 行格式：TCP  127.0.0.1:3080  0.0.0.0:0  LISTENING  12345
            if (!line.Contains("LISTENING") || !line.Contains(suffix))
                continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !int.TryParse(parts[^1], out var pid) || !killed.Add(pid))
                continue;

            try
            {
                using var owner = Process.GetProcessById(pid);
                owner.Kill(entireProcessTree: true);
            }
            catch { /* 进程可能刚好退出，或权限不足；下一轮探测会反映结果 */ }
        }
    }

    /// <summary>启动后轮询：解析 stdout 里的实际 URL（含令牌），探测到首页就绪为止。</summary>
    private async Task<DshServiceInfo> WaitUntilReadyAsync(IProgress<string> status, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReadyTimeout;
        Uri baseUrl = DefaultUrl;
        Uri? launchUrl = null;
        var urlLocked = false;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_ownedProcess!.HasExited)
                throw new InvalidOperationException(
                    $"dsh web 启动后即退出（退出码 {_ownedProcess.ExitCode}）。\n进程输出：\n{Tail(_processOutput.ToString(), 1200)}");

            // dsh web 的 URL 行由 shell 打印到 stdout；带 ?token= 启动令牌（新版），
            // 端口被占时可能换成别的端口
            if (!urlLocked)
            {
                var match = UrlPattern.Match(_processOutput.ToString());
                if (match.Success)
                {
                    launchUrl = new Uri(match.Value.TrimEnd('.', ')', ']'));
                    baseUrl = new Uri(launchUrl.GetLeftPart(UriPartial.Authority) + "/");
                    urlLocked = true;
                    status.Report($"服务地址：{baseUrl}，等待就绪…");
                }
            }

            if (await ProbeAsync(baseUrl, ct))
            {
                status.Report("dsh web 已就绪。");
                return new DshServiceInfo(baseUrl, launchUrl);
            }

            await Task.Delay(500, ct);
        }

        throw new TimeoutException(
            $"等待 dsh web 就绪超时（{ReadyTimeout.TotalSeconds:F0} 秒）。\n进程输出：\n{Tail(_processOutput.ToString(), 1200)}");
    }

    private void StartOwnedProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c dsh web",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        _ownedProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _ownedProcess.OutputDataReceived += (_, e) => { if (e.Data is not null) _processOutput.AppendLine(e.Data); };
        _ownedProcess.ErrorDataReceived += (_, e) => { if (e.Data is not null) _processOutput.AppendLine(e.Data); };

        try
        {
            _ownedProcess.Start();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法启动 dsh web：{ex.Message}", ex);
        }

        _ownedProcess.BeginOutputReadLine();
        _ownedProcess.BeginErrorReadLine();
    }

    /// <summary>
    /// GET 首页判定“是我们的服务”：
    /// 2xx 且含 DSH 特征（旧版无认证）；401 且正文含 dsh（新版认证墙，同样是存活证据）；
    /// 3xx 重定向（令牌被接受后的 303 等）也算。
    /// </summary>
    private async Task<bool> ProbeAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)resp.StatusCode;

            if (code is >= 300 and < 400)
                return true;

            var body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return body.Contains("dsh", StringComparison.OrdinalIgnoreCase);

            if (code is < 200 or >= 300)
                return false;

            foreach (var marker in PageMarkers)
            {
                if (body.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string Tail(string text, int maxChars) =>
        text.Length <= maxChars ? text : "…" + text[^maxChars..];

    /// <summary>
    /// 只清理自己拉起的进程；已存在的服务不受影响。
    /// 默认退出时保留服务常驻（下次秒连）；如需随壳退出，把 killOwned 调为 true。
    /// </summary>
    public void Dispose(bool killOwned = false)
    {
        _http.Dispose();
        if (_ownedProcess is null) return;

        if (killOwned && !_ownedProcess.HasExited)
        {
            try { _ownedProcess.Kill(entireProcessTree: true); } catch { /* 尽力而为 */ }
        }
        _ownedProcess.Dispose();
        _ownedProcess = null;
    }

    public void Dispose() => Dispose(killOwned: false);
}
