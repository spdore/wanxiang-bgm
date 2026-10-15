using System;
using System.IO;

internal static class InstallerTransactionHarness
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        _checks++;
        Console.WriteLine("PASS: " + message);
    }

    private static string Read(string path) { return File.ReadAllText(path); }

    private static void Main()
    {
        string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "installer-transaction-fixtures-" + Guid.NewGuid().ToString("N"));
        string install = Path.Combine(root, "install"), stage = Path.Combine(root, "stage");
        Directory.CreateDirectory(install); Directory.CreateDirectory(stage);
        try
        {
            File.WriteAllText(Path.Combine(install, "BgmHotkey.exe"), "old-player");
            File.WriteAllText(Path.Combine(install, "BgmHotkey.exe.config"), "old-config");
            File.WriteAllText(Path.Combine(install, "user-data.txt"), "preserve");
            File.WriteAllText(Path.Combine(stage, "BgmHotkey.exe"), "new-player");
            File.WriteAllText(Path.Combine(stage, "BgmHotkey.exe.config"), "new-config");
            Installer.CommitFileSet(stage, install, new string[] { "BgmHotkey.exe", "BgmHotkey.exe.config" }, null);
            Check(Read(Path.Combine(install, "BgmHotkey.exe")) == "new-player" && Read(Path.Combine(install, "BgmHotkey.exe.config")) == "new-config", "upgrade commits the complete staged program file set");
            Check(Read(Path.Combine(install, "user-data.txt")) == "preserve", "upgrade leaves unrelated user data untouched");

            Directory.Delete(stage, true); Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(install, "BgmHotkey.exe"), "old-player-2");
            File.WriteAllText(Path.Combine(install, "BgmHotkey.exe.config"), "old-config-2");
            File.WriteAllText(Path.Combine(stage, "BgmHotkey.exe"), "candidate-player-2");
            File.WriteAllText(Path.Combine(stage, "BgmHotkey.exe.config"), "candidate-config-2");
            bool failed = false;
            try
            {
                Installer.CommitFileSet(stage, install, new string[] { "BgmHotkey.exe", "BgmHotkey.exe.config" },
                    delegate(int committed) { if (committed == 1) throw new IOException("injected mid-commit failure"); });
            }
            catch (InvalidOperationException ex) { failed = ex.Message.IndexOf("已恢复上一套程序文件", StringComparison.Ordinal) >= 0; }
            Check(failed, "mid-commit error is reported after rollback completes");
            Check(Read(Path.Combine(install, "BgmHotkey.exe")) == "old-player-2" && Read(Path.Combine(install, "BgmHotkey.exe.config")) == "old-config-2", "failed upgrade restores every prior program file");
            Check(Read(Path.Combine(install, "user-data.txt")) == "preserve", "rollback does not touch user data");

            Directory.Delete(stage, true); Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(stage, "fresh.exe"), "first-install");
            Installer.CommitFileSet(stage, install, new string[] { "fresh.exe" }, null);
            Check(Read(Path.Combine(install, "fresh.exe")) == "first-install", "first install commits a staged file set");
            Console.WriteLine("Installer transaction regression checks passed: " + _checks);
        }
        finally { try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { } }
    }
}
