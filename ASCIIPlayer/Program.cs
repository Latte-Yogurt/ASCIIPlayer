using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ASCIIPlayer
{
    internal static class Program
    {
        // ---- 基本参数 ----

        // ffmpeg 解码后的固定像素流尺寸。640x360 是 16:9，采样时再做自适应缩放
        private const int STREAM_WIDTH = 640;
        private const int STREAM_HEIGHT = 360;

        // 帧率不再写死：-f/--fps 指定，或读源视频帧率；这里只是允许区间与兜底值
        private const int MIN_FPS = 1;
        private const int MAX_FPS = 60;
        private const int FALLBACK_FPS = 24;

        // 暗部提亮一点，不然低亮度区域全被压成空格
        private const double GAMMA = 0.85;

        // 抖动幅度，用来把离散的字符阶梯抹平一点
        private const double DITHER_STRENGTH = 0.5;

        // 编码输出时每个字符占的像素格。8x16 是常见等宽字体的比例
        private const int CHAR_W = 8;
        private const int CHAR_H = 16;

        // 终端网格的下限，低于这个就没法看了
        private const int MIN_COLS = 10;
        private const int MIN_ROWS = 3;

        // 编码网格的下限
        private const int MIN_RENDER_COLS = 20;
        private const int MIN_RENDER_ROWS = 5;

        private const int EXIT_OK = 0;
        private const int EXIT_FAIL = 1;
        private const int EXIT_USAGE = 2;

        // 由疏到密的字符表。第一个必须是空格：索引 0 表示「什么都不画」
        private static readonly char[] Ramp =
            " .'`^\",:;Il!i><~+_-?][}{1)(|\\/tfjrxnuvczXYUJCLQ0OZmwqpdbkhao*#MW&8%B@$".ToCharArray();

        // 8x8 Bayer 抖动矩阵
        private static readonly int[,] BAYER8 = new int[8, 8]
        {
            {  0, 32,  8, 40,  2, 34, 10, 42 },
            { 48, 16, 56, 24, 50, 18, 58, 26 },
            { 12, 44,  4, 36, 14, 46,  6, 38 },
            { 60, 28, 52, 20, 62, 30, 54, 22 },
            {  3, 35, 11, 43,  1, 33,  9, 41 },
            { 51, 19, 59, 27, 49, 17, 57, 25 },
            { 15, 47,  7, 39, 13, 45,  5, 37 },
            { 63, 31, 55, 23, 61, 29, 53, 21 }
        };

        // 亮度 0..255 -> 字符表下标。以前每个字符都调一次 Math.Pow，
        // 高分辨率下每帧几万次，纯 CPU 渲染慢的一大原因。现在预先算好查表
        private static readonly byte[] RAMP_LUT = BUILD_RAMP_LUT();

        private static GlyphAtlas _atlas;
        private static GlyphAtlas ATLAS
        {
            get
            {
                if (_atlas == null) _atlas = GlyphAtlas.Build();
                return _atlas;
            }
        }

        private static readonly string _extPath = Path.GetTempPath();
        private static readonly string _ffmpegPath = Path.Combine(_extPath, "ffmpeg.exe");

        // 回归测试用：ASCIIPLAYER_CONSOLE_SIZE 指向一个 "宽x高" 的文本文件，
        // 每帧都会重新读，测试可以在播放过程中改这个文件来模拟拖动窗口
        private static readonly string _consoleSizeOverride =
            Environment.GetEnvironmentVariable("ASCIIPLAYER_CONSOLE_SIZE");

        // ---- Win32 ----

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleScreenBufferInfo(
            IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpConsoleScreenBufferInfo);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetCurrentConsoleFontEx(
            IntPtr hConsoleOutput, bool bMaximumWindow, ref CONSOLE_FONT_INFO_EX lpConsoleCurrentFontEx);

        // 居中窗口用到的几个
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const int STD_OUTPUT_HANDLE = -11;

        private const uint ENABLE_WRAP_AT_EOL_OUTPUT = 0x0002;
        private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        // SetWindowPos 的标志位
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SMALL_RECT
        {
            public short Left;
            public short Top;
            public short Right;
            public short Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CONSOLE_SCREEN_BUFFER_INFO
        {
            public COORD dwSize;
            public COORD dwCursorPosition;
            public ushort wAttributes;
            public SMALL_RECT srWindow;
            public COORD dwMaximumWindowSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CONSOLE_FONT_INFO_EX
        {
            public uint cbSize;
            public uint nFont;
            public COORD dwFontSize;
            public uint FontFamily;
            public uint FontWeight;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string FaceName;
        }

        // ANSI 转义
        private const string ANSI_HOME = "\x1b[H";
        private const string ANSI_CLEAR = "\x1b[2J\x1b[H";
        private const string ANSI_ERASE_LINE = "\x1b[K";
        private const string ANSI_HIDE_CURSOR = "\x1b[?25l";
        private const string ANSI_SHOW_CURSOR = "\x1b[?25h";
        private const string ANSI_RESET = "\x1b[0m";

        // ---- 启动 ----

        private static int Main(string[] args)
        {
            Options opt = null;
            try
            {
                // 先统一输出编码，这样连参数错误、-h、自检也是 UTF-8
                PREPARE_PROCESS_CONSOLE();

                opt = PARSE_ARGS(args);
                if (opt == null) return EXIT_USAGE;

                if (opt.Help)
                {
                    PRINT_USAGE();
                    return EXIT_OK;
                }

                if (opt.SelfTest) return RUN_SELFTEST();

                if (string.IsNullOrEmpty(opt.VideoPath))
                {
                    PRINT_USAGE();
                    PAUSE_IF_INTERACTIVE(opt);
                    return EXIT_USAGE;
                }

                if (!File.Exists(opt.VideoPath))
                {
                    ERR($"找不到文件: {opt.VideoPath}");
                    PAUSE_IF_INTERACTIVE(opt);
                    return EXIT_USAGE;
                }

                CREATE_COMPONENTS();

                if (!CHECK_FFMPEG_EXIST())
                {
                    ERR("ffmpeg 提取失败，无法继续。");
                    PAUSE_IF_INTERACTIVE(opt);
                    return EXIT_FAIL;
                }

                bool rendering = !string.IsNullOrEmpty(opt.RenderPath);
                if (rendering) VERIFY_OUTPUT_PATH(opt.RenderPath);

                int fps = RESOLVE_FPS(opt.VideoPath, opt.Fps, opt.Verbose);
                bool playing = !rendering || opt.PlayWhileRender;
                bool trueColor = RESOLVE_TRUE_COLOR(opt.ColorMode);

                if (opt.Verbose)
                {
                    VLOG($"ffmpeg: {_ffmpegPath}");
                    VLOG($"帧率: {fps}");
                    VLOG($"颜色: {(trueColor ? "真彩色 (24-bit)" : "256 色")}");
                    VLOG($"模式: {(rendering ? "编码输出" : "终端播放")}{(playing && rendering ? " + 终端播放" : "")}");
                }

                int rc = PROCESS_VIDEO(opt, fps, rendering, playing, trueColor);

                if (rendering && rc == EXIT_OK)
                {
                    Console.WriteLine($"已输出: {opt.RenderPath}");
                }

                PAUSE_IF_INTERACTIVE(opt);
                return rc;
            }
            catch (Exception ex)
            {
                RESTORE_CONSOLE();
                ERR($"出错: {ex.Message}");
                if (opt != null) PAUSE_IF_INTERACTIVE(opt);
                return EXIT_FAIL;
            }
        }

        // ---- 命令行 ----

        private sealed class Options
        {
            public string VideoPath;
            public string RenderPath;
            public bool PlayWhileRender;
            public int Fps;              // 0 = 自动（读源视频）
            public int ResW, ResH;       // 0 = 自动（按窗口）
            public int ColorMode = -1;   // -1 = 自动，0 = 强制 256 色，1 = 强制真彩色
            public bool Mute;
            public bool Verbose;
            public bool NoWait;
            public bool SelfTest;
            public bool Help;
        }

        private static Options PARSE_ARGS(string[] args)
        {
            var o = new Options();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];

                if (a == "-h" || a == "--help")
                {
                    o.Help = true;
                    return o;
                }
                else if (a == "--selftest")
                {
                    o.SelfTest = true;
                    return o;
                }
                else if (a == "-r" || a == "--render")
                {
                    if (i + 1 >= args.Length)
                    {
                        ERR("-r 后面要跟输出视频路径");
                        return null;
                    }
                    o.RenderPath = args[++i];
                }
                else if (a == "-n" || a == "--size")
                {
                    if (i + 1 >= args.Length)
                    {
                        ERR("-n 后面要跟输出分辨率，例如 1920x1080");
                        return null;
                    }
                    string sizeStr = args[++i];
                    if (!PARSE_SIZE(sizeStr, out o.ResW, out o.ResH))
                    {
                        ERR($"尺寸格式不对: {sizeStr}，应该形如 1920x1080（320x240 到 7680x4320）");
                        return null;
                    }
                }
                else if (a == "-f" || a == "--fps")
                {
                    if (i + 1 >= args.Length)
                    {
                        ERR("-f 后面要跟帧率，例如 30");
                        return null;
                    }
                    string fpsStr = args[++i];
                    if (!int.TryParse(fpsStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int f)
                        || f < MIN_FPS || f > MAX_FPS)
                    {
                        ERR($"帧率不对: {fpsStr}，应该在 {MIN_FPS} 到 {MAX_FPS} 之间");
                        return null;
                    }
                    o.Fps = f;
                }
                else if (a == "--play")
                {
                    o.PlayWhileRender = true;
                }
                else if (a == "--mute" || a == "--no-audio")
                {
                    o.Mute = true;
                }
                else if (a == "--color256" || a == "--256")
                {
                    o.ColorMode = 0;
                }
                else if (a == "--truecolor" || a == "--24bit")
                {
                    o.ColorMode = 1;
                }
                else if (a == "-v" || a == "--verbose")
                {
                    o.Verbose = true;
                }
                else if (a == "--no-wait")
                {
                    o.NoWait = true;
                }
                else if (o.VideoPath == null && !a.StartsWith("-"))
                {
                    o.VideoPath = a;
                }
                else
                {
                    ERR($"未知参数: {a}");
                    PRINT_USAGE();
                    return null;
                }
            }

            return o;
        }

        /// <summary>
        /// 解析 "1920x1080" 这种分辨率字符串，x 大小写都行
        /// </summary>
        private static bool PARSE_SIZE(string s, out int w, out int h)
        {
            w = 0;
            h = 0;
            if (string.IsNullOrEmpty(s)) return false;

            int xPos = s.IndexOf('x');
            if (xPos < 0) xPos = s.IndexOf('X');
            if (xPos <= 0 || xPos >= s.Length - 1) return false;

            if (!int.TryParse(s.Substring(0, xPos), NumberStyles.Integer, CultureInfo.InvariantCulture, out w)) return false;
            if (!int.TryParse(s.Substring(xPos + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out h)) return false;

            // 视频分辨率的合理范围。320x240 到 7680x4320 之间
            if (w < 320 || w > 7680) return false;
            if (h < 240 || h > 4320) return false;

            return true;
        }

        private static void PRINT_USAGE()
        {
            Console.WriteLine("用法: ASCIIPlayer.exe <视频文件> [选项]");
            Console.WriteLine();
            Console.WriteLine("选项:");
            Console.WriteLine("  -r, --render <路径>   把 ASCII 动画编码成视频（mp4 / mkv）");
            Console.WriteLine("  -n, --size <WxH>      指定输出视频分辨率，例如 1920x1080");
            Console.WriteLine("                        不指定就用当前窗口对应的分辨率");
            Console.WriteLine("  -f, --fps <帧率>      1 到 60，默认取源视频帧率（取不到时 24）");
            Console.WriteLine("  --play                配合 -r 使用，编码的同时也在终端播放（带声音）");
            Console.WriteLine("  --color256            终端使用 256 色（默认真彩色 24-bit）");
            Console.WriteLine("  --truecolor           强制终端真彩色（Windows 10 1703 及以上默认就是）");
            Console.WriteLine("  --mute                不播放声音");
            Console.WriteLine("  -v, --verbose         把诊断信息打到 stderr");
            Console.WriteLine("  --no-wait             结束后不等待按键（脚本用）");
            Console.WriteLine("  --selftest            内部自检，不依赖 ffmpeg / 控制台");
            Console.WriteLine("  -h, --help            显示本帮助");
            Console.WriteLine();
            Console.WriteLine("示例:");
            Console.WriteLine("  ASCIIPlayer.exe video.mp4                        终端播放");
            Console.WriteLine("  ASCIIPlayer.exe video.mp4 -f 30                  指定帧率播放");
            Console.WriteLine("  ASCIIPlayer.exe video.mp4 -r out.mp4             输出 mp4（帧率跟源视频）");
            Console.WriteLine("  ASCIIPlayer.exe video.mp4 -r out.mp4 -n 1920x1080 -f 30");
            Console.WriteLine("  ASCIIPlayer.exe video.mp4 -r out.mkv -n 2560x1440");
            Console.WriteLine("  ASCIIPlayer.exe video.mp4 -r out.mp4 --play      输出并同时播放（带声音）");
            Console.WriteLine();
        }

        private static void VERIFY_OUTPUT_PATH(string path)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception ex) { throw new IOException($"输出路径不合法: {path} ({ex.Message})"); }

            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                throw new IOException($"输出目录不存在: {dir}");
            }
        }

        private static int RESOLVE_FPS(string videoPath, int requested, bool verbose)
        {
            if (requested > 0) return requested;

            double src = PROBE_SOURCE_FPS(videoPath);
            int fps = src > 0 ? CLAMP_FPS((int)Math.Round(src)) : FALLBACK_FPS;
            if (verbose)
            {
                VLOG(src > 0
                    ? $"源帧率: {src.ToString("0.###", CultureInfo.InvariantCulture)} -> {fps}"
                    : $"源帧率读取失败，使用 {fps}");
            }
            return fps;
        }

        /// <summary>
        /// 帧率夹到允许区间。0 或负数取下限，过大的取上限。
        /// </summary>
        private static int CLAMP_FPS(int fps)
        {
            if (fps < MIN_FPS) return MIN_FPS;
            if (fps > MAX_FPS) return MAX_FPS;
            return fps;
        }

        /// <summary>
        /// 通过 ffmpeg 的元数据输出读源视频帧率。ffmpeg 只给 -i 时会以退出码 1 退出，
        /// 但需要的流信息已经打到 stderr 了，正好拿来做探测
        /// </summary>
        private static double PROBE_SOURCE_FPS(string videoPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = $"-hide_banner -nostdin -i \"{videoPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using (var proc = Process.Start(psi))
                {
                    string err = proc.StandardError.ReadToEnd();
                    proc.WaitForExit(15000);

                    Match m = Regex.Match(err, @"(\d+(?:\.\d+)?)\s*fps");
                    if (m.Success)
                    {
                        double v;
                        if (double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                                CultureInfo.InvariantCulture, out v)
                            && v > 0.1 && v < 1000)
                        {
                            return v;
                        }
                    }
                }
            }
            catch
            {
                // 探测失败就退回兜底帧率
            }

            return 0;
        }

        private static bool RESOLVE_TRUE_COLOR(int mode)
        {
            if (mode == 0) return false;
            if (mode == 1) return true;
            return SUPPORTS_TRUE_COLOR();
        }

        /// <summary>
        /// conhost 从 Windows 10 1703（build 15063）开始支持 24-bit 前景色。
        /// Windows Terminal 也支持。更老的系统退回 256 色
        /// </summary>
        private static bool SUPPORTS_TRUE_COLOR()
        {
            try
            {
                Version v = Environment.OSVersion.Version;
                return v.Major > 10 || (v.Major == 10 && v.Build >= 15063);
            }
            catch
            {
                return false;
            }
        }

        // ---- ffmpeg 资源 ----

        private static bool CHECK_FFMPEG_EXIST()
        {
            return File.Exists(_ffmpegPath);
        }

        private static void EXTRACT_RESOURCE(string resourceName, string outputPath)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException($"没有找到资源 {resourceName}");
                }

                using (FileStream fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(fileStream);
                }
            }
        }

        private static void CREATE_FFMPEG_EXE()
        {
            EXTRACT_RESOURCE("ASCIIPlayer.Resources.ffmpeg.exe", _ffmpegPath);
        }

        private static void CREATE_COMPONENTS()
        {
            if (!File.Exists(_ffmpegPath))
            {
                CREATE_FFMPEG_EXE();
            }
        }

        // ---- 控制台 ----

        private static void PREPARE_PROCESS_CONSOLE()
        {
            try { Console.Title = "ASCIIPlayer"; } catch { }

            try
            {
                if (!Console.IsOutputRedirected)
                {
                    Console.OutputEncoding = Encoding.UTF8;
                }
                else
                {
                    // 被重定向（管道 / 文件）时控制台默认用系统代码页写出去，
                    // 中文在别的工具里就是乱码。统一成无 BOM 的 UTF-8
                    Console.SetOut(new StreamWriter(
                        Console.OpenStandardOutput(), new UTF8Encoding(false))
                    { AutoFlush = true });
                }
            }
            catch { }

            try
            {
                if (Console.IsErrorRedirected)
                {
                    Console.SetError(new StreamWriter(
                        Console.OpenStandardError(), new UTF8Encoding(false))
                    { AutoFlush = true });
                }
            }
            catch { }
        }

        private static bool IS_CONSOLE_ATTACHED()
        {
            try
            {
                return !Console.IsOutputRedirected && !Console.IsInputRedirected && GetConsoleWindow() != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        private static void PAUSE_IF_INTERACTIVE(Options opt)
        {
            if (opt != null && opt.NoWait) return;
            if (!IS_CONSOLE_ATTACHED()) return;

            try
            {
                Console.Write("按任意键退出...");
                Console.ReadKey(true);
            }
            catch
            {
                // 没有控制台输入就算了，别在退出路径上再抛
            }
        }

        private static void CENTER_CONSOLE()
        {
            try
            {
                IntPtr hWnd = GetConsoleWindow();
                if (hWnd == IntPtr.Zero) return;

                RECT rect;
                if (!GetWindowRect(hWnd, out rect)) return;

                int winW = rect.Right - rect.Left;
                int winH = rect.Bottom - rect.Top;

                int screenW = GetSystemMetrics(SM_CXSCREEN);
                int screenH = GetSystemMetrics(SM_CYSCREEN);

                int x = (screenW - winW) / 2;
                int y = (screenH - winH) / 2;
                if (x < 0) x = 0;
                if (y < 0) y = 0;

                // 尺寸不动、Z 序不动，只挪位置
                SetWindowPos(hWnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER);
            }
            catch { }
        }

        private static void WAIT_FOR_WINDOW_STABLE()
        {
            // 挪位置本身是瞬间的，保险起见还是让尺寸稳定一下
            int prevW = -1, prevH = -1;
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(30);
                int w, h;
                GET_VISIBLE_CONSOLE_SIZE(out w, out h);
                if (w == prevW && h == prevH) return;
                prevW = w;
                prevH = h;
            }
        }

        private static void GET_VISIBLE_CONSOLE_SIZE(out int width, out int height)
        {
            // 测试钩子：模拟窗口尺寸，播放中改文件即可模拟拖动窗口
            if (!string.IsNullOrEmpty(_consoleSizeOverride))
            {
                try
                {
                    string s = File.ReadAllText(_consoleSizeOverride).Trim();
                    int x = s.IndexOf('x');
                    int w, h;
                    if (x > 0
                        && int.TryParse(s.Substring(0, x), out w)
                        && int.TryParse(s.Substring(x + 1), out h)
                        && w > 0 && h > 0)
                    {
                        width = w;
                        height = h;
                        return;
                    }
                }
                catch { }
            }

            // Console.WindowWidth 在某些状态下不一定准，用 API 读 srWindow 更靠谱
            try
            {
                IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
                if (h != IntPtr.Zero && h != new IntPtr(-1)
                    && GetConsoleScreenBufferInfo(h, out CONSOLE_SCREEN_BUFFER_INFO info))
                {
                    int w = info.srWindow.Right - info.srWindow.Left + 1;
                    int hh = info.srWindow.Bottom - info.srWindow.Top + 1;
                    if (w > 0 && hh > 0)
                    {
                        width = w;
                        height = hh;
                        return;
                    }
                }
            }
            catch { }

            try
            {
                width = Console.WindowWidth;
                height = Console.WindowHeight;
            }
            catch
            {
                width = 80;
                height = 25;
            }
        }

        /// <summary>
        /// 量出控制台一个字符格的宽高比（高 / 宽）。等宽字体一般是 2.0 左右，
        /// 但用户换成 8x12、10x20 之类的字体后就不一样了。按这个比例取垂直采样数，
        /// 画面才不会被拉长压扁 —— 这就是「播放效果取决于控制台字体」的根源
        /// </summary>
        private static double MEASURE_CELL_ASPECT()
        {
            if (!string.IsNullOrEmpty(_consoleSizeOverride)) return 2.0;
            try
            {
                IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
                if (h != IntPtr.Zero && h != new IntPtr(-1))
                {
                    var fi = new CONSOLE_FONT_INFO_EX();
                    fi.cbSize = (uint)Marshal.SizeOf(typeof(CONSOLE_FONT_INFO_EX));
                    if (GetCurrentConsoleFontEx(h, false, ref fi)
                        && fi.dwFontSize.X > 0 && fi.dwFontSize.Y > 0)
                    {
                        double a = (double)fi.dwFontSize.Y / fi.dwFontSize.X;
                        if (a >= 1.0 && a <= 4.0) return a;
                    }
                }
            }
            catch { }

            // 字体信息拿不到时，用客户区像素尺寸除以字符行列数
            try
            {
                IntPtr wnd = GetConsoleWindow();
                RECT cr;
                if (wnd != IntPtr.Zero && GetClientRect(wnd, out cr))
                {
                    int cw = cr.Right - cr.Left;
                    int ch = cr.Bottom - cr.Top;
                    int cols, rows;
                    GET_VISIBLE_CONSOLE_SIZE(out cols, out rows);
                    if (cw > 0 && ch > 0 && cols > 0 && rows > 0)
                    {
                        double a = ((double)ch / rows) / ((double)cw / cols);
                        if (a >= 1.0 && a <= 4.0) return a;
                    }
                }
            }
            catch { }

            return 2.0;
        }

        private static void ENABLE_VT_PROCESSING()
        {
            try
            {
                IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return;

                uint mode;
                if (GetConsoleMode(h, out mode))
                {
                    SetConsoleMode(h, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
                }
            }
            catch { }
        }

        private static uint _savedConsoleMode;
        private static bool _haveSavedConsoleMode;

        /// <summary>
        /// 关掉「到行尾自动换行」。网格和窗口宽度万一差一格，开着换行会把整帧
        /// 往下推一行、越滚越乱 —— 这就是窗口一变就成乱码的直接原因
        /// </summary>
        private static void SET_LINE_WRAP(bool enabled)
        {
            try
            {
                IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return;

                uint mode;
                if (!GetConsoleMode(h, out mode)) return;

                if (!enabled)
                {
                    if (!_haveSavedConsoleMode)
                    {
                        _savedConsoleMode = mode;
                        _haveSavedConsoleMode = true;
                    }
                    SetConsoleMode(h, mode & ~ENABLE_WRAP_AT_EOL_OUTPUT);
                }
                else if (_haveSavedConsoleMode)
                {
                    SetConsoleMode(h, _savedConsoleMode);
                    _haveSavedConsoleMode = false;
                }
            }
            catch { }
        }

        private static void RESTORE_CONSOLE()
        {
            SET_LINE_WRAP(true);
            try { Console.CursorVisible = true; } catch { }
        }

        // ---- 网格 / 采样 ----

        private sealed class GridSpec
        {
            public readonly int Cols;
            public readonly int Rows;
            public readonly int VScale;

            public GridSpec(int cols, int rows, int vScale)
            {
                Cols = cols;
                Rows = rows;
                VScale = vScale;
            }

            public bool SameAs(GridSpec other)
            {
                return other != null && other.Cols == Cols && other.Rows == Rows && other.VScale == VScale;
            }
        }

        /// <summary>
        /// 由控制台可见区域算字符网格。留一行给提示符，防止滚屏
        /// </summary>
        private static GridSpec MAKE_GRID(int consoleW, int consoleH, double cellAspect)
        {
            int cols = consoleW;
            int rows = consoleH - 1;

            if (cols < MIN_COLS) cols = MIN_COLS;
            if (rows < MIN_ROWS) rows = MIN_ROWS;

            // 垂直方向每个字符格采几行。等宽字体 8x16 时是 2
            int vScale = (int)Math.Round(cellAspect);
            if (vScale < 1) vScale = 1;
            else if (vScale > 4) vScale = 4;

            return new GridSpec(cols, rows, vScale);
        }

        /// <summary>
        /// 一帧的字符网格内容：每个格子的字符表下标 + 该格的平均颜色
        /// </summary>
        private sealed class CellFrame
        {
            public int Cols;
            public int Rows;
            public int VScale;
            public int[] Idx = Array.Empty<int>();
            public int[] Rgb = Array.Empty<int>();

            public void Resize(int cols, int rows, int vScale)
            {
                Cols = cols;
                Rows = rows;
                VScale = vScale;

                int n = cols * rows;
                if (Idx.Length < n)
                {
                    Idx = new int[n];
                    Rgb = new int[n];
                }
            }
        }

        /// <summary>
        /// rgb24 源帧 -> 字符网格。垂直方向按 VScale 取平均，水平方向取格中心
        /// </summary>
        private static void SAMPLE_FRAME(byte[] src, CellFrame f)
        {
            int cols = f.Cols;
            int rows = f.Rows;
            int vScale = f.VScale;
            int samplesV = rows * vScale;

            int[] idx = f.Idx;
            int[] rgb = f.Rgb;

            for (int cy = 0; cy < rows; cy++)
            {
                int bayerRow = cy & 7;
                int rowBase = cy * cols;
                int vyBase = cy * vScale;

                for (int cx = 0; cx < cols; cx++)
                {
                    // 取格子中心像素，避免整体偏半个格子
                    int px = ((2 * cx + 1) * STREAM_WIDTH) / (2 * cols);
                    if (px >= STREAM_WIDTH) px = STREAM_WIDTH - 1;

                    int rs = 0, gs = 0, bs = 0;
                    for (int j = 0; j < vScale; j++)
                    {
                        int vy = vyBase + j;
                        int py = ((2 * vy + 1) * STREAM_HEIGHT) / (2 * samplesV);
                        if (py >= STREAM_HEIGHT) py = STREAM_HEIGHT - 1;

                        int o = (py * STREAM_WIDTH + px) * 3;
                        rs += src[o];
                        gs += src[o + 1];
                        bs += src[o + 2];
                    }

                    int r = rs / vScale;
                    int g = gs / vScale;
                    int b = bs / vScale;

                    int brightness = (r * 30 + g * 59 + b * 11) / 100;

                    int bayer = BAYER8[bayerRow, cx & 7] - 32;
                    int dithered = brightness + (int)(bayer * DITHER_STRENGTH);
                    if (dithered < 0) dithered = 0;
                    else if (dithered > 255) dithered = 255;

                    int cell = rowBase + cx;
                    idx[cell] = RAMP_LUT[dithered];
                    rgb[cell] = (r << 16) | (g << 8) | b;
                }
            }
        }

        /// <summary>
        /// 字符网格 -> 终端文本。真彩色用 38;2;r;g;b，256 色用 38;5;n。
        /// 连续同色只发一次转义，省掉大量重复字节
        /// </summary>
        private static void BUILD_TERMINAL_TEXT(CellFrame f, StringBuilder sb, bool trueColor)
        {
            sb.Clear();
            sb.Append(ANSI_HOME);

            int cols = f.Cols;
            for (int cy = 0; cy < f.Rows; cy++)
            {
                int rowBase = cy * cols;
                int lastColor = -1;

                for (int cx = 0; cx < cols; cx++)
                {
                    int cell = rowBase + cx;
                    int idx = f.Idx[cell];

                    if (idx == 0)
                    {
                        sb.Append(' ');
                        continue;
                    }

                    int color = f.Rgb[cell];
                    if (color != lastColor)
                    {
                        if (trueColor)
                        {
                            sb.Append("\x1b[38;2;")
                              .Append((color >> 16) & 0xFF).Append(';')
                              .Append((color >> 8) & 0xFF).Append(';')
                              .Append(color & 0xFF).Append('m');
                        }
                        else
                        {
                            int c256 = RGB_TO_256((color >> 16) & 0xFF, (color >> 8) & 0xFF, color & 0xFF);
                            sb.Append("\x1b[38;5;").Append(c256).Append('m');
                        }
                        lastColor = color;
                    }

                    sb.Append(Ramp[idx]);
                }

                sb.Append(ANSI_RESET).Append(ANSI_ERASE_LINE);
                if (cy < f.Rows - 1) sb.Append("\r\n");
            }
        }

        // ---- 字符点阵 ----

        /// <summary>
        /// 启动时把字符表里的每个字符一次性画进 8x16 的小位图，记下每个字符
        /// 哪些像素是亮的。之后每帧只做「按坐标写颜色」，不再调 GDI+ 的
        /// DrawString —— 那是原来高分辨率渲染慢的主要原因
        /// </summary>
        private sealed class GlyphAtlas
        {
            // [字符下标][行] -> 该行亮像素的 x 列表
            public readonly int[][][] Rows;
            public readonly int[] LitCount;

            private GlyphAtlas(int[][][] rows, int[] litCount)
            {
                Rows = rows;
                LitCount = litCount;
            }

            public static GlyphAtlas Build()
            {
                int n = Ramp.Length;
                var rows = new int[n][][];
                var lit = new int[n];

                float fontSize = Math.Max(8f, CHAR_H * 0.875f);
                Font font = null;
                try
                {
                    font = new Font("Consolas", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
                }
                catch
                {
                    font = new Font(FontFamily.GenericMonospace, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
                }

                try
                {
                    using (var bmp = new Bitmap(CHAR_W, CHAR_H, PixelFormat.Format32bppArgb))
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;

                        var pixel = new int[CHAR_W * CHAR_H];

                        for (int i = 0; i < n; i++)
                        {
                            g.Clear(Color.Black);
                            g.DrawString(Ramp[i].ToString(), font, Brushes.White, 0, 0);

                            BitmapData data = bmp.LockBits(
                                new Rectangle(0, 0, CHAR_W, CHAR_H),
                                ImageLockMode.ReadOnly,
                                PixelFormat.Format32bppArgb);
                            try
                            {
                                int stride = data.Stride;
                                IntPtr scan0 = data.Scan0;
                                for (int y = 0; y < CHAR_H; y++)
                                {
                                    IntPtr rowPtr = stride >= 0
                                        ? IntPtr.Add(scan0, y * stride)
                                        : IntPtr.Add(scan0, (CHAR_H - 1 - y) * -stride);
                                    Marshal.Copy(rowPtr, pixel, y * CHAR_W, CHAR_W);
                                }
                            }
                            finally
                            {
                                bmp.UnlockBits(data);
                            }

                            rows[i] = new int[CHAR_H][];
                            int count = 0;

                            for (int y = 0; y < CHAR_H; y++)
                            {
                                var xs = new List<int>(CHAR_W);
                                for (int x = 0; x < CHAR_W; x++)
                                {
                                    // 白字黑底，亮度够就算亮像素
                                    int argb = pixel[y * CHAR_W + x];
                                    int b = argb & 0xFF;
                                    if (b >= 128) xs.Add(x);
                                }

                                if (xs.Count == 0)
                                {
                                    rows[i][y] = Array.Empty<int>();
                                }
                                else
                                {
                                    rows[i][y] = xs.ToArray();
                                    count += xs.Count;
                                }
                            }

                            lit[i] = count;
                        }
                    }
                }
                finally
                {
                    if (font != null) font.Dispose();
                }

                return new GlyphAtlas(rows, lit);
            }
        }

        /// <summary>
        /// 字符网格 -> 24bpp BGR 帧缓冲（ffmpeg 的 bgr24）。背景恒为黑，
        /// 只写亮像素，行与行互不重叠，所以可以按行并行
        /// </summary>
        private static void COMPOSE_FRAME(CellFrame f, GlyphAtlas atlas, byte[] dst, int stride)
        {
            Array.Clear(dst, 0, dst.Length);

            int cols = f.Cols;
            int rows = f.Rows;
            int[] idx = f.Idx;
            int[] rgb = f.Rgb;
            int[][][] glyphs = atlas.Rows;

            if (cols * rows >= 2048)
            {
                Parallel.For(0, rows, cy =>
                {
                    COMPOSE_ROW(cy, cols, idx, rgb, glyphs, dst, stride);
                });
            }
            else
            {
                for (int cy = 0; cy < rows; cy++)
                {
                    COMPOSE_ROW(cy, cols, idx, rgb, glyphs, dst, stride);
                }
            }
        }

        private static void COMPOSE_ROW(
            int cy, int cols, int[] idx, int[] rgb, int[][][] glyphs, byte[] dst, int stride)
        {
            int rowBase = cy * cols;
            int cellY = cy * CHAR_H;

            for (int cx = 0; cx < cols; cx++)
            {
                int cell = rowBase + cx;
                int[][] lit = glyphs[idx[cell]];
                if (lit == null) continue;

                int color = rgb[cell];
                byte b = (byte)(color & 0xFF);
                byte g = (byte)((color >> 8) & 0xFF);
                byte r = (byte)((color >> 16) & 0xFF);

                int cellX = cx * CHAR_W;

                for (int y = 0; y < CHAR_H; y++)
                {
                    int[] xs = lit[y];
                    if (xs.Length == 0) continue;

                    int p = (cellY + y) * stride + cellX * 3;
                    for (int k = 0; k < xs.Length; k++)
                    {
                        int o = p + xs[k] * 3;
                        dst[o] = b;
                        dst[o + 1] = g;
                        dst[o + 2] = r;
                    }
                }
            }
        }

        // ---- 颜色 ----

        private static byte[] BUILD_RAMP_LUT()
        {
            var lut = new byte[256];
            int last = Ramp.Length - 1;

            for (int i = 0; i < 256; i++)
            {
                double norm = i / 255.0;
                int idx = (int)(Math.Pow(norm, GAMMA) * last);
                if (idx < 0) idx = 0;
                else if (idx > last) idx = last;
                lut[i] = (byte)idx;
            }

            return lut;
        }

        private static int RGB_TO_256(int r, int g, int b)
        {
            // 6x6x6 色立方，落在 16..231
            int ri = r * 5 / 255;
            int gi = g * 5 / 255;
            int bi = b * 5 / 255;
            return 16 + 36 * ri + 6 * gi + bi;
        }

        /// <summary>
        /// xterm 256 色号转成 Color。
        /// 16..231 是 6x6x6 色立方，232..255 是灰阶。
        /// </summary>
        private static Color XTERM256_TO_RGB(int idx)
        {
            if (idx < 0) idx = 0;
            if (idx > 255) idx = 255;

            if (idx < 16)
            {
                switch (idx)
                {
                    case 0: return Color.FromArgb(0, 0, 0);
                    case 1: return Color.FromArgb(128, 0, 0);
                    case 2: return Color.FromArgb(0, 128, 0);
                    case 3: return Color.FromArgb(128, 128, 0);
                    case 4: return Color.FromArgb(0, 0, 128);
                    case 5: return Color.FromArgb(128, 0, 128);
                    case 6: return Color.FromArgb(0, 128, 128);
                    case 7: return Color.FromArgb(192, 192, 192);
                    case 8: return Color.FromArgb(128, 128, 128);
                    case 9: return Color.FromArgb(255, 0, 0);
                    case 10: return Color.FromArgb(0, 255, 0);
                    case 11: return Color.FromArgb(255, 255, 0);
                    case 12: return Color.FromArgb(0, 0, 255);
                    case 13: return Color.FromArgb(255, 0, 255);
                    case 14: return Color.FromArgb(0, 255, 255);
                    case 15: return Color.FromArgb(255, 255, 255);
                    default: return Color.Black;
                }
            }
            else if (idx < 232)
            {
                int n = idx - 16;
                int ri = n / 36;
                int gi = (n / 6) % 6;
                int bi = n % 6;
                return Color.FromArgb(ri * 51, gi * 51, bi * 51);
            }
            else
            {
                int v = (idx - 232) * 10 + 8;
                if (v > 255) v = 255;
                return Color.FromArgb(v, v, v);
            }
        }

        // ---- 主流程 ----

        /// <summary>
        /// 播放 + 可选编码。纯播放和「边编码边播放」走同一条路径，
        /// 所以声音、缩放、重采样只有一份实现，不会再出现「-r --play 没声音」
        /// 这种两套代码各自演化出来的差异
        /// </summary>
        private static int PROCESS_VIDEO(Options opt, int fps, bool rendering, bool playing, bool trueColor)
        {
            string videoPath = opt.VideoPath;
            string renderPath = opt.RenderPath;

            bool interactive = IS_CONSOLE_ATTACHED();
            bool fixedRes = rendering && opt.ResW > 0 && opt.ResH > 0;

            int encCols, encRows, encVScale;
            double cellAspect = 2.0;

            if (fixedRes)
            {
                // 向下取整到字符格的整数倍
                encCols = Math.Max(MIN_RENDER_COLS, opt.ResW / CHAR_W);
                encRows = Math.Max(MIN_RENDER_ROWS, opt.ResH / CHAR_H);
                encVScale = 2;
            }
            else
            {
                // 没给分辨率：按启动时的控制台窗口定网格，之后不再变
                if (interactive)
                {
                    Thread.Sleep(50);
                    CENTER_CONSOLE();
                    WAIT_FOR_WINDOW_STABLE();
                }

                int winW, winH;
                GET_VISIBLE_CONSOLE_SIZE(out winW, out winH);
                cellAspect = interactive ? MEASURE_CELL_ASPECT() : 2.0;
                GridSpec g = MAKE_GRID(winW, winH, cellAspect);
                encCols = Math.Max(MIN_RENDER_COLS, g.Cols);
                encRows = Math.Max(MIN_RENDER_ROWS, g.Rows);
                encVScale = g.VScale;
            }

            int videoW = encCols * CHAR_W;
            int videoH = encRows * CHAR_H;
            if ((videoW & 1) != 0) videoW++;
            if ((videoH & 1) != 0) videoH++;
            int stride = videoW * 3;

            if (opt.Verbose)
            {
                VLOG($"字符网格: {encCols} x {encRows} (vScale {encVScale})");
                if (rendering) VLOG($"视频分辨率: {videoW} x {videoH}");
            }

            var encFrame = new CellFrame();
            if (rendering) encFrame.Resize(encCols, encRows, encVScale);

            byte[] renderBytes = null;
            Process ffProc = null;
            Stream ffIn = null;
            StderrCollector ffErr = null;

            if (rendering)
            {
                renderBytes = new byte[stride * videoH];

                // ffmpeg 从 stdin 收渲染好的帧，从原视频读音轨，编码成 h264 + aac。
                // 帧缓冲由我们直接按 BGR 写，所以 pix_fmt 用 bgr24
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments =
                        $"-y " +
                        $"-f rawvideo -pix_fmt bgr24 -s {videoW}x{videoH} -r {fps} -i - " +
                        $"-i \"{videoPath}\" " +
                        $"-map 0:v:0 -map 1:a:0? " +
                        $"-c:v libx264 -preset medium -crf 20 -pix_fmt yuv420p " +
                        $"-c:a aac -b:a 192k " +
                        $"-shortest " +
                        $"\"{renderPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                ffProc = Process.Start(psi);
                ffIn = ffProc.StandardInput.BaseStream;
                ffErr = new StderrCollector(ffProc);

                // 边编码边播放时这些行会插进动画里把画面打乱，所以只在纯编码时打印
                if (!playing)
                {
                    Console.WriteLine($"字符网格: {encCols} x {encRows}");
                    Console.WriteLine($"视频分辨率: {videoW} x {videoH}");
                    Console.WriteLine($"帧率: {fps}");
                }
            }

            // 音频：只要会在终端里播放就准备声音，-r + --play 也算
            SoundPlayer player = null;
            string audioPath = null;
            if (playing && !opt.Mute)
            {
                audioPath = Path.Combine(
                    _extPath,
                    "asciiplayer_audio_" + Guid.NewGuid().ToString("N") + ".wav");

                try
                {
                    if (EXTRACT_AUDIO(videoPath, audioPath))
                    {
                        player = new SoundPlayer(audioPath);
                        player.Load();
                        VLOG("音频: 已加载");
                    }
                    else
                    {
                        VLOG("音频: 源视频没有音轨，或提取失败");
                    }
                }
                catch (Exception ex)
                {
                    player = null;
                    VLOG($"音频: 加载失败 ({ex.Message})");
                }
            }

            StreamWriter term = null;
            StringBuilder sb = null;
            CellFrame dispFrame = null;
            GridSpec dispGrid = null;
            bool audioStarted = false;
            int paintSkipped = 0;
            int lastWinW = -1, lastWinH = -1;
            double cachedAspect = 2.0;

            var sw = Stopwatch.StartNew();
            var pace = new Stopwatch();          // 第一帧真正显示时才开始走，和音频对齐
            bool paceStarted = false;
            long frames = 0;
            double frameMs = 1000.0 / fps;
            double targetMs = 0;
            bool encodeFailed = false;
            int ffExitCode = 0;

            try
            {
                ENABLE_VT_PROCESSING();

                if (playing)
                {
                    SET_LINE_WRAP(false);
                    try { Console.CursorVisible = false; } catch { }

                    term = new StreamWriter(Console.OpenStandardOutput(), Encoding.ASCII, 1 << 16)
                    {
                        AutoFlush = false
                    };
                    term.Write(ANSI_HIDE_CURSOR);
                    term.Write(ANSI_CLEAR);
                    term.Flush();

                    sb = new StringBuilder(encCols * encRows * 8 + 256);
                    dispFrame = new CellFrame();   // 网格跟着窗口走，先空着
                }

                string filter =
                    $"fps={fps}," +
                    $"scale={STREAM_WIDTH}:{STREAM_HEIGHT}:force_original_aspect_ratio=decrease," +
                    $"pad={STREAM_WIDTH}:{STREAM_HEIGHT}:(ow-iw)/2:(oh-ih)/2";

                var srcPsi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = $"-hide_banner -nostdin -i \"{videoPath}\" -vf \"{filter}\" -f rawvideo -pix_fmt rgb24 -",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = null
                };

                using (var src = Process.Start(srcPsi))
                {
                    var srcErr = new StderrCollector(src);
                    Stream stdout = src.StandardOutput.BaseStream;

                    int frameSize = STREAM_WIDTH * STREAM_HEIGHT * 3;
                    byte[] buffer = new byte[frameSize];

                    while (true)
                    {
                        // 按时间轴对齐。慢的时候不补帧，直接跟上当前时间
                        if (playing && frames > 0 && paceStarted)
                        {
                            double ahead = targetMs - pace.Elapsed.TotalMilliseconds;
                            if (ahead > 0) Thread.Sleep((int)Math.Ceiling(ahead));
                        }

                        int read = READ_FULL(stdout, buffer, frameSize);
                        if (read < frameSize) break;

                        if (rendering)
                        {
                            SAMPLE_FRAME(buffer, encFrame);
                            COMPOSE_FRAME(encFrame, ATLAS, renderBytes, stride);

                            try
                            {
                                ffIn.Write(renderBytes, 0, renderBytes.Length);
                            }
                            catch (Exception ex)
                            {
                                encodeFailed = true;
                                ERR($"写入编码器失败: {ex.Message}");
                                break;
                            }

                            if (!playing && frames % 30 == 0)
                            {
                                Console.Write($"\r已渲染 {frames} 帧 ({sw.Elapsed.TotalSeconds:F1}s)");
                            }
                        }

                        if (playing && term != null)
                        {
                            int winW, winH;
                            GET_VISIBLE_CONSOLE_SIZE(out winW, out winH);

                            if (winW >= MIN_COLS && winH >= MIN_ROWS)
                            {
                                // 窗口尺寸随时可能变。变了就重新量字体比例、重新算网格、
                                // 清屏重采样，而不是把固定网格硬塞进变过的窗口里 ——
                                // 那就是改窗口大小以后画面变成乱码的来源
                                if (winW != lastWinW || winH != lastWinH)
                                {
                                    lastWinW = winW;
                                    lastWinH = winH;
                                    cachedAspect = MEASURE_CELL_ASPECT();
                                    dispGrid = null;
                                }

                                GridSpec g = MAKE_GRID(winW, winH, cachedAspect);
                                if (dispGrid == null || !dispGrid.SameAs(g))
                                {
                                    dispGrid = g;
                                    dispFrame.Resize(g.Cols, g.Rows, g.VScale);
                                    term.Write(ANSI_CLEAR);
                                }

                                SAMPLE_FRAME(buffer, dispFrame);

                                if (!paceStarted)
                                {
                                    // 首帧要显示的这一刻才算时间零点：音频从这里开始，
                                    // 前面 ffmpeg 启动、解音频的耗时不会变成音画错位
                                    pace.Restart();
                                    paceStarted = true;
                                }

                                double late = pace.Elapsed.TotalMilliseconds - targetMs;
                                bool paint = frames == 0 || late <= frameMs * 3;

                                if (paint)
                                {
                                    if (!audioStarted && player != null)
                                    {
                                        try { player.Play(); } catch { }
                                    }
                                    audioStarted = true;

                                    BUILD_TERMINAL_TEXT(dispFrame, sb, trueColor);
                                    try
                                    {
                                        term.Write(sb);
                                        term.Flush();
                                    }
                                    catch
                                    {
                                        // 终端关了就只继续编码
                                        try { term.Dispose(); } catch { }
                                        term = null;
                                    }
                                }
                                else
                                {
                                    paintSkipped++;
                                }
                            }
                        }

                        frames++;
                        targetMs = frames * frameMs;
                    }

                    if (rendering && !playing) Console.WriteLine();

                    try
                    {
                        if (src.WaitForExit(2000) && src.ExitCode != 0)
                        {
                            VLOG($"源解码 ffmpeg 退出码 {src.ExitCode}");
                            string srcTail = srcErr.Tail(5);
                            if (srcTail.Length > 0) VLOG(srcTail);
                        }
                    }
                    catch { }

                    if (frames == 0)
                    {
                        ERR("没有从 ffmpeg 收到任何帧，无法继续");
                        var tail = srcErr.Tail();
                        if (tail.Length > 0) Console.Error.WriteLine(tail);
                        encodeFailed = true;
                    }
                }
            }
            finally
            {
                // 通知编码器帧发完了
                if (ffIn != null)
                {
                    try { ffIn.Flush(); ffIn.Close(); } catch { }
                }

                if (playing)
                {
                    try
                    {
                        if (term != null)
                        {
                            term.Write(ANSI_RESET + ANSI_SHOW_CURSOR);
                            term.Flush();
                        }
                    }
                    catch { }
                    try { term?.Dispose(); } catch { }
                }

                RESTORE_CONSOLE();

                // 等编码器收尾。退出码必须在 Dispose 之前读，否则拿不到
                if (ffProc != null)
                {
                    bool exited = false;
                    try { exited = ffProc.WaitForExit(120000); } catch { }
                    if (!exited)
                    {
                        try { ffProc.Kill(); } catch { }
                    }
                    try { ffExitCode = exited ? ffProc.ExitCode : -1; } catch { ffExitCode = -1; }
                    try { ffProc.Dispose(); } catch { }
                }

                try { player?.Stop(); } catch { }
                try { player?.Dispose(); } catch { }
                try { if (audioPath != null && File.Exists(audioPath)) File.Delete(audioPath); } catch { }
            }

            if (opt.Verbose)
            {
                double sec = sw.Elapsed.TotalSeconds;
                VLOG($"处理 {frames} 帧，用时 {sec:F2}s" + (sec > 0 ? $"，{frames / sec:F1} 帧/秒" : ""));
                if (paintSkipped > 0) VLOG($"终端播放跳帧 {paintSkipped}（编码跟不上实时播放）");
            }

            if (encodeFailed) return EXIT_FAIL;

            if (rendering)
            {
                // ffmpeg 的退出码才算数
                if (ffExitCode != 0)
                {
                    ERR($"编码失败 (ffmpeg 退出码 {ffExitCode})");
                    if (ffErr != null)
                    {
                        string tail = ffErr.Tail();
                        if (tail.Length > 0) Console.Error.WriteLine(tail);
                    }
                    return EXIT_FAIL;
                }

                if (!File.Exists(renderPath))
                {
                    ERR($"输出文件没有生成: {renderPath}");
                    return EXIT_FAIL;
                }

                var info = new FileInfo(renderPath);
                if (info.Length == 0)
                {
                    ERR($"输出文件是空的: {renderPath}");
                    return EXIT_FAIL;
                }
            }

            return EXIT_OK;
        }

        private static bool EXTRACT_AUDIO(string videoPath, string audioPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = $"-y -hide_banner -nostdin -i \"{videoPath}\" -vn -ac 2 -ar 44100 -f wav \"{audioPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using (var proc = Process.Start(psi))
                {
                    proc.StandardError.ReadToEnd();
                    proc.WaitForExit(60000);

                    return proc.ExitCode == 0
                        && File.Exists(audioPath)
                        && new FileInfo(audioPath).Length > 44;
                }
            }
            catch
            {
                return false;
            }
        }

        private static int READ_FULL(Stream s, byte[] buf, int n)
        {
            int total = 0;
            while (total < n)
            {
                int r = s.Read(buf, total, n - total);
                if (r <= 0) break;
                total += r;
            }
            return total;
        }

        /// <summary>
        /// 后台把子进程的 stderr 读走（不读会阻塞子进程），只留最后几十行，
        /// 失败时打出来，比原来无条件丢弃好排查
        /// </summary>
        private sealed class StderrCollector
        {
            private readonly Queue<string> _tail = new Queue<string>();
            private const int MAX_LINES = 60;

            public StderrCollector(Process p)
            {
                var t = new Thread(() =>
                {
                    try
                    {
                        string line;
                        while ((line = p.StandardError.ReadLine()) != null)
                        {
                            lock (_tail)
                            {
                                _tail.Enqueue(line);
                                if (_tail.Count > MAX_LINES) _tail.Dequeue();
                            }
                        }
                    }
                    catch { }
                })
                { IsBackground = true };
                t.Start();
            }

            public string Tail(int count = 15)
            {
                lock (_tail)
                {
                    var all = _tail.ToArray();
                    int start = Math.Max(0, all.Length - count);
                    return string.Join(Environment.NewLine, all, start, all.Length - start);
                }
            }
        }

        // ---- 输出 ----

        private static void VLOG(string message)
        {
            try { Console.Error.WriteLine("[asciiplayer] " + message); } catch { }
        }

        private static void ERR(string message)
        {
            try { Console.Error.WriteLine(message); } catch { }
        }

        // ---- 自检 ----

        /// <summary>
        /// 不依赖 ffmpeg / 控制台的内部自检，回归测试直接跑这个。
        /// 覆盖：参数解析、颜色映射、查找表、字符点阵、网格换算、采样与合成、终端转义
        /// </summary>
        private static int RUN_SELFTEST()
        {
            int pass = 0;
            int fail = 0;

            Action<string, bool, string> check = (name, ok, detail) =>
            {
                if (ok)
                {
                    pass++;
                    Console.WriteLine("PASS " + name);
                }
                else
                {
                    fail++;
                    Console.WriteLine("FAIL " + name + (string.IsNullOrEmpty(detail) ? "" : " :: " + detail));
                }
            };

            // 1. 分辨率解析
            int w, h;
            check("parse_size_valid", PARSE_SIZE("1920x1080", out w, out h) && w == 1920 && h == 1080, "");
            check("parse_size_uppercase", PARSE_SIZE("640X480", out w, out h) && w == 640 && h == 480, "");
            check("parse_size_no_x", !PARSE_SIZE("1920", out w, out h), "");
            check("parse_size_too_small", !PARSE_SIZE("100x100", out w, out h), "");

            // 2. 帧率夹取
            check("fps_clamp_low", CLAMP_FPS(0) == MIN_FPS, "");
            check("fps_clamp_high", CLAMP_FPS(1000) == MAX_FPS, "");
            check("fps_clamp_keep", CLAMP_FPS(30) == 30, "");

            // 3. 颜色映射
            check("rgb256_black", RGB_TO_256(0, 0, 0) == 16, "" + RGB_TO_256(0, 0, 0));
            check("rgb256_white", RGB_TO_256(255, 255, 255) == 231, "" + RGB_TO_256(255, 255, 255));
            check("rgb256_red", RGB_TO_256(255, 0, 0) == 196, "" + RGB_TO_256(255, 0, 0));
            check("rgb256_green", RGB_TO_256(0, 255, 0) == 46, "" + RGB_TO_256(0, 255, 0));
            check("rgb256_blue", RGB_TO_256(0, 0, 255) == 21, "" + RGB_TO_256(0, 0, 255));

            Color c = XTERM256_TO_RGB(231);
            check("xterm231_white", c.R == 255 && c.G == 255 && c.B == 255, $"{c.R},{c.G},{c.B}");
            c = XTERM256_TO_RGB(196);
            check("xterm196_red", c.R == 255 && c.G == 0 && c.B == 0, $"{c.R},{c.G},{c.B}");
            c = XTERM256_TO_RGB(16);
            check("xterm16_black", c.R == 0 && c.G == 0 && c.B == 0, $"{c.R},{c.G},{c.B}");
            c = XTERM256_TO_RGB(255);
            check("xterm255_gray238", c.R == 238 && c.G == 238 && c.B == 238, $"{c.R},{c.G},{c.B}");

            // 4. 亮度查找表
            bool lutOk = RAMP_LUT.Length == 256 && RAMP_LUT[0] == 0 && RAMP_LUT[255] == Ramp.Length - 1;
            for (int i = 1; i < 256 && lutOk; i++)
            {
                if (RAMP_LUT[i] < RAMP_LUT[i - 1]) lutOk = false;
            }
            check("ramp_lut", lutOk, "");

            // 5. 字符点阵
            GlyphAtlas atlas = ATLAS;
            check("atlas_count", atlas.Rows.Length == Ramp.Length, "" + atlas.Rows.Length);
            check("atlas_space_empty", atlas.LitCount[0] == 0, "" + atlas.LitCount[0]);

            bool atlasOk = true;
            int emptyGlyphs = 0;
            for (int i = 1; i < Ramp.Length; i++)
            {
                if (atlas.LitCount[i] == 0) emptyGlyphs++;
                for (int y = 0; y < CHAR_H && atlasOk; y++)
                {
                    var xs = atlas.Rows[i][y];
                    for (int k = 0; k < xs.Length; k++)
                    {
                        if (xs[k] < 0 || xs[k] >= CHAR_W) atlasOk = false;
                    }
                }
            }
            check("atlas_glyphs_nonempty", emptyGlyphs == 0, "空字符数 " + emptyGlyphs);
            check("atlas_bounds", atlasOk, "");

            // 6. 网格换算（字体比例 2.0 = 8x16）
            GridSpec g = MAKE_GRID(80, 25, 2.0);
            check("grid_normal", g.Cols == 80 && g.Rows == 24 && g.VScale == 2, $"{g.Cols}x{g.Rows}/{g.VScale}");

            g = MAKE_GRID(4, 2, 2.0);
            check("grid_min_clamp", g.Cols == MIN_COLS && g.Rows == MIN_ROWS, $"{g.Cols}x{g.Rows}");

            check("grid_aspect_1_5", MAKE_GRID(80, 25, 1.5).VScale == 2, "" + MAKE_GRID(80, 25, 1.5).VScale);
            check("grid_aspect_1_2", MAKE_GRID(80, 25, 1.2).VScale == 1, "" + MAKE_GRID(80, 25, 1.2).VScale);
            check("grid_aspect_huge", MAKE_GRID(80, 25, 9.0).VScale == 4, "" + MAKE_GRID(80, 25, 9.0).VScale);
            check("grid_aspect_tiny", MAKE_GRID(80, 25, 0.2).VScale == 1, "" + MAKE_GRID(80, 25, 0.2).VScale);

            // 7. 采样 + 合成：全白帧
            int frameSize = STREAM_WIDTH * STREAM_HEIGHT * 3;
            var white = new byte[frameSize];
            for (int i = 0; i < white.Length; i++) white[i] = 255;

            var black = new byte[frameSize];

            var frame = new CellFrame();
            frame.Resize(32, 18, 2);
            SAMPLE_FRAME(white, frame);

            bool allLit = true;
            for (int i = 0; i < frame.Cols * frame.Rows; i++)
            {
                if (frame.Idx[i] == 0) { allLit = false; break; }
            }
            check("sample_white_lit", allLit, "");
            check("sample_white_color", frame.Rgb[0] == 0xFFFFFF, "0x" + frame.Rgb[0].ToString("X6"));

            SAMPLE_FRAME(black, frame);
            bool allDark = true;
            for (int i = 0; i < frame.Cols * frame.Rows; i++)
            {
                // 抖动会把纯黑抬起来一点（有意为之，用来过渡暗部），但不能出亮字符
                if (frame.Idx[i] > 8) { allDark = false; break; }
            }
            check("sample_black_near_dark", allDark, "");

            // 合成：白帧必须有非黑像素，黑帧必须全黑
            int vw = frame.Cols * CHAR_W;
            int vh = frame.Rows * CHAR_H;
            int vstride = vw * 3;
            var dst = new byte[vstride * vh];

            SAMPLE_FRAME(white, frame);
            COMPOSE_FRAME(frame, atlas, dst, vstride);
            bool anyNonZero = false;
            for (int i = 0; i < dst.Length; i++)
            {
                if (dst[i] != 0) { anyNonZero = true; break; }
            }
            check("compose_white_visible", anyNonZero, "");

            Array.Clear(dst, 0, dst.Length);
            SAMPLE_FRAME(black, frame);
            COMPOSE_FRAME(frame, atlas, dst, vstride);
            bool allZero = true;
            for (int i = 0; i < dst.Length; i++)
            {
                if (dst[i] != 0) { allZero = false; break; }
            }
            check("compose_black_empty", allZero, "");

            // 8. 终端文本：真彩色 / 256 色 / 全黑
            var sb = new StringBuilder();
            SAMPLE_FRAME(white, frame);

            BUILD_TERMINAL_TEXT(frame, sb, true);
            string trueText = sb.ToString();
            check("term_truecolor_escape", trueText.Contains("38;2;255;255;255"), "");
            check("term_truecolor_no_256", !trueText.Contains("38;5;"), "");
            check("term_home", trueText.StartsWith(ANSI_HOME, StringComparison.Ordinal), "");
            check("term_rows", CountOccurrences(trueText, "\r\n") == frame.Rows - 1,
                "" + CountOccurrences(trueText, "\r\n"));

            BUILD_TERMINAL_TEXT(frame, sb, false);
            string palText = sb.ToString();
            check("term_256_escape", palText.Contains("38;5;231"), "");
            check("term_256_no_truecolor", !palText.Contains("38;2;"), "");

            // 纯空格帧：不该出现任何颜色转义，空格数正好等于格子数
            var blank = new CellFrame();
            blank.Resize(8, 4, 2);
            BUILD_TERMINAL_TEXT(blank, sb, true);
            string blankText = sb.ToString();
            check("term_blank_no_color", !blankText.Contains("38;2;") && !blankText.Contains("38;5;"), "");
            check("term_blank_spaces", CountOccurrences(blankText, " ") == 8 * 4, "");

            // 9. 极端窗口尺寸不能越界
            bool extremeOk = true;
            try
            {
                GridSpec big = MAKE_GRID(500, 200, 4.0);
                var bigFrame = new CellFrame();
                bigFrame.Resize(big.Cols, big.Rows, big.VScale);
                SAMPLE_FRAME(white, bigFrame);
                COMPOSE_FRAME(bigFrame, atlas, new byte[big.Cols * CHAR_W * big.Rows * CHAR_H * 3],
                    big.Cols * CHAR_W * 3);
            }
            catch (Exception ex)
            {
                extremeOk = false;
                Console.WriteLine("  异常: " + ex.Message);
            }
            check("extreme_grid_safe", extremeOk, "");

            Console.WriteLine();
            Console.WriteLine($"自检: {pass} 通过, {fail} 失败");
            return fail == 0 ? EXIT_OK : EXIT_FAIL;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int pos = 0;
            while ((pos = haystack.IndexOf(needle, pos, StringComparison.Ordinal)) >= 0)
            {
                count++;
                pos += needle.Length;
            }
            return count;
        }
    }
}
