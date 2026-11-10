using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Hark.Core;

namespace Hark.App;

public sealed record ScreenShot(string Path, string ActiveWindowTitle);

/// <summary>Captures the whole desktop (all monitors) to a JPEG in Hark's data folder.</summary>
public static class ScreenCapture
{
    private const int MaxWidth = 3840;

    public static string Folder
    {
        get
        {
            var dir = System.IO.Path.Combine(AppPaths.DataDirectory, "captures");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Hides Hark's own window so it is not in the picture, captures, then brings it back.</summary>
    public static async Task<ScreenShot?> CaptureAsync(Window? hide)
    {
        var wasVisible = hide is { IsVisible: true };
        try
        {
            if (wasVisible)
            {
                hide!.Hide();
                await Task.Delay(350); // let the window underneath repaint and take focus
            }
            var title = ForegroundTitle();
            var path = await Task.Run(CaptureToFile);
            return new ScreenShot(path, title);
        }
        catch (Exception ex)
        {
            Log.Error("Screen capture failed", ex);
            return null;
        }
        finally
        {
            if (wasVisible)
            {
                hide!.Show();
                hide.Activate();
            }
        }
    }

    private static string CaptureToFile()
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var full = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var g = System.Drawing.Graphics.FromImage(full))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, full.Size);

        var path = System.IO.Path.Combine(Folder, $"screen-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg");
        if (full.Width <= MaxWidth)
        {
            Save(full, path);
            return path;
        }

        // Very wide multi-monitor desktops are scaled down so the image stays a sensible size.
        var height = (int)Math.Round(full.Height * (MaxWidth / (double)full.Width));
        using var scaled = new System.Drawing.Bitmap(MaxWidth, height);
        using (var g = System.Drawing.Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, 0, 0, MaxWidth, height);
        }
        Save(scaled, path);
        return path;
    }

    private static void Save(System.Drawing.Bitmap bitmap, string path)
    {
        var jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
        using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
        parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
        bitmap.Save(path, jpeg, parameters);
    }

    /// <summary>Removes captures older than 30 days.</summary>
    public static void CleanUp()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Folder, "screen-*.*"))
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-30)) File.Delete(file);
        }
        catch (Exception ex)
        {
            Log.Error("Cleaning old captures failed", ex);
        }
    }

    private static string ForegroundTitle()
    {
        try
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero) return "";
            var text = new StringBuilder(512);
            return GetWindowText(handle, text, text.Capacity) > 0 ? text.ToString() : "";
        }
        catch
        {
            return "";
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
}
