using Android.App;
using global::Android.Content;
using Android.Hardware.Usb;
using Android.OS;
using Android.Util;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Application = Android.App.Application;

namespace FacialCameraBroadcaster.Platforms.Android
{
    public class UsbCameraDevice
    {
        public UsbDevice Device { get; init; }
        public UsbInterface VideoInterface { get; init; }
        public UsbEndpoint VideoEndpoint { get; init; }

        /// <summary>Stored at creation so we can match after detach without touching the Java UsbDevice.</summary>
        public int VendorId { get; init; }
        /// <summary>Stored at creation so we can match after detach without touching the Java UsbDevice.</summary>
        public int ProductId { get; init; }

        public string DeviceName => Device.DeviceName;

        public override string ToString() =>
            $"USB Camera: {DeviceName}, VID:0x{VendorId:X4}, PID:0x{ProductId:X4}";
    }

    public class UsbCameraEnumerator
    {
        private const string UsbPermissionAction = "com.FacialCameraBroadcaster.USB_PERMISSION";
        private readonly UsbManager usbManager;
        private static readonly SemaphoreSlim _enumLock = new(1, 1);

        public UsbCameraEnumerator()
        {
            usbManager = (UsbManager)Application.Context.GetSystemService(Context.UsbService);
        }

        private const string Tag = "UsbCameraEnumerator";

        /// <summary>Number of USB devices currently in UsbManager.DeviceList (before any filtering).</summary>
        public int GetConnectedDeviceCount()
        {
            return usbManager.DeviceList?.Count ?? 0;
        }

        /// <summary>
        /// Enumerates all connected USB cameras with UVC VideoStreaming interfaces.
        /// Only one enumeration runs at a time to avoid permission dialog races and timeouts.
        /// </summary>
        public async Task<List<UsbCameraDevice>> EnumerateCamerasAsync()
        {
            await _enumLock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await EnumerateCamerasCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _enumLock.Release();
            }
        }

        private async Task<List<UsbCameraDevice>> EnumerateCamerasCoreAsync()
        {
            var cameras = new List<UsbCameraDevice>();
            int deviceCount = usbManager.DeviceList?.Count ?? 0;
            Log.Info(Tag, $"USB devices in list: {deviceCount}");

            if (deviceCount == 0)
                return cameras;

            foreach (var entry in usbManager.DeviceList!)
            {
                var device = entry.Value;
                Log.Info(Tag, $"Device: {device.DeviceName} VID=0x{device.VendorId:X4} PID=0x{device.ProductId:X4} interfaces={device.InterfaceCount}");

                var uvcInterface = FindVideoStreamingInterface(device);
                if (uvcInterface == null)
                {
                    Log.Warn(Tag, $"  No video interface found for {device.DeviceName}");
                    continue;
                }

                var endpoint = FindBulkInEndpoint(uvcInterface);
                if (endpoint == null)
                {
                    Log.Warn(Tag, $"  No IN endpoint found on interface for {device.DeviceName}");
                    continue;
                }

                bool permission = await RequestPermissionAsync(device);
                if (permission)
                {
                    cameras.Add(new UsbCameraDevice
                    {
                        Device = device,
                        VideoInterface = uvcInterface,
                        VideoEndpoint = endpoint,
                        VendorId = device.VendorId,
                        ProductId = device.ProductId
                    });
                    Log.Info(Tag, $"  Added camera: {device.DeviceName}");
                }
                else
                {
                    Log.Warn(Tag, $"  Permission denied or timeout for {device.DeviceName}");
                }
            }

            Log.Info(Tag, $"Enumerate done: {cameras.Count} camera(s)");
            return cameras;
        }

        private UsbInterface? FindVideoStreamingInterface(UsbDevice device)
        {
            for (int i = 0; i < device.InterfaceCount; i++)
            {
                var intf = device.GetInterface(i);
                // VideoStreaming: Class 0x0E, Subclass 0x02 (optional: some devices use different class)
                if ((int)intf.InterfaceClass == 0x0E && (int)intf.InterfaceSubclass == 0x02)
                    return intf;
            }
            // Fallback: use first interface with a video-capable IN endpoint, skipping CDC/serial (0x02)
            for (int i = 0; i < device.InterfaceCount; i++)
            {
                var intf = device.GetInterface(i);
                if ((int)intf.InterfaceClass == 0x02) continue; // skip CDC
                if (FindBulkInEndpoint(intf) != null)
                    return intf;
            }
            // Last resort: any interface with any IN endpoint (e.g. non-standard UVC)
            for (int i = 0; i < device.InterfaceCount; i++)
            {
                var intf = device.GetInterface(i);
                if (FindBulkInEndpoint(intf) != null)
                    return intf;
            }
            return null;
        }

        private UsbEndpoint? FindBulkInEndpoint(UsbInterface intf)
        {
            for (int e = 0; e < intf.EndpointCount; e++)
            {
                var ep = intf.GetEndpoint(e);
                // IN = device-to-host (bit 7 of address, or Direction == DirMask)
                bool isIn = ep.Direction == UsbAddressing.DirMask;
                if (!isIn) continue;

                // Prefer interrupt (OpenIris) or bulk; accept any IN endpoint as last resort
                const int UsbEndpointXferBulk = 2;
                bool knownVideo = ep.Type == UsbAddressing.XferInterrupt
                    || (int)ep.Type == UsbEndpointXferBulk;
                if (knownVideo)
                    return ep;
            }
            // Last resort: any IN endpoint (e.g. isochronous)
            for (int e = 0; e < intf.EndpointCount; e++)
            {
                var ep = intf.GetEndpoint(e);
                if (ep.Direction == UsbAddressing.DirMask)
                    return ep;
            }
            return null;
        }


        private async Task<bool> RequestPermissionAsync(UsbDevice device)
        {
            if (usbManager.HasPermission(device))
            {
                Log.Info(Tag, $"Already have permission for {device.DeviceName}");
                return true;
            }

            var context = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (context == null)
            {
                Log.Warn(Tag, "CurrentActivity is null; cannot request USB permission.");
                return false;
            }

            string devicePath = device.DeviceName;
            var tcs = new TaskCompletionSource<bool>();
            var receiver = new PermissionReceiver(devicePath, tcs);
            var filter = new IntentFilter(UsbPermissionAction);
            if ((int)global::Android.OS.Build.VERSION.SdkInt >= 33)
                context.RegisterReceiver(receiver, filter, global::Android.Content.ReceiverFlags.NotExported);
            else
                context.RegisterReceiver(receiver, filter);

            var pending = PendingIntent.GetBroadcast(
                context,
                0,
                new Intent(UsbPermissionAction),
                PendingIntentFlags.Immutable
            );

            usbManager.RequestPermission(device, pending);

            // Timeout after 8s so reconnect can retry sooner; second enumeration often gets HasPermission
            var timeout = Task.Delay(8000);
            var completed = await Task.WhenAny(tcs.Task, timeout);
            try { context.UnregisterReceiver(receiver); } catch { }

            if (completed == timeout)
            {
                Log.Warn(Tag, $"Permission timeout for {device.DeviceName}");
                return false;
            }
            return await tcs.Task;
        }


        private class PermissionReceiver : BroadcastReceiver
        {
            private readonly string _requestedDevicePath;
            private readonly TaskCompletionSource<bool> _tcs;

            public PermissionReceiver(string requestedDevicePath, TaskCompletionSource<bool> tcs)
            {
                _requestedDevicePath = requestedDevicePath;
                _tcs = tcs;
            }

            public override void OnReceive(Context context, Intent intent)
            {
                if (intent.Action != UsbPermissionAction)
                    return;

                UsbDevice? device = (UsbDevice?)intent.GetParcelableExtra(UsbManager.ExtraDevice);
                bool granted = intent.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false);
                string? path = device?.DeviceName;
                if (path != _requestedDevicePath)
                    return;
                Log.Info(Tag, $"Permission result for {path}: {granted}");
                _tcs.TrySetResult(granted);
            }
        }
    }
}
