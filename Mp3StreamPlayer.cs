using System;
using System.Collections.Concurrent;
using System.Threading;

namespace BgmHotkey
{
    internal sealed class Mp3StreamPlayer : IDisposable
    {
        private const int QueueCapacity = 12; // 240 ms of 20 ms blocks absorbs scheduling jitter on the virtual route.
        private readonly string _path;
        private readonly double _startSeconds;
        private readonly BlockingCollection<short[]> _blocks =
            new BlockingCollection<short[]>(new ConcurrentQueue<short[]>(), QueueCapacity);
        private readonly Thread _decoderThread;
        private volatile bool _stopRequested;
        private volatile bool _completed;
        private volatile bool _disposeWhenCompleted;
        private Exception _error;
        private int _disposed;

        public Mp3StreamPlayer(string path, double startSeconds)
        {
            _path = path;
            _startSeconds = startSeconds;
            _decoderThread = new Thread(DecodeLoop);
            _decoderThread.IsBackground = true;
            _decoderThread.Name = "BGM MP3 stream decoder";
            _decoderThread.SetApartmentState(ApartmentState.MTA);
        }

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

        public void Start()
        {
            _decoderThread.Start();
        }

        public bool TryReadBlock(short[] destination)
        {
            Array.Clear(destination, 0, destination.Length);
            short[] block;
            try
            {
                if (!_blocks.TryTake(out block))
                    return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            Buffer.BlockCopy(block, 0, destination, 0, Math.Min(block.Length, destination.Length) * 2);
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
                using (Mp3Decoder.StreamingReader reader = new Mp3Decoder.StreamingReader(_path, _startSeconds))
                {
                    while (!_stopRequested)
                    {
                        short[] block = new short[WinMm.BlockFrames * WinMm.OutputChannels];
                        int frames = reader.ReadFrames(block, WinMm.BlockFrames);
                        if (frames <= 0)
                            break;

                        while (!_stopRequested && !_blocks.TryAdd(block, 100))
                        {
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _error = ex;
                AppPaths.Log("流式播放失败：" + ex);
            }
            finally
            {
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
        }
    }
}
