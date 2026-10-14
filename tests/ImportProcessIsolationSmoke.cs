using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

internal static class ImportProcessIsolationSmoke
{
    private static Assembly _player;
    private static Type _operationType;
    private static MethodInfo _run;
    private static MethodInfo _quote;
    private static string _fixture;
    private static string _directory;

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: smoke.exe <player-exe> <process-fixture-exe>");
            return 2;
        }
        try
        {
            _player = Assembly.LoadFrom(args[0]);
            _operationType = _player.GetType("BgmHotkey.SongImportOperation", true);
            Type runner = _player.GetType("BgmHotkey.ImportProcessRunner", true);
            _run = runner.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            _quote = runner.GetMethod("QuoteArgument", BindingFlags.Public | BindingFlags.Static);
            if (_run == null || _quote == null) throw new MissingMethodException("ImportProcessRunner methods");
            _fixture = Path.GetFullPath(args[1]);
            _directory = Path.Combine(Path.GetTempPath(), "BgmImportFaults-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);

            TestPreCancelledDoesNotStart();
            TestTimeoutReapsAndBatchCanContinue();
            TestIdleTimeoutRequiresProgressProtocol();
            TestCancelIsImmediateAndReapsTree();
            TestCancelNearResultReturn();
            TestOutputFloodIsDrainedAndBounded();
            TestExitedChildWithInheritedPipeIsReaped();
            TestInvalidAndMissingOutputAreBounded();

            Console.WriteLine("Import process isolation smoke: OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Import process isolation smoke failed: {0}", ex);
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch { }
        }
    }

    private static void TestPreCancelledDoesNotStart()
    {
        object operation = NewOperation();
        Cancel(operation);
        string marker = Path.Combine(_directory, "pre-cancel.marker");
        object result = Run(operation, "hang", 3000, 0, marker);
        Assert(Status(result) == "Cancelled", "pre-start cancellation was not reported as cancelled");
        Assert(!File.Exists(marker), "pre-cancelled process was started");
        Console.WriteLine("cancel before process start: OK");
    }

    private static void TestTimeoutReapsAndBatchCanContinue()
    {
        object operation = NewOperation();
        string pidFile = Path.Combine(_directory, "timeout.pid");
        Stopwatch clock = Stopwatch.StartNew();
        object timeout = Run(operation, "hang", 1200, 0, pidFile);
        if (Status(timeout) != "TimedOut")
            Console.WriteLine("timeout fixture returned status={0}, error={1}, cleanup={2}, stdout={3}, stderr={4}",
                Status(timeout), Field(timeout, "Error"), Field(timeout, "CleanupSafe"), Field(timeout, "StandardOutput"), Field(timeout, "StandardError"));
        Assert(Status(timeout) == "TimedOut", "hung process did not report TimedOut");
        Assert(clock.ElapsedMilliseconds < 7000, "hung process exceeded the bounded timeout and reap window");
        Assert(ReadPid(pidFile) > 0 && WaitForProcessGone(ReadPid(pidFile), 5000), "timed out helper process was not reaped");
        Assert(!IsCancelled(operation), "single-file timeout incorrectly cancelled the batch");

        object next = Run(operation, "exit", 3000, 0);
        Assert(Status(next) == "Exited" && ExitCode(next) == 0, "a later file could not run after a per-file timeout");
        Console.WriteLine("total timeout, owned process reap, and next-file continuation: OK");
    }

    private static void TestIdleTimeoutRequiresProgressProtocol()
    {
        object operation = NewOperation();
        Stopwatch clock = Stopwatch.StartNew();
        object result = Run(operation, "progress-then-hang", 5000, 600);
        Assert(Status(result) == "TimedOut", "reported progress followed by a stall did not hit the no-progress timeout");
        Assert(clock.ElapsedMilliseconds < 6000, "no-progress timeout did not finish within its bounded window");
        Assert(!IsCancelled(operation), "no-progress timeout incorrectly changed the user-cancel state");
        Console.WriteLine("progress-based idle deadline: OK");
    }

    private static void TestCancelIsImmediateAndReapsTree()
    {
        object operation = NewOperation();
        string parentPidFile = Path.Combine(_directory, "cancel-parent.pid");
        string childPidFile = Path.Combine(_directory, "cancel-child.pid");
        long cancelMilliseconds = -1;
        Thread cancelWorker = new Thread(new ThreadStart(delegate
        {
            Stopwatch wait = Stopwatch.StartNew();
            while (!File.Exists(childPidFile) && wait.ElapsedMilliseconds < 4000) Thread.Sleep(10);
            Stopwatch call = Stopwatch.StartNew();
            Cancel(operation);
            cancelMilliseconds = call.ElapsedMilliseconds;
        }));
        cancelWorker.IsBackground = true;
        cancelWorker.Start();
        object result = Run(operation, "spawn-hold", 10000, 0, parentPidFile, childPidFile);
        Assert(cancelWorker.Join(5000), "cancellation worker did not return");
        if (Status(result) != "Cancelled")
            Console.WriteLine("cancel fixture returned status={0}, error={1}, cleanup={2}, exit={3}, parent={4}, child={5}",
                Status(result), Field(result, "Error"), Field(result, "CleanupSafe"), Field(result, "ExitCode"), ReadPid(parentPidFile), ReadPid(childPidFile));
        Assert(Status(result) == "Cancelled", "active user cancellation was not reported as Cancelled");
        Assert(cancelMilliseconds >= 0 && cancelMilliseconds < 250, "Cancel blocked while the process was starting or being terminated");
        int parentPid = ReadPid(parentPidFile);
        int childPid = ReadPid(childPidFile);
        Assert(parentPid > 0 && WaitForProcessGone(parentPid, 5000), "cancelled parent process was not reaped");
        Assert(childPid > 0 && WaitForProcessGone(childPid, 5000), "owned child process was not reaped with its parent");

        object nextOperation = NewOperation();
        object next = Run(nextOperation, "exit", 3000, 0);
        Assert(Status(next) == "Exited" && ExitCode(next) == 0, "a new import could not start after cancellation");
        Console.WriteLine("cancel during run, immediate UI-facing return, descendant reap, and next import: OK");
    }

    private static void TestOutputFloodIsDrainedAndBounded()
    {
        object result = Run(NewOperation(), "flood", 5000, 0);
        Assert(Status(result) == "Exited" && ExitCode(result) == 0, "output-flooding helper did not finish");
        string stdout = Field(result, "StandardOutput") as string ?? "";
        string stderr = Field(result, "StandardError") as string ?? "";
        Assert(stdout.Length <= 65536 && stderr.Length <= 16384, "captured child output exceeded its memory bounds");
        Console.WriteLine("parallel stdout/stderr drain and bounded capture: OK");
    }

    private static void TestCancelNearResultReturn()
    {
        object operation = NewOperation();
        string marker = Path.Combine(_directory, "near-result.pid");
        object[] result = new object[1];
        Thread runner = new Thread(new ThreadStart(delegate
        {
            result[0] = Run(operation, "complete-after-delay", 5000, 0, marker);
        }));
        runner.IsBackground = true;
        runner.Start();
        Stopwatch wait = Stopwatch.StartNew();
        while (!File.Exists(marker) && wait.ElapsedMilliseconds < 3000) Thread.Sleep(10);
        Assert(File.Exists(marker), "near-result process did not start");
        Thread.Sleep(1150);
        Cancel(operation);
        Assert(runner.Join(5000), "near-result cancellation did not finish the runner");
        Assert(Status(result[0]) == "Cancelled", "cancellation near the result boundary was reported as success");
        Assert(WaitForProcessGone(ReadPid(marker), 5000), "near-result process was not reaped");
        Console.WriteLine("cancellation near result return: OK");
    }

    private static void TestExitedChildWithInheritedPipeIsReaped()
    {
        object operation = NewOperation();
        string parentPidFile = Path.Combine(_directory, "pipe-parent.pid");
        string childPidFile = Path.Combine(_directory, "pipe-child.pid");
        Stopwatch clock = Stopwatch.StartNew();
        object result = Run(operation, "exit-with-child", 5000, 0, parentPidFile, childPidFile);
        Assert(Status(result) == "TimedOut", "a child retaining the output pipe was not classified as a bounded output timeout");
        Assert(clock.ElapsedMilliseconds < 10000, "inherited output pipe exceeded the bounded EOF and reap windows");
        int parentPid = ReadPid(parentPidFile);
        int childPid = ReadPid(childPidFile);
        Assert(parentPid > 0 && WaitForProcessGone(parentPid, 5000), "pipe-holding fixture parent did not exit");
        Assert(childPid > 0 && WaitForProcessGone(childPid, 5000), "pipe-holding descendant was not terminated");
        Assert((bool)Field(result, "CleanupSafe"), "pipe-holding descendant cleanup could not be confirmed");
        Console.WriteLine("inherited pipe closure and descendant cleanup: OK");
    }

    private static void TestInvalidAndMissingOutputAreBounded()
    {
        object invalid = Run(NewOperation(), "invalid", 3000, 0);
        object missing = Run(NewOperation(), "no-output", 3000, 0);
        Assert(Status(invalid) == "Exited" && ExitCode(invalid) == 0, "invalid-result fixture did not exit cleanly");
        Assert(Status(missing) == "Exited" && ExitCode(missing) == 0, "no-output fixture did not exit cleanly");
        Assert(!String.IsNullOrWhiteSpace(Field(invalid, "StandardOutput") as string), "invalid result output was lost");
        Assert(String.IsNullOrWhiteSpace(Field(missing, "StandardOutput") as string), "no-output fixture unexpectedly returned output");
        Console.WriteLine("invalid JSON and exit-zero/no-output response handling inputs: OK");
    }

    private static object Run(object operation, string mode, int totalTimeout, int noProgressTimeout, params string[] extraArguments)
    {
        StringBuilder arguments = new StringBuilder(Quote(mode));
        foreach (string argument in extraArguments) arguments.Append(' ').Append(Quote(argument));
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = _fixture,
            Arguments = arguments.ToString(),
            WorkingDirectory = _directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        return _run.Invoke(null, new object[] { startInfo, operation, totalTimeout, noProgressTimeout });
    }

    private static string Quote(string value)
    {
        return (string)_quote.Invoke(null, new object[] { value });
    }

    private static object NewOperation()
    {
        return Activator.CreateInstance(_operationType, true);
    }

    private static void Cancel(object operation)
    {
        _operationType.GetMethod("Cancel", BindingFlags.Public | BindingFlags.Instance).Invoke(operation, null);
    }

    private static bool IsCancelled(object operation)
    {
        return (bool)_operationType.GetProperty("IsCancelled", BindingFlags.Public | BindingFlags.Instance).GetValue(operation, null);
    }

    private static object Field(object value, string field)
    {
        return value.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance).GetValue(value);
    }

    private static string Status(object result)
    {
        return Convert.ToString(Field(result, "Status"));
    }

    private static int ExitCode(object result)
    {
        return (int)Field(result, "ExitCode");
    }

    private static int ReadPid(string path)
    {
        try { return Int32.Parse(File.ReadAllText(path)); }
        catch { return 0; }
    }

    private static bool WaitForProcessGone(int processId, int timeoutMilliseconds)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMilliseconds)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                    if (process.HasExited) return true;
            }
            catch (ArgumentException) { return true; }
            catch (InvalidOperationException) { return true; }
            Thread.Sleep(25);
        }
        return false;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
