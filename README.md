# DshBar — dsh 桌面壳

DeepSeek Harness（dsh）的 Windows 桌面客户端。一个常驻系统托盘的 WPF 小工具，自动管理本机 `dsh web` 服务，并用 WebView2 内嵌 Harness Web GUI。

## 功能

- **服务守护**：探测本机 `127.0.0.1:3080` 的 dsh web 服务；未运行时自动派生 `dsh web` 子进程并等待就绪
- **WebView2 内嵌**：直接嵌入 Harness Web GUI，固定用户数据目录，保留登录状态
- **托盘 + 热键**：`Alt` + 双击 `H` 唤出/隐藏窗口；关窗最小化到托盘常驻
- **一键更新**：通过 npm 全局更新 `@deepseek-ai/dsh`（对比 npm registry 的 dist-tags.latest，完整 semver 预发布比较）
- **服务重启**：杀掉占用端口的 dsh 进程并启动全新实例
- **开机自启**：注册 HKCU Run 键，`--background` 参数静默驻留
- **跟随 GUI 语言/主题**：语言与深色主题与 Harness Web GUI 设置同步

## 构建

要求：.NET 8 SDK（Windows，WPF）。

```bash
dotnet build src/DshBar.csproj -c Release
```

发布（单文件、免安装 WebView2）：

```bash
dotnet publish src/DshBar.csproj -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -o publish-single
```

## 使用

- 双击 `DshBar.exe` 启动，首次会检查并启动本机 dsh web 服务
- 系统托盘鲸鱼图标：左键/双击唤出窗口；右键菜单提供开关、重启、更新、开机自启、退出
- 全局热键：按住 `Alt` 快速按两下 `H` 切换窗口显隐
- 右上角 ✕ 最小化到托盘，退出请走托盘菜单

## 技术栈

- WPF（.NET 8, net8.0-windows）
- Microsoft.Web.WebView2
- Hardcodet.NotifyIcon.Wpf（托盘图标）
- Win32 P/Invoke：全局热键、DWM 圆角、非客户区拖拽调整大小

## 版本

0.3.0
