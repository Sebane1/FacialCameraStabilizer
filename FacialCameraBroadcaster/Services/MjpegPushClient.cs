using System.Net.Sockets;

namespace FacialCameraBroadcaster.Services
{
    /// <summary>
    /// Connects to a remote endpoint (e.g. FacialCameraStabilizer ingest port) and pushes MJPEG frames.
    /// Protocol: 4-byte big-endian length + raw JPEG bytes per frame.
    /// </summary>
    public class MjpegPushClient
    {
        private readonly string _host;
        private readonly int _port;
        private readonly Func<byte[]?> _getLatestFrame;
        private readonly Func<int> _getSendIntervalMs;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cts;
        private Task? _runTask;
        private bool _running;
        private readonly object _lock = new();

        public string Host => _host;
        public int Port => _port;
        public bool IsRunning => _running;
        public bool IsConnected => _client?.Connected ?? false;

        /// <param name="getSendIntervalMs">Returns delay in ms between sends (e.g. 16 for 60fps, 33 for 30fps). If null, defaults to 16.</param>
        public MjpegPushClient(string host, int port, Func<byte[]?> getLatestFrame, Func<int>? getSendIntervalMs = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _port = port;
            _getLatestFrame = getLatestFrame ?? throw new ArgumentNullException(nameof(getLatestFrame));
            _getSendIntervalMs = getSendIntervalMs ?? (() => 16);
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_running) return;
                _running = true;
                _cts = new CancellationTokenSource();
                _runTask = Task.Run(() => RunLoop(_cts.Token));
            }
        }

        public async Task StopAsync()
        {
            lock (_lock)
            {
                _running = false;
                _cts?.Cancel();
            }
            try
            {
                _client?.Close();
            }
            catch { }
            if (_runTask != null)
                await _runTask.ConfigureAwait(false);
            _runTask = null;
            _stream = null;
            _client = null;
        }

        private async Task RunLoop(CancellationToken ct)
        {
            const int reconnectDelayMs = 2000;
            byte[]? lastFrame = null;
            long lastResendMs = 0;
            const int resendIntervalMs = 1000;

            while (_running && !ct.IsCancellationRequested)
            {
                try
                {
                    _client = new TcpClient();
                    await _client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
                    _stream = _client.GetStream();

                    while (_running && _client.Connected && !ct.IsCancellationRequested)
                    {
                        byte[]? frame = null;
                        try { frame = _getLatestFrame(); } catch { }

                        bool isNew = frame != null && frame.Length > 0 &&
                            (lastFrame == null || frame.Length != lastFrame.Length || !frame.AsSpan().SequenceEqual(lastFrame));

                        if (isNew && frame != null && frame.Length > 0)
                        {
                            lastFrame = frame;
                            lastResendMs = Environment.TickCount64;
                            WriteFrame(_stream, frame);
                        }
                        else if (lastFrame != null && lastFrame.Length > 0)
                        {
                            long now = Environment.TickCount64;
                            if (now - lastResendMs >= resendIntervalMs)
                            {
                                lastResendMs = now;
                                WriteFrame(_stream, lastFrame);
                            }
                        }

                        int intervalMs = Math.Clamp(_getSendIntervalMs(), 16, 500);
                        await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { /* will reconnect */ }
                finally
                {
                    try { _stream?.Close(); _client?.Close(); } catch { }
                    _stream = null;
                    _client = null;
                }

                if (_running && !ct.IsCancellationRequested)
                    await Task.Delay(reconnectDelayMs, ct).ConfigureAwait(false);
            }
        }

        private static void WriteFrame(NetworkStream stream, byte[] frame)
        {
            byte[] lenBuf = new byte[4];
            int len = frame.Length;
            lenBuf[0] = (byte)(len >> 24);
            lenBuf[1] = (byte)(len >> 16);
            lenBuf[2] = (byte)(len >> 8);
            lenBuf[3] = (byte)len;
            stream.Write(lenBuf, 0, 4);
            stream.Write(frame, 0, frame.Length);
            stream.Flush();
        }
    }
}
