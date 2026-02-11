public class CameraConfig
{
    public string Name { get; set; }                  // Logical name
    /// <summary>When set, feed pulls MJPEG from this URL (e.g. Quest/Broadcaster server). Ignored if IngestPort is set.</summary>
    public string Url { get; set; }                   // ESP32 or Broadcaster MJPEG URL
    /// <summary>When set, feed listens on this port for pushed MJPEG frames (e.g. from Broadcaster in client mode). Takes precedence over Url.</summary>
    public ushort? IngestPort { get; set; }
    public ushort Port { get; set; }                  // Proxy output port (localhost)
    public List<string> CameraPathAliases { get; set; } // HTTP path aliases
}