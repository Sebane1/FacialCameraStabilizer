using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FacialCameraBroadcaster.Services
{
    /// <summary>
    /// Serves MJPEG over HTTP (multipart/x-mixed-replace) so clients can view the stream in a browser.
    /// </summary>
    public class MjpegStreamServer
    {
        private readonly Func<byte[]?> _getLatestFrame;
        private readonly int _port;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptTask;
        private bool _running;

        public int Port => _port;
        public bool IsRunning => _running;

        /// <param name="port">Port to listen on (e.g. 8080).</param>
        /// <param name="getLatestFrame">Called to get the current JPEG frame; null if no frame available.</param>
        public MjpegStreamServer(int port, Func<byte[]?> getLatestFrame)
        {
            _port = port;
            _getLatestFrame = getLatestFrame ?? throw new ArgumentNullException(nameof(getLatestFrame));
        }

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _running = true;
            _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
        }

        public async Task StopAsync()
        {
            _running = false;
            _cts?.Cancel();
            _listener?.Stop();
            if (_acceptTask != null)
                await _acceptTask.ConfigureAwait(false);
            _listener = null;
            _acceptTask = null;
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (_running && !ct.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    _ = Task.Run(() => ServeClient(client), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception) { /* ignore */ }
            }
        }

        private void ServeClient(TcpClient client)
        {
            try
            {
                using var stream = client.GetStream();
                var buffer = new byte[4096];
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) return;

                string request = Encoding.ASCII.GetString(buffer, 0, read);
                if (!request.StartsWith("GET ", StringComparison.OrdinalIgnoreCase))
                    return;

                // Respond with multipart MJPEG
                string boundary = "frame";
                string header =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: multipart/x-mixed-replace; boundary=" + boundary + "\r\n" +
                    "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
                    "Pragma: no-cache\r\n" +
                    "Connection: keep-alive\r\n\r\n";
                byte[] headerBytes = Encoding.ASCII.GetBytes(header);

                stream.Write(headerBytes, 0, headerBytes.Length);
                stream.Flush();

                byte[]? lastFrame = null;
                long lastResendMs = 0;
                const int resendIntervalMs = 1000; // Re-send last frame every 1s when frozen so stream stays alive
                while (_running && client.Connected)
                {
                    byte[]? frame = null;
                    try { frame = _getLatestFrame(); } catch { /* keep lastFrame, server never dies */ }
                    bool isNew = frame != null && frame.Length > 0 && (lastFrame == null || frame.Length != lastFrame.Length || !frame.AsSpan().SequenceEqual(lastFrame));
                    if (isNew)
                    {
                        lastFrame = frame;
                        lastResendMs = Environment.TickCount64;
                        WriteFrame(stream, boundary, frame!);
                    }
                    else if (lastFrame != null && lastFrame.Length > 0)
                    {
                        long now = Environment.TickCount64;
                        if (now - lastResendMs >= resendIntervalMs)
                        {
                            lastResendMs = now;
                            try { WriteFrame(stream, boundary, lastFrame); } catch { /* client may have disconnected */ }
                        }
                    }
                    Thread.Sleep(33); // ~30 fps max
                }
            }
            catch (Exception) { /* ignore */ }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private static void WriteFrame(NetworkStream stream, string boundary, byte[] frame)
        {
            string partHeader = "--" + boundary + "\r\nContent-Type: image/jpeg\r\nContent-Length: " + frame.Length + "\r\n\r\n";
            byte[] partHeaderBytes = Encoding.ASCII.GetBytes(partHeader);
            stream.Write(partHeaderBytes, 0, partHeaderBytes.Length);
            stream.Write(frame, 0, frame.Length);
            stream.Write(Encoding.ASCII.GetBytes("\r\n"), 0, 2);
            stream.Flush();
        }
    }
}
