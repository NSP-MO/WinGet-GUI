using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WingetGui.Services;

public static class IconService
{
    private static readonly ConcurrentDictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static ImageSource? _defaultAppIcon;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int ExtractIconEx(string szFileName, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, int nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static ImageSource GetDefaultIcon()
    {
        if (_defaultAppIcon != null) return _defaultAppIcon;

        try
        {
            var uri = new Uri("pack://application:,,,/assets/app.png", UriKind.Absolute);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.UriSource = uri;
            bi.DecodePixelWidth = 24;
            bi.DecodePixelHeight = 24;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            _defaultAppIcon = bi;
            return _defaultAppIcon;
        }
        catch
        {
            // Fallback to geometric glyph if resource stream is unavailable
        }

        // Create a crisp fallback application glyph
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x4a, 0x4a, 0x4a)), 1.0);
            pen.Freeze();
            var bgBrush = new SolidColorBrush(Color.FromRgb(0x2d, 0x2d, 0x30));
            bgBrush.Freeze();
            var rect = new Rect(1, 1, 14, 14);
            dc.DrawRoundedRectangle(bgBrush, pen, rect, 2, 2);

            var lineBrush = new SolidColorBrush(Color.FromRgb(0x0e, 0x63, 0x9c));
            lineBrush.Freeze();
            dc.DrawRectangle(lineBrush, null, new Rect(3, 4, 10, 2));

            var textPen = new SolidColorBrush(Color.FromRgb(0x85, 0x85, 0x85));
            textPen.Freeze();
            dc.DrawRectangle(textPen, null, new Rect(3, 8, 7, 1.5));
            dc.DrawRectangle(textPen, null, new Rect(3, 11, 5, 1.5));
        }

        var rtb = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        _defaultAppIcon = rtb;
        return _defaultAppIcon;
    }

    public static ImageSource GetIcon(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return GetDefaultIcon();
        }

        var clean = CleanPath(rawPath, out var index);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return GetDefaultIcon();
        }

        var cacheKey = $"{clean}#{index}";
        if (IconCache.TryGetValue(cacheKey, out var cached) && cached != null)
        {
            return cached;
        }

        try
        {
            if (File.Exists(clean))
            {
                // Try ExtractIconEx first if index specified or is EXE/DLL/ICO
                IntPtr hIcon = IntPtr.Zero;
                if (index != 0 || clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || clean.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractIconEx(clean, index, out _, out hIcon, 1);
                }

                // If ExtractIconEx didn't yield an icon, fallback to SHGetFileInfo
                if (hIcon == IntPtr.Zero)
                {
                    var shinfo = new SHFILEINFO();
                    SHGetFileInfo(clean, 0, ref shinfo, (uint)Marshal.SizeOf(shinfo), SHGFI_ICON | SHGFI_SMALLICON);
                    hIcon = shinfo.hIcon;
                }

                if (hIcon != IntPtr.Zero)
                {
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(
                        hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    DestroyIcon(hIcon);
                    IconCache[cacheKey] = bmp;
                    return bmp;
                }
            }
        }
        catch
        {
            // Ignore icon extraction failures and return fallback
        }

        var fallback = GetDefaultIcon();
        IconCache[cacheKey] = fallback;
        return fallback;
    }

    private static string CleanPath(string input, out int index)
    {
        index = 0;
        input = input.Trim().Trim('\"', '\'');

        var commaIdx = input.LastIndexOf(',');
        if (commaIdx > 0 && commaIdx < input.Length - 1)
        {
            var indexStr = input[(commaIdx + 1)..].Trim();
            if (int.TryParse(indexStr, out var parsedIndex))
            {
                index = parsedIndex;
                input = input[..commaIdx].Trim().Trim('\"', '\'');
            }
        }

        // Expand environment variables (e.g., %SystemRoot%)
        input = Environment.ExpandEnvironmentVariables(input);

        return input;
    }
}
