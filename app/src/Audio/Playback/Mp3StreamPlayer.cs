using System;
using System.Collections.Concurrent;
using System.Threading;

namespace BgmHotkey
{
    internal sealed class Mp3StreamPlayer : IDisposable
    {
        private const int QueueCapacity = 12; // 240 ms of 20 ms blocks absorbs scheduling jitter on the virtual route.
        private readonly string _path;
        private sealed class PcmBlock
        {
            public short[] Samples;
            public int Frames;
        }

        private readonly double _startSeconds;
        private readonly double? _endSeconds;
        private readonly BlockingCollection<PcmBlock> _blocks =
            new BlockingCollection<PcmBlock>(new ConcurrentQueue<PcmBlock>(), QueueCapacity);
        private readonly Thread _decoderThread;
        private volatile bool _stopRequested;
        private volatile bool _completed;
        private volatile bool _disposeWhenCompleted;
        private readonly ManualResetEvent _ready = new ManualResetEvent(false);
        private Exception _error;
        private int _disposed;
        private long _consumedFrames;
        private double _durationSeconds;

        public Mp3StreamPlayer(string path, double startSeconds, double? endSeconds)
        {
            _path = path;
            _startSeconds = startSeconds;
            _endSeconds = endSeconds;
            _decoderThread = new Thread(DecodeLoop);
            _decoderThread.IsBackground = true;
            _decoderThread.Name = "BGM audio stream decoder";
            _decoderThread.SetApartmentState(ApartmentState.MTA);
        }

        public double DurationSeconds { get { return _durationSeconds; } }
        public double PositionSeconds { get { return _startSeconds + Interlocked.Read(ref _consumedFrames) / (double)WinMm.SampleRate; } }

        public Exception Error
        {
            get { return _error; }
        }

        public bool IsFinished
        {
            get
            {
                if (!_completed) return false;
                try { return _blocks.Count == 0; }
                catch (ObjectDisposedException) { return true; }
            }
        }

        public bool WaitUntilReady(int millisecondsTimeout)
        {
            return _ready.WaitOne(millisecondsTimeout);
        }

        public void Start()
        {
            _decoderThread.Start();
        }

        public bool TryReadBlock(short[] destination, out int frames)
        {
            frames = 0;
            Array.Clear(destination, 0, destination.Length);
            PcmBlock block;
            try
            {
                if (!_blocks.TryTake(out block))
                    return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            frames = block.Frames;
            Buffer.BlockCopy(block.Samples, 0, destination, 0, Math.Min(block.Samples.Length, destination.Length) * 2);
            Interlocked.Add(ref _consumedFrames, frames);
            return true;
        }

        public void Cancel()
        {
            _stopRequested = true;
        }

        private void DecodeLoop()
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Normal;
                using (Mp3Decoder.StreamingReader reader = new Mp3Decoder.StreamingReader(
                    _path, _startSeconds, _endSeconds, delegate { return _stopRequested; }))
                {
                    _durationSeconds = reader.DurationSeconds;
                    if (_startSeconds >= _durationSeconds)
                        throw new InvalidOperationException("开始锚点必须早于音频时长。");
                    if (_endSeconds.HasValue && (_endSeconds.Value > _durationSeconds || _endSeconds.Value <= _startSeconds))
                        throw new InvalidOperationException("结束锚点必须大于开始锚点且不能超过音频时长。");

                    // Prime the bounded queue before reporting ready. This prevents the first
                    // output block from racing the decoder open and producing a short dropout.
                    int primed = 0;
                    while (!_stopRequested && primed < 4)
                    {
                        short[] samples = new short[WinMm.BlockFrames * WinMm.OutputChannels];
                        int frames = reader.ReadFrames(samples, WinMm.BlockFrames);
                        if (frames <= 0) break;
                        PcmBlock pcm = new PcmBlock { Samples = samples, Frames = frames };
                        while (!_stopRequested && !_blocks.TryAdd(pcm, 100)) { }
                        if (_stopRequested) break;
                        primed++;
                    }
                    _ready.Set();
                    while (!_stopRequested)
                    {
                        short[] block = new short[WinMm.BlockFrames * WinMm.OutputChannels];
                        int frames = reader.ReadFrames(block, WinMm.BlockFrames);
                        if (frames <= 0)
                            break;

                        PcmBlock pcm = new PcmBlock { Samples = block, Frames = frames };
                        while (!_stopRequested && !_blocks.TryAdd(pcm, 100))
                        {
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!_stopRequested)
                {
                    _error = ex;
                    AppPaths.Log("流式播放失败：" + ex);
                }
            }
            finally
            {
                _ready.Set();
                try { _blocks.CompleteAdding(); } catch (ObjectDisposedException) { }
                _completed = true;
                if (_disposeWhenCompleted)
                    try { _blocks.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _disposeWhenCompleted = true;
            Cancel();
            if ((_decoderThread.ThreadState & ThreadState.Unstarted) != 0)
            {
                try { _blocks.Dispose(); } catch { }
                return;
            }
            if (_decoderThread.IsAlive && Thread.CurrentThread != _decoderThread)
                _decoderThread.Join(1500);
            if (_completed)
                try { _blocks.Dispose(); } catch { }
            if (_completed)
                try { _ready.Dispose(); } catch { }
        }
    }
}
