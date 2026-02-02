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
        /// <param name="vendorId">Device VendorId (captured immediately so parcelable can be recycled).</param>
        /// <param name="productId">Device ProductId.</param>
        /// <param name="isAttached">True when device was attached, false when detached.</param>
        public static event Action<int, int, bool>? UsbDeviceChanged;

        public override void OnReceive(Context context, Intent intent)
        {
            var action = intent.Action;
            var device = (UsbDevice?)intent.GetParcelableExtra(UsbManager.ExtraDevice);

            if (device == null)
                return;

            int vid, pid;
            try
            {
                vid = device.VendorId;
                pid = device.ProductId;
            }
            catch
            {
                return;
            }

            bool isAttached = action == UsbManager.ActionUsbDeviceAttached;
            UsbDeviceChanged?.Invoke(vid, pid, isAttached);
        }
    }
}
