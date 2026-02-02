using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic;
#if ANDROID
using FacialCameraBroadcaster.Platforms.Android;
using Android.Hardware.Usb;
#endif

namespace FacialCameraBroadcaster
{
    public partial class MainPage : ContentPage
    {
#if ANDROID
        private UsbCameraEnumerator? enumerator;
        private List<UsbCameraDevice> cameras = new();
#endif

        public MainPage()
        {
            InitializeComponent();

#if ANDROID

#endif
        }

#if ANDROID
        protected override async void OnAppearing()
        {
            base.OnAppearing();
            enumerator = new UsbCameraEnumerator();
            UsbCameraBroadcastReceiver.UsbDeviceChanged += device =>
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await LoadUsbCamerasAsync();
                });
            };

            Loaded += async (_, _) => await LoadUsbCamerasAsync();
        }

        private async System.Threading.Tasks.Task LoadUsbCamerasAsync()
        {
            cameras = await enumerator!.EnumerateCamerasAsync();

            LeftEyeCameraPicker.ItemsSource = cameras;
            RightEyeCameraPicker.ItemsSource = cameras;
            MouthCameraPicker.ItemsSource = cameras;

            LeftEyeCameraPicker.ItemDisplayBinding =
                RightEyeCameraPicker.ItemDisplayBinding =
                MouthCameraPicker.ItemDisplayBinding =
                    new Binding(nameof(UsbCameraDevice.DeviceName));

        }
#endif

// ===== CAMERA SELECTION HANDLERS =====
#if ANDROID
        private void OnCameraSelected(Picker cameraPicker, Picker interfacePicker, Picker endpointPicker)
        {
            interfacePicker.ItemsSource = null;
            endpointPicker.ItemsSource = null;

            if (cameraPicker.SelectedItem is not UsbCameraDevice cam)
                return;

            var interfaces = new List<UsbInterface>();
            for (int i = 0; i < cam.Device.InterfaceCount; i++)
                interfaces.Add(cam.Device.GetInterface(i));

            interfacePicker.ItemsSource = interfaces;
            interfacePicker.ItemDisplayBinding =
                new Binding(nameof(UsbInterface.Id));
        }

        private void OnInterfaceSelected(Picker interfacePicker, Picker endpointPicker)
        {
            endpointPicker.ItemsSource = null;

            if (interfacePicker.SelectedItem is not UsbInterface intf)
                return;

            var endpoints = new List<UsbEndpoint>();
            for (int i = 0; i < intf.EndpointCount; i++)
                endpoints.Add(intf.GetEndpoint(i));

            endpointPicker.ItemsSource = endpoints;
            endpointPicker.ItemDisplayBinding =
                new Binding(nameof(UsbEndpoint.Address));
        }

        // ===== LEFT EYE =====

        private void LeftEyeCameraPicker_SelectedIndexChanged(object sender, EventArgs e) =>
            OnCameraSelected(LeftEyeCameraPicker, LeftEyeInterfacePicker, LeftEyeEndpointPicker);

        private void LeftEyeInterfacePicker_SelectedIndexChanged(object sender, EventArgs e) =>
            OnInterfaceSelected(LeftEyeInterfacePicker, LeftEyeEndpointPicker);

        private void OnStartLeftEyeClicked(object sender, EventArgs e)
        {
            StartCamera("Left Eye", LeftEyeCameraPicker, LeftEyeInterfacePicker, LeftEyeEndpointPicker);
        }

        // ===== RIGHT EYE =====

        private void RightEyeCameraPicker_SelectedIndexChanged(object sender, EventArgs e) =>
            OnCameraSelected(RightEyeCameraPicker, RightEyeInterfacePicker, RightEyeEndpointPicker);

        private void RightEyeInterfacePicker_SelectedIndexChanged(object sender, EventArgs e) =>
            OnInterfaceSelected(RightEyeInterfacePicker, RightEyeEndpointPicker);

        private void OnStartRightEyeClicked(object sender, EventArgs e)
        {
            StartCamera("Right Eye", RightEyeCameraPicker, RightEyeInterfacePicker, RightEyeEndpointPicker);
        }

        // ===== MOUTH =====

        private void MouthCameraPicker_SelectedIndexChanged(object sender, EventArgs e) =>
            OnCameraSelected(MouthCameraPicker, MouthInterfacePicker, MouthEndpointPicker);

        private void MouthInterfacePicker_SelectedIndexChanged(object sender, EventArgs e) =>
            OnInterfaceSelected(MouthInterfacePicker, MouthEndpointPicker);

        private void OnStartMouthClicked(object sender, EventArgs e)
        {
            StartCamera("Mouth", MouthCameraPicker, MouthInterfacePicker, MouthEndpointPicker);
        }

        // ===== START CAMERA (stub) =====

        private void StartCamera(
            string slot,
            Picker cameraPicker,
            Picker interfacePicker,
            Picker endpointPicker)
        {
            if (cameraPicker.SelectedItem is not UsbCameraDevice cam ||
                interfacePicker.SelectedItem is not UsbInterface intf ||
                endpointPicker.SelectedItem is not UsbEndpoint ep)
            {
                DisplayAlert("Error", $"Please select camera, interface, and endpoint for {slot}.", "OK");
                return;
            }

            // TODO: Start USB read loop + MJPEG rebroadcast
            DisplayAlert(
                "Starting Camera",
                $"{slot}\n{cam}\nInterface {intf.Id}\nEndpoint 0x{ep.Address:X2}",
                "OK");
        }
#endif
    }
}
