public class CameraConfig
{
    public string Name { get; set; }                  // Logical name
    public string Url { get; set; }                   // ESP32 MJPEG URL
    public ushort Port { get; set; }                  // Proxy output port
    public List<string> CameraPathAliases { get; set; } // HTTP path aliases
}