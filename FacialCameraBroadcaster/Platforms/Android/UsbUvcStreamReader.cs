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

        /// <summary>The camera device this reader is using (for disconnect matching by VID/PID).</summary>
        public UsbCameraDevice Camera => _camera;

        public UsbUvcStreamReader(UsbCameraDevice camera)
        {
            _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        }

        /// <summary>When StartAsync returns false, this describes why (for UI message).</summary>
        public static string? LastOpenError { get; private set; }

        private const int ClaimInterfaceRetries = 12;
        private const int ClaimInterfaceRetryDelayMs = 5000;

        /// <summary>Opens the device, claims the video interface, and starts the read loop.</summary>
        public async Task<bool> StartAsync()
        {
            LastOpenError = null;
            Log.Info("UsbUvcStreamReader", $"StartAsync for {_camera.Device.DeviceName} (VID 0x{_camera.VendorId:X4} PID 0x{_camera.ProductId:X4})");
            var manager = (UsbManager)Application.Context.GetSystemService(global::Android.Content.Context.UsbService)!;
            _connection = manager.OpenDevice(_camera.Device);
            if (_connection == null)
            {
                LastOpenError = "USB permission denied or device not openable.";
                Log.Error("UsbUvcStreamReader", $"OpenDevice failed for {_camera.Device.DeviceName} (VID 0x{_camera.VendorId:X4} PID 0x{_camera.ProductId:X4})");
                return false;
            }

            for (int attempt = 1; attempt <= ClaimInterfaceRetries; attempt++)
            {
                if (_connection.ClaimInterface(_camera.VideoInterface, true))
                {
                    Log.Info("UsbUvcStreamReader", $"ClaimInterface succeeded for {_camera.Device.DeviceName} (attempt {attempt})");
                    break;
                }

                if (attempt == ClaimInterfaceRetries)
                {
                    LastOpenError = "Interface in use. Unplug the camera, wait a few seconds, tap Refresh, then try Start again.";
                    Log.Error("UsbUvcStreamReader", $"ClaimInterface failed for {_camera.Device.DeviceName} after {attempt} attempt(s)");
                    _connection.Close();
                    _connection = null;
                    return false;
                }

                Log.Info("UsbUvcStreamReader", $"ClaimInterface attempt {attempt} failed for {_camera.Device.DeviceName}, retrying in {ClaimInterfaceRetryDelayMs}ms...");
                _connection.Close();
                _connection = null;
                await Task.Delay(ClaimInterfaceRetryDelayMs).ConfigureAwait(false);
                _connection = manager.OpenDevice(_camera.Device);
                if (_connection == null)
                {
                    LastOpenError = "USB permission denied or device not openable.";
                    return false;
                }
            }

            _frameBuffer = new byte[MaxFrameSize];
            _frameLength = 0;
            _cts = new CancellationTokenSource();
            _running = true;
            _readTask = Task.Run(() => ReadLoop(_cts.Token));
            await Task.Yield();
            return true;
        }

        /// <summary>Normal stop: wait for read loop (it releases interface in its finally), then clear refs.</summary>
        public async Task StopAsync()
        {
            try
            {
                _running = false;
                _cts?.Cancel();
                if (_readTask != null)
                {
                    try { await _readTask.ConfigureAwait(false); }
                    catch { }
                }
                // ReadLoop releases in its finally; only release here if we never started the loop
                if (_connection != null)
                {
                    try { _connection.ReleaseInterface(_camera.VideoInterface); } catch { }
                    try { _connection.Close(); } catch { }
                }
            }
            catch { }
            finally
            {
                _connection = null;
                _readTask = null;
            }
        }

        /// <summary>
        /// Abandon the reader without blocking on Java/USB. Use when device was unplugged.
        /// Tries to release the interface immediately (best chance before USB stack tears down),
        /// then again after a delay so the interface can be reclaimed after replug.
        /// </summary>
        public void Abandon()
        {
            _running = false;
            _cts?.Cancel();
            if (_readTask != null)
                _readTask.ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnFaulted);

            var conn = _connection;
            var cam = _camera;
            _connection = null;
            _readTask = null;

            if (conn != null && cam != null)
            {
                // Release immediately so we free the interface before USB stack invalidates the connection
                _ = Task.Run(() => TryReleaseConnection(conn, cam));
                // Retry release after delay in case first run didn't execute or kernel was slow
                _ = Task.Run(async () =>
                {
                    await Task.Delay(4000).ConfigureAwait(false);
                    TryReleaseConnection(conn, cam);
                });
            }
        }

        private static void TryReleaseConnection(UsbDeviceConnection conn, UsbCameraDevice cam)
        {
            try
            {
                conn.ReleaseInterface(cam.VideoInterface);
                conn.Close();
                Log.Info("UsbUvcStreamReader", $"Released interface and closed connection for {cam.Device.DeviceName}");
            }
            catch (Exception ex) { Log.Warn("UsbUvcStreamReader", $"Release/close failed: {ex.Message}"); }
        }

        private void ReadLoop(CancellationToken ct)
        {
            UsbEndpoint? endpoint = null;
            try { endpoint = _camera.VideoEndpoint; } catch { return; }
            if (endpoint == null) return;

            var conn = _connection;
            var cam = _camera;
            if (conn == null || cam == null) return;

            var accumulator = new List<byte>(MaxFrameSize);
            int frameStart = -1;

            try
            {
                while (_running && !ct.IsCancellationRequested && _connection != null)
                {
                    int len = 0;
                    try { len = _connection.BulkTransfer(endpoint, _readBuffer, _readBuffer.Length, 500); }
                    catch { break; /* device unplugged */ }
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
            finally
            {
                TryReleaseConnection(conn, cam);
            }
        }
    }
}
