using Android.Graphics;
using Android.Util;
using Application = Android.App.Application;

namespace FacialCameraBroadcaster.Platforms.Android
{
    /// <summary>
    /// Compares two JPEG frames to see if they are substantially the same (same camera, same scene).
    /// Used when reconnecting to match physical cameras after Android assigns new device paths.
    /// </summary>
    public static class FrameComparison
    {
        private const string Tag = "FrameComparison";
        private const int SampleSize = 32;
        private const int PixelTolerance = 25;
        private const double MinSimilarFraction = 0.85;

        /// <summary>Returns true if the two frames are substantially similar (likely same camera/scene).</summary>
        public static bool AreSubstantiallySimilar(byte[]? frameA, byte[]? frameB)
        {
            if (frameA == null || frameA.Length < 100 || frameB == null || frameB.Length < 100)
                return false;

            try
            {
                using var bmpA = DecodeAndScale(frameA);
                using var bmpB = DecodeAndScale(frameB);
                if (bmpA == null || bmpB == null) return false;

                int w = bmpA.Width;
                int h = bmpA.Height;
                if (bmpB.Width != w || bmpB.Height != h) return false;

                int[] pixelsA = new int[w * h];
                int[] pixelsB = new int[w * h];
                bmpA.GetPixels(pixelsA, 0, w, 0, 0, w, h);
                bmpB.GetPixels(pixelsB, 0, w, 0, 0, w, h);

                int similar = 0;
                int total = w * h;
                for (int i = 0; i < total; i++)
                {
                    int rA = (pixelsA[i] >> 16) & 0xFF;
                    int gA = (pixelsA[i] >> 8) & 0xFF;
                    int bA = pixelsA[i] & 0xFF;
                    int rB = (pixelsB[i] >> 16) & 0xFF;
                    int gB = (pixelsB[i] >> 8) & 0xFF;
                    int bB = pixelsB[i] & 0xFF;
                    int dr = Math.Abs(rA - rB);
                    int dg = Math.Abs(gA - gB);
                    int db = Math.Abs(bA - bB);
                    if (dr <= PixelTolerance && dg <= PixelTolerance && db <= PixelTolerance)
                        similar++;
                }
                double fraction = (double)similar / total;
                return fraction >= MinSimilarFraction;
            }
            catch (Exception ex)
            {
                Log.Warn(Tag, $"Compare failed: {ex.Message}");
                return false;
            }
        }

        private static Bitmap? DecodeAndScale(byte[] jpeg)
        {
            try
            {
                var opts = new BitmapFactory.Options { InSampleSize = 8 };
                using var full = BitmapFactory.DecodeByteArray(jpeg, 0, jpeg.Length, opts);
                if (full == null) return null;
                var scaled = Bitmap.CreateScaledBitmap(full, SampleSize, SampleSize, true);
                if (scaled == null) return null;
                if (!scaled.IsMutable)
                {
                    var mutable = scaled.Copy(Bitmap.Config.Argb8888!, true);
                    scaled.Recycle();
                    return mutable;
                }
                return scaled;
            }
            catch
            {
                return null;
            }
        }
    }
}
