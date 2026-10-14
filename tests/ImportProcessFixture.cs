using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class ImportProcessFixture
{
    private const int StartfUseStdHandles = 0x00000100;
    private const uint CreateNoWindow = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string reserved;
        public string desktop;
        public string title;
        public int x;
        public int y;
        public int xSize;
        public int ySize;
        public int xCountChars;
        public int yCountChars;
        public int fillAttribute;
        public int flags;
        public short showWindow;
        public short reserved2Size;
        public IntPtr reserved2;
        public IntPtr standardInput;
        public IntPtr standardOutput;
        public IntPtr standardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr process;
        public IntPtr thread;
        public int processId;
        public int threadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreateProcess(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private static int Main(string[] args)
    {
        if (args.Length == 0) return 2;
        string mode = args[0];
        if (mode == "child")
        {
            if (args.Length > 1) File.WriteAllText(args[1], Process.GetCurrentProcess().Id.ToString());
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        if (mode == "hang")
        {
            if (args.Length > 1) File.WriteAllText(args[1], Process.GetCurrentProcess().Id.ToString());
            Console.WriteLine("started");
            Console.Error.WriteLine("started");
            Console.Out.Flush();
            Console.Error.Flush();
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        if (mode == "progress-then-hang")
        {
            Console.WriteLine("IMPORT_PROGRESS:4194304");
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        if (mode == "flood")
        {
            string block = new string('X', 4096);
            for (int i = 0; i < 80; i++)
            {
                Console.Out.WriteLine(block);
                Console.Error.WriteLine(block);
            }
            Console.Out.Flush();
            Console.Error.Flush();
            return 0;
        }
        if (mode == "invalid")
        {
            Console.WriteLine("this is not JSON");
            return 0;
        }
        if (mode == "no-output") return 0;
        if (mode == "complete-after-delay")
        {
            if (args.Length > 1) File.WriteAllText(args[1], Process.GetCurrentProcess().Id.ToString());
            Console.WriteLine("result pending");
            Console.Out.Flush();
            Thread.Sleep(1500);
            Console.WriteLine("result ready");
            return 0;
        }
        if (mode == "exit")
        {
            Console.WriteLine("ok");
            return 0;
        }
        if (mode == "spawn-hold" || mode == "exit-with-child")
        {
            if (args.Length < 3) return 2;
            File.WriteAllText(args[1], Process.GetCurrentProcess().Id.ToString());
            int childId;
            if (!SpawnChild(out childId)) return 3;
            File.WriteAllText(args[2], childId.ToString());
            Console.WriteLine("child=" + childId);
            Console.Out.Flush();
            if (mode == "exit-with-child") return 0;
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
        return 2;
    }

    private static bool SpawnChild(out int processId)
    {
        string executable = AssemblyPath();
        string command = "\"" + executable.Replace("\"", "\\\"") + "\" child";
        StartupInfo startup = new StartupInfo();
        startup.cb = Marshal.SizeOf(typeof(StartupInfo));
        startup.flags = StartfUseStdHandles;
        startup.standardInput = GetStdHandle(-10);
        startup.standardOutput = GetStdHandle(-11);
        startup.standardError = GetStdHandle(-12);
        ProcessInformation information;
        bool created = CreateProcess(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true,
            CreateNoWindow, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out information);
        if (!created)
        {
            processId = 0;
            return false;
        }
        processId = information.processId;
        CloseHandle(information.thread);
        CloseHandle(information.process);
        return true;
    }

    private static string AssemblyPath()
    {
        return Process.GetCurrentProcess().MainModule.FileName;
    }
}
