using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace FacialCameraBroadcaster.Platforms.Android
{
    /// <summary>
    /// Keeps the app process in foreground so MJPEG streaming and USB reads continue when the app is backgrounded.
    /// </summary>
    [Service(Name = "com.SebaneStudios.FacialCameraBroadcaster.StreamingForegroundService", Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
    public class StreamingForegroundService : Service
    {
        private const int NotificationId = 9001;
        private const string ChannelId = "facial_camera_streaming";

        public override IBinder? OnBind(Intent? intent) => null;

        public override void OnCreate()
        {
            base.OnCreate();
            CreateNotificationChannel();
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            var builder = new Notification.Builder(this);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                builder.SetChannelId(ChannelId);
            var notification = builder
                .SetContentTitle("Facial Camera Broadcaster")
                .SetContentText("Streaming MJPEG feeds…")
                .SetSmallIcon(global::Android.Resource.Drawable.IcMenuCamera)
                .SetOngoing(true)
                .Build();

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
            else
                StartForeground(NotificationId, notification);

            return StartCommandResult.Sticky;
        }

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O)
                return;

            var channel = new NotificationChannel(ChannelId, "Camera streaming", NotificationImportance.Low)
            {
                Description = "Shows when MJPEG streams are active"
            };

            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.CreateNotificationChannel(channel);
        }

        /// <summary>Start the foreground service so streaming continues when app is in background.</summary>
        public static void Start(global::Android.Content.Context context)
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var intent = new Intent(context, typeof(StreamingForegroundService));
                context.StartForegroundService(intent);
            }
            else
            {
                var intent = new Intent(context, typeof(StreamingForegroundService));
                context.StartService(intent);
            }
        }

        /// <summary>Stop the foreground service when all streams are stopped.</summary>
        public static void Stop(global::Android.Content.Context context)
        {
            var intent = new Intent(context, typeof(StreamingForegroundService));
            context.StopService(intent);
        }
    }
}
