using LibVLCSharp.Shared;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading;

namespace VideoZoom
{
    public sealed class VlcRenderer : IDisposable
    {
        private readonly LibVLC _libVLC;
        private readonly LibVLCSharp.Shared.MediaPlayer _mp;

        // Active frame buffer (pinned)
        private GCHandle _frameHandle;
        private IntPtr _framePtr = IntPtr.Zero;
        private byte[] _frameBytes = Array.Empty<byte>();

        // Optional second buffer to reduce tearing
        private GCHandle _frameHandle2;
        private IntPtr _framePtr2 = IntPtr.Zero;
        private byte[] _frameBytes2 = Array.Empty<byte>();
        private volatile bool _useBuffer2 = false;

        public int VideoW { get; private set; }
        public int VideoH { get; private set; }
        public int Pitch { get; private set; } // bytes per row

        public WriteableBitmap? FullFrameBitmap { get; set; }

        // Concurrency guards
        private volatile int _frameUpdateInProgress = 0; // 0/1 via Interlocked
        private readonly SemaphoreSlim _bitmapUpdateSemaphore = new(1, 1);

        public VlcRenderer()
        {
            Core.Initialize();
            _libVLC = new LibVLC("--no-osd", "--quiet");
            _mp = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
            FullFrameBitmap = null;
            InstallCallbacks();
        }

        public double Position
        {
            get => _mp.Position; // 0.0 to 1.0
            set => _mp.Position = (float)value;
        }

        private void InstallCallbacks()
        {
            // Negotiation callback (LibVLC 3.x signature with opaque/chroma on Windows)
            _mp.SetVideoFormatCallbacks(
                (ref nint opaque, nint chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines) =>
                {
                    // 1) Request RV32 (BGRA) explicitly
                    unsafe
                    {
                        var c = (byte*)chroma;
                        c[0] = (byte)'R'; c[1] = (byte)'V'; c[2] = (byte)'3'; c[3] = (byte)'2';
                    }

                    if (width == 0 || height == 0)
                    {
                        width = 1920; height = 1080;
                    }

                    VideoW = (int)width;
                    VideoH = (int)height;

                    Pitch = VideoW * 4;  // 32bpp
                    pitches = (uint)Pitch;
                    lines = (uint)VideoH;

                    int bufSize = Pitch * VideoH;

                    FreeBuffers();
                    _frameBytes = new byte[bufSize];
                    _frameHandle = GCHandle.Alloc(_frameBytes, GCHandleType.Pinned);
                    _framePtr = _frameHandle.AddrOfPinnedObject();

                    _frameBytes2 = new byte[bufSize];
                    _frameHandle2 = GCHandle.Alloc(_frameBytes2, GCHandleType.Pinned);
                    _framePtr2 = _frameHandle2.AddrOfPinnedObject();
                    _useBuffer2 = false;

                    var disp = Application.Current?.Dispatcher;
                    if (disp != null && !disp.HasShutdownStarted)
                    {
                        disp.Invoke(() =>
                        {
                            FullFrameBitmap = new WriteableBitmap(VideoW, VideoH, 96, 96, PixelFormats.Bgra32, null);
                        });
                    }

                    return 1;
                },
                (ref nint opaque) => { FreeBuffers(); }
            );


            _mp.SetVideoCallbacks
            (
                // Lock: provide current plane pointer (swap between two buffers)
                (opaque, planes) =>
                {
                    unsafe
                    {
                        var planePtr = (IntPtr*)planes;
                        planePtr[0] = _useBuffer2 ? _framePtr2 : _framePtr;
                    }
                    return IntPtr.Zero;
                },
                // Unlock: flip the buffer flag so Display reads the just-written buffer
                (opaque, picture, planes) =>
                {
                    _useBuffer2 = !_useBuffer2;
                },
                // Display: copy into WriteableBitmap
                (opaque, picture) =>
                {
                    var disp = Application.Current?.Dispatcher;
                    if (FullFrameBitmap == null || disp == null || disp.HasShutdownStarted)
                    {
                        // Ensure we clear the in-progress flag if set
                        return;
                    }

                    // simple backpressure: if an update is already queued or running, drop this frame
                    if (Interlocked.Exchange(ref _frameUpdateInProgress, 1) == 1) return;

                    disp.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            // Recreate on stride/size mismatch
                            if (FullFrameBitmap.BackBufferStride != Pitch ||
                                FullFrameBitmap.PixelWidth != VideoW ||
                                FullFrameBitmap.PixelHeight != VideoH)
                            {
                                FullFrameBitmap = new WriteableBitmap(VideoW, VideoH, 96, 96, PixelFormats.Bgra32, null);
                            }
                            // Avoid colliding with compositor's read lock
                            if (!FullFrameBitmap.TryLock(new System.Windows.Duration(TimeSpan.Zero)))
                                return; // drop this frame quietly                        
                            try
                            {
                                unsafe
                                {
                                    var srcPtr = _useBuffer2 ? _framePtr : _framePtr2;
                                    if(srcPtr != 0)
                                    { 
                                        long copySize = (long)Pitch * (long)VideoH;
                                        Buffer.MemoryCopy(
                                        source: (void*)srcPtr,
                                        destination: (void*)FullFrameBitmap.BackBuffer,
                                        destinationSizeInBytes: FullFrameBitmap.BackBufferStride * FullFrameBitmap.PixelHeight,
                                        sourceBytesToCopy: copySize);
                                    }
                                }
                               FullFrameBitmap.AddDirtyRect(new Int32Rect(0, 0, VideoW, VideoH));
                            }
                            finally
                            {
                                FullFrameBitmap.Unlock();
                            }
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _frameUpdateInProgress, 0);
                        }
                    }), System.Windows.Threading.DispatcherPriority.Normal);

                }
            );
        }

        private void FreeBuffers()
        {
            if (_frameHandle.IsAllocated) _frameHandle.Free();
            _framePtr = IntPtr.Zero;
            _frameBytes = Array.Empty<byte>();

            if (_frameHandle2.IsAllocated) _frameHandle2.Free();
            _framePtr2 = IntPtr.Zero;
            _frameBytes2 = Array.Empty<byte>();
        }

        public void Open(string path)
        {
            // Dispose the previous media to avoid leaks
            _mp.Stop();
            _mp.Media?.Dispose();
            var media = new Media(_libVLC, new Uri(path));
            _mp.Media = media;
        }

        public void Play() => _mp.Play();
        public void Pause() => _mp.Pause();
        public void Stop()
        {
            _mp.Stop();
        }

        public void Dispose()
        {
            try
            {
                _mp?.Stop();
            }
            catch { /* ignore */ }

            _mp?.Media?.Dispose();
            _mp?.Dispose();
            _libVLC?.Dispose();

            FreeBuffers();
            _bitmapUpdateSemaphore?.Dispose();
        }
    }
}
