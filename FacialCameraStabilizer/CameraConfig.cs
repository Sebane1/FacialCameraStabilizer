using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FacialCameraStabilizer;

public class CameraConfig : INotifyPropertyChanged
{
    private string _name = string.Empty;
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    private bool _isServer;
    public bool IsServer 
    { 
        get => _isServer; 
        set 
        {
            if (SetProperty(ref _isServer, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayUrl)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayIngestPort)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUrlEditable)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIngestPortEditable)));
            }
        } 
    }

    private string _url = string.Empty;
    public string Url 
    { 
        get => _url; 
        set 
        {
            if (SetProperty(ref _url, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayUrl)));
            }
        } 
    }

    private ushort? _ingestPort;
    public ushort? IngestPort 
    { 
        get => _ingestPort; 
        set 
        {
            if (SetProperty(ref _ingestPort, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayUrl)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayIngestPort)));
            }
        } 
    }

    private ushort _port;
    public ushort Port { get => _port; set => SetProperty(ref _port, value); }

    private List<string> _cameraPathAliases = new List<string>();
    public List<string> CameraPathAliases { get => _cameraPathAliases; set => SetProperty(ref _cameraPathAliases, value); }

    public bool IsUrlEditable => !IsServer;
    public bool IsIngestPortEditable => IsServer;

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayUrl
    {
        get => IsServer ? $"http://{GetLocalIp()}:{IngestPort ?? 9000}" : Url;
        set
        {
            if (!IsServer) Url = value;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayIngestPort
    {
        get => IsServer ? (IngestPort?.ToString() ?? "9000") : "N/A";
        set
        {
            if (IsServer && ushort.TryParse(value, out var port))
            {
                IngestPort = port;
            }
        }
    }

    private static string GetLocalIp()
    {
        var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
        foreach (var ip in host.AddressList)
        {
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var s = ip.ToString();
                if (!s.StartsWith("169.") && !s.StartsWith("192.168.0.")) return s;
            }
        }
        return "127.0.0.1";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}