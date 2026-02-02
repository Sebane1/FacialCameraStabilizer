using Android.App;
using global::Android.Content;
using Android.Hardware.Usb;
using Android.Util;

namespace FacialCameraBroadcaster.Platforms.Android
{
    [BroadcastReceiver(Enabled = true, Exported = true)]
    [IntentFilter(new[] { UsbManager.ActionUsbDeviceAttached, UsbManager.ActionUsbDeviceDetached })]
    public class UsbCameraBroadcastReceiver : BroadcastReceiver
    {
        public static event Action<UsbDevice>? UsbDeviceChanged;

        public override void OnReceive(Context context, Intent intent)
        {
            var action = intent.Action;
            var device = (UsbDevice)intent.GetParcelableExtra(UsbManager.ExtraDevice);

            if (device == null)
            {
                return;
            }

            UsbDeviceChanged?.Invoke(device);
        }
    }
}
