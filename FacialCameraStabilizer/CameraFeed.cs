using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

public class CameraFeed
{
    CameraConfig Config;
    HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    ConcurrentDictionary<Guid, HttpListenerResponse> Clients = new();
    byte[]? LastFrame;
    long LastFrameTime = 0;

    public CameraFeed(CameraConfig config) => Config = config;

    public async Task StartAsync()
    {
        StartListener();
        _ = Task.Run(FrameRepeaterLoop);
        if (Config.IngestPort.HasValue)
            _ = Task.Run(IngestServerLoop);
        else
            _ = Task.Run(Esp32ReaderLoop);
    }

    void StartListener()
    {
        HttpListener listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{Config.Port}/");
        listener.Start();

        _ = Task.Run(async () =>
        {
            while (true)
            {
                var ctx = await listener.GetContextAsync();
                // Check which path the client requested
                string path = ctx.Request.Url?.AbsolutePath.Trim('/') ?? "";
                var aliases = Config.CameraPathAliases ?? new List<string>();
                if (aliases.Contains(path))
                {
                    _ = Task.Run(() => HandleClient(ctx));
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }
            }
        });
    }



    void HandleClient(HttpListenerContext ctx)
    {
        ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
        ctx.Response.SendChunked = true;
        ctx.Response.Headers.Add("Cache-Control", "no-cache");

        var id = Guid.NewGuid();
        Clients[id] = ctx.Response;

        // Send cached frame immediately if available
        if (LastFrame != null)
        {
            try { WriteMJPEGFrame(ctx.Response, LastFrame); }
            catch { }
        }

        try
        {
            while (ctx.Response.OutputStream.CanWrite)
            {
                Thread.Sleep(100);
            }
        }
        catch { }
        finally
        {
            Clients.TryRemove(id, out _);
            ctx.Response.OutputStream.Close();
        }
    }

    /// <summary>Listens for pushed MJPEG frames (e.g. from Broadcaster in client mode). Protocol: 4-byte big-endian length + raw JPEG bytes per frame.</summary>
    async Task IngestServerLoop()
    {
        ushort port = Config.IngestPort!.Value;
        var listener = new TcpListener(System.Net.IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"{Config.Name} listening for push on port {port}...");
        foreach (var item in Config.CameraPathAliases ?? new List<string>())
            Console.WriteLine($"{Config.Name} will be accessible at http://localhost:{Config.Port}/" + item);

        while (true)
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                byte[] lenBuf = new byte[4];
                while (true)
                {
                    using var cts = new CancellationTokenSource(3000);
                    int lenRead = 0;
                    while (lenRead < 4)
                    {
                        int n = await stream.ReadAsync(lenBuf.AsMemory(lenRead, 4 - lenRead), cts.Token);
                        if (n == 0) break;
                        lenRead += n;
                    }
                    if (lenRead < 4) break;
                    int frameLen = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                    if (frameLen <= 0 || frameLen > 10 * 1024 * 1024) break; // sanity: max 10 MB per frame
                    byte[] frame = new byte[frameLen];
                    int frameRead = 0;
                    while (frameRead < frameLen)
                    {
                        int n = await stream.ReadAsync(frame.AsMemory(frameRead, frameLen - frameRead), cts.Token);
                        if (n == 0) break;
                        frameRead += n;
                    }
                    if (frameRead < frameLen) break;
                    LastFrame = frame;
                    LastFrameTime = Environment.TickCount64;
                    Broadcast(LastFrame, LastFrame.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{Config.Name} ingest error: {ex.Message}");
            }
            await Task.Delay(100);
        }
    }

    async Task Esp32ReaderLoop()
    {
        const int reconnectDelayMs = 1000;
        const int stallTimeoutMs = 3000;
        const int watchdogIntervalMs = 500;

        while (true)
        {
            using var cts = new CancellationTokenSource();
            var readCts = cts;
            long lastFrameSeen = Environment.TickCount64;

            // Start watchdog
            var watchdog = Task.Run(async () =>
            {
                while (!readCts.Token.IsCancellationRequested)
                {
                    await Task.Delay(watchdogIntervalMs);
                    if (LastFrameTime == 0)
                    {
                        continue;
                    }

                    long age = Environment.TickCount64 - LastFrameTime;
                    if (age > stallTimeoutMs)
                    {
                        Console.WriteLine($"{Config.Name} frames stalled, canceling read to reconnect...");
                        readCts.Cancel();
                    }
                }
            });

            try
            {
                Console.WriteLine($"{Config.Name} connecting to {Config.Url}...");
                foreach (var item in Config.CameraPathAliases ?? new List<string>())
                {
                    Console.WriteLine($"{Config.Name} will be accessible at http://localhost:{Config.Port}/" + item);
                }
                using var stream = await Http.GetStreamAsync(Config.Url, readCts.Token);

                byte[] buffer = new byte[4096];
                List<byte> frameBuffer = new List<byte>();
                bool inFrame = false;

                while (!readCts.Token.IsCancellationRequested)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, readCts.Token);
                    if (read == 0) break;

                    for (int i = 0; i < read; i++)
                    {
                        byte b = buffer[i];
                        if (!inFrame && b == 0xFF && i + 1 < read && buffer[i + 1] == 0xD8)
                        {
                            frameBuffer.Clear();
                            inFrame = true;
                        }
                        if (inFrame) frameBuffer.Add(b);
                        if (inFrame && b == 0xD9 && frameBuffer.Count > 2 && frameBuffer[^2] == 0xFF)
                        {
                            LastFrame = frameBuffer.ToArray();
                            LastFrameTime = Environment.TickCount64;
                            inFrame = false;

                            Broadcast(LastFrame, LastFrame.Length);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Feed has been killed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{Config.Name} error: {ex.Message}");
            }

            await Task.Delay(reconnectDelayMs);
        }
    }


    async Task FrameRepeaterLoop()
    {
        const int StallTimeoutMs = 750;
        const int KeepAliveMs = 250;

        while (true)
        {
            await Task.Delay(KeepAliveMs);
            if (LastFrame == null)
            {
                continue;
            }

            long age = Environment.TickCount64 - LastFrameTime;
            if (age > StallTimeoutMs) Broadcast(LastFrame, LastFrame.Length);
        }
    }

    void Broadcast(byte[] data, int length)
    {
        foreach (var client in Clients)
        {
            try
            {
                WriteMJPEGFrame(client.Value, data);
            }
            catch
            {
                Clients.TryRemove(client.Key, out _);
            }
        }
    }

    void WriteMJPEGFrame(HttpListenerResponse response, byte[] frame)
    {
        try
        {
            var output = response.OutputStream;
            string header = $"\r\n--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n";
            byte[] headerBytes = System.Text.Encoding.ASCII.GetBytes(header);

            output.Write(headerBytes, 0, headerBytes.Length);
            output.Write(frame, 0, frame.Length);
            output.Flush();
        }
        catch
        {
            Console.WriteLine("Failed to write frame.");
        }
    }
}