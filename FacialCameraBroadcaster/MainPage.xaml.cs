using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
#if ANDROID
using Android.Util;
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

        private const string PrefLeftEye = "FacialCamera_LeftEye";
        private const string PrefRightEye = "FacialCamera_RightEye";
        private const string PrefMouth = "FacialCamera_Mouth";
        private const string PrefLeftEyeIndex = "FacialCamera_LeftEyeIndex";
        private const string PrefRightEyeIndex = "FacialCamera_RightEyeIndex";
        private const string PrefMouthIndex = "FacialCamera_MouthIndex";

        private IDispatcherTimer? _previewTimer;
        private IDispatcherTimer? _reconnectTimer;
        private int _lastLeftFrameLen, _lastRightFrameLen, _lastMouthFrameLen;
        private bool _leftShowA = true, _rightShowA = true, _mouthShowA = true;
        private bool _leftPendingSwap, _rightPendingSwap, _mouthPendingSwap;
        private bool _leftWantsReconnect, _rightWantsReconnect, _mouthWantsReconnect;
        private byte[]? _leftLastFrame, _rightLastFrame, _mouthLastFrame;
        private bool _ignoreLeftPickerChange, _ignoreRightPickerChange, _ignoreMouthPickerChange;
#endif

        public MainPage()
        {
            InitializeComponent();
        }

#if ANDROID
        protected override void OnAppearing()
        {
            base.OnAppearing();
            _isInForeground = true;
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
            else if (_leftWantsReconnect || _rightWantsReconnect || _mouthWantsReconnect)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500);
                    MainThread.BeginInvokeOnMainThread(async () =>
                    {
                        if (_isInForeground)
                        {
                            await LoadUsbCamerasAsync();
                            await TryReconnectDisconnectedSlotsAsync();
                        }
                    });
                });
            }
        }

        private bool _loadedOnce;
        /// <summary>True when MainPage is visible; used to avoid showing toasts in background.</summary>
        private bool _isInForeground;

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            _isInForeground = false;
            UsbCameraBroadcastReceiver.UsbDeviceChanged -= OnUsbDeviceChanged;
            StopPreviewTimer();
            StopReconnectTimer();
        }

        private void OnUsbDeviceChanged(int vendorId, int productId, bool isAttached)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Task task = !isAttached
                    ? OnUsbDeviceDetachedAsync(vendorId, productId)
                    : OnUsbDeviceAttachedAsync();
                task.ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"USB device changed error: {t.Exception}");
                        MainThread.BeginInvokeOnMainThread(() => ShowToast("USB event error — try refreshing camera list"));
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
            });
        }

        private async Task OnUsbDeviceDetachedAsync(int detachVid, int detachPid)
        {
            string? slotStopped = null;
            if (leftEyeReader != null && leftEyeReader.Camera.VendorId == detachVid && leftEyeReader.Camera.ProductId == detachPid)
            {
                try
                {
                    var last = leftEyeReader.LatestFrame;
                    if (last != null && last.Length > 0) _leftLastFrame = last;
                }
                catch { }
                leftEyeReader.Abandon();
                leftEyeReader = null;
                _leftWantsReconnect = true;
                try { SetStreamState("Left Eye", true, PortLeftEye, frozen: true); } catch { }
                slotStopped = "Left eye";
            }
            if (rightEyeReader != null && rightEyeReader.Camera.VendorId == detachVid && rightEyeReader.Camera.ProductId == detachPid)
            {
                try
                {
                    var last = rightEyeReader.LatestFrame;
                    if (last != null && last.Length > 0) _rightLastFrame = last;
                }
                catch { }
                rightEyeReader.Abandon();
                rightEyeReader = null;
                _rightWantsReconnect = true;
                try { SetStreamState("Right Eye", true, PortRightEye, frozen: true); } catch { }
                slotStopped = slotStopped == null ? "Right eye" : "Camera(s)";
            }
            if (mouthReader != null && mouthReader.Camera.VendorId == detachVid && mouthReader.Camera.ProductId == detachPid)
            {
                try
                {
                    var last = mouthReader.LatestFrame;
                    if (last != null && last.Length > 0) _mouthLastFrame = last;
                }
                catch { }
                mouthReader.Abandon();
                mouthReader = null;
                _mouthWantsReconnect = true;
                try { SetStreamState("Mouth", true, PortMouth, frozen: true); } catch { }
                slotStopped = slotStopped == null ? "Mouth" : "Camera(s)";
            }
            if (slotStopped != null && _isInForeground)
                try { ShowToast($"{slotStopped} camera disconnected — sending last frame until reconnect"); } catch { }
            try { StartReconnectTimerIfNeeded(); } catch { }
        }

        private void StartReconnectTimerIfNeeded()
        {
            if (!_leftWantsReconnect && !_rightWantsReconnect && !_mouthWantsReconnect) return;
            if (_reconnectTimer != null) return;
            _reconnectTimer = Application.Current!.Dispatcher.CreateTimer();
            _reconnectTimer.Interval = TimeSpan.FromSeconds(5);
            _reconnectTimer.Tick += async (_, _) =>
            {
                try
                {
                    if (!_leftWantsReconnect && !_rightWantsReconnect && !_mouthWantsReconnect) { StopReconnectTimer(); return; }
                    await LoadUsbCamerasAsync();
                    // After replug, first enumeration often hits permission timeout; retry once when devices exist but no cameras
                    if (cameras.Count == 0 && enumerator != null && enumerator.GetConnectedDeviceCount() > 0)
                    {
                        Log.Info(ReconnectTag, "cameras=0 but devices>0, retrying enumeration in 2s");
                        await Task.Delay(2000);
                        await LoadUsbCamerasAsync();
                    }
                    await TryReconnectDisconnectedSlotsAsync();
                    if (!_leftWantsReconnect && !_rightWantsReconnect && !_mouthWantsReconnect)
                        StopReconnectTimer();
                }
                catch { /* observe so task doesn't fault unobserved */ }
            };
            _reconnectTimer.Start();
        }

        private void StopReconnectTimer()
        {
            _reconnectTimer?.Stop();
            _reconnectTimer = null;
        }

        private async Task OnUsbDeviceAttachedAsync()
        {
            try
            {
                bool wantsReconnect = _leftWantsReconnect || _rightWantsReconnect || _mouthWantsReconnect;
                if (wantsReconnect)
                    await Task.Delay(5000).ConfigureAwait(true);
                await LoadUsbCamerasAsync();
                await TryReconnectDisconnectedSlotsAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"USB attach/reconnect error: {ex}");
                if (_isInForeground)
                    ShowToast("Reconnect error — try Refresh cameras");
            }
            if (!_leftWantsReconnect && !_rightWantsReconnect && !_mouthWantsReconnect)
                StopReconnectTimer();
        }

        private const string ReconnectTag = "FacialCameraReconnect";

        /// <summary>Get camera for slot by saved index (in path-sorted list); if index invalid or already used, fallback by ID then first available.</summary>
        private static UsbCameraDevice? GetCameraForSlot(List<UsbCameraDevice> sortedCameras, HashSet<string> usedPaths, string prefIndexKey, string prefIdKey)
        {
            int idx = Preferences.Get(prefIndexKey, -1);
            if (idx >= 0 && idx < sortedCameras.Count)
            {
                var cam = sortedCameras[idx];
                if (!usedPaths.Contains(cam.Device.DeviceName))
                    return cam;
            }
            return FindMatchingCamera(sortedCameras, Preferences.Get(prefIdKey, null), usedPaths) ?? FindFirstAvailableCamera(sortedCameras, usedPaths);
        }

        private async Task TryReconnectDisconnectedSlotsAsync()
        {
            var usedPaths = new HashSet<string>(StringComparer.Ordinal);
            var sortedCameras = cameras ?? new List<UsbCameraDevice>();
            if (sortedCameras.Count == 0)
                return;

            Log.Info(ReconnectTag, $"TryReconnect: leftWants={_leftWantsReconnect} rightWants={_rightWantsReconnect} mouthWants={_mouthWantsReconnect} cameras={sortedCameras.Count}");

            if (_leftWantsReconnect && leftEyeReader == null)
            {
                var leftCam = GetCameraForSlot(sortedCameras, usedPaths, PrefLeftEyeIndex, PrefLeftEye);
                if (leftCam != null)
                {
                    _ignoreLeftPickerChange = true;
                    try { LeftEyeCameraPicker.SelectedItem = leftCam; } finally { _ignoreLeftPickerChange = false; }
                    bool ok = leftEyeServer != null
                        ? await ReconnectStreamAsync("Left Eye", leftCam, PortLeftEye, r => { leftEyeReader = r; }, SetStreamState)
                        : await StartCameraAsync("Left Eye", LeftEyeCameraPicker, PortLeftEye, () => GetLeftEyeFrame(), r => { leftEyeReader = r; }, s => { leftEyeServer = s; }, SetStreamState, silentFail: true);
                    if (ok) { usedPaths.Add(leftCam.Device.DeviceName); _leftWantsReconnect = false; StartStreamingServiceIfNeeded(); if (_isInForeground) ShowToast("Left eye camera reconnected"); }
                }
            }

            if (_rightWantsReconnect && rightEyeReader == null)
            {
                var rightCam = GetCameraForSlot(sortedCameras, usedPaths, PrefRightEyeIndex, PrefRightEye);
                if (rightCam != null)
                {
                    _ignoreRightPickerChange = true;
                    try { RightEyeCameraPicker.SelectedItem = rightCam; } finally { _ignoreRightPickerChange = false; }
                    bool ok = rightEyeServer != null
                        ? await ReconnectStreamAsync("Right Eye", rightCam, PortRightEye, r => { rightEyeReader = r; }, SetStreamState)
                        : await StartCameraAsync("Right Eye", RightEyeCameraPicker, PortRightEye, () => GetRightEyeFrame(), r => { rightEyeReader = r; }, s => { rightEyeServer = s; }, SetStreamState, silentFail: true);
                    if (ok) { usedPaths.Add(rightCam.Device.DeviceName); _rightWantsReconnect = false; StartStreamingServiceIfNeeded(); if (_isInForeground) ShowToast("Right eye camera reconnected"); }
                }
            }

            if (_mouthWantsReconnect && mouthReader == null)
            {
                var mouthCam = GetCameraForSlot(sortedCameras, usedPaths, PrefMouthIndex, PrefMouth);
                if (mouthCam != null)
                {
                    _ignoreMouthPickerChange = true;
                    try { MouthCameraPicker.SelectedItem = mouthCam; } finally { _ignoreMouthPickerChange = false; }
                    bool ok = mouthServer != null
                        ? await ReconnectStreamAsync("Mouth", mouthCam, PortMouth, r => { mouthReader = r; }, SetStreamState)
                        : await StartCameraAsync("Mouth", MouthCameraPicker, PortMouth, () => GetMouthFrame(), r => { mouthReader = r; }, s => { mouthServer = s; }, SetStreamState, silentFail: true);
                    if (ok) { usedPaths.Add(mouthCam.Device.DeviceName); _mouthWantsReconnect = false; StartStreamingServiceIfNeeded(); if (_isInForeground) ShowToast("Mouth camera reconnected"); }
                }
            }
        }

        private byte[]? GetLeftEyeFrame() => leftEyeReader?.LatestFrame ?? _leftLastFrame;
        private byte[]? GetRightEyeFrame() => rightEyeReader?.LatestFrame ?? _rightLastFrame;
        private byte[]? GetMouthFrame() => mouthReader?.LatestFrame ?? _mouthLastFrame;

        private static void ShowToast(string message)
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as global::Android.App.Activity;
            if (activity != null)
                activity.RunOnUiThread(() => global::Android.Widget.Toast.MakeText(activity, message, global::Android.Widget.ToastLength.Short).Show());
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

        private static readonly SemaphoreSlim _loadCamerasLock = new(1, 1);

        private async void OnRefreshCamerasClicked(object sender, EventArgs e)
        {
            await LoadUsbCamerasAsync();
        }

        private async Task LoadUsbCamerasAsync()
        {
            await _loadCamerasLock.WaitAsync();
            try
            {
                await LoadUsbCamerasCoreAsync();
            }
            finally
            {
                _loadCamerasLock.Release();
            }
        }

        private async Task LoadUsbCamerasCoreAsync()
        {
            bool skipPermissionRequest = !_isInForeground;
            cameras = await enumerator!.EnumerateCamerasAsync(skipRequestingPermission: skipPermissionRequest);
            cameras = SortCamerasByPathNumber(cameras);
            LeftEyeCameraPicker.ItemsSource = cameras.ToList();
            RightEyeCameraPicker.ItemsSource = cameras.ToList();
            MouthCameraPicker.ItemsSource = cameras.ToList();
            LeftEyeCameraPicker.ItemDisplayBinding =
                RightEyeCameraPicker.ItemDisplayBinding =
                MouthCameraPicker.ItemDisplayBinding =
                    new Binding(nameof(UsbCameraDevice.DeviceName));

            RestoreSavedSelection();
            await Task.Delay(400);
            // When reconnecting, only TryReconnectDisconnectedSlotsAsync should start cameras; skip auto-start to avoid double-start and leaked readers holding the interface
            bool anyWantsReconnect = _leftWantsReconnect || _rightWantsReconnect || _mouthWantsReconnect;
            if (!anyWantsReconnect)
                await TryAutoStartSavedAsync();

            int connected = enumerator!.GetConnectedDeviceCount();
            if (cameras.Count == 0)
                StreamInfoLabel.Text = connected == 0
                    ? "No USB devices detected. Connect a USB camera (e.g. OpenIris) and tap Refresh. Use an OTG cable if needed."
                    : "USB devices detected but access not granted yet. Tap Refresh and allow when prompted, or wait for automatic retry.";
            else
                StreamInfoLabel.Text = "Select a camera to start streaming automatically. View streams in a browser on the same network.";
        }

        private static string GetDeviceId(UsbCameraDevice cam)
        {
            return $"{cam.Device.VendorId:X4}_{cam.Device.ProductId:X4}_{cam.Device.DeviceName}";
        }

        /// <summary>Parse trailing number from path (e.g. /dev/bus/usb/002/008 -> 8). Used to sort cameras consistently.</summary>
        private static int GetPathNumber(string? deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return 0;
            var parts = deviceName.Split('/');
            if (parts.Length > 0 && int.TryParse(parts[^1], out int n)) return n;
            return 0;
        }

        /// <summary>Sort by path number descending (e.g. /010, /009, /008) so selection by index is stable across reconnects.</summary>
        private static List<UsbCameraDevice> SortCamerasByPathNumber(List<UsbCameraDevice> list)
        {
            return list.OrderByDescending(c => GetPathNumber(c.Device?.DeviceName)).ToList();
        }

        private static UsbCameraDevice? FindMatchingCamera(List<UsbCameraDevice> list, string? savedId)
        {
            return FindMatchingCamera(list, savedId, null);
        }

        /// <param name="excludeDevicePaths">Device paths (e.g. DeviceName) already assigned this reconnect round; pass null to not exclude.</param>
        private static UsbCameraDevice? FindMatchingCamera(List<UsbCameraDevice> list, string? savedId, HashSet<string>? excludeDevicePaths)
        {
            if (string.IsNullOrEmpty(savedId) || list.Count == 0) return null;
            bool excluded(UsbCameraDevice c) => excludeDevicePaths != null && excludeDevicePaths.Contains(c.Device.DeviceName);
            var exact = list.FirstOrDefault(c => GetDeviceId(c) == savedId && !excluded(c));
            if (exact != null) return exact;
            var parts = savedId.Split('_');
            if (parts.Length >= 2 && int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out int vid) && int.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out int pid))
                return list.FirstOrDefault(c => c.VendorId == vid && c.ProductId == pid && !excluded(c));
            return null;
        }

        /// <summary>First camera not in excludeDevicePaths; used when no saved preference or path changed after replug.</summary>
        private static UsbCameraDevice? FindFirstAvailableCamera(List<UsbCameraDevice> list, HashSet<string>? excludeDevicePaths)
        {
            if (list == null || list.Count == 0) return null;
            bool excluded(UsbCameraDevice c) => excludeDevicePaths != null && excludeDevicePaths.Contains(c.Device.DeviceName);
            return list.FirstOrDefault(c => !excluded(c));
        }

        private void RestoreSavedSelection()
        {
            _ignoreLeftPickerChange = _ignoreRightPickerChange = _ignoreMouthPickerChange = true;
            try
            {
                int leftIdx = Preferences.Get(PrefLeftEyeIndex, -1);
                int rightIdx = Preferences.Get(PrefRightEyeIndex, -1);
                int mouthIdx = Preferences.Get(PrefMouthIndex, -1);
                if (leftIdx >= 0 && leftIdx < cameras.Count) LeftEyeCameraPicker.SelectedItem = cameras[leftIdx];
                else if (FindMatchingCamera(cameras, Preferences.Get(PrefLeftEye, null)) is { } left) LeftEyeCameraPicker.SelectedItem = left;
                if (rightIdx >= 0 && rightIdx < cameras.Count) RightEyeCameraPicker.SelectedItem = cameras[rightIdx];
                else if (FindMatchingCamera(cameras, Preferences.Get(PrefRightEye, null)) is { } right) RightEyeCameraPicker.SelectedItem = right;
                if (mouthIdx >= 0 && mouthIdx < cameras.Count) MouthCameraPicker.SelectedItem = cameras[mouthIdx];
                else if (FindMatchingCamera(cameras, Preferences.Get(PrefMouth, null)) is { } mouth) MouthCameraPicker.SelectedItem = mouth;
            }
            finally { _ignoreLeftPickerChange = _ignoreRightPickerChange = _ignoreMouthPickerChange = false; }
        }

        private async Task TryAutoStartSavedAsync()
        {
            string? leftId = Preferences.Get(PrefLeftEye, null);
            string? rightId = Preferences.Get(PrefRightEye, null);
            string? mouthId = Preferences.Get(PrefMouth, null);

            if (leftEyeReader == null && leftId != null && LeftEyeCameraPicker.SelectedItem is UsbCameraDevice leftCam && (GetDeviceId(leftCam) == leftId || DeviceIdVidPidMatch(GetDeviceId(leftCam), leftId)))
            {
                if (!await StartCameraAsync("Left Eye", LeftEyeCameraPicker, PortLeftEye, () => GetLeftEyeFrame(), r => { leftEyeReader = r; }, s => { leftEyeServer = s; }, SetStreamState, silentFail: true))
                    _leftWantsReconnect = true;
            }
            if (rightEyeReader == null && rightId != null && RightEyeCameraPicker.SelectedItem is UsbCameraDevice rightCam && (GetDeviceId(rightCam) == rightId || DeviceIdVidPidMatch(GetDeviceId(rightCam), rightId)))
            {
                if (!await StartCameraAsync("Right Eye", RightEyeCameraPicker, PortRightEye, () => GetRightEyeFrame(), r => { rightEyeReader = r; }, s => { rightEyeServer = s; }, SetStreamState, silentFail: true))
                    _rightWantsReconnect = true;
            }
            if (mouthReader == null && mouthId != null && MouthCameraPicker.SelectedItem is UsbCameraDevice mouthCam && (GetDeviceId(mouthCam) == mouthId || DeviceIdVidPidMatch(GetDeviceId(mouthCam), mouthId)))
            {
                if (!await StartCameraAsync("Mouth", MouthCameraPicker, PortMouth, () => GetMouthFrame(), r => { mouthReader = r; }, s => { mouthServer = s; }, SetStreamState, silentFail: true))
                    _mouthWantsReconnect = true;
            }
            StartReconnectTimerIfNeeded();
        }

        private static bool DeviceIdVidPidMatch(string currentId, string savedId)
        {
            var cur = currentId.Split('_');
            var sav = savedId.Split('_');
            return cur.Length >= 2 && sav.Length >= 2 && cur[0] == sav[0] && cur[1] == sav[1];
        }

        private async Task<bool> ReconnectStreamAsync(string slot, UsbCameraDevice cam, int port,
            Action<UsbUvcStreamReader> setReader, Action<string, bool, int, bool> setStreamState)
        {
            var reader = new UsbUvcStreamReader(cam);
            if (!await reader.StartAsync())
            {
                return false;
            }
            setReader(reader);
            setStreamState(slot, true, port, false);
            return true;
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

        private void SetStreamState(string slot, bool running, int port, bool frozen = false)
        {
            string ip = GetLocalIpAddress();
            string url = running ? $"http://{ip}:{port}/" : "";
            if (running && !frozen)
            {
                string mdnsName = MdnsServiceRegistration.GetServiceNameForSlot(slot);
                url += $"  (mDNS: {mdnsName}.local)";
            }
            if (frozen && running) url += " (frozen - reconnecting…)";
            if (slot == "Left Eye")
            {
                LeftEyeStreamLabel.Text = url;
                LeftEyeStartButton.IsEnabled = !running;
                LeftEyeStopButton.IsEnabled = running;
                LeftEyePreviewFrame.IsVisible = running;
            }
            else if (slot == "Right Eye")
            {
                RightEyeStreamLabel.Text = url;
                RightEyeStartButton.IsEnabled = !running;
                RightEyeStopButton.IsEnabled = running;
                RightEyePreviewFrame.IsVisible = running;
            }
            else
            {
                MouthStreamLabel.Text = url;
                MouthStartButton.IsEnabled = !running;
                MouthStopButton.IsEnabled = running;
                MouthPreviewFrame.IsVisible = running;
            }
        }

        private async void OnLeftEyePickerSelected(object sender, EventArgs e)
        {
            if (_ignoreLeftPickerChange) return;
            if (LeftEyeCameraPicker.SelectedItem is not UsbCameraDevice cam) return;
            if (leftEyeReader != null && GetDeviceId(leftEyeReader.Camera) == GetDeviceId(cam)) return;
            if (leftEyeReader != null)
                await StopCameraAsync("Left Eye", PortLeftEye, () => leftEyeReader, () => leftEyeServer,
                    () => { leftEyeReader = null; leftEyeServer = null; _leftLastFrame = null; }, SetStreamState);
            if (leftEyeReader == null)
            {
                if (!await StartCameraAsync("Left Eye", LeftEyeCameraPicker, PortLeftEye, () => GetLeftEyeFrame(), r => { leftEyeReader = r; }, s => { leftEyeServer = s; }, SetStreamState, silentFail: true))
                { _leftWantsReconnect = true; StartReconnectTimerIfNeeded(); }
            }
        }

        private async void OnStartLeftEyeClicked(object sender, EventArgs e) => await StartCameraAsync("Left Eye", LeftEyeCameraPicker, PortLeftEye, () => GetLeftEyeFrame(),
            r => { leftEyeReader = r; }, s => { leftEyeServer = s; }, SetStreamState);

        private async void OnStopLeftEyeClicked(object sender, EventArgs e) => await StopCameraAsync("Left Eye", PortLeftEye,
            () => leftEyeReader, () => leftEyeServer,
            () => { leftEyeReader = null; leftEyeServer = null; _leftLastFrame = null; }, SetStreamState);

        private async void OnRightEyePickerSelected(object sender, EventArgs e)
        {
            if (_ignoreRightPickerChange) return;
            if (RightEyeCameraPicker.SelectedItem is not UsbCameraDevice cam) return;
            if (rightEyeReader != null && GetDeviceId(rightEyeReader.Camera) == GetDeviceId(cam)) return;
            if (rightEyeReader != null)
                await StopCameraAsync("Right Eye", PortRightEye, () => rightEyeReader, () => rightEyeServer,
                    () => { rightEyeReader = null; rightEyeServer = null; _rightLastFrame = null; }, SetStreamState);
            if (rightEyeReader == null)
            {
                if (!await StartCameraAsync("Right Eye", RightEyeCameraPicker, PortRightEye, () => GetRightEyeFrame(), r => { rightEyeReader = r; }, s => { rightEyeServer = s; }, SetStreamState, silentFail: true))
                { _rightWantsReconnect = true; StartReconnectTimerIfNeeded(); }
            }
        }

        private async void OnStartRightEyeClicked(object sender, EventArgs e) => await StartCameraAsync("Right Eye", RightEyeCameraPicker, PortRightEye, () => GetRightEyeFrame(),
            r => { rightEyeReader = r; }, s => { rightEyeServer = s; }, SetStreamState);

        private async void OnStopRightEyeClicked(object sender, EventArgs e) => await StopCameraAsync("Right Eye", PortRightEye,
            () => rightEyeReader, () => rightEyeServer,
            () => { rightEyeReader = null; rightEyeServer = null; _rightLastFrame = null; }, SetStreamState);

        private async void OnMouthPickerSelected(object sender, EventArgs e)
        {
            if (_ignoreMouthPickerChange) return;
            if (MouthCameraPicker.SelectedItem is not UsbCameraDevice cam) return;
            if (mouthReader != null && GetDeviceId(mouthReader.Camera) == GetDeviceId(cam)) return;
            if (mouthReader != null)
                await StopCameraAsync("Mouth", PortMouth, () => mouthReader, () => mouthServer,
                    () => { mouthReader = null; mouthServer = null; _mouthLastFrame = null; }, SetStreamState);
            if (mouthReader == null)
            {
                if (!await StartCameraAsync("Mouth", MouthCameraPicker, PortMouth, () => GetMouthFrame(), r => { mouthReader = r; }, s => { mouthServer = s; }, SetStreamState, silentFail: true))
                { _mouthWantsReconnect = true; StartReconnectTimerIfNeeded(); }
            }
        }

        private async void OnStartMouthClicked(object sender, EventArgs e) => await StartCameraAsync("Mouth", MouthCameraPicker, PortMouth, () => GetMouthFrame(),
            r => { mouthReader = r; }, s => { mouthServer = s; }, SetStreamState);

        private async void OnStopMouthClicked(object sender, EventArgs e) => await StopCameraAsync("Mouth", PortMouth,
            () => mouthReader, () => mouthServer,
            () => { mouthReader = null; mouthServer = null; _mouthLastFrame = null; }, SetStreamState);

        private async Task<bool> StartCameraAsync(string slot, Picker cameraPicker, int port, Func<byte[]?> getFrame,
            Action<UsbUvcStreamReader> setReader, Action<MjpegStreamServer> setServer,
            Action<string, bool, int, bool> setStreamState, bool silentFail = false)
        {
            if (cameraPicker.SelectedItem is not UsbCameraDevice cam)
            {
                if (!silentFail) DisplayAlert("Error", $"Please select a USB camera for {slot}.", "OK");
                return false;
            }
            var reader = new UsbUvcStreamReader(cam);
            if (!await reader.StartAsync())
            {
                if (!silentFail)
                {
                    var msg = UsbUvcStreamReader.LastOpenError ?? "Check USB permission and that the device has a bulk/interrupt IN endpoint.";
                    DisplayAlert("Error", $"{slot}: {msg}", "OK");
                }
                return false;
            }
            var server = new MjpegStreamServer(port, getFrame);
            server.Start();
            setReader(reader);
            setServer(server);
            setStreamState(slot, true, port, false);
            MdnsServiceRegistration.Register(slot, port);

            int idx = cameras.IndexOf(cam);
            if (slot == "Left Eye") { Preferences.Set(PrefLeftEye, GetDeviceId(cam)); if (idx >= 0) Preferences.Set(PrefLeftEyeIndex, idx); }
            else if (slot == "Right Eye") { Preferences.Set(PrefRightEye, GetDeviceId(cam)); if (idx >= 0) Preferences.Set(PrefRightEyeIndex, idx); }
            else if (slot == "Mouth") { Preferences.Set(PrefMouth, GetDeviceId(cam)); if (idx >= 0) Preferences.Set(PrefMouthIndex, idx); }

            StartStreamingServiceIfNeeded();
            return true;
        }

        private async Task StopCameraAsync(string slot, int port,
            Func<UsbUvcStreamReader?> getReader, Func<MjpegStreamServer?> getServer,
            Action clearRefs, Action<string, bool, int, bool> setStreamState)
        {
            MdnsServiceRegistration.Unregister(slot);
            var reader = getReader();
            var server = getServer();
            if (reader != null) await reader.StopAsync();
            if (server != null) await server.StopAsync();
            clearRefs();
            setStreamState(slot, false, port, false);
            StopStreamingServiceIfIdle();
        }

        private async void StartStreamingServiceIfNeeded()
        {
            if (leftEyeReader == null && rightEyeReader == null && mouthReader == null)
                return;
            if (await Permissions.RequestAsync<Permissions.PostNotifications>() != PermissionStatus.Granted)
            { }
            var context = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as global::Android.Content.Context;
            if (context != null)
                StreamingForegroundService.Start(context);
        }

        private void StopStreamingServiceIfIdle()
        {
            if (leftEyeServer != null || rightEyeServer != null || mouthServer != null)
                return;
            var context = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as global::Android.Content.Context;
            if (context != null)
                StreamingForegroundService.Stop(context);
        }
    }
}
