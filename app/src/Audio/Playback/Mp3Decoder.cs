using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace BgmHotkey
{
    internal static class Mp3Decoder
    {
        private const int MfVersion = 0x20070;
        private const int MfStartupFull = 0;
        private const int AllStreams = -2;
        private const int FirstAudioStream = -3;
        private const int MediaSourceStream = -1;
        private const int EndOfStream = 0x00000002;
        private const int SourceReaderError = 0x00000001;
        private const int SampleRate = WinMm.SampleRate;
        private const int Channels = WinMm.OutputChannels;
        private const int BitsPerSample = WinMm.BitsPerSample;

        private static readonly Guid MediaTypeMajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid MediaTypeSubtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MediaTypeAudio = new Guid("73647561-0000-0010-8000-00aa00389b71");
        private static readonly Guid AudioFormatPcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid AudioChannels = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        private static readonly Guid AudioSamplesPerSecond = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        private static readonly Guid AudioBitsPerSample = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        private static readonly Guid PresentationDuration = new Guid("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMediaType(out IMFMediaType mediaType);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MFCreateSourceReaderFromURL(
            [MarshalAs(UnmanagedType.LPWStr)] string url,
            IntPtr attributes,
            out IMFSourceReader sourceReader);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoUninitialize();

        private static void SetGuid(IMFAttributes attributes, Guid key, Guid value)
        {
            int hr = attributes.SetGUID(ref key, ref value);
            Marshal.ThrowExceptionForHR(hr);
        }

        internal sealed class StreamingReader : IDisposable
        {
            private const int PropVariantInt64 = 20;
            private const int PropVariantUInt64 = 21;
            private readonly Queue<short[]> _pendingBuffers = new Queue<short[]>();
            private IMFSourceReader _reader;
            private IMFMediaType _nativeType;
            private IMFMediaType _outputType;
            private IMFMediaType _decodedType;
            private short[] _currentSamples;
            private int _currentOffset;
            private bool _comInitialized;
            private bool _mfStarted;
            private bool _endOfStream;
            private bool _disposed;
            private long _seekTargetTime;
            private readonly long _startFrame;
            private readonly long _endFrame;
            private long _framesRead;
            public double DurationSeconds { get; private set; }
            public double PositionSeconds { get { return (_startFrame + Interlocked.Read(ref _framesRead)) / (double)SampleRate; } }

            [StructLayout(LayoutKind.Explicit, Size = 16)]
            private struct Int64PropVariant
            {
                [FieldOffset(0)] public ushort VariantType;
                [FieldOffset(8)] public long Int64Value;
            }

            public StreamingReader(string path, double startSeconds, double? endSeconds, Func<bool> cancelRequested = null)
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException("找不到 BGM 文件：" + path, path);

                string stage = "initialize COM";
                int hr = CoInitializeEx(IntPtr.Zero, 0);
                Marshal.ThrowExceptionForHR(hr);
                _comInitialized = true;
                _startFrame = Math.Max(0, (long)Math.Round(startSeconds * SampleRate));
                _endFrame = endSeconds.HasValue ? Math.Max(_startFrame, (long)Math.Round(endSeconds.Value * SampleRate)) : Int64.MaxValue;
                try
                {
                    stage = "start Media Foundation";
                    hr = MFStartup(MfVersion, MfStartupFull);
                    Marshal.ThrowExceptionForHR(hr);
                    _mfStarted = true;

                    stage = "open audio source";
                    hr = MFCreateSourceReaderFromURL(Path.GetFullPath(path), IntPtr.Zero, out _reader);
                    Marshal.ThrowExceptionForHR(hr);
                    stage = "read audio duration";
                    DurationSeconds = ReadDurationSeconds();
                    stage = "select the audio stream";
                    hr = _reader.SetStreamSelection(AllStreams, 0);
                    Marshal.ThrowExceptionForHR(hr);
                    hr = _reader.SetStreamSelection(FirstAudioStream, 1);
                    Marshal.ThrowExceptionForHR(hr);

                    stage = "read source media type";
                    hr = _reader.GetNativeMediaType(FirstAudioStream, 0, out _nativeType);
                    Marshal.ThrowExceptionForHR(hr);
                    Guid majorType;
                    Guid majorTypeKey = MediaTypeMajorType;
                    stage = "read MP3 stream type";
                    hr = _nativeType.GetGUID(ref majorTypeKey, out majorType);
                    Marshal.ThrowExceptionForHR(hr);
                    if (majorType != MediaTypeAudio)
                        throw new InvalidOperationException("所选文件中没有音频流。");

                    stage = "create PCM media type";
                    hr = MFCreateMediaType(out _outputType);
                    Marshal.ThrowExceptionForHR(hr);
                    SetGuid(_outputType, MediaTypeMajorType, majorType);
                    SetGuid(_outputType, MediaTypeSubtype, AudioFormatPcm);
                    Guid requestedKey = AudioChannels;
                    Marshal.ThrowExceptionForHR(_outputType.SetUINT32(ref requestedKey, Channels));
                    requestedKey = AudioSamplesPerSecond;
                    Marshal.ThrowExceptionForHR(_outputType.SetUINT32(ref requestedKey, SampleRate));
                    requestedKey = AudioBitsPerSample;
                    Marshal.ThrowExceptionForHR(_outputType.SetUINT32(ref requestedKey, BitsPerSample));

                    stage = "configure streaming audio decode";
                    hr = _reader.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, _outputType);
                    Marshal.ThrowExceptionForHR(hr);
                    stage = "read decoded PCM format";
                    hr = _reader.GetCurrentMediaType(FirstAudioStream, out _decodedType);
                    Marshal.ThrowExceptionForHR(hr);
                    int channels;
                    int rate;
                    int bits;
                    Guid key = AudioChannels;
                    Marshal.ThrowExceptionForHR(_decodedType.GetUINT32(ref key, out channels));
                    key = AudioSamplesPerSecond;
                    Marshal.ThrowExceptionForHR(_decodedType.GetUINT32(ref key, out rate));
                    key = AudioBitsPerSample;
                    Marshal.ThrowExceptionForHR(_decodedType.GetUINT32(ref key, out bits));
                    if (channels != Channels || rate != SampleRate || bits != BitsPerSample)
                    {
                        throw new InvalidOperationException(
                            "音频格式无法转换为 44100 Hz、双声道、16 位 PCM；当前为 " +
                            rate + " Hz、" + channels + " 声道、" + bits + " 位。");
                    }
                    hr = _reader.SetStreamSelection(FirstAudioStream, 1);
                    Marshal.ThrowExceptionForHR(hr);

                    if (startSeconds >= DurationSeconds)
                        throw new InvalidOperationException("开始锚点必须早于音频时长。");
                    if (endSeconds.HasValue && (endSeconds.Value <= startSeconds || endSeconds.Value > DurationSeconds))
                        throw new InvalidOperationException("结束锚点必须大于开始锚点且不能超过音频时长。");
                    if (startSeconds > 0)
                    {
                        if (String.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
                        {
                            // Windows' VBR MP3 time index can report a plausible timestamp for
                            // the wrong content. Decode forward through bounded PCM buffers and
                            // count sample frames so seeks follow the actual timeline.
                            _seekTargetTime = 0;
                            DiscardFrames(_startFrame, cancelRequested);
                        }
                        else
                        {
                            Seek(startSeconds);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    Dispose();
                    throw new InvalidOperationException("打开流式音频解码器失败（" + stage + "）：" + ex.Message, ex);
                }
            }

            private double ReadDurationSeconds()
            {
                Int64PropVariant value = new Int64PropVariant();
                IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Int64PropVariant)));
                try
                {
                    Marshal.StructureToPtr(value, memory, false);
                    Guid key = PresentationDuration;
                    int hr = _reader.GetPresentationAttribute(MediaSourceStream, ref key, memory);
                    Marshal.ThrowExceptionForHR(hr);
                    value = (Int64PropVariant)Marshal.PtrToStructure(memory, typeof(Int64PropVariant));
                    if ((value.VariantType != PropVariantUInt64 && value.VariantType != PropVariantInt64) || value.Int64Value <= 0)
                        throw new InvalidOperationException("系统没有提供有效的音频时长。");
                    return value.Int64Value / 10000000.0;
                }
                finally
                {
                    Marshal.FreeHGlobal(memory);
                }
            }

            public int ReadFrames(short[] destination, int frameCount)
            {
                if (destination == null) throw new ArgumentNullException("destination");
                if (_disposed) throw new ObjectDisposedException("StreamingReader");
                if (frameCount < 0 || frameCount > destination.Length / Channels) throw new ArgumentOutOfRangeException("frameCount");
                int targetSamples = frameCount * Channels;
                targetSamples -= targetSamples % Channels;
                long remainingFrames = _endFrame == Int64.MaxValue ? Int64.MaxValue : _endFrame - _startFrame - Interlocked.Read(ref _framesRead);
                if (remainingFrames <= 0) return 0;
                if (remainingFrames < frameCount) targetSamples = (int)remainingFrames * Channels;
                Array.Clear(destination, 0, destination.Length);

                int written = 0;
                while (written < targetSamples)
                {
                    if (_currentSamples == null || _currentOffset >= _currentSamples.Length)
                    {
                        _currentSamples = null;
                        _currentOffset = 0;
                        if (_pendingBuffers.Count == 0 && !LoadNextSample())
                            break;
                        if (_pendingBuffers.Count > 0)
                            _currentSamples = _pendingBuffers.Dequeue();
                    }

                    int count = Math.Min(targetSamples - written, _currentSamples.Length - _currentOffset);
                    count -= count % Channels;
                    if (count <= 0) break;
                    Array.Copy(_currentSamples, _currentOffset, destination, written, count);
                    written += count;
                    _currentOffset += count;
                }

                int frames = written / Channels;
                if (frames > 0) Interlocked.Add(ref _framesRead, frames);
                return frames;
            }

            private void Seek(double seconds)
            {
                _seekTargetTime = (long)(seconds * 10000000.0);
                Guid timeFormat = Guid.Empty;
                Int64PropVariant value = new Int64PropVariant
                {
                    VariantType = PropVariantInt64,
                    Int64Value = _seekTargetTime
                };
                IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Int64PropVariant)));
                try
                {
                    Marshal.StructureToPtr(value, memory, false);
                    int hr = _reader.SetCurrentPosition(ref timeFormat, memory);
                    if (hr < 0)
                        AppPaths.Log("MP3 定位失败，正在从头流式跳过到指定位置：" + Marshal.GetExceptionForHR(hr).Message);
                }
                finally
                {
                    Marshal.FreeHGlobal(memory);
                }
            }

            private void DiscardFrames(long frameCount, Func<bool> cancelRequested)
            {
                long remaining = frameCount;
                while (remaining > 0)
                {
                    if (cancelRequested != null && cancelRequested())
                        throw new OperationCanceledException("MP3 seek was superseded.");
                    if (_currentSamples == null || _currentOffset >= _currentSamples.Length)
                    {
                        _currentSamples = null;
                        _currentOffset = 0;
                        if (_pendingBuffers.Count == 0 && !LoadNextSample())
                            throw new InvalidOperationException("MP3 在起播锚点之前已结束。");
                        if (_pendingBuffers.Count > 0)
                            _currentSamples = _pendingBuffers.Dequeue();
                    }
                    int available = (_currentSamples.Length - _currentOffset) / Channels;
                    if (available <= 0) continue;
                    int take = (int)Math.Min(remaining, available);
                    _currentOffset += take * Channels;
                    remaining -= take;
                }
            }

            private bool LoadNextSample()
            {
                while (!_endOfStream)
                {
                    int actualStream;
                    int flags;
                    long timestamp;
                    IMFSample sample;
                    int hr = _reader.ReadSample(FirstAudioStream, 0, out actualStream, out flags, out timestamp, out sample);
                    Marshal.ThrowExceptionForHR(hr);
                    if ((flags & SourceReaderError) != 0)
                    {
                        if (sample != null) Marshal.ReleaseComObject(sample);
                        throw new InvalidOperationException("Media Foundation 解码音频时返回错误。");
                    }
                    if ((flags & EndOfStream) != 0)
                        _endOfStream = true;

                    if (sample != null)
                    {
                        try
                        {
                            int bufferCount;
                            hr = sample.GetBufferCount(out bufferCount);
                            Marshal.ThrowExceptionForHR(hr);

                            long samplesToSkip = GetFallbackSkipSamples(timestamp);
                            for (int index = 0; index < bufferCount; index++)
                            {
                                IMFMediaBuffer buffer = null;
                                try
                                {
                                    hr = sample.GetBufferByIndex(index, out buffer);
                                    Marshal.ThrowExceptionForHR(hr);
                                    IntPtr data;
                                    int maxLength;
                                    int currentLength;
                                    hr = buffer.Lock(out data, out maxLength, out currentLength);
                                    Marshal.ThrowExceptionForHR(hr);
                                    try
                                    {
                                        int alignedBytes = currentLength - (currentLength % (Channels * 2));
                                        int sampleCount = alignedBytes / 2;
                                        if (sampleCount > 0)
                                        {
                                            short[] samples = new short[sampleCount];
                                            Marshal.Copy(data, samples, 0, sampleCount);
                                            int skip = (int)Math.Min(samplesToSkip, sampleCount);
                                            skip -= skip % Channels;
                                            samplesToSkip -= skip;
                                            if (skip > 0)
                                            {
                                                short[] remaining = new short[sampleCount - skip];
                                                Array.Copy(samples, skip, remaining, 0, remaining.Length);
                                                samples = remaining;
                                            }
                                            if (samples.Length > 0)
                                                _pendingBuffers.Enqueue(samples);
                                        }
                                    }
                                    finally
                                    {
                                        buffer.Unlock();
                                    }
                                }
                                finally
                                {
                                    if (buffer != null) Marshal.ReleaseComObject(buffer);
                                }
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(sample);
                        }
                    }

                    if (_pendingBuffers.Count > 0)
                        return true;
                }
                return false;
            }

            private long GetFallbackSkipSamples(long timestamp)
            {
                if (_seekTargetTime <= timestamp)
                    return 0;

                long difference = _seekTargetTime - timestamp;
                long frames = (long)Math.Ceiling(difference * (double)SampleRate / 10000000.0);
                return frames * Channels;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_decodedType != null) { Marshal.ReleaseComObject(_decodedType); _decodedType = null; }
                if (_outputType != null) { Marshal.ReleaseComObject(_outputType); _outputType = null; }
                if (_nativeType != null) { Marshal.ReleaseComObject(_nativeType); _nativeType = null; }
                if (_reader != null) { Marshal.ReleaseComObject(_reader); _reader = null; }
                if (_mfStarted) { MFShutdown(); _mfStarted = false; }
                if (_comInitialized) { CoUninitialize(); _comInitialized = false; }
            }
        }

    }

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IntPtr theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out long value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out int length);
        [PreserveSig] int GetString(ref Guid key, IntPtr value, int size, out int length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
        [PreserveSig] int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr value, int size, out int length);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr value, out int size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, long value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr value, int size);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes destination);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType : IMFAttributes
    {
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(int streamIndex, out int selected);
        [PreserveSig] int SetStreamSelection(int streamIndex, int selected);
        [PreserveSig] int GetNativeMediaType(int streamIndex, int mediaTypeIndex, out IMFMediaType mediaType);
        [PreserveSig] int GetCurrentMediaType(int streamIndex, out IMFMediaType mediaType);
        [PreserveSig] int SetCurrentMediaType(int streamIndex, IntPtr reserved, [MarshalAs(UnmanagedType.Interface)] IMFMediaType mediaType);
        [PreserveSig] int SetCurrentPosition(ref Guid timeFormat, IntPtr position);
        [PreserveSig] int ReadSample(int streamIndex, int controlFlags, out int actualStreamIndex,
            out int streamFlags, out long timestamp, [MarshalAs(UnmanagedType.Interface)] out IMFSample sample);
        [PreserveSig] int Flush(int streamIndex);
        [PreserveSig] int GetServiceForStream(int streamIndex, ref Guid service, ref Guid iid, out IntPtr result);
        [PreserveSig] int GetPresentationAttribute(int streamIndex, ref Guid attribute, IntPtr value);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample
    {
        // Flatten IMFAttributes here. COM interop does not reliably include methods
        // inherited from an imported IUnknown interface when dispatching the sample
        // interface's own methods; without these declarations GetSampleFlags lands on
        // IMFAttributes.GetItem in the native vtable.
        [PreserveSig] int GetItem(ref Guid key, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, IntPtr value, out int result);
        [PreserveSig] int Compare(IntPtr theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int GetUINT64(ref Guid key, out long value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out int length);
        [PreserveSig] int GetString(ref Guid key, IntPtr value, int size, out int length);
        [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out int length);
        [PreserveSig] int GetBlobSize(ref Guid key, out int size);
        [PreserveSig] int GetBlob(ref Guid key, IntPtr value, int size, out int length);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr value, out int size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
        [PreserveSig] int SetItem(ref Guid key, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, int value);
        [PreserveSig] int SetUINT64(ref Guid key, long value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, IntPtr value, int size);
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetItemByIndex(int index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems([MarshalAs(UnmanagedType.Interface)] IMFAttributes destination);

        [PreserveSig] int GetSampleFlags(out int flags);
        [PreserveSig] int SetSampleFlags(int flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out int count);
        [PreserveSig] int GetBufferByIndex(int index, [MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer([MarshalAs(UnmanagedType.Interface)] out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer([MarshalAs(UnmanagedType.Interface)] IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(int index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out int length);
        [PreserveSig] int CopyToBuffer([MarshalAs(UnmanagedType.Interface)] IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int currentLength);
        [PreserveSig] int SetCurrentLength(int currentLength);
        [PreserveSig] int GetMaxLength(out int maxLength);
    }
}
