using AOApps.Dns;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
class Program
{
    static List<CameraConfig> DefaultCameras = new List<CameraConfig>();
    static async Task Main()
    {
        List<CameraConfig> cameras;

        try
        {
            string json = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"));
            cameras = JsonSerializer.Deserialize<List<CameraConfig>>(json)
                      ?? throw new Exception("Failed to deserialize camera config.");
        }
        catch (Exception ex)
        {
            cameras = DefaultCameras;
        }

        foreach (var cam in cameras)
        {
            var feed = new CameraFeed(cam);
            _ = feed.StartAsync();
        }

        Console.WriteLine("Multi-camera proxy running...");
        await Task.Delay(-1);
    }

}
