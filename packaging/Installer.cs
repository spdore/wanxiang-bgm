using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: TargetFramework(".NETFramework,Version=v4.8")]
[assembly: AssemblyVersion("1.9.7.0")]
[assembly: AssemblyFileVersion("1.9.7.0")]
[assembly: AssemblyTitle("Wanxiang BGM Player Installer")]

internal static class Installer
{
    internal static string Folder = GetInstallFolder();
    private static string GetInstallFolder()
    {
        using(RegistryKey key=Registry.CurrentUser.OpenSubKey(RegistryPath)) {
            string saved=key==null?null:key.GetValue("InstallLocation") as string;
            if(!String.IsNullOrWhiteSpace(saved)&&Path.IsPathRooted(saved)) {
                string full=Path.GetFullPath(saved);
                if(full.TrimEnd(Path.DirectorySeparatorChar)!=Path.GetPathRoot(full).TrimEnd(Path.DirectorySeparatorChar))return full;
            }
        }
        return Path.Combine(UserDirectory(Environment.SpecialFolder.LocalApplicationData,"LOCALAPPDATA",""),"Programs","WanxiangBgm");
    }
    internal static string DestinationFor(string selected)
    {
        if(String.IsNullOrWhiteSpace(selected)||!Path.IsPathRooted(selected))throw new ArgumentException("请选择有效的文件夹。");
        string full=Path.GetFullPath(selected).TrimEnd(Path.DirectorySeparatorChar);
        return String.Equals(Path.GetFileName(full),"WanxiangBgm",StringComparison.OrdinalIgnoreCase)?full:Path.Combine(full+Path.DirectorySeparatorChar,"WanxiangBgm");
    }
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WanxiangBgm";
    private static readonly string MenuFolder = Path.Combine(UserDirectory(Environment.SpecialFolder.Programs, "APPDATA", @"Microsoft\Windows\Start Menu\Programs"), "万象 BGM 播放器");
    private static readonly string DesktopFolder = UserDirectory(Environment.SpecialFolder.DesktopDirectory, "USERPROFILE", "Desktop");
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    internal static bool IsSupportedNativeArchitecture(ushort nativeMachine)
    {
        return nativeMachine == 0x8664; // IMAGE_FILE_MACHINE_AMD64
    }

    internal static bool IsAmd64Windows()
    {
        if (!Environment.Is64BitOperatingSystem) return false;
        try
        {
            ushort processMachine;
            ushort nativeMachine;
            return IsWow64Process2(GetCurrentProcess(), out processMachine, out nativeMachine) && IsSupportedNativeArchitecture(nativeMachine);
        }
        catch (EntryPointNotFoundException)
        {
            string native = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
            if (String.IsNullOrWhiteSpace(native)) native = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
            return String.Equals(native, "AMD64", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static bool IsSupportedWindowsVersion(Version os)
    {
        return os != null && (os.Major > 10 || (os.Major == 10 && os.Build >= 18362));
    }

    internal static bool ShouldInstallWebView2Runtime(bool runtimePresent) { return !runtimePresent; }
    internal static bool ShouldInstallCable(bool cablePresent) { return !cablePresent; }
    internal static bool IsElevationCancelled(int nativeErrorCode) { return nativeErrorCode == 1223; }
    internal static bool IsWebView2InstallVerified(bool runtimeDetectedAfterInstall) { return runtimeDetectedAfterInstall; }
    internal static string CableInstallStatus(bool cableDetectedAfterInstall)
    {
        return cableDetectedAfterInstall
            ? "已检测到 VB-CABLE。请按官方安装程序提示重启 Windows 后再使用语音混音。"
            : "VB-CABLE 官方安装程序已关闭，但当前还未检测到设备。请完成驱动窗口中的安装步骤并重启 Windows；之后可在播放器的语音设置中刷新设备。";
    }
    internal static bool IsValidWebView2Version(string value)
    {
        Version parsed;
        return !String.IsNullOrWhiteSpace(value) && Version.TryParse(value, out parsed) && parsed > new Version(0, 0, 0, 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OutputCaps {
        public ushort Manufacturer, Product; public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Formats; public ushort Channels, Reserved; public uint Support;
    }
    [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW")] private static extern uint waveOutGetDevCaps(IntPtr device, out OutputCaps caps, uint size);
    internal static bool HasCable() {
        for (uint i = 0; i < waveOutGetNumDevs(); i++) {
            OutputCaps caps;
            if (waveOutGetDevCaps(new IntPtr(i), out caps, (uint)Marshal.SizeOf(typeof(OutputCaps))) == 0 && !String.IsNullOrEmpty(caps.Name) && caps.Name.StartsWith("CABLE Input", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    internal static string InstallDriver() {
        if (!ShouldInstallCable(HasCable())) return "已检测到 VB-CABLE，无需重新安装。";
        string temporary = Path.Combine(Path.GetTempPath(), "WanxiangBgm-VBCABLE-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try {
            string archive = Path.Combine(temporary, "VB-CABLE.zip");
            WriteResource("Driver", archive); ZipFile.ExtractToDirectory(archive, temporary);
            string directory = Path.Combine(temporary, "VBCABLE_Driver_Pack45");
            string setup = Path.Combine(directory, Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe");
            Process process;
            try { process = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, Verb = "runas", WorkingDirectory = directory }); }
            catch (Win32Exception ex) { if (IsElevationCancelled(ex.NativeErrorCode)) return "你取消了管理员授权；播放器已安装，VB-CABLE 尚未安装。可重新运行安装包安装驱动。"; throw; }
            if (process == null) return "无法启动 VB-CABLE 官方安装程序。";
            using (process) { while (!process.WaitForExit(100)) Application.DoEvents(); }
            for (int i = 0; i < 20 && !HasCable(); i++) { Thread.Sleep(250); Application.DoEvents(); }
            return CableInstallStatus(HasCable());
        } finally {
            if (Path.GetFullPath(temporary).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(temporary, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private const string WebView2ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    internal static bool HasWebView2Runtime(out string version)
    {
        version = "";
        RegistryView[] views = Environment.Is64BitOperatingSystem ? new RegistryView[] { RegistryView.Registry64, RegistryView.Registry32 } : new RegistryView[] { RegistryView.Registry32 };
        RegistryHive[] hives = new RegistryHive[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine };
        foreach (RegistryHive hive in hives) foreach (RegistryView view in views)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey key = root.OpenSubKey(@"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + WebView2ClientId))
                {
                    string found = key == null ? null : key.GetValue("pv") as string;
                    if (IsValidWebView2Version(found)) { version = found; return true; }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            catch (ArgumentException) { }
        }
        return false;
    }

    private static void InstallWebView2Runtime(Action<string> report)
    {
        string installedVersion;
        if (!ShouldInstallWebView2Runtime(HasWebView2Runtime(out installedVersion))) { report("已检测到 WebView2 Runtime " + installedVersion + "，跳过安装。"); return; }
        report("缺少 WebView2 Runtime，正在通过微软官方 Bootstrapper 联网安装…");
        string temporary = Path.Combine(Path.GetTempPath(), "WanxiangBgm-WebView2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string setup = Path.Combine(temporary, "MicrosoftEdgeWebview2Setup.exe");
            WriteResource("WebBootstrapper", setup);
            using (Process process = Process.Start(new ProcessStartInfo(setup, "/silent /install") { UseShellExecute = false, WorkingDirectory = temporary }))
            {
                if (process == null) throw new InvalidOperationException("无法启动微软官方 WebView2 Bootstrapper。");
                while (!process.WaitForExit(100)) Application.DoEvents();
                int code = process.ExitCode;
                if (!IsWebView2InstallVerified(HasWebView2Runtime(out installedVersion)))
                    throw new InvalidOperationException("微软 Bootstrapper 未能安装 WebView2 Runtime（退出代码 " + code + "）。缺少 Runtime 时需要联网访问 Microsoft 下载服务；请检查网络或组织策略后重试。");
                report("WebView2 Runtime 已就绪（" + installedVersion + "），并已通过安装后检测。");
            }
        }
        finally
        {
            if (Path.GetFullPath(temporary).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                try { Directory.Delete(temporary, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string UserDirectory(Environment.SpecialFolder kind, string variable, string suffix)
    {
        string path = Environment.GetFolderPath(kind);
        if (String.IsNullOrWhiteSpace(path))
        {
            string root = Environment.GetEnvironmentVariable(variable);
            if (!String.IsNullOrWhiteSpace(root)) path = Path.Combine(root, suffix);
        }
        if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new InvalidOperationException("无法获取当前用户的安装目录。");
        return path;
    }

    [STAThread]
    private static void Main(string[] args)
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length == 2 && args[0] == "/remove")
        {
            try
            {
                try { Process.GetProcessById(Int32.Parse(args[1])).WaitForExit(10000); } catch (ArgumentException) { }
                RemoveFiles();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "卸载失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            return;
        }
        if (args.Length > 0 && args[0] == "/uninstall")
        {
            Uninstall();
            return;
        }
#if UNINSTALLER_ONLY
        MessageBox.Show("此程序仅用于卸载万象 BGM 播放器。", "万象 BGM 播放器", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
#else
        if (!IsAmd64Windows())
        {
            MessageBox.Show("此安装包仅支持 AMD64（Intel/AMD）架构的 64 位 Windows 10/11。", "不支持的系统", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var form=new SetupForm();
        Application.Run(form);
#endif
    }

    internal static void CheckNotRunning()
    {
        try
        {
            using (Mutex mutex = Mutex.OpenExisting(@"Local\BgmHotkeyShare.SingleInstance"))
            {
                bool acquired;
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("请先关闭正在运行的分享版播放器，再安装或卸载。");
                mutex.ReleaseMutex();
            }
        }
        catch (WaitHandleCannotBeOpenedException) { }
    }

    private static void WriteResource(string resource, string path)
    {
        using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
        {
            if (source == null) throw new InvalidOperationException("安装包缺少文件：" + resource);
            string staged = path + ".new";
            try
            {
                using (FileStream destination = File.Create(staged)) source.CopyTo(destination);
                if (File.Exists(path)) File.Replace(staged, path, null);
                else File.Move(staged, path);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }
    }

    internal static void Install(bool desktopShortcut, Action<string> report)
    {
        CheckNotRunning();
        Version os = Environment.OSVersion.Version;
        if (!IsSupportedWindowsVersion(os))
            throw new InvalidOperationException("此版本需要 64 位 Windows 10 1903（内部版本 18362）或更高版本，或 Windows 11。该系统要求也确保 .NET Framework 4.8 可用。");
        using (RegistryKey framework = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
        {
            if (framework == null || Convert.ToInt32(framework.GetValue("Release", 0)) < 528040)
                throw new InvalidOperationException("请先安装 Microsoft .NET Framework 4.8，再运行安装程序。");
        }
        InstallWebView2Runtime(report);
        report("正在安装播放器文件与快捷方式…");
        Directory.CreateDirectory(Folder);
        WriteResource("Player", Path.Combine(Folder, "BgmHotkey.exe"));
        WriteResource("IconLicense", Path.Combine(Folder, "Tabler-LICENSE.txt"));
        WriteResource("WebLicense", Path.Combine(Folder, "WebView2-LICENSE.txt"));
        WriteResource("WebNotice", Path.Combine(Folder, "WebView2-NOTICE.txt"));
        WriteResource("MusicConverterNotice", Path.Combine(Folder, "MusicConverter-NOTICES.txt"));
        WriteResource("WebCore", Path.Combine(Folder, "Microsoft.Web.WebView2.Core.dll"));
        WriteResource("WebForms", Path.Combine(Folder, "Microsoft.Web.WebView2.WinForms.dll"));
        WriteResource(Environment.Is64BitOperatingSystem ? "WebLoader64" : "WebLoader32", Path.Combine(Folder, "WebView2Loader.dll"));
        WriteResource("Config", Path.Combine(Folder, "BgmHotkey.exe.config"));
        WriteResource("Guide", Path.Combine(Folder, "Installation Guide.txt"));
        WriteResource("Uninstaller", Path.Combine(Folder, "Uninstall.exe"));
        WriteResource("Config", Path.Combine(Folder, "Uninstall.exe.config"));
        Directory.CreateDirectory(MenuFolder);
        Shortcut(Path.Combine(MenuFolder, "万象 BGM 播放器.lnk"), Path.Combine(Folder, "BgmHotkey.exe"), "");
        Shortcut(Path.Combine(MenuFolder, "卸载万象 BGM 播放器.lnk"), Path.Combine(Folder, "Uninstall.exe"), "/uninstall");
        if (desktopShortcut)
            Shortcut(Path.Combine(DesktopFolder, "万象 BGM 播放器.lnk"), Path.Combine(Folder, "BgmHotkey.exe"), "");
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
        {
            key.SetValue("DisplayName", "万象 BGM 播放器");
            key.SetValue("DisplayVersion", "1.9.7");
            key.SetValue("InstallLocation", Folder);
            key.SetValue("DisplayIcon", Path.Combine(Folder, "BgmHotkey.exe"));
            key.SetValue("UninstallString", "\"" + Path.Combine(Folder, "Uninstall.exe") + "\" /uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }

    private static void Shortcut(string path, string target, string arguments)
    {
        object shell = null;
        object shortcut = null;
        try
        {
            Type type = Type.GetTypeFromProgID("WScript.Shell");
            shell = Activator.CreateInstance(type);
            shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Folder });
            shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
            if (shell != null) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void Uninstall()
    {
        try
        {
            CheckNotRunning();
            if (MessageBox.Show("卸载万象 BGM 播放器？\n个人歌曲和设置会保留。", "卸载", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            // Run a temporary copy so the installed uninstaller can also be removed.
            string installed = Path.Combine(Folder, "Uninstall.exe");
            if (String.Equals(Path.GetFullPath(Application.ExecutablePath), installed, StringComparison.OrdinalIgnoreCase))
            {
                string temporary = Path.Combine(Path.GetTempPath(), "WanxiangBgm-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(installed, temporary);
                Process.Start(new ProcessStartInfo(temporary, "/remove " + Process.GetCurrentProcess().Id) { UseShellExecute = false });
                return;
            }
            RemoveFiles();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "卸载失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    internal static void RemoveFiles()
    {
        CheckNotRunning();
        foreach (string file in new string[] { "Tabler-LICENSE.txt", "WebView2-LICENSE.txt", "WebView2-NOTICE.txt", "MusicConverter-NOTICES.txt", "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll", "BgmHotkey.exe", "BgmHotkey.exe.config", "Installation Guide.txt", "Uninstall.exe", "Uninstall.exe.config" })
            File.Delete(Path.Combine(Folder, file));
        foreach (string file in new string[] { "万象 BGM 播放器.lnk", "卸载万象 BGM 播放器.lnk" }) File.Delete(Path.Combine(MenuFolder, file));
        File.Delete(Path.Combine(DesktopFolder, "万象 BGM 播放器.lnk"));
        if (Directory.Exists(MenuFolder) && Directory.GetFileSystemEntries(MenuFolder).Length == 0) Directory.Delete(MenuFolder);
        if (Directory.Exists(Folder) && Directory.GetFileSystemEntries(Folder).Length == 0) Directory.Delete(Folder);
        Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, false);
        MessageBox.Show("程序已卸载。个人歌曲与设置已保留。", "卸载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

internal sealed class SetupForm : Form
{
    private readonly Button _next;
    private readonly Button _back;
    private readonly Button _cancel;
    private readonly Label _destination;
    private readonly Button _browse;
    private readonly CheckBox _desktop;
    private readonly CheckBox _driver;
    private readonly Panel[] _pages = new Panel[4];
    private readonly Panel _pageHost;
    private readonly Label _pageTitle;
    private readonly Label _pageStep;
    private readonly Label _progressText;
    private readonly ProgressBar _progress;
    private readonly Label _finishText;
    private readonly ToolTip _pathTip;
    private int _step;
    private bool _busy;
    private bool _playerInstalled;
    private bool _driverPending;

    internal SetupForm()
    {
        SuspendLayout();
        Text = "安装万象 BGM 播放器";
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(760, 560);
        MinimumSize = new Size(680, 510);
        BackColor = Color.FromArgb(246, 248, 250);
        ForeColor = Color.FromArgb(27, 41, 53);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(26, 20, 26, 18), Margin = new Padding(0) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        Controls.Add(root);

        TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize)); header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Label brand = new Label { Text = "万象 / BGM", AutoSize = true, Font = new Font(Font.FontFamily, 19F, FontStyle.Bold), ForeColor = Color.FromArgb(19, 42, 58), Margin = new Padding(0, 0, 0, 0) };
        _pageStep = new Label { Text = "安装向导", AutoSize = true, ForeColor = Color.FromArgb(90, 111, 126), TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(0, 8, 0, 0) };
        _pageTitle = new Label { Text = "播放器安装向导", AutoSize = true, Font = new Font(Font.FontFamily, 9F), ForeColor = Color.FromArgb(96, 112, 125), Margin = new Padding(0, 0, 0, 0) };
        header.Controls.Add(brand, 0, 0); header.Controls.Add(_pageStep, 1, 0); header.Controls.Add(_pageTitle, 0, 1); header.SetColumnSpan(_pageTitle, 2);
        root.Controls.Add(header, 0, 0);

        _pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(22), Margin = new Padding(0, 8, 0, 8) };
        _pageHost.Paint += delegate(object sender, PaintEventArgs e) { using (Pen pen = new Pen(Color.FromArgb(222, 230, 235))) e.Graphics.DrawRectangle(pen, 0, 0, _pageHost.Width - 1, _pageHost.Height - 1); };
        root.Controls.Add(_pageHost, 0, 1);

        _pathTip = new ToolTip();
        _destination = new Label { Text = Installer.Folder, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.FromArgb(247, 249, 251), Padding = new Padding(12, 0, 12, 0), Margin = new Padding(0, 0, 10, 0), MinimumSize = new Size(0, 42), AccessibleName = "安装位置" };
        _pathTip.SetToolTip(_destination, Installer.Folder);
        _browse = new Button { Text = "浏览…", AutoSize = true, MinimumSize = new Size(94, 42), FlatStyle = FlatStyle.Flat, BackColor = Color.White, Margin = new Padding(0) };
        _browse.FlatAppearance.BorderColor = Color.FromArgb(180, 194, 203);
        _browse.Click += delegate { try { string selected = ModernFolderPicker.Select(this, Installer.Folder); if (selected == null) return; Installer.Folder = Installer.DestinationFor(selected); _destination.Text = Installer.Folder; _pathTip.SetToolTip(_destination, Installer.Folder); } catch (Exception ex) { MessageBox.Show(this, "无法打开文件夹选择器：" + ex.Message, "安装位置", MessageBoxButtons.OK, MessageBoxIcon.Error); } };

        _desktop = new CheckBox { Text = "创建桌面快捷方式", Checked = true, AutoSize = true, Margin = new Padding(0, 8, 0, 2) };
        bool cableExists = Installer.HasCable();
        _driver = new CheckBox { Text = cableExists ? "已检测到 VB-CABLE，保留现有设备" : "安装 VB-CABLE 虚拟音频设备", Checked = Installer.ShouldInstallCable(cableExists), Enabled = Installer.ShouldInstallCable(cableExists), AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        _finishText = new Label { Dock = DockStyle.Fill, AutoSize = false, ForeColor = Color.FromArgb(48, 65, 78), Padding = new Padding(0), TextAlign = ContentAlignment.TopLeft };
        _progressText = new Label { Dock = DockStyle.Top, AutoSize = true, Text = "正在准备安装…", Margin = new Padding(0, 0, 0, 16), ForeColor = Color.FromArgb(47, 68, 84) };
        _progress = new ProgressBar { Dock = DockStyle.Top, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 24, Height = 10 };

        _pages[0] = BuildPage(new Label { Text = "一个安装包，完成播放器与运行环境准备。", AutoSize = true, MaximumSize = new Size(590, 0), Font = new Font(Font.FontFamily, 18F, FontStyle.Bold), ForeColor = Color.FromArgb(23, 47, 63), Margin = new Padding(0, 0, 0, 14) },
            Body("默认曲库为空。安装后添加自己的歌曲并设置快捷键。升级时会保留已有歌曲和设置。"),
            Body("安装器会先检测 WebView2 Runtime 并复用已有版本。若缺少，将通过微软官方 Bootstrapper 联网安装；请保持网络畅通。"),
            Body("VB-CABLE 官方驱动也已包含。首次安装需要 Windows 管理员授权，并按驱动提示重启电脑。"),
            Body("VB-CABLE 由 VB-Audio 提供，官方网站为 www.vb-cable.com。它采用 donationware 模式，可通过官网了解并支持作者："),
            WebLink());

        TableLayoutPanel location = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 5, AutoSize = true, Margin = new Padding(0) };
        location.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); location.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        location.RowStyles.Add(new RowStyle(SizeType.AutoSize)); location.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); location.RowStyles.Add(new RowStyle(SizeType.AutoSize)); location.RowStyles.Add(new RowStyle(SizeType.AutoSize)); location.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        location.Controls.Add(Body("安装位置"), 0, 0); location.SetColumnSpan(location.GetControlFromPosition(0, 0), 2);
        location.Controls.Add(_destination, 0, 1); location.Controls.Add(_browse, 1, 1);
        location.Controls.Add(_desktop, 0, 2); location.SetColumnSpan(_desktop, 2);
        location.Controls.Add(_driver, 0, 3); location.SetColumnSpan(_driver, 2);
        location.Controls.Add(Body(cableExists ? "现有 VB-CABLE 不会被重装。" : "缺少时会启动官方驱动安装程序；请按其界面完成安装、授权与重启。"), 0, 4); location.SetColumnSpan(location.GetControlFromPosition(0, 4), 2);
        _pages[1] = BuildPage(location);

        TableLayoutPanel progressPage = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 16, 0, 0) };
        progressPage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); progressPage.RowStyles.Add(new RowStyle(SizeType.AutoSize)); progressPage.RowStyles.Add(new RowStyle(SizeType.Absolute, 16)); progressPage.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        progressPage.Controls.Add(Body("请保持安装程序打开。现有歌曲和配置不会被覆盖删除。"), 0, 2);
        _pages[2] = BuildPage(_progressText, _progress, progressPage);
        _pages[3] = BuildPage(_finishText);
        foreach (Panel page in _pages) _pageHost.Controls.Add(page);

        FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        _next = new Button { Text = "下一步", AutoSize = true, MinimumSize = new Size(112, 40), BackColor = Color.FromArgb(19, 116, 132), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(0) };
        _next.FlatAppearance.BorderSize = 0; _next.Click += NextClicked;
        _back = new Button { Text = "上一步", AutoSize = true, MinimumSize = new Size(92, 40), FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 10, 0) };
        _back.Click += delegate { if (_step == 1) ShowStep(0); };
        _cancel = new Button { Text = "关闭", AutoSize = true, MinimumSize = new Size(82, 40), FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 10, 0) };
        _cancel.Click += delegate { Close(); };
        actions.Controls.Add(_next); actions.Controls.Add(_back); actions.Controls.Add(_cancel); root.Controls.Add(actions, 0, 2);
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (_busy) e.Cancel = true; };
        AcceptButton = _next; AutoScaleDimensions = new SizeF(96, 96); ShowStep(0); ResumeLayout(true);
    }

    private static Label Body(string text) { return new Label { Text = text, AutoSize = true, MaximumSize = new Size(600, 0), Font = new Font("Microsoft YaHei UI", 10F), ForeColor = Color.FromArgb(83, 101, 115), Margin = new Padding(0, 0, 0, 14) }; }
    private static LinkLabel WebLink() { LinkLabel link = new LinkLabel { Text = "VB-Audio 官方产品页与捐赠方式", AutoSize = true, LinkColor = Color.FromArgb(13, 126, 145), VisitedLinkColor = Color.FromArgb(13, 126, 145), Margin = new Padding(0, 0, 0, 6) }; link.Click += delegate { try { Process.Start(new ProcessStartInfo("https://vb-audio.com/Cable/") { UseShellExecute = true }); } catch { } }; return link; }
    private static Panel BuildPage(params Control[] controls)
    {
        TableLayoutPanel flow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = controls.Length, Padding = new Padding(0), Margin = new Padding(0) };
        flow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < controls.Length; i++) { flow.RowStyles.Add(new RowStyle(SizeType.AutoSize)); flow.Controls.Add(controls[i], 0, i); }
        Panel page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false, Padding = new Padding(0), BackColor = Color.White };
        page.Controls.Add(flow); flow.Location = new Point(0, 0); return page;
    }
    private void ShowStep(int step)
    {
        _step = step; for (int i = 0; i < _pages.Length; i++) _pages[i].Visible = i == step;
        _pages[step].BringToFront(); _pageStep.Text = step == 0 ? "欢迎" : step == 1 ? "安装位置" : step == 2 ? "正在安装" : _playerInstalled ? "已完成" : "安装未完成";
        _pageTitle.Text = step == 0 ? "准备安装播放器" : step == 1 ? "确认安装选项" : step == 2 ? "正在准备运行环境" : _playerInstalled ? "播放器已安装" : "请重试安装";
        _back.Visible = step == 1; _back.Enabled = step == 1; _cancel.Visible = step != 2; _browse.Enabled = step == 1;
        _next.Visible = step != 2; _next.Text = step == 0 ? "下一步" : step == 1 ? "安装" : _driverPending ? "重试 VB-CABLE" : _playerInstalled ? "启动播放器" : "重试";
        _next.Enabled = true; AcceptButton = _next;
    }
    private void NextClicked(object sender, EventArgs e)
    {
        if (_step == 0) { ShowStep(1); return; }
        if (_step == 1 || (_step == 3 && !_playerInstalled)) { InstallClicked(); return; }
        if (_step == 3 && _driverPending) { RetryDriverClicked(); return; }
        if (_step == 3 && _playerInstalled)
        {
            try { Process.Start(Path.Combine(Installer.Folder, "BgmHotkey.exe")); Close(); }
            catch (Exception ex) { _finishText.Text = "播放器已安装，但启动失败：" + ex.Message; }
        }
    }
    private void InstallClicked()
    {
        _busy = true; _next.Enabled = false; _back.Enabled = false; _cancel.Enabled = false; ShowStep(2); _busy = true; _cancel.Visible = false; Refresh();
        string driverMessage = "";
        try
        {
            Installer.Install(_desktop.Checked, delegate(string message) { _progressText.Text = message; _progressText.Refresh(); Application.DoEvents(); });
            _playerInstalled = true;
            if (_driver.Checked) { _driverPending = !Installer.HasCable(); _progressText.Text = "正在启动 VB-AUDIO 官方驱动安装程序…"; _progressText.Refresh(); Application.DoEvents(); driverMessage = Installer.InstallDriver(); _driverPending = !Installer.HasCable(); }
            else { _driverPending = false; driverMessage = Installer.HasCable() ? "已保留系统中现有的 VB-CABLE。" : "未安装 VB-CABLE；播放器仍可本地播放，语音混音需要该驱动。"; }
            _finishText.Text = FinishSummary(driverMessage);
            ShowStep(3);
        }
        catch (Exception ex)
        {
            _driverPending = _playerInstalled && _driver.Checked && !Installer.HasCable();
            _finishText.Text = "安装未完成：" + ex.Message + "\r\n\r\n请解决提示的问题后点击重试。歌曲和用户设置不会被删除。";
            ShowStep(3);
        }
        finally { _busy = false; _cancel.Enabled = true; _next.Enabled = true; _back.Enabled = true; }
    }

    private string FinishSummary(string driverMessage)
    {
        string text = "播放器与 WebView2 Runtime 已准备完成。";
        text += "\r\n\r\n" + driverMessage;
        if (_driverPending) text += "\r\n\r\n播放器已可启动；按“重试 VB-CABLE”再次运行官方安装程序，或关闭此向导稍后再安装。";
        if (Installer.HasCable()) text += "\r\n\r\n游戏或 QQ 的麦克风设备请选择 CABLE Output。";
        text += "\r\n\r\n歌曲和设置保存在用户目录，不包含在安装包中。";
        return text;
    }

    private void RetryDriverClicked()
    {
        _busy = true; _next.Enabled = false; _back.Enabled = false; _cancel.Enabled = false; ShowStep(2); _cancel.Visible = false; Refresh();
        try
        {
            _progressText.Text = "正在启动 VB-AUDIO 官方驱动安装程序…"; _progressText.Refresh(); Application.DoEvents();
            string message = Installer.InstallDriver();
            _driverPending = !Installer.HasCable();
            _finishText.Text = FinishSummary(message);
            ShowStep(3);
        }
        catch (Exception ex)
        {
            _driverPending = !Installer.HasCable();
            _finishText.Text = "VB-CABLE 安装未完成：" + ex.Message + "\r\n\r\n请查看官方安装程序提示后，点击“重试 VB-CABLE”。";
            ShowStep(3);
        }
        finally { _busy = false; _cancel.Enabled = true; _next.Enabled = true; _back.Enabled = true; }
    }
}

internal static class ModernFolderPicker {
 [ComImport,Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
 private interface IFileDialog {
  [PreserveSig]int Show(IntPtr owner);
  void SetFileTypes(uint count,IntPtr specs);void SetFileTypeIndex(uint index);void GetFileTypeIndex(out uint index);
  void Advise(IntPtr events,out uint cookie);void Unadvise(uint cookie);void SetOptions(uint options);void GetOptions(out uint options);
  void SetDefaultFolder(IShellItem item);void SetFolder(IShellItem item);void GetFolder(out IShellItem item);void GetCurrentSelection(out IShellItem item);
  void SetFileName([MarshalAs(UnmanagedType.LPWStr)]string name);void GetFileName(out IntPtr name);
  void SetTitle([MarshalAs(UnmanagedType.LPWStr)]string title);void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)]string text);void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)]string label);
  void GetResult(out IShellItem item);void AddPlace(IShellItem item,int alignment);void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)]string extension);
  void Close(int result);void SetClientGuid(ref Guid guid);void ClearClientData();void SetFilter(IntPtr filter);
 }
 [ComImport,Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
 private interface IShellItem {
  void BindToHandler(IntPtr context,ref Guid handler,ref Guid iid,out IntPtr result);void GetParent(out IShellItem parent);
  void GetDisplayName(uint kind,out IntPtr name);void GetAttributes(uint mask,out uint attributes);void Compare(IShellItem item,uint hint,out int order);
 }
 [DllImport("shell32.dll",CharSet=CharSet.Unicode,PreserveSig=false)]static extern void SHCreateItemFromParsingName(string name,IntPtr context,ref Guid iid,out IShellItem item);
 internal static string Select(IWin32Window owner,string current) {
  var dialog=(IFileDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")));
  IShellItem initial=null,result=null;IntPtr name=IntPtr.Zero;
  try {
   uint options;dialog.GetOptions(out options);dialog.SetOptions(options|0x20|0x40|0x8|0x02000000);
   dialog.SetTitle("选择安装位置");dialog.SetOkButtonLabel("选择此文件夹");
   string start=current;while(!String.IsNullOrEmpty(start)&&!Directory.Exists(start))start=Path.GetDirectoryName(start);
   if(!String.IsNullOrEmpty(start)){Guid iid=new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");SHCreateItemFromParsingName(start,IntPtr.Zero,ref iid,out initial);dialog.SetFolder(initial);}
   int hr=dialog.Show(owner.Handle);if(hr==unchecked((int)0x800704C7))return null;Marshal.ThrowExceptionForHR(hr);
   dialog.GetResult(out result);result.GetDisplayName(0x80058000,out name);return Marshal.PtrToStringUni(name);
  }finally{if(name!=IntPtr.Zero)Marshal.FreeCoTaskMem(name);if(result!=null)Marshal.ReleaseComObject(result);if(initial!=null)Marshal.ReleaseComObject(initial);Marshal.ReleaseComObject(dialog);}
 }
}
