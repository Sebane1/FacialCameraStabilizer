using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Media;
using Android.OS;
using Android.Util;
using System;
using System.IO;
using System.Threading.Tasks;
using Application = Android.App.Application;

namespace FacialCameraBroadcaster.Platforms.Android
{
    public class AndroidDeviceCameraReader : Java.Lang.Object, ImageReader.IOnImageAvailableListener, ICameraReader
    {
        private readonly AndroidCameraDevice _cameraDevice;
        private CameraDevice? _camera;
        private CameraCaptureSession? _captureSession;
        private ImageReader? _imageReader;
        private Handler? _backgroundHandler;
        private HandlerThread? _backgroundThread;
        
        private byte[]? _latestFrame;
        private readonly object _frameLock = new();

        public AndroidCameraDevice Camera => _cameraDevice;
        ICameraDevice ICameraReader.Camera => _cameraDevice;

        public bool IsRunning => _captureSession != null;
        public byte[]? LatestFrame
        {
            get
            {
                lock (_frameLock)
                {
                    if (_latestFrame == null) return null;
                    byte[] copy = new byte[_latestFrame.Length];
                    Array.Copy(_latestFrame, copy, _latestFrame.Length);
                    return copy;
                }
            }
        }

        public AndroidDeviceCameraReader(AndroidCameraDevice camera)
        {
            _cameraDevice = camera;
        }

        public static string? LastOpenError { get; private set; }

        public Task<bool> StartAsync()
        {
            var tcs = new TaskCompletionSource<bool>();
            try
            {
                _backgroundThread = new HandlerThread("CameraBackground");
                _backgroundThread.Start();
                _backgroundHandler = new Handler(_backgroundThread.Looper!);

                var manager = (CameraManager)Application.Context.GetSystemService(Context.CameraService)!;
                manager.OpenCamera(_cameraDevice.CameraId, new MyStateCallback(this, tcs), _backgroundHandler);
            }
            catch (Exception ex)
            {
                LastOpenError = ex.Message;
                tcs.TrySetResult(false);
            }
            return tcs.Task;
        }

        private class MyStateCallback : CameraDevice.StateCallback
        {
            private readonly AndroidDeviceCameraReader _parent;
            private readonly TaskCompletionSource<bool> _tcs;
            public MyStateCallback(AndroidDeviceCameraReader parent, TaskCompletionSource<bool> tcs) { _parent = parent; _tcs = tcs; }
            public override void OnOpened(CameraDevice camera)
            {
                _parent._camera = camera;
                _parent.StartCaptureSession();
                _tcs.TrySetResult(true);
            }
            public override void OnDisconnected(CameraDevice camera) { camera.Close(); _parent._camera = null; }
            public override void OnError(CameraDevice camera, CameraError error) { camera.Close(); _parent._camera = null; AndroidDeviceCameraReader.LastOpenError = error.ToString(); _tcs.TrySetResult(false); }
        }

        private void StartCaptureSession()
        {
            try
            {
                _imageReader = ImageReader.NewInstance(640, 480, ImageFormatType.Yuv420888, 2);
                _imageReader!.SetOnImageAvailableListener(this, _backgroundHandler);

                var outputSurfaces = new System.Collections.Generic.List<global::Android.Views.Surface> { _imageReader.Surface! };
                _camera!.CreateCaptureSession(outputSurfaces, new SessionCallback(this), _backgroundHandler);
            }
            catch (Exception ex)
            {
                Log.Error("CameraReader", "Session failed: " + ex.Message);
            }
        }

        private class SessionCallback : CameraCaptureSession.StateCallback
        {
            private readonly AndroidDeviceCameraReader _parent;
            public SessionCallback(AndroidDeviceCameraReader parent) { _parent = parent; }
            public override void OnConfigured(CameraCaptureSession session)
            {
                if (_parent._camera == null) return;
                _parent._captureSession = session;
                try
                {
                    var builder = _parent._camera.CreateCaptureRequest(CameraTemplate.Preview)!;
                    builder.AddTarget(_parent._imageReader!.Surface!);
                    session.SetRepeatingRequest(builder.Build(), null, _parent._backgroundHandler);
                }
                catch (Exception e) { Log.Error("CameraReader", e.Message); }
            }
            public override void OnConfigureFailed(CameraCaptureSession session) { }
        }

        public void OnImageAvailable(ImageReader? reader)
        {
            try
            {
                using var image = reader?.AcquireLatestImage();
                if (image == null) return;
                
                var yPlane = image.GetPlanes()![0];
                var buffer = yPlane.Buffer!;
                int ySize = buffer.Remaining();
                byte[] yBytes = new byte[ySize];
                buffer.Get(yBytes);

                int width = image.Width;
                int height = image.Height;
                
                byte[] nv21Bytes = new byte[width * height + (width / 2) * (height / 2) * 2];
                // YUY420888 plane might have row stride > width. Let's handle row stride if necessary.
                // Assuming tight packing for 640x480.
                if (yPlane.RowStride == width) {
                    Array.Copy(yBytes, nv21Bytes, yBytes.Length);
                } else {
                    for (int row = 0; row < height; row++) {
                        Array.Copy(yBytes, row * yPlane.RowStride, nv21Bytes, row * width, width);
                    }
                }
                
                // UV to 128 (grayscale)
                for (int i = width * height; i < nv21Bytes.Length; i++) {
                    nv21Bytes[i] = 128;
                }

                using var yuvImage = new YuvImage(nv21Bytes, ImageFormatType.Nv21, width, height, null);
                using var outStream = new MemoryStream();
                yuvImage.CompressToJpeg(new global::Android.Graphics.Rect(0, 0, width, height), 80, outStream);

                var jpegBytes = outStream.ToArray();
                lock (_frameLock)
                {
                    _latestFrame = jpegBytes;
                }
            }
            catch { }
        }

        public async Task StopAsync()
        {
            try
            {
                _captureSession?.Close();
                _captureSession = null;
                _camera?.Close();
                _camera = null;
                _imageReader?.Close();
                _imageReader = null;
                _backgroundThread?.QuitSafely();
                _backgroundThread?.Join();
                _backgroundThread = null;
                _backgroundHandler = null;
            }
            catch { }
            await Task.CompletedTask;
        }

        public void Abandon()
        {
            _ = StopAsync();
        }
    }
}
