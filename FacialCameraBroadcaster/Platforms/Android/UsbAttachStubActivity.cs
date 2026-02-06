using Android.App;
using global::Android.Content;
using global::Android.Content.PM;
using Android.Hardware.Usb;
using Android.OS;

namespace FacialCameraBroadcaster
{
    /// <summary>
    /// Declares the app as a handler for USB device attach so Android can remember permission.
    /// Never shows UI and finishes immediately. TaskAffinity="" to reduce focus steal on Quest.
    /// </summary>
    [Activity(
        Theme = "@android:style/Theme.NoDisplay",
        MainLauncher = false,
        Exported = true,
        TaskAffinity = "",
        ExcludeFromRecents = true,
        NoHistory = true,
        LaunchMode = global::Android.Content.PM.LaunchMode.SingleTask)]
    [IntentFilter(new[] { UsbManager.ActionUsbDeviceAttached }, Categories = new[] { Intent.CategoryDefault })]
    [MetaData(UsbManager.ActionUsbDeviceAttached, Resource = "@xml/device_filter")]
    public class UsbAttachStubActivity : Activity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            Finish();
        }
    }
}
