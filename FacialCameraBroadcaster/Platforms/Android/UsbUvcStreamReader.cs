using Android.Hardware.Usb;
using Android.Util;
using System.Threading;
using System.Threading.Tasks;
using Application = Android.App.Application;

namespace FacialCameraBroadcaster.Platforms.Android
{
    /// <summary>
    /// Reads UVC payloads from a USB camera bulk IN endpoint and extracts JPEG frames (SOI FF D8 ... EOI FF D9).
    /// </summary>
    public class UsbUvcStreamReader
    {
        private const int BufferSize = 256 * 1024; // 256 KB per read
        private const int MaxFrameSize = 1024 * 1024;   // 1 MB max single frame
        private static readonly byte[] JpegSoi = { 0xFF, 0xD8 };
        private static readonly byte[] JpegEoi = { 0xFF, 0xD9 };

        private UsbDeviceConnection? _connection;
        private readonly UsbCameraDevice _camera;
        private byte[] _readBuffer = new byte[BufferSize];
        private byte[]? _frameBuffer;
        private int _frameLength;
        private readonly object _frameLock = new();
        private CancellationTokenSource? _cts;
        private Task? _readTask;
        private bool _running;

        /// <summary>Latest complete JPEG frame (copy). Null if no frame yet.</summary>
        public byte[]? LatestFrame
        {
            get
            {
                lock (_frameLock)
                {
                    if (_frameLength <= 0 || _frameBuffer == null) return null;
                    var copy = new byte[_frameLength];
                    Array.Copy(_frameBuffer, 0, copy, 0, _frameLength);
                    return copy;
                }
            }
        }

        public bool IsRunning => _running;

        public UsbUvcStreamReader(UsbCameraDevice camera)
        {
            _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        }

        /// <summary>Opens the device, claims the video interface, and starts the read loop.</summary>
        public async Task<bool> StartAsync()
        {
            var manager = (UsbManager)Application.Context.GetSystemService(global::Android.Content.Context.UsbService)!;
            _connection = manager.OpenDevice(_camera.Device);
            if (_connection == null)
            {
                Log.Error("UsbUvcStreamReader", "OpenDevice failed");
                return false;
            }

            if (!_connection.ClaimInterface(_camera.VideoInterface, true))
            {
                Log.Error("UsbUvcStreamReader", "ClaimInterface failed");
                _connection.Close();
                _connection = null;
                return false;
            }

            _frameBuffer = new byte[MaxFrameSize];
            _frameLength = 0;
            _cts = new CancellationTokenSource();
            _running = true;
            _readTask = Task.Run(() => ReadLoop(_cts.Token));
            await Task.Yield();
            return true;
        }

        public async Task StopAsync()
        {
            _running = false;
            _cts?.Cancel();
            if (_readTask != null)
                await _readTask.ConfigureAwait(false);
            _connection?.ReleaseInterface(_camera.VideoInterface);
            _connection?.Close();
            _connection = null;
            _readTask = null;
        }

        private void ReadLoop(CancellationToken ct)
        {
            var endpoint = _camera.VideoEndpoint;
            var accumulator = new List<byte>(MaxFrameSize);
            int frameStart = -1; // index in accumulator of SOI

            try
            {
                while (_running && !ct.IsCancellationRequested && _connection != null)
                {
                    int len = _connection.BulkTransfer(endpoint, _readBuffer, _readBuffer.Length, 500);
                    if (len <= 0)
                        continue;

                    for (int i = 0; i < len; i++)
                        accumulator.Add(_readBuffer[i]);

                    // Keep buffer bounded: only from last SOI onward, or drop if too big
                    if (frameStart >= 0 && accumulator.Count - frameStart > MaxFrameSize)
                    {
                        accumulator.Clear();
                        frameStart = -1;
                        continue;
                    }
                    if (frameStart < 0 && accumulator.Count > MaxFrameSize)
                    {
                        accumulator.Clear();
                        continue;
                    }

                    // Look for SOI if we're not inside a frame
                    if (frameStart < 0)
                    {
                        for (int i = 0; i + 2 <= accumulator.Count; i++)
                        {
                            if (accumulator[i] == JpegSoi[0] && accumulator[i + 1] == JpegSoi[1])
                            {
                                frameStart = i;
                                break;
                            }
                        }
                        if (frameStart < 0)
                        {
                            // Keep only last byte (might be 0xFF) for next SOI
                            if (accumulator.Count > 1)
                                accumulator.RemoveRange(0, accumulator.Count - 1);
                            continue;
                        }
                    }

                    // Look for EOI after frameStart
                    for (int i = frameStart + 2; i + 2 <= accumulator.Count; i++)
                    {
                        if (accumulator[i] == JpegEoi[0] && accumulator[i + 1] == JpegEoi[1])
                        {
                            int frameLen = i + 2 - frameStart;
                            if (frameLen > 0 && frameLen <= MaxFrameSize)
                            {
                                byte[] frame = new byte[frameLen];
                                for (int j = 0; j < frameLen; j++)
                                    frame[j] = accumulator[frameStart + j];
                                lock (_frameLock)
                                {
                                    if (_frameBuffer != null && frameLen <= _frameBuffer.Length)
                                    {
                                        Array.Copy(frame, 0, _frameBuffer, 0, frameLen);
                                        _frameLength = frameLen;
                                    }
                                }
                            }
                            accumulator.RemoveRange(0, i + 2);
                            frameStart = -1;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("UsbUvcStreamReader", $"ReadLoop error: {ex.Message}");
            }
        }
    }
}
