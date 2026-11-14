using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Jarvis.Core.Abstractions;

namespace Jarvis.Windows;

/// <summary>Takes exactly one screenshot per call. There is no recording, no timer and no background capture.</summary>
public sealed class ScreenCaptureService : IScreenCapture
{
    public const int MaxWidth = 1920;

    public Task<CapturedImage> CaptureAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("No display is available to capture.");

            using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(full))
            {
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            var image = full;
            Bitmap? scaled = null;
            try
            {
                if (full.Width > MaxWidth)
                {
                    var height = (int)Math.Round(full.Height * (MaxWidth / (double)full.Width));
                    scaled = new Bitmap(MaxWidth, height, PixelFormat.Format24bppRgb);
                    using var graphics = Graphics.FromImage(scaled);
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(full, 0, 0, MaxWidth, height);
                    image = scaled;
                }

                using var stream = new MemoryStream();
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
                image.Save(stream, codec, parameters);
                return new CapturedImage(stream.ToArray(), "image/jpeg", image.Width, image.Height);
            }
            finally
            {
                scaled?.Dispose();
            }
        }, ct);
    }
}

/// <summary>Reads the clipboard once per call. The clipboard is never watched or polled.</summary>
public sealed class ClipboardService : IClipboardService
{
    public string? GetText()
    {
        string? text = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Forms.Clipboard.ContainsText()) text = System.Windows.Forms.Clipboard.GetText();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        // The Windows clipboard can only be accessed from a single-threaded apartment.
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(5))) throw new IOException("The clipboard did not respond.");
        if (failure is not null) throw new IOException("The clipboard could not be read: " + failure.Message);
        return text;
    }
}
