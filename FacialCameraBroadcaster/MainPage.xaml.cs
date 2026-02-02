using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
#if ANDROID
using FacialCameraBroadcaster.Platforms.Android;
using FacialCameraBroadcaster.Services;
#endif

namespace FacialCameraBroadcaster
{
    public partial class MainPage : ContentPage
    {
#if ANDROID
        private UsbCameraEnumerator? enumerator;
        private List<UsbCameraDevice> cameras = new();

        private UsbUvcStreamReader? leftEyeReader;
        private MjpegStreamServer? leftEyeServer;
        private UsbUvcStreamReader? rightEyeReader;
        private MjpegStreamServer? rightEyeServer;
        private UsbUvcStreamReader? mouthReader;
        private MjpegStreamServer? mouthServer;

        private const int PortLeftEye = 8080;
        private const int PortRightEye = 8081;
        private const int PortMouth = 8082;

        private IDispatcherTimer? _previewTimer;
        private int _lastLeftFrameLen, _lastRightFrameLen, _lastMouthFrameLen;
        private bool _leftShowA = true, _rightShowA = true, _mouthShowA = true;
        private bool _leftPendingSwap, _rightPendingSwap, _mouthPendingSwap;
#endif

        public MainPage()
        {
            InitializeComponent();
        }

#if ANDROID
        protected override void OnAppearing()
        {
            base.OnAppearing();
            enumerator = new UsbCameraEnumerator();
            UsbCameraBroadcastReceiver.UsbDeviceChanged += OnUsbDeviceChanged;
            StartPreviewTimer();
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                Loaded += async (_, _) =>
                {
                    await Task.Delay(500);
                    await LoadUsbCamerasAsync();
                };
            }
        }

        private bool _loadedOnce;

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            UsbCameraBroadcastReceiver.UsbDeviceChanged -= OnUsbDeviceChanged;
            StopPreviewTimer();
        }

        private void OnUsbDeviceChanged(Android.Hardware.Usb.UsbDevice _)
        {
            MainThread.BeginInvokeOnMainThread(async () => await LoadUsbCamerasAsync());
        }

        private void StartPreviewTimer()
        {
            _previewTimer ??= Application.Current!.Dispatcher.CreateTimer();
            _previewTimer.Interval = TimeSpan.FromMilliseconds(100);
            _previewTimer.Tick += (_, _) => UpdatePreviews();
            _previewTimer.Start();
        }

        private void StopPreviewTimer()
        {
            _previewTimer?.Stop();
        }

        private void UpdatePreviews()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Swap from previous tick (gives back buffer time to decode)
                if (_leftPendingSwap) { SwapPreviewVisibility(LeftEyePreviewA, LeftEyePreviewB, ref _leftShowA); _leftPendingSwap = false; }
                if (_rightPendingSwap) { SwapPreviewVisibility(RightEyePreviewA, RightEyePreviewB, ref _rightShowA); _rightPendingSwap = false; }
                if (_mouthPendingSwap) { SwapPreviewVisibility(MouthPreviewA, MouthPreviewB, ref _mouthShowA); _mouthPendingSwap = false; }
            });

            if (leftEyeReader != null)
            {
                var frame = leftEyeReader.LatestFrame;
                if (frame != null && frame.Length > 0 && frame.Length != _lastLeftFrameLen)
                {
                    _lastLeftFrameLen = frame.Length;
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        SetPreviewBackBuffer(LeftEyePreviewA, LeftEyePreviewB, _leftShowA, frame);
                        _leftPendingSwap = true;
                    });
                }
            }
            else
                _lastLeftFrameLen = 0;

            if (rightEyeReader != null)
            {
                var frame = rightEyeReader.LatestFrame;
                if (frame != null && frame.Length > 0 && frame.Length != _lastRightFrameLen)
                {
                    _lastRightFrameLen = frame.Length;
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        SetPreviewBackBuffer(RightEyePreviewA, RightEyePreviewB, _rightShowA, frame);
                        _rightPendingSwap = true;
                    });
                }
            }
            else
                _lastRightFrameLen = 0;

            if (mouthReader != null)
            {
                var frame = mouthReader.LatestFrame;
                if (frame != null && frame.Length > 0 && frame.Length != _lastMouthFrameLen)
                {
                    _lastMouthFrameLen = frame.Length;
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        SetPreviewBackBuffer(MouthPreviewA, MouthPreviewB, _mouthShowA, frame);
                        _mouthPendingSwap = true;
                    });
                }
            }
            else
                _lastMouthFrameLen = 0;
        }

        private static void SetPreviewBackBuffer(Image imageA, Image imageB, bool showA, byte[] jpegBytes)
        {
            try
            {
                var back = showA ? imageB : imageA;
                back.Source = ImageSource.FromStream(() => new MemoryStream(jpegBytes));
            }
            catch { }
        }

        private static void SwapPreviewVisibility(Image imageA, Image imageB, ref bool showA)
        {
            showA = !showA;
            imageA.IsVisible = showA;
            imageB.IsVisible = !showA;
        }
#endif

        private async void OnRefreshCamerasClicked(object sender, EventArgs e)
        {
            await LoadUsbCamerasAsync();
        }

        private async Task LoadUsbCamerasAsync()
        {
            cameras = await enumerator!.EnumerateCamerasAsync();
            LeftEyeCameraPicker.ItemsSource = cameras.ToList();
            RightEyeCameraPicker.ItemsSource = cameras.ToList();
            MouthCameraPicker.ItemsSource = cameras.ToList();
            LeftEyeCameraPicker.ItemDisplayBinding =
                RightEyeCameraPicker.ItemDisplayBinding =
                MouthCameraPicker.ItemDisplayBinding =
                    new Binding(nameof(UsbCameraDevice.DeviceName));

            int connected = enumerator!.GetConnectedDeviceCount();
            if (cameras.Count == 0)
                StreamInfoLabel.Text = connected == 0
                    ? "No USB devices detected. Connect a USB camera (e.g. OpenIris) and tap Refresh. Use an OTG cable if needed."
                    : $"USB devices connected: {connected}, but none matched as camera. Tap Refresh again and allow access when prompted. Check logcat tag 'UsbCameraEnumerator' for details.";
            else
                StreamInfoLabel.Text = "Select a camera and tap Start to broadcast MJPEG. View streams in a browser on the same network.";
        }

        private static string GetLocalIpAddress()
        {
            try
            {
                var ni = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                        && n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up);
                if (ni == null) return "127.0.0.1";
                var addr = ni.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork);
                return addr?.Address?.ToString() ?? "127.0.0.1";
            }
            catch { return "127.0.0.1"; }
        }

        private void SetStreamState(string slot, bool running, int port)
        {
            string ip = GetLocalIpAddress();
            string url = $"http://{ip}:{port}/";
            if (slot == "Left Eye")
            {
                LeftEyeStreamLabel.Text = running ? url : "";
                LeftEyeStartButton.IsEnabled = !running;
                LeftEyeStopButton.IsEnabled = running;
                LeftEyePreviewFrame.IsVisible = running;
            }
            else if (slot == "Right Eye")
            {
                RightEyeStreamLabel.Text = running ? url : "";
                RightEyeStartButton.IsEnabled = !running;
                RightEyeStopButton.IsEnabled = running;
                RightEyePreviewFrame.IsVisible = running;
            }
            else
            {
                MouthStreamLabel.Text = running ? url : "";
                MouthStartButton.IsEnabled = !running;
                MouthStopButton.IsEnabled = running;
                MouthPreviewFrame.IsVisible = running;
            }
        }

        private async void OnStartLeftEyeClicked(object sender, EventArgs e) => await StartCameraAsync("Left Eye", LeftEyeCameraPicker, PortLeftEye,
            r => { leftEyeReader = r; }, s => { leftEyeServer = s; }, SetStreamState);

        private async void OnStopLeftEyeClicked(object sender, EventArgs e) => await StopCameraAsync("Left Eye", PortLeftEye,
            () => leftEyeReader, () => leftEyeServer,
            () => { leftEyeReader = null; leftEyeServer = null; }, SetStreamState);

        private async void OnStartRightEyeClicked(object sender, EventArgs e) => await StartCameraAsync("Right Eye", RightEyeCameraPicker, PortRightEye,
            r => { rightEyeReader = r; }, s => { rightEyeServer = s; }, SetStreamState);

        private async void OnStopRightEyeClicked(object sender, EventArgs e) => await StopCameraAsync("Right Eye", PortRightEye,
            () => rightEyeReader, () => rightEyeServer,
            () => { rightEyeReader = null; rightEyeServer = null; }, SetStreamState);

        private async void OnStartMouthClicked(object sender, EventArgs e) => await StartCameraAsync("Mouth", MouthCameraPicker, PortMouth,
            r => { mouthReader = r; }, s => { mouthServer = s; }, SetStreamState);

        private async void OnStopMouthClicked(object sender, EventArgs e) => await StopCameraAsync("Mouth", PortMouth,
            () => mouthReader, () => mouthServer,
            () => { mouthReader = null; mouthServer = null; }, SetStreamState);

        private async Task StartCameraAsync(string slot, Picker cameraPicker, int port,
            Action<UsbUvcStreamReader> setReader, Action<MjpegStreamServer> setServer,
            Action<string, bool, int> setStreamState)
        {
            if (cameraPicker.SelectedItem is not UsbCameraDevice cam)
            {
                DisplayAlert("Error", $"Please select a USB camera for {slot}.", "OK");
                return;
            }
            var reader = new UsbUvcStreamReader(cam);
            bool ok = await reader.StartAsync();
            if (!ok)
            {
                DisplayAlert("Error", $"Could not open camera for {slot}. Check USB permission and that the device uses a bulk IN endpoint.", "OK");
                return;
            }
            var server = new MjpegStreamServer(port, () => reader.LatestFrame);
            server.Start();
            setReader(reader);
            setServer(server);
            setStreamState(slot, true, port);
        }

        private async Task StopCameraAsync(string slot, int port,
            Func<UsbUvcStreamReader?> getReader, Func<MjpegStreamServer?> getServer,
            Action clearRefs, Action<string, bool, int> setStreamState)
        {
            var reader = getReader();
            var server = getServer();
            if (reader != null) await reader.StopAsync();
            if (server != null) await server.StopAsync();
            clearRefs();
            setStreamState(slot, false, port);
        }
    }
}
