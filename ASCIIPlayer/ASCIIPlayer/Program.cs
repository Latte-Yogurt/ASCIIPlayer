using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ASCIIPlayer
{
    internal class Program
    {
        // ffmpeg 输出的固定像素流尺寸。640x360 是 16:9，覆盖常见终端分辨率够用
        private const int STREAM_WIDTH = 640;
        private const int STREAM_HEIGHT = 360;

        private const int FPS = 10;

        // 暗部提亮一点，不然低亮度区域全被压成空格
        private const double GAMMA = 0.85;

        // 抖动幅度，用来把离散的字符阶梯抹平一点
        private const double DITHER_STRENGTH = 0.5;

        // 渲染视频时每个字符占的像素格。8x16 是常见等宽字体的比例
        private const int CHAR_W = 8;
        private const int CHAR_H = 16;

        // 由疏到密的字符表
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

        private readonly static string _extPath = Path.GetTempPath();
        private readonly static string _ffmpegPath = Path.Combine(_extPath, "ffmpeg.exe");

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

        // 居中窗口用到的几个
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const int STD_OUTPUT_HANDLE = -11;
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

        // ANSI 转义
        private const string ANSI_HOME = "\x1b[H";
        private const string ANSI_CLEAR = "\x1b[2J\x1b[H";
        private const string ANSI_HIDE_CURSOR = "\x1b[?25l";
        private const string ANSI_SHOW_CURSOR = "\x1b[?25h";
        private const string ANSI_RESET = "\x1b[0m";

        static void Main(string[] args)
        {
            Console.Title = "ASCIIPlayer";
            Console.OutputEncoding = Encoding.UTF8;

            // 启动就把窗口挪到屏幕中间。稍微等一拍，让控制台先把初始尺寸定下来
            Thread.Sleep(50);
            CENTER_CONSOLE();

            if (args.Length == 0)
            {
                PRINT_USAGE();
                Console.Write("按任意键退出...");
                Console.ReadKey(true);
                return;
            }

            string videoPath = null;
            string renderPath = null;
            bool playWhileRender = false;
            int outResW = 0, outResH = 0;

            // 第一个非选项参数当视频路径，其他按选项处理
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];

                if (a == "-r" || a == "--render")
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.WriteLine("错误: -r 后面要跟输出视频路径");
                        return;
                    }
                    renderPath = args[++i];
                }
                else if (a == "-n" || a == "--size")
                {
                    if (i + 1 >= args.Length)
                    {
                        Console.WriteLine("错误: -n 后面要跟输出分辨率，例如 1920x1080");
                        return;
                    }
                    string sizeStr = args[++i];
                    if (!PARSE_SIZE(sizeStr, out outResW, out outResH))
                    {
                        Console.WriteLine($"错误: 尺寸格式不对: {sizeStr}，应该形如 1920x1080");
                        return;
                    }
                }
                else if (a == "--play")
                {
                    playWhileRender = true;
                }
                else if (a == "-h" || a == "--help")
                {
                    PRINT_USAGE();
                    return;
                }
                else if (videoPath == null && !a.StartsWith("-"))
                {
                    videoPath = a;
                }
                else
                {
                    Console.WriteLine($"未知参数: {a}");
                    PRINT_USAGE();
                    return;
                }
            }

            if (string.IsNullOrEmpty(videoPath))
            {
                PRINT_USAGE();
                Console.Write("按任意键退出...");
                Console.ReadKey(true);
                return;
            }

            if (!File.Exists(videoPath))
            {
                Console.WriteLine($"找不到文件: {videoPath}");
                Console.Write("按任意键退出...");
                Console.ReadKey(true);
                return;
            }

            CREATE_COMPONENTS();

            if (!CHECK_FFMPEG_EXIST())
            {
                Console.WriteLine("ffmpeg 提取失败，无法继续。");
                Console.Write("按任意键退出...");
                Console.ReadKey(true);
                return;
            }

            try
            {
                if (string.IsNullOrEmpty(renderPath))
                {
                    // 没有 -r 就走终端播放
                    PLAY_ASCII(videoPath);
                }
                else
                {
                    // 有 -r 就渲染成视频文件
                    PROCESS_VIDEO(videoPath, renderPath, playWhileRender, outResW, outResH);
                    Console.WriteLine($"已输出: {renderPath}");
                    Console.Write("按任意键退出...");
                    Console.ReadKey(true);
                }
            }
            catch (Exception ex)
            {
                Console.CursorVisible = true;
                Console.WriteLine();
                Console.WriteLine($"出错: {ex.Message}");
                Console.Write("按任意键退出...");
                Console.ReadKey(true);
            }
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

            if (!int.TryParse(s.Substring(0, xPos), out w)) return false;
            if (!int.TryParse(s.Substring(xPos + 1), out h)) return false;

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
            Console.WriteLine("  --play                配合 -r 使用，编码的同时也在终端播放");
            Console.WriteLine("  -h, --help            显示本帮助");
            Console.WriteLine();
            Console.WriteLine("示例:");
            Console.WriteLine("  ASCIIPlayer.exe video.mp4                        终端播放");
            Console.WriteLine("  ASCIIPlayer.exe video.mp4 -r out.mp4             输出 mp4");
            Console.WriteLine("  ASCIIPlayer.exe video.mp4 -r out.mp4 -n 1920x1080");
            Console.WriteLine("  ASCIIPlayer.exe video.mp4 -r out.mkv -n 2560x1440");
            Console.WriteLine("  ASCIIPlayer.exe video.mp4 -r out.mp4 --play      输出并播放");
            Console.WriteLine();
        }

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
                    Console.Clear();
                    Console.WriteLine();
                    Console.Write($"没有找到资源{resourceName}，按任意键继续...");
                    Console.ReadLine();
                    Console.Clear();
                    return;
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

        private static void PLAY_ASCII(string videoPath)
        {
            ENABLE_VT_PROCESSING();

            Thread.Sleep(200);

            // 提取音频
            string audioPath = Path.Combine(
                _extPath,
                "asciiplayer_audio_" + Guid.NewGuid().ToString("N") + ".wav");

            SoundPlayer player = null;
            try
            {
                if (EXTRACT_AUDIO(videoPath, audioPath))
                {
                    player = new SoundPlayer(audioPath);
                    player.Load();
                }
            }
            catch
            {
                player = null;
            }

            try
            {
                string filter =
                    $"fps={FPS}," +
                    $"scale={STREAM_WIDTH}:{STREAM_HEIGHT}:force_original_aspect_ratio=decrease," +
                    $"pad={STREAM_WIDTH}:{STREAM_HEIGHT}:(ow-iw)/2:(oh-ih)/2";

                string arguments = $"-i \"{videoPath}\" -vf \"{filter}\" -f rawvideo -pix_fmt rgb24 -";

                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = null
                };

                Stream stdoutConsole = Console.OpenStandardOutput();

                WRITE_RAW(stdoutConsole, ANSI_HIDE_CURSOR);

                using (var proc = Process.Start(psi))
                {
                    Stream stdout = proc.StandardOutput.BaseStream;

                    var stderrThread = new Thread(() =>
                    {
                        try { proc.StandardError.ReadToEnd(); } catch { }
                    })
                    { IsBackground = true };
                    stderrThread.Start();

                    try { player?.Play(); } catch { }

                    int frameSize = STREAM_WIDTH * STREAM_HEIGHT * 3;
                    byte[] buffer = new byte[frameSize];

                    var sb = new StringBuilder(400 * 120 * 14 + 100 * 16 + 16);
                    int frameDelay = 1000 / FPS;
                    int rampLast = Ramp.Length - 1;

                    int lastWinW = -1, lastWinH = -1;

                    var sw = Stopwatch.StartNew();
                    long frameIndex = 0;

                    while (true)
                    {
                        int read = READ_FULL(stdout, buffer, frameSize);
                        if (read < frameSize) break;

                        int winW, winH;
                        GET_VISIBLE_CONSOLE_SIZE(out winW, out winH);

                        if (winW < 10 || winH < 3)
                        {
                            // 窗口太小或不可用，跳过这一帧
                            Thread.Sleep(frameDelay);
                            continue;
                        }

                        int asciiWidth = winW;
                        int asciiHeight = winH - 1;   // 留一行防滚动

                        // 窗口尺寸变了，清一次屏覆盖旧内容
                        if (winW != lastWinW || winH != lastWinH)
                        {
                            WRITE_RAW(stdoutConsole, ANSI_CLEAR);
                            lastWinW = winW;
                            lastWinH = winH;
                        }

                        int vW = asciiWidth;
                        int vH = asciiHeight * 2;

                        sb.Clear();
                        sb.Append(ANSI_HOME);

                        for (int cy = 0; cy < asciiHeight; cy++)
                        {
                            int vyTop = cy * 2;
                            int vyBot = cy * 2 + 1;

                            int pyTop = vyTop * STREAM_HEIGHT / vH;
                            int pyBot = vyBot * STREAM_HEIGHT / vH;
                            if (pyTop >= STREAM_HEIGHT) pyTop = STREAM_HEIGHT - 1;
                            if (pyBot >= STREAM_HEIGHT) pyBot = STREAM_HEIGHT - 1;

                            int rowTopBase = pyTop * STREAM_WIDTH;
                            int rowBotBase = pyBot * STREAM_WIDTH;
                            int bayerRow = cy & 7;

                            for (int cx = 0; cx < asciiWidth; cx++)
                            {
                                int px = cx * STREAM_WIDTH / vW;
                                if (px >= STREAM_WIDTH) px = STREAM_WIDTH - 1;

                                int ti = (rowTopBase + px) * 3;
                                int bi = (rowBotBase + px) * 3;

                                int r = (buffer[ti] + buffer[bi]) >> 1;
                                int g = (buffer[ti + 1] + buffer[bi + 1]) >> 1;
                                int b = (buffer[ti + 2] + buffer[bi + 2]) >> 1;

                                int brightness = (r * 30 + g * 59 + b * 11) / 100;

                                int bayer = BAYER8[bayerRow, cx & 7] - 32;
                                int dithered = brightness + (int)(bayer * DITHER_STRENGTH);

                                if (dithered < 0) dithered = 0;
                                else if (dithered > 255) dithered = 255;

                                double norm = dithered / 255.0;
                                int idx = (int)(Math.Pow(norm, GAMMA) * rampLast);
                                if (idx < 0) idx = 0;
                                else if (idx > rampLast) idx = rampLast;

                                char c = Ramp[idx];

                                if (c == ' ')
                                {
                                    sb.Append(' ');
                                }
                                else
                                {
                                    int color = RGB_TO_256(r, g, b);
                                    sb.Append("\x1b[38;5;").Append(color).Append('m');
                                    sb.Append(c);
                                }
                            }

                            sb.Append(ANSI_RESET);

                            if (cy < asciiHeight - 1)
                                sb.Append("\r\n");
                        }

                        try
                        {
                            WRITE_RAW(stdoutConsole, sb.ToString());
                        }
                        catch
                        {
                            break;
                        }

                        frameIndex++;
                        long targetMs = frameIndex * frameDelay;
                        long elapsed = sw.ElapsedMilliseconds;
                        int sleep = (int)(targetMs - elapsed);
                        if (sleep > 0) Thread.Sleep(sleep);
                    }

                    try { proc.WaitForExit(2000); } catch { }
                }

                try { WRITE_RAW(stdoutConsole, ANSI_RESET + ANSI_SHOW_CURSOR); } catch { }
            }
            finally
            {
                try { player?.Stop(); } catch { }
                try { player?.Dispose(); } catch { }
                try { if (File.Exists(audioPath)) File.Delete(audioPath); } catch { }

                Console.CursorVisible = true;
            }
        }

        /// <summary>
        /// 把视频渲染成 ASCII 动画并编码成 mp4/mkv
        /// </summary>
        private static void PROCESS_VIDEO(string videoPath, string renderPath, bool playWhileRender, int userResW, int userResH)
        {
            bool rendering = !string.IsNullOrEmpty(renderPath);
            // 没有 -r 就是纯播放；有 -r 且带 --play 就是边渲染边播放
            bool playing = !rendering || playWhileRender;

            int asciiWidth, asciiHeight;

            // 用户给了分辨率就按分辨率反推字符网格
            bool userGaveRes = (userResW > 0 && userResH > 0);

            if (rendering && userGaveRes)
            {
                // 向下取整到字符格的整数倍，保证视频分辨率正好
                asciiWidth = Math.Max(20, userResW / CHAR_W);
                asciiHeight = Math.Max(5, userResH / CHAR_H);
            }
            else
            {
                // 没给分辨率：把窗口挪到屏幕中央，量出当前字符网格
                if (playing || rendering)
                {
                    ENABLE_VT_PROCESSING();
                    CENTER_CONSOLE();
                    WAIT_FOR_WINDOW_STABLE();
                }

                int winW, winH;
                GET_VISIBLE_CONSOLE_SIZE(out winW, out winH);
                asciiWidth = winW;
                asciiHeight = winH - 1;
            }

            if (asciiWidth < 20) asciiWidth = 20;
            if (asciiHeight < 5) asciiHeight = 5;

            // 纯播放时用 SoundPlayer 放声音。渲染模式走 ffmpeg 混音，不用这个
            string audioPath = null;
            SoundPlayer player = null;
            if (playing && !rendering)
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
                    }
                }
                catch
                {
                    player = null;
                }
            }

            // 渲染相关资源
            Bitmap bmp = null;
            Graphics g = null;
            Font font = null;
            SolidBrush[] brushes = null;
            Process ffProc = null;
            Stream ffIn = null;
            byte[] renderBytes = null;
            int videoW = 0, videoH = 0;

            if (rendering)
            {
                videoW = asciiWidth * CHAR_W;
                videoH = asciiHeight * CHAR_H;
                if (videoW % 2 != 0) videoW++;
                if (videoH % 2 != 0) videoH++;

                // 字体大小跟着字符格走。14px 大致能填满 8x16 的格子
                float fontSize = Math.Max(8f, CHAR_H * 0.875f);
                font = new Font("Consolas", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
                bmp = new Bitmap(videoW, videoH, PixelFormat.Format24bppRgb);
                g = Graphics.FromImage(bmp);
                g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
                g.Clear(Color.Black);

                // 256 色的画刷缓存，省得每个字符 new 一个
                brushes = new SolidBrush[256];
                for (int i = 0; i < 256; i++)
                {
                    brushes[i] = new SolidBrush(XTERM256_TO_RGB(i));
                }

                renderBytes = new byte[videoW * videoH * 3];

                // ffmpeg 从 stdin 收渲染好的帧，从原视频读音轨，编码成 h264 + aac
                // GDI+ 的 24bppRgb 在内存里是 BGR 顺序，所以输入格式写 bgr24，
                // 不能写 rgb24，不然红蓝会互换
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments =
                        $"-y " +
                        $"-f rawvideo -pix_fmt bgr24 -s {videoW}x{videoH} -r {FPS} -i - " +
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

                var ffErrThread = new Thread(() =>
                {
                    try { ffProc.StandardError.ReadToEnd(); } catch { }
                })
                { IsBackground = true };
                ffErrThread.Start();

                Console.WriteLine($"字符网格: {asciiWidth} x {asciiHeight}");
                Console.WriteLine($"视频分辨率: {videoW} x {videoH}");
            }

            Stream stdoutConsole = null;
            if (playing)
            {
                stdoutConsole = Console.OpenStandardOutput();
                WRITE_RAW(stdoutConsole, ANSI_HIDE_CURSOR);
                WRITE_RAW(stdoutConsole, ANSI_CLEAR);
            }

            try
            {
                string filter =
                    $"fps={FPS}," +
                    $"scale={STREAM_WIDTH}:{STREAM_HEIGHT}:force_original_aspect_ratio=decrease," +
                    $"pad={STREAM_WIDTH}:{STREAM_HEIGHT}:(ow-iw)/2:(oh-ih)/2";

                // 源视频流用 rgb24，和 ffmpeg 内部约定一致
                var srcPsi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = $"-i \"{videoPath}\" -vf \"{filter}\" -f rawvideo -pix_fmt rgb24 -",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = null
                };

                using (var src = Process.Start(srcPsi))
                {
                    var srcErrThread = new Thread(() =>
                    {
                        try { src.StandardError.ReadToEnd(); } catch { }
                    })
                    { IsBackground = true };
                    srcErrThread.Start();

                    if (player != null)
                    {
                        try { player.Play(); } catch { }
                    }

                    Stream stdout = src.StandardOutput.BaseStream;
                    int frameSize = STREAM_WIDTH * STREAM_HEIGHT * 3;
                    byte[] buffer = new byte[frameSize];

                    var sb = new StringBuilder(asciiWidth * asciiHeight * 14 + asciiHeight * 16 + 16);
                    int frameDelay = 1000 / FPS;
                    int rampLast = Ramp.Length - 1;
                    int vW = asciiWidth;
                    int vH = asciiHeight * 2;

                    var sw = Stopwatch.StartNew();
                    long frameIndex = 0;

                    while (true)
                    {
                        int read = READ_FULL(stdout, buffer, frameSize);
                        if (read < frameSize) break;

                        // 渲染到 Bitmap 并喂给 ffmpeg
                        if (rendering)
                        {
                            g.Clear(Color.Black);

                            for (int cy = 0; cy < asciiHeight; cy++)
                            {
                                int vyTop = cy * 2;
                                int vyBot = cy * 2 + 1;

                                int pyTop = vyTop * STREAM_HEIGHT / vH;
                                int pyBot = vyBot * STREAM_HEIGHT / vH;
                                if (pyTop >= STREAM_HEIGHT) pyTop = STREAM_HEIGHT - 1;
                                if (pyBot >= STREAM_HEIGHT) pyBot = STREAM_HEIGHT - 1;

                                int rowTopBase = pyTop * STREAM_WIDTH;
                                int rowBotBase = pyBot * STREAM_WIDTH;
                                int bayerRow = cy & 7;

                                for (int cx = 0; cx < asciiWidth; cx++)
                                {
                                    int px = cx * STREAM_WIDTH / vW;
                                    if (px >= STREAM_WIDTH) px = STREAM_WIDTH - 1;

                                    int ti = (rowTopBase + px) * 3;
                                    int bi = (rowBotBase + px) * 3;

                                    int r = (buffer[ti] + buffer[bi]) >> 1;
                                    int gg = (buffer[ti + 1] + buffer[bi + 1]) >> 1;
                                    int b = (buffer[ti + 2] + buffer[bi + 2]) >> 1;

                                    int brightness = (r * 30 + gg * 59 + b * 11) / 100;

                                    int bayer = BAYER8[bayerRow, cx & 7] - 32;
                                    int dithered = brightness + (int)(bayer * DITHER_STRENGTH);
                                    if (dithered < 0) dithered = 0;
                                    else if (dithered > 255) dithered = 255;

                                    double norm = dithered / 255.0;
                                    int idx = (int)(Math.Pow(norm, GAMMA) * rampLast);
                                    if (idx < 0) idx = 0;
                                    else if (idx > rampLast) idx = rampLast;

                                    char c = Ramp[idx];
                                    if (c == ' ') continue;

                                    int color = RGB_TO_256(r, gg, b);
                                    g.DrawString(c.ToString(), font, brushes[color],
                                        cx * CHAR_W, cy * CHAR_H);
                                }
                            }

                            // 拷位图到字节数组。stride 有可能大于 width*3，因为按 4 字节对齐
                            BitmapData data = bmp.LockBits(
                                new Rectangle(0, 0, videoW, videoH),
                                ImageLockMode.ReadOnly,
                                PixelFormat.Format24bppRgb);
                            try
                            {
                                int stride = data.Stride;
                                int rowBytes = videoW * 3;
                                IntPtr scan0 = data.Scan0;

                                if (stride == rowBytes)
                                {
                                    Marshal.Copy(scan0, renderBytes, 0, stride * videoH);
                                }
                                else
                                {
                                    for (int y = 0; y < videoH; y++)
                                    {
                                        IntPtr rowPtr = IntPtr.Add(scan0, y * stride);
                                        Marshal.Copy(rowPtr, renderBytes, y * rowBytes, rowBytes);
                                    }
                                }
                            }
                            finally
                            {
                                bmp.UnlockBits(data);
                            }

                            ffIn.Write(renderBytes, 0, renderBytes.Length);

                            if (frameIndex % 30 == 0)
                            {
                                Console.Write($"\r已渲染 {frameIndex} 帧 ({sw.Elapsed.TotalSeconds:F1}s)");
                            }
                        }

                        // 终端播放。网格在启动时就定了，直接用
                        if (playing)
                        {
                            sb.Clear();
                            sb.Append(ANSI_HOME);

                            for (int cy = 0; cy < asciiHeight; cy++)
                            {
                                int vyTop = cy * 2;
                                int vyBot = cy * 2 + 1;

                                int pyTop = vyTop * STREAM_HEIGHT / vH;
                                int pyBot = vyBot * STREAM_HEIGHT / vH;
                                if (pyTop >= STREAM_HEIGHT) pyTop = STREAM_HEIGHT - 1;
                                if (pyBot >= STREAM_HEIGHT) pyBot = STREAM_HEIGHT - 1;

                                int rowTopBase = pyTop * STREAM_WIDTH;
                                int rowBotBase = pyBot * STREAM_WIDTH;
                                int bayerRow = cy & 7;

                                for (int cx = 0; cx < asciiWidth; cx++)
                                {
                                    int px = cx * STREAM_WIDTH / vW;
                                    if (px >= STREAM_WIDTH) px = STREAM_WIDTH - 1;

                                    int ti = (rowTopBase + px) * 3;
                                    int bi = (rowBotBase + px) * 3;

                                    int r = (buffer[ti] + buffer[bi]) >> 1;
                                    int gg = (buffer[ti + 1] + buffer[bi + 1]) >> 1;
                                    int b = (buffer[ti + 2] + buffer[bi + 2]) >> 1;

                                    int brightness = (r * 30 + gg * 59 + b * 11) / 100;

                                    int bayer = BAYER8[bayerRow, cx & 7] - 32;
                                    int dithered = brightness + (int)(bayer * DITHER_STRENGTH);
                                    if (dithered < 0) dithered = 0;
                                    else if (dithered > 255) dithered = 255;

                                    double norm = dithered / 255.0;
                                    int idx = (int)(Math.Pow(norm, GAMMA) * rampLast);
                                    if (idx < 0) idx = 0;
                                    else if (idx > rampLast) idx = rampLast;

                                    char c = Ramp[idx];

                                    if (c == ' ')
                                    {
                                        sb.Append(' ');
                                    }
                                    else
                                    {
                                        int color = RGB_TO_256(r, gg, b);
                                        sb.Append("\x1b[38;5;").Append(color).Append('m');
                                        sb.Append(c);
                                    }
                                }

                                sb.Append(ANSI_RESET);

                                if (cy < asciiHeight - 1)
                                    sb.Append("\r\n");
                            }

                            try
                            {
                                WRITE_RAW(stdoutConsole, sb.ToString());
                            }
                            catch
                            {
                                // 终端关了就停了，渲染还得继续
                            }
                        }

                        // 时间轴对齐
                        frameIndex++;
                        if (playing)
                        {
                            long targetMs = frameIndex * frameDelay;
                            long elapsed = sw.ElapsedMilliseconds;
                            int sleep = (int)(targetMs - elapsed);
                            if (sleep > 0) Thread.Sleep(sleep);
                        }
                    }

                    if (rendering)
                    {
                        Console.WriteLine();
                    }

                    // 关掉 ffmpeg stdin 通知它帧发完了
                    if (ffIn != null)
                    {
                        try { ffIn.Flush(); ffIn.Close(); } catch { }
                    }

                    try { src.WaitForExit(2000); } catch { }
                }

                if (playing)
                {
                    try { WRITE_RAW(stdoutConsole, ANSI_RESET + ANSI_SHOW_CURSOR); } catch { }
                }
            }
            finally
            {
                // 等编码器收尾
                if (ffProc != null)
                {
                    try { ffProc.WaitForExit(); } catch { }
                    try { ffProc.Dispose(); } catch { }
                }

                if (brushes != null)
                {
                    for (int i = 0; i < 256; i++)
                    {
                        try { brushes[i].Dispose(); } catch { }
                    }
                }

                try { g?.Dispose(); } catch { }
                try { bmp?.Dispose(); } catch { }
                try { font?.Dispose(); } catch { }

                try { player?.Stop(); } catch { }
                try { player?.Dispose(); } catch { }
                try { if (audioPath != null && File.Exists(audioPath)) File.Delete(audioPath); } catch { }

                if (playing)
                {
                    Console.CursorVisible = true;
                }
            }
        }

        /// <summary>
        /// 把控制台窗口挪到屏幕正中间，尺寸不变
        /// </summary>
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

        private static int RGB_TO_256(int r, int g, int b)
        {
            // 6x6x6 色立方，落在 16..231
            int ri = r * 5 / 255;
            int gi = g * 5 / 255;
            int bi = b * 5 / 255;
            return 16 + 36 * ri + 6 * gi + bi;
        }

        /// <summary>
        /// xterm 256 色号转成 Color，用来给字符上色。
        /// 16..231 是 6x6x6 色立方，232..255 是灰阶。
        /// </summary>
        private static Color XTERM256_TO_RGB(int idx)
        {
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

        private static void ENABLE_VT_PROCESSING()
        {
            try
            {
                IntPtr h = GetStdHandle(STD_OUTPUT_HANDLE);
                if (h == IntPtr.Zero || h == new IntPtr(-1)) return;

                if (GetConsoleMode(h, out uint mode))
                {
                    SetConsoleMode(h, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
                }
            }
            catch { }
        }

        private static void WRITE_RAW(Stream stream, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static bool EXTRACT_AUDIO(string videoPath, string audioPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = $"-y -i \"{videoPath}\" -vn -ac 2 -ar 44100 -f wav \"{audioPath}\"",
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
    }
}