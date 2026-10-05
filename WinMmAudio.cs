using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;

namespace BgmHotkey
{
    internal static class WinMm
    {
        internal const uint CallbackEvent = 0x00050000;
        internal const uint WaveFormatPcm = 1;
        internal const uint HeaderDone = 0x00000001;
        internal const uint Mapper = 0xFFFFFFFF;
        internal const int SampleRate = 44100;
        internal const int OutputChannels = 2;
        internal const int BitsPerSample = 16;
        internal const int BlockFrames = 882;       // 20 ms at 44.1 kHz
        internal const int CaptureFrames = 882;     // 20 ms capture buffers

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        internal struct WaveFormatEx
        {
            public ushort FormatTag;
            public ushort Channels;
            public uint SamplesPerSec;
            public uint AvgBytesPerSec;
            public ushort BlockAlign;
            public ushort BitsPerSample;
            public ushort ExtraSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WaveHdr
        {
            public IntPtr Data;
            public uint BufferLength;
            public uint BytesRecorded;
            public UIntPtr User;
            public uint Flags;
            public uint Loops;
            public IntPtr Next;
            public UIntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto, Pack = 2)]
        internal struct WaveOutCaps
        {
            public ushort ManufacturerId;
            public ushort ProductId;
            public uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string Name;
            public uint Formats;
            public ushort Channels;
            public ushort Reserved;
            public uint Support;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto, Pack = 2)]
        internal struct WaveInCaps
        {
            public ushort ManufacturerId;
            public ushort ProductId;
            public uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string Name;
            public uint Formats;
            public ushort Channels;
            public ushort Reserved;
        }

        [DllImport("winmm.dll")]
        internal static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll")]
        internal static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        internal static extern uint waveOutGetDevCaps(IntPtr deviceId, out WaveOutCaps caps, uint size);
        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        internal static extern uint waveInGetDevCaps(IntPtr deviceId, out WaveInCaps caps, uint size);
        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern uint waveOutGetErrorText(uint error, System.Text.StringBuilder text, uint length);
        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern uint waveInGetErrorText(uint error, System.Text.StringBuilder text, uint length);

        [DllImport("winmm.dll")]
        internal static extern uint waveOutOpen(out IntPtr handle, uint deviceId, ref WaveFormatEx format,
            IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")]
        internal static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")]
        internal static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")]
        internal static extern uint waveOutReset(IntPtr handle);
        [DllImport("winmm.dll")]
        internal static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")]
        internal static extern uint waveOutClose(IntPtr handle);

        [DllImport("winmm.dll")]
        internal static extern uint waveInOpen(out IntPtr handle, uint deviceId, ref WaveFormatEx format,
            IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")]
        internal static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")]
        internal static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")]
        internal static extern uint waveInStart(IntPtr handle);
        [DllImport("winmm.dll")]
        internal static extern uint waveInStop(IntPtr handle);
        [DllImport("winmm.dll")]
        internal static extern uint waveInReset(IntPtr handle);
        [DllImport("winmm.dll")]
        internal static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")]
        internal static extern uint waveInClose(IntPtr handle);
        [DllImport("winmm.dll")]
        internal static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")]
        internal static extern uint timeEndPeriod(uint period);

        internal static WaveFormatEx CreateFormat(ushort channels)
        {
            return new WaveFormatEx
            {
                FormatTag = (ushort)WaveFormatPcm,
                Channels = channels,
                SamplesPerSec = SampleRate,
                BitsPerSample = BitsPerSample,
                BlockAlign = (ushort)(channels * BitsPerSample / 8),
                AvgBytesPerSec = (uint)(SampleRate * channels * BitsPerSample / 8),
                ExtraSize = 0
            };
        }

        internal static Exception Error(string operation, uint code, bool input)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder(256);
            if (input)
                waveInGetErrorText(code, text, (uint)text.Capacity);
            else
                waveOutGetErrorText(code, text, (uint)text.Capacity);
            string description = text.Length > 0 ? text.ToString() : "MMRESULT " + code;
            return new InvalidOperationException(operation + "：" + description);
        }
    }

    internal sealed class PcmSampleQueue
    {
        private readonly object _gate = new object();
        private readonly Queue<short[]> _chunks = new Queue<short[]>();
        private readonly int _channels;
        private short[] _current;
        private int _frameOffset;
        private int _queuedFrames;

        public PcmSampleQueue(int channels)
        {
            _channels = channels;
        }

        public void Enqueue(short[] samples)
        {
            lock (_gate)
            {
                _chunks.Enqueue(samples);
                _queuedFrames += samples.Length / _channels;
                // Keep microphone latency bounded if an output device stalls.
                while (_queuedFrames > WinMm.CaptureFrames * 8 && _chunks.Count > 1)
                {
                    short[] discarded = _chunks.Dequeue();
                    _queuedFrames -= discarded.Length / _channels;
                }
            }
        }

        public void ReadStereo(short[] destination, int frames)
        {
            Array.Clear(destination, 0, frames * 2);
            lock (_gate)
            {
                int written = 0;
                while (written < frames)
                {
                    if (_current == null || _frameOffset >= _current.Length / _channels)
                    {
                        if (_chunks.Count == 0)
                            break;
                        _current = _chunks.Dequeue();
                        _frameOffset = 0;
                    }

                    int currentFrames = _current.Length / _channels - _frameOffset;
                    int take = Math.Min(frames - written, currentFrames);
                    for (int i = 0; i < take; i++)
                    {
                        int source = (_frameOffset + i) * _channels;
                        int target = (written + i) * 2;
                        short left = _current[source];
                        short right = _channels == 1 ? left : _current[source + 1];
                        destination[target] = left;
                        destination[target + 1] = right;
                    }
                    _frameOffset += take;
                    written += take;
                    _queuedFrames = Math.Max(0, _queuedFrames - take);
                }
            }
        }
    }

    internal sealed class WaveInCapture : IDisposable
    {
        private const int BufferCount = 4;
        private readonly EventWaitHandle _completed = new EventWaitHandle(false, EventResetMode.AutoReset);
        private readonly PcmSampleQueue _queue;
        private readonly List<IntPtr> _headers = new List<IntPtr>();
        private readonly List<IntPtr> _data = new List<IntPtr>();
        private readonly int _headerSize = Marshal.SizeOf(typeof(WinMm.WaveHdr));
        private readonly int _channels;
        private readonly int _bytesPerBuffer;
        private IntPtr _handle;
        private Thread _worker;
        private volatile bool _running;

        public string DeviceName { get; private set; }

        public WaveInCapture(uint deviceId, string deviceName)
        {
            DeviceName = deviceName;
            Exception firstError = null;
            int channels = 2;
            if (!TryOpen(deviceId, channels, out firstError))
            {
                channels = 1;
                Exception secondError;
                if (!TryOpen(deviceId, channels, out secondError))
                {
                    _completed.Dispose();
                    throw new InvalidOperationException("无法打开麦克风 " + deviceName + "。", secondError ?? firstError);
                }
            }
            _channels = channels;
            _bytesPerBuffer = WinMm.CaptureFrames * _channels * 2;
            _queue = new PcmSampleQueue(_channels);
            try
            {
                for (int i = 0; i < BufferCount; i++)
                {
                    IntPtr data = Marshal.AllocHGlobal(_bytesPerBuffer);
                    IntPtr header = Marshal.AllocHGlobal(_headerSize);
                    _data.Add(data);
                    _headers.Add(header);
                    Marshal.StructureToPtr(new WinMm.WaveHdr
                    {
                        Data = data,
                        BufferLength = (uint)_bytesPerBuffer,
                        BytesRecorded = 0,
                        User = UIntPtr.Zero,
                        Flags = 0,
                        Loops = 0,
                        Next = IntPtr.Zero,
                        Reserved = UIntPtr.Zero
                    }, header, false);
                    Check(WinMm.waveInPrepareHeader(_handle, header, (uint)_headerSize), "准备麦克风缓冲区");
                    Check(WinMm.waveInAddBuffer(_handle, header, (uint)_headerSize), "提交麦克风缓冲区");
                }
                Check(WinMm.waveInStart(_handle), "启动麦克风");
                _running = true;
                _worker = new Thread(CaptureLoop);
                _worker.IsBackground = true;
                _worker.Name = "BGM microphone capture";
                _worker.Start();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void ReadStereo(short[] destination, int frames)
        {
            _queue.ReadStereo(destination, frames);
        }

        private bool TryOpen(uint deviceId, int channels, out Exception error)
        {
            error = null;
            WinMm.WaveFormatEx format = WinMm.CreateFormat((ushort)channels);
            uint result = WinMm.waveInOpen(out _handle, deviceId, ref format,
                _completed.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, WinMm.CallbackEvent);
            if (result == 0)
                return true;
            error = WinMm.Error("打开麦克风", result, true);
            _handle = IntPtr.Zero;
            return false;
        }

        private void CaptureLoop()
        {
            while (_running)
            {
                _completed.WaitOne(100);
                for (int i = 0; i < _headers.Count && _running; i++)
                {
                    IntPtr headerPointer = _headers[i];
                    WinMm.WaveHdr header = (WinMm.WaveHdr)Marshal.PtrToStructure(headerPointer, typeof(WinMm.WaveHdr));
                    if ((header.Flags & WinMm.HeaderDone) == 0)
                        continue;

                    int byteCount = (int)Math.Min(header.BytesRecorded, (uint)_bytesPerBuffer);
                    byteCount -= byteCount % (_channels * 2);
                    if (byteCount > 0)
                    {
                        byte[] bytes = new byte[byteCount];
                        Marshal.Copy(header.Data, bytes, 0, byteCount);
                        short[] samples = new short[byteCount / 2];
                        Buffer.BlockCopy(bytes, 0, samples, 0, byteCount);
                        _queue.Enqueue(samples);
                    }

                    header.BytesRecorded = 0;
                    Marshal.StructureToPtr(header, headerPointer, false);
                    uint result = WinMm.waveInAddBuffer(_handle, headerPointer, (uint)_headerSize);
                    if (result != 0)
                        AppPaths.Log("重新提交麦克风缓冲区失败：" + result);
                }
            }
        }

        private static void Check(uint result, string operation)
        {
            if (result != 0)
                throw WinMm.Error(operation, result, true);
        }

        public void Dispose()
        {
            _running = false;
            if (_handle != IntPtr.Zero)
            {
                WinMm.waveInStop(_handle);
                WinMm.waveInReset(_handle);
                _completed.Set();
            }
            if (_worker != null && _worker.IsAlive && Thread.CurrentThread != _worker)
                _worker.Join(1000);
            if (_handle != IntPtr.Zero)
            {
                foreach (IntPtr header in _headers)
                    WinMm.waveInUnprepareHeader(_handle, header, (uint)_headerSize);
                WinMm.waveInClose(_handle);
                _handle = IntPtr.Zero;
            }
            foreach (IntPtr header in _headers)
                if (header != IntPtr.Zero) Marshal.FreeHGlobal(header);
            foreach (IntPtr data in _data)
                if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
            _headers.Clear();
            _data.Clear();
            _completed.Dispose();
        }
    }

    internal sealed class WaveOutSink : IDisposable
    {
        private const int BufferCount = 4;
        private readonly EventWaitHandle _completed = new EventWaitHandle(false, EventResetMode.AutoReset);
        private readonly List<IntPtr> _headers = new List<IntPtr>();
        private readonly List<IntPtr> _data = new List<IntPtr>();
        private readonly bool[] _submitted = new bool[BufferCount];
        private readonly int _headerSize = Marshal.SizeOf(typeof(WinMm.WaveHdr));
        private readonly int _bytesPerBuffer = WinMm.BlockFrames * WinMm.OutputChannels * 2;
        private IntPtr _handle;
        private int _nextIndex;
        public string DeviceName { get; private set; }

        public WaveOutSink(uint deviceId, string deviceName)
        {
            DeviceName = deviceName;
            WinMm.WaveFormatEx format = WinMm.CreateFormat(WinMm.OutputChannels);
            uint result = WinMm.waveOutOpen(out _handle, deviceId, ref format,
                _completed.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, WinMm.CallbackEvent);
            if (result != 0)
            {
                _completed.Dispose();
                throw WinMm.Error("打开播放设备 " + deviceName, result, false);
            }
            try
            {
                for (int i = 0; i < BufferCount; i++)
                {
                    IntPtr data = Marshal.AllocHGlobal(_bytesPerBuffer);
                    IntPtr header = Marshal.AllocHGlobal(_headerSize);
                    _data.Add(data);
                    _headers.Add(header);
                    Marshal.StructureToPtr(new WinMm.WaveHdr
                    {
                        Data = data,
                        BufferLength = (uint)_bytesPerBuffer,
                        BytesRecorded = 0,
                        User = UIntPtr.Zero,
                        Flags = 0,
                        Loops = 0,
                        Next = IntPtr.Zero,
                        Reserved = UIntPtr.Zero
                    }, header, false);
                    Check(WinMm.waveOutPrepareHeader(_handle, header, (uint)_headerSize), "准备播放缓冲区");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Write(short[] samples)
        {
            if (samples == null || samples.Length * 2 != _bytesPerBuffer)
                return;

            int selected = -1;
            Stopwatch waiting = Stopwatch.StartNew();
            while (selected < 0)
            {
                for (int offset = 0; offset < BufferCount; offset++)
                {
                    int index = (_nextIndex + offset) % BufferCount;
                    WinMm.WaveHdr header = (WinMm.WaveHdr)Marshal.PtrToStructure(_headers[index], typeof(WinMm.WaveHdr));
                    if (!_submitted[index] || (header.Flags & WinMm.HeaderDone) != 0)
                    {
                        selected = index;
                        _nextIndex = (index + 1) % BufferCount;
                        break;
                    }
                }
                if (selected < 0)
                {
                    if (waiting.ElapsedMilliseconds >= 1000)
                        throw new InvalidOperationException("播放设备超过一秒没有完成缓冲区，请刷新音频设备。");
                    _completed.WaitOne(20);
                }
            }

            Marshal.Copy(samples, 0, _data[selected], samples.Length);
            uint result = WinMm.waveOutWrite(_handle, _headers[selected], (uint)_headerSize);
            if (result != 0)
                throw WinMm.Error("写入播放缓冲区", result, false);
            _submitted[selected] = true;
        }

        private static void Check(uint result, string operation)
        {
            if (result != 0)
                throw WinMm.Error(operation, result, false);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                WinMm.waveOutReset(_handle);
                foreach (IntPtr header in _headers)
                    WinMm.waveOutUnprepareHeader(_handle, header, (uint)_headerSize);
                WinMm.waveOutClose(_handle);
                _handle = IntPtr.Zero;
            }
            foreach (IntPtr header in _headers)
                if (header != IntPtr.Zero) Marshal.FreeHGlobal(header);
            foreach (IntPtr data in _data)
                if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
            _headers.Clear();
            _data.Clear();
            _completed.Dispose();
        }
    }
}
