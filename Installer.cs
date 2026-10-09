using System;
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
    internal static void InstallDriver() {
        if (HasCable()) return;
        string temporary = Path.Combine(Path.GetTempPath(), "WanxiangBgm-VBCABLE-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try {
            string archive = Path.Combine(temporary, "VB-CABLE.zip");
            WriteResource("Driver", archive); ZipFile.ExtractToDirectory(archive, temporary);
            string directory = Path.Combine(temporary, "VBCABLE_Driver_Pack45");
            string setup = Path.Combine(directory, Environment.Is64BitOperatingSystem ? "VBCABLE_Setup_x64.exe" : "VBCABLE_Setup.exe");
            using (Process process = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, Verb = "runas", WorkingDirectory = directory })) { while (!process.WaitForExit(100)) Application.DoEvents(); }
        } finally {
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
        var form=new SetupForm();
        if(Array.IndexOf(args,"--capture-ui")>=0){
            var capture=new System.Windows.Forms.Timer{Interval=1200};capture.Tick+=delegate{capture.Stop();using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath),"setup-preview.png"));}capture.Dispose();};form.Shown+=delegate{capture.Start();};
        }
        Application.Run(form);
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

    internal static void Install(bool desktopShortcut)
    {
        CheckNotRunning();
        using (RegistryKey framework = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
        {
            if (framework == null || Convert.ToInt32(framework.GetValue("Release", 0)) < 528040)
                throw new InvalidOperationException("请先安装 Microsoft .NET Framework 4.8，再运行安装程序。");
        }
        Directory.CreateDirectory(Folder);
        WriteResource("Player", Path.Combine(Folder, "BgmHotkey.exe"));
        WriteResource("IconLicense", Path.Combine(Folder, "Tabler-LICENSE.txt"));
        WriteResource("WebLicense", Path.Combine(Folder, "WebView2-LICENSE.txt"));
        WriteResource("WebNotice", Path.Combine(Folder, "WebView2-NOTICE.txt"));
        WriteResource("WebCore", Path.Combine(Folder, "Microsoft.Web.WebView2.Core.dll"));
        WriteResource("WebForms", Path.Combine(Folder, "Microsoft.Web.WebView2.WinForms.dll"));
        WriteResource(Environment.Is64BitOperatingSystem ? "WebLoader64" : "WebLoader32", Path.Combine(Folder, "WebView2Loader.dll"));
        WriteResource("Config", Path.Combine(Folder, "BgmHotkey.exe.config"));
        WriteResource("Guide", Path.Combine(Folder, "使用说明.txt"));
        File.Copy(Application.ExecutablePath, Path.Combine(Folder, "Uninstall.exe"), true);
        WriteResource("Config", Path.Combine(Folder, "Uninstall.exe.config"));
        Directory.CreateDirectory(MenuFolder);
        Shortcut(Path.Combine(MenuFolder, "万象 BGM 播放器.lnk"), Path.Combine(Folder, "BgmHotkey.exe"), "");
        Shortcut(Path.Combine(MenuFolder, "卸载万象 BGM 播放器.lnk"), Path.Combine(Folder, "Uninstall.exe"), "/uninstall");
        if (desktopShortcut)
            Shortcut(Path.Combine(DesktopFolder, "万象 BGM 播放器.lnk"), Path.Combine(Folder, "BgmHotkey.exe"), "");
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
        {
            key.SetValue("DisplayName", "万象 BGM 播放器");
            key.SetValue("DisplayVersion", "1.6.0");
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
        foreach (string file in new string[] { "Tabler-LICENSE.txt", "WebView2-LICENSE.txt", "WebView2-NOTICE.txt", "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll", "BgmHotkey.exe", "BgmHotkey.exe.config", "使用说明.txt", "Uninstall.exe", "Uninstall.exe.config" })
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
    private readonly Button _install;
    private readonly Label _result;
    private readonly Label _destination;
    private readonly Button _browse;
    private readonly CheckBox _desktop;
    private readonly CheckBox _driver;
    internal SetupForm()
    {
        SuspendLayout();
        Text = "安装万象 BGM 播放器";
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(720, 610);
        MinimumSize = new Size(660, 610);
        BackColor = Color.FromArgb(247,248,252);
        ForeColor=Color.FromArgb(40,48,70);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(28), Margin = new Padding(0) };
        for (int i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(i == 6 ? SizeType.Percent : SizeType.AutoSize, i == 6 ? 100 : 0));
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "万象 / BGM", AutoSize = true, Font = new Font(Font.FontFamily, 23F, FontStyle.Bold), Margin = new Padding(0, 0, 0, 18) }, 0, 0);
        layout.Controls.Add(new Label { Text = "Windows 分享版 · 默认空曲库\n导入自己的 MP3，绑定快捷键，即可播放。", AutoSize = true, Margin = new Padding(0, 0, 0, 18) }, 0, 1);
        layout.Controls.Add(new Label { Text = "内含 VB-CABLE 官方驱动，安装驱动需管理员授权和重启。\nVB-CABLE 由 VB-Audio 提供，采用 donationware 模式。\n官网：https://vb-audio.com/Cable/；欢迎捐赠或购买许可。", AutoSize = true, Margin = new Padding(0, 0, 0, 14) }, 0, 2);
        var location=new TableLayoutPanel{AutoSize=true,Dock=DockStyle.Top,ColumnCount=2,RowCount=2,Margin=new Padding(0,4,0,20)};
        location.RowStyles.Add(new RowStyle(SizeType.AutoSize));location.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
        location.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));location.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var locationTitle=new Label{Text="安装位置",AutoSize=true,Margin=new Padding(0,0,0,8)};location.Controls.Add(locationTitle,0,0);location.SetColumnSpan(locationTitle,2);
        _destination=new Label{Text=Installer.Folder,AutoEllipsis=true,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft,BackColor=Color.White,Padding=new Padding(12,0,12,0),Margin=new Padding(0,0,12,0),MinimumSize=new Size(0,44),AccessibleName="安装位置"};
        var pathTip=new ToolTip();pathTip.SetToolTip(_destination,Installer.Folder);
        _browse=new Button{Text="选择文件夹…",AutoSize=true,MinimumSize=new Size(125,44),FlatStyle=FlatStyle.Flat,BackColor=Color.White,Margin=new Padding(0),AccessibleName="选择安装文件夹"};
        _browse.FlatAppearance.BorderColor=Color.FromArgb(128,139,161);
        _browse.Click+=delegate{
            try {
                string selected=ModernFolderPicker.Select(this,Installer.Folder);
                if(selected==null)return;
                Installer.Folder=Installer.DestinationFor(selected);_destination.Text=Installer.Folder;pathTip.SetToolTip(_destination,Installer.Folder);
            }catch(Exception ex){_result.Text="无法打开文件夹选择器："+ex.Message;}

        };
        location.Controls.Add(_destination,0,1);location.Controls.Add(_browse,1,1);layout.Controls.Add(location,0,3);
        _desktop = new CheckBox { Text = "创建桌面快捷方式", Checked = true, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
        layout.Controls.Add(_desktop, 0, 4);
        bool cableExists = Installer.HasCable();
        _driver = new CheckBox { Text = cableExists ? "已检测到 VB-CABLE，保留现有驱动" : "同时安装 VB-CABLE（用于游戏语音混音）", Checked = !cableExists, Enabled = !cableExists, AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
        layout.Controls.Add(_driver, 0, 5);
        _result = new Label { Dock = DockStyle.Fill, Text = "安装不会包含任何歌曲或其他人的个人配置。", ForeColor = Color.FromArgb(98,107,128), Margin = new Padding(0, 12, 0, 12) };
        layout.Controls.Add(_result, 0, 6);
        FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
        _install = new Button { Text = "安装", AutoSize = true, Padding = new Padding(20, 8, 20, 8), BackColor = Color.FromArgb(186,49,87), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        _install.FlatAppearance.BorderSize = 0;
        _install.Click += InstallClicked;
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!_install.Enabled) e.Cancel = true; };
        actions.Controls.Add(_install);
        Button cancel = new Button { Text = "关闭", AutoSize = true, Padding = new Padding(16, 8, 16, 8), Margin = new Padding(0, 3, 12, 3) };
        cancel.Click += delegate { Close(); };
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 7);
        AutoScaleDimensions = new SizeF(96F, 96F);
        ResumeLayout(true);
    }

    private void InstallClicked(object sender, EventArgs e)
    {
        if (_install.Text == "启动播放器")
        {
            try { Process.Start(Path.Combine(Installer.Folder, "BgmHotkey.exe")); Close(); }
            catch (Exception ex) { _result.Text = "启动失败：" + ex.Message; }
            return;
        }
        try
        {
            _install.Enabled = false; _browse.Enabled=false;
            _result.Text = "正在安装……";
            Refresh();
            Installer.Install(_desktop.Checked);

            _install.Text = "启动播放器"; _browse.Enabled=false;
            _result.Text = "播放器已安装。点击“启动播放器”，添加自己的歌曲。";
            if (_driver.Checked) {
                _result.Text = "播放器已安装，正在打开 VB-CABLE 官方安装程序……"; Refresh();
                Installer.InstallDriver();
                _result.Text = "播放器已安装，驱动安装窗口已关闭。\n请按官方提示完成安装并重启，语音输入选择 CABLE Output。";
            }
        }
        catch (Exception ex) { _result.Text = (_install.Text == "启动播放器" ? "播放器已安装；驱动安装未完成：" : "安装失败：") + ex.Message; }
        finally { _install.Enabled = true; _browse.Enabled=_install.Text!="启动播放器"; }
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
