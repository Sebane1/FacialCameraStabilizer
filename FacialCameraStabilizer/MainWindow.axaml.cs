using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace FacialCameraStabilizer;

public partial class MainWindow : Window
{
    public ObservableCollection<CameraConfig> Cameras { get; set; } = new();
    private List<CameraFeed> _activeFeeds = new();
    private CancellationTokenSource? _serverCts;
    private string _configPath;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        LoadConfig();

        // Subscribe to global static log event from CameraFeed
        CameraFeed.LogMessage += OnLogMessage;

        StartFeeds();
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                string json = File.ReadAllText(_configPath);
                var configList = JsonSerializer.Deserialize<List<CameraConfig>>(json);
                if (configList != null)
                {
                    Cameras.Clear();
                    foreach (var c in configList)
                    {
                        Cameras.Add(c);
                    }
                }
            }
            Log($"Loaded {Cameras.Count} cameras from config.");
        }
        catch (Exception ex)
        {
            Log($"Error loading config: {ex.Message}");
        }
    }

    private void AddButton_Click(object? sender, RoutedEventArgs e)
    {
        Cameras.Add(new CameraConfig { Name = "New Camera", Port = 8080 });
    }

    private void RemoveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (CameraGrid.SelectedItem is CameraConfig cam)
        {
            Cameras.Remove(cam);
        }
    }

    private void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string json = JsonSerializer.Serialize(Cameras, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
            Log("Configuration saved successfully. Rebooting feeds...");
            StopFeeds();
            StartFeeds();
        }
        catch (Exception ex)
        {
            Log($"Error saving config: {ex.Message}");
        }
    }

    private void StartFeeds()
    {
        if (_serverCts != null) return;
        
        Log("Starting proxy servers...");
        _serverCts = new CancellationTokenSource();
        _activeFeeds.Clear();

        foreach (var cam in Cameras)
        {
            var feed = new CameraFeed(cam);
            _activeFeeds.Add(feed);
            _ = feed.StartAsync(_serverCts.Token);
        }
    }

    private void StopFeeds()
    {
        if (_serverCts == null) return;

        Log("Stopping proxy servers...");
        _serverCts.Cancel();
        _serverCts.Dispose();
        _serverCts = null;
        _activeFeeds.Clear();
    }

    private void OnLogMessage(string message)
    {
        Log(message);
    }

    private void Log(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogTextBox.Text += $"[{timestamp}] {message}{Environment.NewLine}";
            LogTextBox.CaretIndex = LogTextBox.Text?.Length ?? 0;
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _serverCts?.Cancel();
        base.OnClosed(e);
    }
}
