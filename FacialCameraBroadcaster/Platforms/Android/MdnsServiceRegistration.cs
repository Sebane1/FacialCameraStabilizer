using Android.Net.Nsd;
using Android.Util;
using Application = Android.App.Application;

namespace FacialCameraBroadcaster.Platforms.Android
{
    public static class MdnsServiceRegistration
    {
        private const string LogTag = "MdnsService";
        private const string ServiceType = "_http._tcp.";

        private static NsdManager? _nsdManager;
        private static RegistrationListener? _leftListener;
        private static RegistrationListener? _rightListener;
        private static RegistrationListener? _mouthListener;

        private static NsdManager GetManager()
        {
            if (_nsdManager == null)
                _nsdManager = (NsdManager)Application.Context
                    .GetSystemService(global::Android.Content.Context.NsdService)!;

            return _nsdManager;
        }

        public static string GetServiceNameForSlot(string slot) => slot switch
        {
            "Left Eye" => "lefteye",
            "Right Eye" => "righteye",
            "Mouth" => "mouth",
            _ => slot.ToLowerInvariant().Replace(" ", "")
        };

        public static void Register(string slot, int port)
        {
            try
            {
                var name = GetServiceNameForSlot(slot);

                var info = new NsdServiceInfo
                {
                    ServiceName = name,
                    ServiceType = ServiceType,
                    Port = port
                };

                var listener = new RegistrationListener(slot, name, port);

                GetManager().RegisterService(info, NsdProtocol.DnsSd, listener);
                StoreListener(slot, listener);
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, $"Register {slot}: {ex.Message}");
            }
        }

        public static void Unregister(string slot)
        {
            try
            {
                var listener = GetListener(slot);

                if (listener != null)
                {
                    GetManager().UnregisterService(listener);
                    StoreListener(slot, null);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, $"Unregister {slot}: {ex.Message}");
            }
        }

        private static void StoreListener(string slot, RegistrationListener? listener)
        {
            if (slot == "Left Eye") _leftListener = listener;
            else if (slot == "Right Eye") _rightListener = listener;
            else if (slot == "Mouth") _mouthListener = listener;
        }

        private static RegistrationListener? GetListener(string slot) =>
            slot == "Left Eye" ? _leftListener :
            slot == "Right Eye" ? _rightListener :
            slot == "Mouth" ? _mouthListener : null;

        private class RegistrationListener : Java.Lang.Object, NsdManager.IRegistrationListener
        {
            private readonly string _name;
            private readonly int _port;

            public RegistrationListener(string slot, string name, int port)
            {
                _name = name;
                _port = port;
            }

            public void OnRegistrationFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode)
            {
                Log.Warn(LogTag, $"{_name} registration failed: {errorCode}");
            }

            public void OnServiceRegistered(NsdServiceInfo? serviceInfo)
            {
                Log.Info(LogTag, $"{_name}._http._tcp registered on port {_port}");
            }

            public void OnServiceUnregistered(NsdServiceInfo? serviceInfo)
            {
                Log.Info(LogTag, $"{_name} unregistered");
            }

            public void OnUnregistrationFailed(NsdServiceInfo? serviceInfo, NsdFailure errorCode)
            {
                Log.Warn(LogTag, $"{_name} unregistration failed: {errorCode}");
            }
        }
    }
}
