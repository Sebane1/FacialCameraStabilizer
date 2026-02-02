using Android.App;
using global::Android.Content;
using global::Android.Content.PM;
using Android.Hardware.Usb;
using Android.OS;

namespace FacialCameraBroadcaster
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    [IntentFilter(new[] { UsbManager.ActionUsbDeviceAttached }, Categories = new[] { Intent.CategoryDefault })]
    public class MainActivity : MauiAppCompatActivity
    {
    }
}
