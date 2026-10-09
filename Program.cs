using System;
using System.Threading;
using System.Windows.Forms;

namespace BgmHotkey
{
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            bool createdNew = false;
            Mutex mutex = null;
            AudioEngine engine = null;
            MainMenuForm menu = null;
            HotkeyService hotkeys = null;
            try
            {
                mutex = new Mutex(true, @"Local\BgmHotkeyShare.SingleInstance", out createdNew);
                if (!createdNew)
                    return 0;

                DisplayDpi.Initialize();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
                {
                    AppPaths.Log("Windows Forms 未处理错误：" + e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    AppPaths.Log("未处理错误：" + e.ExceptionObject);
                };

                AppSettings settings = SettingsStore.Load();
                AudioDeviceCatalog devices = new AudioDeviceCatalog();
                engine = new AudioEngine(settings, devices);
                engine.Start();
                menu = new MainMenuForm(settings, devices, engine);
                hotkeys = new HotkeyService(menu);
                string hookError = hotkeys.Install();
                if (!String.IsNullOrWhiteSpace(hookError))
                {
                    AppPaths.Log("安装全局键盘钩子失败：" + hookError);
                    menu.SetHotkeyError("全局快捷键不可用：" + hookError);
                }

                Application.Run(menu);
                return 0;
            }
            catch (Exception ex)
            {
                AppPaths.Log("播放器启动失败：" + ex);
                return 1;
            }
            finally
            {
                if (hotkeys != null) hotkeys.Dispose();
                if (menu != null) menu.Dispose();
                if (engine != null) engine.Dispose();
                if (mutex != null)
                {
                    try { if (createdNew) mutex.ReleaseMutex(); } catch { }
                    mutex.Dispose();
                }
            }
        }
    }
}
