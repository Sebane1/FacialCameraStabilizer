using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Android.OS;
using Android.Util;
using System.Collections.Generic;
using System.Threading.Tasks;
using Application = Android.App.Application;

namespace FacialCameraBroadcaster.Platforms.Android
{
    public class UsbCameraDevice
    {
        public UsbDevice Device { get; init; }
        public UsbInterface VideoInterface { get; init; }
        public UsbEndpoint VideoEndpoint { get; init; }

        public string DeviceName => Device.DeviceName;

        public override string ToString() =>
            $"USB Camera: {DeviceName}, VID:0x{Device.VendorId:X4}, PID:0x{Device.ProductId:X4}";
    }

    public class UsbCameraEnumerator
    {
        private const string UsbPermissionAction = "com.FacialCameraBroadcaster.USB_PERMISSION";
        private readonly UsbManager usbManager;

        public UsbCameraEnumerator()
        {
            usbManager = (UsbManager)Application.Context.GetSystemService(Context.UsbService);
        }

        /// <summary>
        /// Enumerates all connected USB cameras with UVC VideoStreaming interfaces
        /// </summary>
        public async Task<List<UsbCameraDevice>> EnumerateCamerasAsync()
        {
            var cameras = new List<UsbCameraDevice>();

            foreach (var entry in usbManager.DeviceList)
            {
                var device = entry.Value;
                var uvcInterface = FindVideoStreamingInterface(device);
                if (uvcInterface != null)
                {
                    var endpoint = FindBulkInEndpoint(uvcInterface);
                    if (endpoint != null)
                    {
                        bool permission = await RequestPermissionAsync(device);
                        if (permission)
                        {
                            cameras.Add(new UsbCameraDevice
                            {
                                Device = device,
                                VideoInterface = uvcInterface,
                                VideoEndpoint = endpoint
                            });
                        }
                        else
                        {
                            Log.Warn("UsbCameraEnumerator", $"Permission denied for {device.DeviceName}");
                        }
                    }
                }
            }

            return cameras;
        }

        private UsbInterface? FindVideoStreamingInterface(UsbDevice device)
        {
            for (int i = 0; i < device.InterfaceCount; i++)
            {
                var intf = device.GetInterface(i);
                // VideoStreaming: Class 0x0E, Subclass 0x02
                //if ((int)intf.InterfaceClass == 0x0E && (int)intf.InterfaceSubclass == 0x02)
                //{
                    return intf;
                //}
            }
            return null;
        }

        private UsbEndpoint? FindBulkInEndpoint(UsbInterface intf)
        {
            for (int e = 0; e < intf.EndpointCount; e++)
            {
                var ep = intf.GetEndpoint(e);

                bool isIn = ep.Direction == UsbAddressing.DirMask;

                bool isBulk = ep.Type == UsbAddressing.XferInterrupt;

                if (isIn && isBulk)
                    return ep;
            }
            return null;
        }


        private Task<bool> RequestPermissionAsync(UsbDevice device)
        {
            var tcs = new TaskCompletionSource<bool>();

            // Use MAUI's current Activity instead of Application.Context
            var context = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;

            var receiver = new PermissionReceiver(tcs);
            var filter = new IntentFilter(UsbPermissionAction);
            context.RegisterReceiver(receiver, filter);

            var intent = PendingIntent.GetBroadcast(
                context,
                0,
                new Intent(UsbPermissionAction),
                PendingIntentFlags.Immutable
            );

            usbManager.RequestPermission(device, intent);

            return tcs.Task;
        }


        private class PermissionReceiver : BroadcastReceiver
        {
            private readonly TaskCompletionSource<bool> tcs;
            public PermissionReceiver()
            {
            }
            public PermissionReceiver(TaskCompletionSource<bool> tcs)
            {
                this.tcs = tcs;
            }

            public override void OnReceive(Context context, Intent intent)
            {
                if (intent.Action != UsbPermissionAction)
                    return;

                UsbDevice device = (UsbDevice)intent.GetParcelableExtra(UsbManager.ExtraDevice);
                bool granted = intent.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false);
                Log.Info("UsbCameraEnumerator", $"Permission result for {device?.DeviceName}: {granted}");

                // Complete the task
                tcs.TrySetResult(granted);

                // Unregister receiver immediately
                try
                {
                    context.UnregisterReceiver(this);
                }
                catch (Exception ex)
                {
                    Log.Warn("UsbCameraEnumerator", $"Failed to unregister receiver: {ex}");
                }
            }
        }
    }
}
