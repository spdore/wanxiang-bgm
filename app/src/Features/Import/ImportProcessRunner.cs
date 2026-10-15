using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BgmHotkey
{
    internal interface IImportChildControl
    {
        void RequestTerminate();
    }

    internal enum ImportProcessStatus
    {
        Exited,
        TimedOut,
        Cancelled,
        Failed
    }

    internal sealed class ImportProcessResult
    {
        public ImportProcessStatus Status;
        public int ExitCode;
        public string StandardOutput = "";
        public string StandardError = "";
        public long ElapsedMilliseconds;
        public bool CleanupSafe = true;
        public string Error = "";
    }

    internal static class ImportTimeoutPolicy
    {
        public const long MaximumEncryptedInputBytes = 256L * 1024L * 1024L;
        public const long MaximumDecodedOutputBytes = 512L * 1024L * 1024L;
        public const int OutputEofGraceMilliseconds = 1500;
        public const int ProcessTerminationGraceMilliseconds = 2500;
        public const int CopyNoProgressMilliseconds = 120000;
        public const int FileInspectionMilliseconds = 30000;
        public const int HelperExtractionMilliseconds = 60000;

        public static int ConversionTotalMilliseconds(long fileBytes)
        {
            return ScaledTimeout(fileBytes, 120000L, 8000L, 1200000L);
        }

        public static int ValidationProbeTotalMilliseconds(long fileBytes)
        {
            return ScaledTimeout(fileBytes, 90000L, 2000L, 300000L);
        }

        public static int ValidationFullTotalMilliseconds(long fileBytes)
        {
            return ScaledTimeout(fileBytes, 120000L, 30000L, 1800000L);
        }

        public static int CopyTotalMilliseconds(long fileBytes)
        {
            return ScaledTimeout(fileBytes, 120000L, 4000L, 3600000L);
        }

        public static int ReuseTotalMilliseconds(long fileBytes)
        {
            // Reuse detection may hash more than one same-sized library item. It
            // has no truthful byte-progress protocol, so use one fixed per-file
            // deadline with a 20-minute ceiling rather than an idle deadline.
            return ScaledTimeout(fileBytes, 300000L, 8000L, 1200000L);
        }

        public static int FingerprintTotalMilliseconds(long fileBytes)
        {
            return ScaledTimeout(fileBytes, 120000L, 4000L, 1800000L);
        }

        public static int LoudnessAnalysisTotalMilliseconds(long fileBytes)
        {
            return ScaledTimeout(fileBytes, 300000L, 12000L, 3600000L);
        }

        private static int ScaledTimeout(long fileBytes, long baseMilliseconds, long perMiBMilliseconds, long maximumMilliseconds)
        {
            long nonNegativeBytes = Math.Max(0L, fileBytes);
            long mib = nonNegativeBytes / 1048576L + (nonNegativeBytes % 1048576L == 0 ? 0L : 1L);
            long result;
            try { result = checked(baseMilliseconds + checked(mib * perMiBMilliseconds)); }
            catch (OverflowException) { result = maximumMilliseconds; }
            if (result > maximumMilliseconds) result = maximumMilliseconds;
            if (result < 1000L) result = 1000L;
            return (int)result;
        }
    }

    internal static class ImportProcessRunner
    {
        private const int MaximumStandardOutputCharacters = 65536;
        private const int MaximumStandardErrorCharacters = 16384;

        public static ImportProcessResult Run(ProcessStartInfo startInfo, SongImportOperation operation, int totalTimeoutMilliseconds, int noProgressTimeoutMilliseconds)
        {
            if (startInfo == null) throw new ArgumentNullException("startInfo");
            if (totalTimeoutMilliseconds < 1) throw new ArgumentOutOfRangeException("totalTimeoutMilliseconds");

            Stopwatch clock = Stopwatch.StartNew();
            ImportProcessResult result = new ImportProcessResult { Status = ImportProcessStatus.Failed };
            OwnedImportProcess owned = null;
            BoundedProcessReader stdout = null;
            BoundedProcessReader stderr = null;
            bool processExited = false;
            try
            {
                if (operation != null && operation.IsCancelled)
                {
                    result.Status = ImportProcessStatus.Cancelled;
                    return result;
                }

                owned = new OwnedImportProcess(startInfo);
                if (operation != null && !operation.RegisterProcess(owned))
                {
                    result.Status = ImportProcessStatus.Cancelled;
                    return result;
                }

                owned.Start();
                stdout = new BoundedProcessReader(owned.Process.StandardOutput, MaximumStandardOutputCharacters, operation);
                stderr = new BoundedProcessReader(owned.Process.StandardError, MaximumStandardErrorCharacters, operation);
                stdout.Start();
                stderr.Start();

                long lastProgress = 0L;
                long lastProgressAt = 0L;
                ImportProcessStatus completion = ImportProcessStatus.Exited;
                while (!owned.WaitForExit(100))
                {
                    if (operation != null && operation.IsCancelled)
                    {
                        completion = ImportProcessStatus.Cancelled;
                        break;
                    }
                    if (clock.ElapsedMilliseconds >= totalTimeoutMilliseconds)
                    {
                        completion = ImportProcessStatus.TimedOut;
                        break;
                    }
                    if (noProgressTimeoutMilliseconds > 0)
                    {
                        long observed = operation == null ? 0L : operation.ProgressRevision;
                        if (observed != lastProgress)
                        {
                            lastProgress = observed;
                            lastProgressAt = clock.ElapsedMilliseconds;
                        }
                        if (clock.ElapsedMilliseconds - lastProgressAt >= noProgressTimeoutMilliseconds)
                        {
                            completion = ImportProcessStatus.TimedOut;
                            result.Error = "no-progress";
                            break;
                        }
                    }
                }

                processExited = owned.WaitForExit(0);
                if (completion == ImportProcessStatus.Exited && processExited)
                {
                    bool stdoutEnded = stdout.WaitCompleted(ImportTimeoutPolicy.OutputEofGraceMilliseconds);
                    bool stderrEnded = stderr.WaitCompleted(ImportTimeoutPolicy.OutputEofGraceMilliseconds);
                    if (!stdoutEnded || !stderrEnded)
                    {
                        completion = ImportProcessStatus.TimedOut;
                        result.Error = "output-pipe-timeout";
                        result.CleanupSafe = owned.TerminateAndWait(ImportTimeoutPolicy.ProcessTerminationGraceMilliseconds);
                    }
                    else
                    {
                        result.CleanupSafe = owned.StopOrConfirmTreeStopped(ImportTimeoutPolicy.ProcessTerminationGraceMilliseconds);
                    }
                }
                else if (!processExited)
                {
                    result.CleanupSafe = owned.TerminateAndWait(ImportTimeoutPolicy.ProcessTerminationGraceMilliseconds);
                }
                else
                {
                    result.CleanupSafe = owned.StopOrConfirmTreeStopped(ImportTimeoutPolicy.ProcessTerminationGraceMilliseconds);
                }

                result.Status = completion;
                if (processExited)
                {
                    try { result.ExitCode = owned.Process.ExitCode; }
                    catch { result.Status = ImportProcessStatus.Failed; }
                }
                if (result.Status == ImportProcessStatus.Exited && !processExited)
                    result.Status = ImportProcessStatus.Failed;
            }
            catch (Exception ex)
            {
                result.Status = operation != null && operation.IsCancelled ? ImportProcessStatus.Cancelled : ImportProcessStatus.Failed;
                result.Error = ex.GetType().Name + ": " + ex.Message;
                if (owned != null && owned.IsStarted)
                    result.CleanupSafe = owned.TerminateAndWait(ImportTimeoutPolicy.ProcessTerminationGraceMilliseconds);
            }
            finally
            {
                if (stdout != null)
                {
                    if (!stdout.WaitCompleted(250) && !stdout.CloseReader()) result.CleanupSafe = false;
                    stdout.Dispose();
                }
                if (stderr != null)
                {
                    if (!stderr.WaitCompleted(250) && !stderr.CloseReader()) result.CleanupSafe = false;
                    stderr.Dispose();
                }
                if (owned != null)
                {
                    if (owned.IsStarted && !owned.WaitForExit(0) && result.CleanupSafe && !owned.TerminateAndWait(ImportTimeoutPolicy.ProcessTerminationGraceMilliseconds))
                        result.CleanupSafe = false;
                    owned.Dispose();
                }
                if (operation != null) operation.ClearProcess(owned);
                result.ElapsedMilliseconds = clock.ElapsedMilliseconds;
                if (stdout != null) result.StandardOutput = stdout.Snapshot();
                if (stderr != null) result.StandardError = stderr.Snapshot();
                if (operation != null && operation.IsCancelled) result.Status = ImportProcessStatus.Cancelled;
                if (!result.CleanupSafe && operation != null) operation.MarkCleanupUnsafe();
            }
            return result;
        }

        public static string QuoteArgument(string argument)
        {
            if (argument == null) argument = "";
            StringBuilder result = new StringBuilder();
            result.Append('"');
            int slashes = 0;
            for (int i = 0; i < argument.Length; i++)
            {
                char c = argument[i];
                if (c == '\\') { slashes++; continue; }
                if (c == '"')
                {
                    result.Append('\\', slashes * 2 + 1);
                    result.Append('"');
                    slashes = 0;
                    continue;
                }
                result.Append('\\', slashes);
                slashes = 0;
                result.Append(c);
            }
            result.Append('\\', slashes * 2);
            result.Append('"');
            return result.ToString();
        }

        private sealed class BoundedProcessReader : IDisposable
        {
            private readonly StreamReader _reader;
            private readonly int _maximum;
            private readonly SongImportOperation _operation;
            private readonly object _gate = new object();
            private readonly StringBuilder _text = new StringBuilder();
            private readonly StringBuilder _line = new StringBuilder();
            private readonly ManualResetEvent _completed = new ManualResetEvent(false);
            private Thread _thread;
            private bool _disposed;

            public BoundedProcessReader(StreamReader reader, int maximum, SongImportOperation operation)
            {
                _reader = reader;
                _maximum = maximum;
                _operation = operation;
            }

            public void Start()
            {
                _thread = new Thread(ReadLoop);
                _thread.IsBackground = true;
                _thread.Name = "BGM import process output";
                _thread.Start();
            }

            public bool WaitCompleted(int milliseconds)
            {
                return _completed.WaitOne(Math.Max(0, milliseconds));
            }

            public string Snapshot()
            {
                lock (_gate) return _text.ToString();
            }

            private void ReadLoop()
            {
                char[] buffer = new char[4096];
                try
                {
                    int count;
                    while ((count = _reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        lock (_gate)
                        {
                            if (count >= _maximum)
                            {
                                _text.Clear();
                                _text.Append(buffer, count - _maximum, _maximum);
                            }
                            else
                            {
                                int excess = _text.Length + count - _maximum;
                                if (excess > 0) _text.Remove(0, Math.Min(excess, _text.Length));
                                _text.Append(buffer, 0, count);
                            }
                        }
                        ReadProgress(buffer, count);
                    }
                }
                catch (ObjectDisposedException) { }
                catch (IOException) { }
                finally { _completed.Set(); }
            }

            private void ReadProgress(char[] buffer, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    char value = buffer[i];
                    if (value == '\n')
                    {
                        string line = _line.ToString().TrimEnd('\r');
                        _line.Clear();
                        if (_operation != null && line.StartsWith("IMPORT_PROGRESS:", StringComparison.Ordinal))
                        {
                            long bytes;
                            if (Int64.TryParse(line.Substring("IMPORT_PROGRESS:".Length), out bytes))
                                _operation.ReportProgress(bytes);
                        }
                    }
                    else if (_line.Length < 128) _line.Append(value);
                }
            }

            public bool CloseReader()
            {
                try { _reader.Dispose(); } catch { }
                if (_thread == null || !_thread.IsAlive) return true;
                return _thread.Join(500);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                bool completed = _completed.WaitOne(0);
                if (!completed) completed = CloseReader();
                if (completed) _completed.Dispose();
            }
        }

        private sealed class OwnedImportProcess : IDisposable, IImportChildControl
        {
            private const uint JobObjectExtendedLimitInformation = 9;
            private const uint JobObjectBasicAccountingInformation = 1;
            private const uint JobObjectLimitKillOnJobClose = 0x00002000;
            private readonly ProcessStartInfo _startInfo;
            private Process _process;
            private IntPtr _job = IntPtr.Zero;
            private int _terminateRequested;
            private int _terminateQueued;
            private bool _started;
            private bool _disposed;

            [StructLayout(LayoutKind.Sequential)]
            private struct BasicLimitInformation
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct BasicAccountingInformation
            {
                public long TotalUserTime;
                public long TotalKernelTime;
                public long ThisPeriodTotalUserTime;
                public long ThisPeriodTotalKernelTime;
                public uint TotalPageFaultCount;
                public uint TotalProcesses;
                public uint ActiveProcesses;
                public uint TotalTerminatedProcesses;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct IoCounters
            {
                public ulong ReadOperationCount;
                public ulong WriteOperationCount;
                public ulong OtherOperationCount;
                public ulong ReadTransferCount;
                public ulong WriteTransferCount;
                public ulong OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct ExtendedLimitInformation
            {
                public BasicLimitInformation BasicLimitInformation;
                public IoCounters IoInfo;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(IntPtr job, uint informationClass, ref ExtendedLimitInformation information, uint informationLength);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool QueryInformationJobObject(IntPtr job, uint informationClass, out BasicAccountingInformation information, uint informationLength, IntPtr returnLength);
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool CloseHandle(IntPtr handle);

            public OwnedImportProcess(ProcessStartInfo startInfo)
            {
                _startInfo = startInfo;
                ExtendedLimitInformation limits = new ExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                _job = CreateJobObject(IntPtr.Zero, null);
                if (_job == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法创建导入工作进程隔离容器。");
                if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimitInformation))))
                {
                    int error = Marshal.GetLastWin32Error();
                    CloseHandle(_job);
                    _job = IntPtr.Zero;
                    throw new System.ComponentModel.Win32Exception(error, "无法配置导入工作进程隔离容器。");
                }
                _process = new Process { StartInfo = _startInfo };
            }

            public Process Process { get { return _process; } }
            public bool IsStarted { get { return _started; } }

            public void Start()
            {
                if (!_process.Start()) throw new InvalidOperationException("无法启动本地导入工作进程。");
                _started = true;
                if (!AssignProcessToJobObject(_job, _process.Handle))
                {
                    int error = Marshal.GetLastWin32Error();
                    try { _process.Kill(); } catch { }
                    _process.WaitForExit(1000);
                    throw new System.ComponentModel.Win32Exception(error, "无法将导入工作进程加入隔离容器。");
                }
                if (Interlocked.CompareExchange(ref _terminateRequested, 0, 0) != 0) TerminateNow();
            }

            public bool WaitForExit(int milliseconds)
            {
                if (!_started) return true;
                try { return _process.WaitForExit(Math.Max(0, milliseconds)); }
                catch (InvalidOperationException) { return true; }
            }

            public void RequestTerminate()
            {
                Interlocked.Exchange(ref _terminateRequested, 1);
                if (Interlocked.CompareExchange(ref _terminateQueued, 1, 0) == 0)
                    ThreadPool.QueueUserWorkItem(delegate { TerminateNow(); });
            }

            public bool TerminateAndWait(int graceMilliseconds)
            {
                RequestTerminate();
                int grace = Math.Max(0, graceMilliseconds);
                Stopwatch clock = Stopwatch.StartNew();
                int first = Math.Min(grace, Math.Max(100, grace / 2));
                if (WaitForExit(first) && WaitForJobEmpty(RemainingMilliseconds(clock, grace))) return true;
                TerminateNow();
                int remaining = RemainingMilliseconds(clock, grace);
                if (WaitForExit(remaining) && WaitForJobEmpty(RemainingMilliseconds(clock, grace))) return true;
                try { if (_process != null && !_process.HasExited) _process.Kill(); } catch { }
                remaining = RemainingMilliseconds(clock, grace);
                if (!WaitForExit(remaining)) return false;
                return WaitForJobEmpty(RemainingMilliseconds(clock, grace));
            }

            private static int RemainingMilliseconds(Stopwatch clock, int totalMilliseconds)
            {
                return Math.Max(0, totalMilliseconds - (int)Math.Min(Int32.MaxValue, clock.ElapsedMilliseconds));
            }

            public bool StopOrConfirmTreeStopped(int graceMilliseconds)
            {
                if (WaitForJobEmpty(0)) return true;
                TerminateNow();
                return WaitForJobEmpty(graceMilliseconds);
            }

            private void TerminateNow()
            {
                try
                {
                    if (_job != IntPtr.Zero) TerminateJobObject(_job, 1);
                    else if (_process != null && _started && !_process.HasExited) _process.Kill();
                }
                catch { }
            }

            private bool WaitForJobEmpty(int milliseconds)
            {
                if (_job == IntPtr.Zero) return true;
                Stopwatch clock = Stopwatch.StartNew();
                do
                {
                    BasicAccountingInformation information;
                    if (QueryInformationJobObject(_job, JobObjectBasicAccountingInformation, out information,
                        (uint)Marshal.SizeOf(typeof(BasicAccountingInformation)), IntPtr.Zero) && information.ActiveProcesses == 0)
                        return true;
                    if (clock.ElapsedMilliseconds >= milliseconds) break;
                    Thread.Sleep(25);
                }
                while (true);
                return false;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_job != IntPtr.Zero)
                {
                    try { CloseHandle(_job); } catch { }
                    _job = IntPtr.Zero;
                }
                if (_process != null)
                {
                    _process.Dispose();
                    _process = null;
                }
            }
        }
    }
}
