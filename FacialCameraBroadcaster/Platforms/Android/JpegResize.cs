using Android.Graphics;

namespace FacialCameraBroadcaster.Platforms.Android
{
    /// <summary>
    /// Resizes JPEG frames for streaming. Used in low-resolution mode to reduce bandwidth on poor networks.
    /// </summary>
    public static class JpegResize
    {
        /// <summary>
        /// Decodes the JPEG, scales to the given size, and re-encodes as JPEG.
        /// Returns null if input is null or decode/scale fails.
        /// </summary>
        /// <param name="jpeg">Source JPEG bytes (camera frame).</param>
        /// <param name="width">Target width (e.g. 128).</param>
        /// <param name="height">Target height (e.g. 128).</param>
        /// <param name="quality">JPEG compression quality 0–100 (e.g. 80).</param>
        public static byte[]? ScaleToSize(byte[]? jpeg, int width, int height, int quality = 80)
        {
            if (jpeg == null || jpeg.Length == 0) return null;
            if (width <= 0 || height <= 0) return null;

            Bitmap? decoded = null;
            Bitmap? scaled = null;
            try
            {
                decoded = BitmapFactory.DecodeByteArray(jpeg, 0, jpeg.Length);
                if (decoded == null) return null;

                scaled = Bitmap.CreateScaledBitmap(decoded, width, height, true);
                if (scaled == null) return null;

                using var stream = new System.IO.MemoryStream();
                bool ok = scaled.Compress(Bitmap.CompressFormat.Jpeg!, Math.Clamp(quality, 1, 100), stream);
                if (!ok) return null;
                return stream.ToArray();
            }
            catch
            {
                return null;
            }
            finally
            {
                scaled?.Recycle();
                decoded?.Recycle();
            }
        }
    }
}
