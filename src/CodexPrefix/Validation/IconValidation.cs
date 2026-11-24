using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace CodexPrefix;

// 仅检查本程序资源与自己创建的隐藏测试窗口，不检索或操作 Codex。
internal static class IconValidation
{
    internal static void Run(Action<bool, string> check)
    {
        var uri = new Uri("pack://application:,,,/assets/app-icon/release-v2/app.ico");
        using var resource = Application.GetResourceStream(uri).Stream;
        using var memory = new MemoryStream();
        resource.CopyTo(memory);
        var icon = memory.ToArray();
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        check(BitConverter.ToUInt16(icon, 0) == 0 && BitConverter.ToUInt16(icon, 2) == 1, "应内嵌标准 ICO 类型");
        check(BitConverter.ToUInt16(icon, 4) == sizes.Length, "ICO 应包含九个光学尺寸");
        var payloads = new Dictionary<int, byte[]>();
        for (var index = 0; index < sizes.Length; index++)
        {
            var entry = 6 + index * 16;
            var width = icon[entry] == 0 ? 256 : icon[entry];
            var height = icon[entry + 1] == 0 ? 256 : icon[entry + 1];
            check(width == sizes[index] && height == sizes[index], "ICO 目录尺寸必须正确");
            check(BitConverter.ToUInt16(icon, entry + 6) == 32, "ICO 必须保留 32 位颜色与透明度");
            var length = checked((int)BitConverter.ToUInt32(icon, entry + 8));
            var offset = checked((int)BitConverter.ToUInt32(icon, entry + 12));
            check(offset >= 6 + sizes.Length * 16 && length > 0 && offset + length <= icon.Length, "ICO 帧偏移必须有效");
            var bytes = icon.AsSpan(offset, length).ToArray();
            payloads.Add(width, bytes);
            using var png = new MemoryStream(bytes);
            var frame = BitmapFrame.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            check(frame.PixelWidth == width && frame.PixelHeight == height, "图标帧必须可解码且尺寸匹配");
            var bgra = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var pixels = new byte[width * height * 4];
            bgra.CopyPixels(pixels, width * 4, 0);
            check(pixels[3] == 0 && pixels[^1] == 0, "图标外围应透明");
            check(pixels[((height / 2) * width + width / 2) * 4 + 3] == 255, "黑色底板内部不得被误抠除");
            var bounds = Bounds(bgra);
            check(bounds.Width >= width * .8 && bounds.Height >= height * .8, "正式帧主体不得因过多透明留白而过小");
            if (width <= 24)
            {
                check(Enumerable.Range(0, width * height).All(p => pixels[p * 4 + 3] is 0 or 255), "小尺寸原稿必须为整像素轮廓，不含缩放后的模糊半透明边缘");
                var colors = Enumerable.Range(0, width * height).Where(p => pixels[p * 4 + 3] == 255).Select(p => BitConverter.ToUInt32(pixels, p * 4)).Distinct().ToArray();
                check(colors.Length <= 16 && colors.Length >= 8, "小尺寸应使用有限色板，同时保留彩星多色");
                int caret = width == 16 ? 9 : width == 20 ? 11 : 13;
                int middle = width == 16 ? 8 : width == 20 ? 10 : 12;
                check(pixels[(middle * width + caret) * 4] >= 240 && pixels[(middle * width + caret) * 4 + 2] >= 240, "小尺寸必须保留清晰白色竖光标");
                check(pixels[(middle * width + caret + 2) * 4] < 150, "光标右侧不得增加横杠");
            }
        }
        var module = LoadLibraryExW(Environment.ProcessPath!, 0, 0x22);
        check(module != 0, "应能读取当前 EXE 的原生资源");
        try
        {
            var groups = new List<byte[]>();
            EnumResourceNamesProc callback = (library, type, name, _) => { groups.Add(ReadResource(library, name, type)); return true; };
            check(EnumResourceNamesW(module, (nint)14, callback, 0) && groups.Count > 0, "EXE 必须包含 Windows 图标组");
            var group = groups[0];
            check(BitConverter.ToUInt16(group, 4) == sizes.Length, "EXE 图标组应保留九个尺寸");
            for (var index = 0; index < sizes.Length; index++)
            {
                var entry = 6 + index * 14;
                var width = group[entry] == 0 ? 256 : group[entry];
                var nativePayload = ReadResource(module, (nint)BitConverter.ToUInt16(group, entry + 12), (nint)3);
                check(payloads.TryGetValue(width, out var expected) && nativePayload.SequenceEqual(expected), "EXE 原生图标必须与确认的 ICO 帧一致");
            }
            GC.KeepAlive(callback);
        }
        finally { if (module != 0) FreeLibrary(module); }

        var app = Application.Current;
        var originalMode = app.ShutdownMode;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        check(app.FindResource("AppIcon") is BitmapFrame { Decoder: IconBitmapDecoder }, "窗口图标必须保留多帧 ICO 解码器，不能使用 BitmapImage 首帧");
        var sharedIcon = (BitmapFrame)app.FindResource("AppIcon");
        check(sharedIcon.Decoder.Frames.Select(frame => frame.PixelWidth).SequenceEqual(sizes), "窗口资源应能访问全部九档光学帧");
        var window = new Window { Title = "图标资源内部自检", Style = (Style)app.FindResource(typeof(Window)), Width = 80, Height = 60, ShowInTaskbar = false };
        try
        {
            // 不调用 Show；只建立自己的隐藏 HWND 并核对大小图标，不接触外部窗口。
            var handle = new WindowInteropHelper(window).EnsureHandle();
            check(window.Icon is not null, "统一窗口样式应设置应用图标");
            foreach (var kind in new[] { 0, 1 })
            {
                var nativeIcon = SendMessageW(handle, 0x7F, kind, 0);
                check(nativeIcon != 0, "Windows 应能读取原生大小图标");
                var actual = Imaging.CreateBitmapSourceFromHIcon(nativeIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                check(sizes.Contains(actual.PixelWidth) && actual.PixelWidth == actual.PixelHeight, "原生图标应使用 ICO 对应帧，而不是给首帧填充透明画布");
                var bounds = Bounds(actual);
                check(bounds.Width >= actual.PixelWidth * .8 && bounds.Height >= actual.PixelHeight * .8, "原生窗口图标实际主体应占画布至少八成，避免任务栏图标过小");
                var expected = sharedIcon.Decoder.Frames.Single(frame => frame.PixelWidth == actual.PixelWidth);
                check(bounds == Bounds(expected), "原生大小图标的实际内容范围必须与对应光学帧一致");
            }
        }
        finally { window.Close(); app.ShutdownMode = originalMode; }
    }

    static Int32Rect Bounds(System.Windows.Media.Imaging.BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            if (pixels[(y * width + x) * 4 + 3] < 128) continue;
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return right < left ? Int32Rect.Empty : new Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }

    static byte[] ReadResource(nint module, nint name, nint type)
    {
        var resource = FindResourceW(module, name, type);
        if (resource == 0) throw new InvalidOperationException("缺少原生图标资源");
        var data = LockResource(LoadResource(module, resource));
        var bytes = new byte[checked((int)SizeofResource(module, resource))];
        Marshal.Copy(data, bytes, 0, bytes.Length);
        return bytes;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    delegate bool EnumResourceNamesProc(nint module, nint type, nint name, nint parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint LoadLibraryExW(string file, nint reserved, uint flags);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool FreeLibrary(nint module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EnumResourceNamesW(nint module, nint type, EnumResourceNamesProc callback, nint parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint FindResourceW(nint module, nint name, nint type);
    [DllImport("kernel32.dll")] static extern nint LoadResource(nint module, nint resource);
    [DllImport("kernel32.dll")] static extern nint LockResource(nint resource);
    [DllImport("kernel32.dll")] static extern uint SizeofResource(nint module, nint resource);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
}
