using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace MosaicCensorSystem.Capture
{
    public class ScreenCapturer : ICapturer, IDisposable
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();
        
        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr hWnd);
        
        [DllImport("user32.dll")]
        private static extern IntPtr ReleaseDC(IntPtr hWnd, IntPtr hDC);
        
        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hObject, int nXDest, int nYDest, int nWidth, int nHeight, 
            IntPtr hObjectSource, int nXSrc, int nYSrc, int dwRop);
        
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);
        
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
        
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        private const int SRCCOPY = 0x00CC0020;

        private readonly Dictionary<string, object> config;
        private readonly double captureDownscale;
        private readonly bool debugMode;
        private readonly int debugSaveInterval;

        private readonly int screenWidth;
        private readonly int screenHeight;
        private readonly int screenLeft;
        private readonly int screenTop;
        private readonly int captureWidth;
        private readonly int captureHeight;

        private readonly Rectangle monitor;
        private Mat prevFrame;
        private int frameCount = 0;

        private readonly BlockingCollection<Mat> frameQueue;
        private readonly CancellationTokenSource cancellationTokenSource;
        private Thread captureThread;

        private IntPtr excludeHwnd = IntPtr.Zero;
        private readonly List<Rectangle> excludeRegions = new List<Rectangle>();
        private readonly string debugDir = "debug_captures";

        public ScreenCapturer(Dictionary<string, object> config = null)
        {
            this.config = config ?? Config.GetSection("capture");
            
            captureDownscale = Convert.ToDouble(this.config.GetValueOrDefault("downscale", 1.0));
            debugMode = Convert.ToBoolean(this.config.GetValueOrDefault("debug_mode", false));
            debugSaveInterval = Convert.ToInt32(this.config.GetValueOrDefault("debug_save_interval", 300));

            screenWidth = Screen.PrimaryScreen.Bounds.Width;
            screenHeight = Screen.PrimaryScreen.Bounds.Height;
            screenLeft = Screen.PrimaryScreen.Bounds.Left;
            screenTop = Screen.PrimaryScreen.Bounds.Top;

            captureWidth = (int)(screenWidth * captureDownscale);
            captureHeight = (int)(screenHeight * captureDownscale);

            Console.WriteLine($"✅ 화면 해상도: {screenWidth}x{screenHeight}, 캡처 크기: {captureWidth}x{captureHeight}");

            monitor = new Rectangle(screenLeft, screenTop, screenWidth, screenHeight);

            int queueSize = Convert.ToInt32(this.config.GetValueOrDefault("queue_size", 2));
            frameQueue = new BlockingCollection<Mat>(queueSize);
            cancellationTokenSource = new CancellationTokenSource();

            if (debugMode)
            {
                Directory.CreateDirectory(debugDir);
            }

            StartCaptureThread();
        }

        public void SetExcludeHwnd(IntPtr hwnd)
        {
            excludeHwnd = hwnd;
            Console.WriteLine($"✅ 제외 윈도우 핸들 설정: {hwnd}");
        }

        public void AddExcludeRegion(int x, int y, int width, int height)
        {
            excludeRegions.Add(new Rectangle(x, y, width, height));
            Console.WriteLine($"✅ 제외 영역 추가: ({x}, {y}, {width}, {height})");
        }

        public void ClearExcludeRegions()
        {
            excludeRegions.Clear();
        }

        public void StartCaptureThread()
        {
            if (captureThread != null && captureThread.IsAlive)
            {
                Console.WriteLine("⚠️ 캡처 스레드가 이미 실행 중입니다.");
                return;
            }

            captureThread = new Thread(CaptureThreadFunc)
            {
                Name = "ScreenCaptureThread",
                Priority = ThreadPriority.Highest,
                IsBackground = true
            };
            captureThread.Start();
            Console.WriteLine("✅ 캡처 스레드 시작됨");
        }

        public void StopCaptureThread()
        {
            if (captureThread != null && captureThread.IsAlive)
            {
                cancellationTokenSource.Cancel();
                captureThread.Join(1000);
                Console.WriteLine("✅ 캡처 스레드 중지됨");
            }
        }

        private void CaptureThreadFunc()
        {
            Console.WriteLine("🔄 캡처 스레드 시작");
            var lastFrameTime = DateTime.Now;
            int retryCount = 0;

            while (!cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var elapsed = (DateTime.Now - lastFrameTime).TotalSeconds;
                    if (elapsed < 0.01)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    var frame = CaptureScreen();
                    lastFrameTime = DateTime.Now;

                    if (frame != null && !frame.Empty())
                    {
                        frameCount++;
                        
                        if (frameQueue.Count >= frameQueue.BoundedCapacity)
                        {
                            if (frameQueue.TryTake(out var oldFrame))
                            {
                                oldFrame?.Dispose();
                            }
                        }

                        frameQueue.TryAdd(frame.Clone());
                        frame.Dispose();
                        retryCount = 0;
                    }
                    else
                    {
                        retryCount++;
                        if (retryCount > 5)
                        {
                            Console.WriteLine($"⚠️ 연속 {retryCount}회 캡처 실패");
                            retryCount = 0;
                            Thread.Sleep(100);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"❌ 캡처 스레드 오류: {e.Message}");
                    retryCount++;
                    if (retryCount > 5)
                    {
                        retryCount = 0;
                    }
                    Thread.Sleep(100);
                }
            }

            Console.WriteLine("🛑 캡처 스레드 종료");
        }

        private Mat CaptureScreen()
        {
            IntPtr desktopDC = IntPtr.Zero;
            IntPtr memoryDC = IntPtr.Zero;
            IntPtr bitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                desktopDC = GetWindowDC(GetDesktopWindow());
                memoryDC = CreateCompatibleDC(desktopDC);

                bitmap = CreateCompatibleBitmap(desktopDC, screenWidth, screenHeight);
                oldBitmap = SelectObject(memoryDC, bitmap);

                BitBlt(memoryDC, 0, 0, screenWidth, screenHeight, desktopDC, screenLeft, screenTop, SRCCOPY);

                using (var screenBitmap = Image.FromHbitmap(bitmap))
                {
                    Bitmap bmp = (Bitmap)screenBitmap;
                    Mat img = BitmapConverter.ToMat(bmp);

                    if (img.Channels() == 4)
                    {
                        Cv2.CvtColor(img, img, ColorConversionCodes.BGRA2BGR);
                    }

                    if (Math.Abs(captureDownscale - 1.0) > 0.001)
                    {
                        Mat resized = new Mat();
                        Cv2.Resize(img, resized, new OpenCvSharp.Size(captureWidth, captureHeight), 
                            interpolation: InterpolationFlags.Nearest);
                        img.Dispose();
                        img = resized;
                    }

                    foreach (var region in excludeRegions)
                    {
                        if (region.X >= 0 && region.Y >= 0 && 
                            region.X < img.Width && region.Y < img.Height)
                        {
                            int endX = Math.Min(region.X + region.Width, img.Width);
                            int endY = Math.Min(region.Y + region.Height, img.Height);

                            img[new Rect(region.X, region.Y, endX - region.X, endY - region.Y)] = new Scalar(0, 0, 0);
                        }
                    }

                    if (debugMode && frameCount % debugSaveInterval == 0)
                    {
                        try
                        {
                            string debugPath = Path.Combine(debugDir, 
                                $"screen_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
                            Cv2.ImWrite(debugPath, img, new ImageEncodingParam(ImwriteFlags.JpegQuality, 80));
                            Console.WriteLine($"📸 디버깅용 화면 캡처 저장: {debugPath} (크기: {img.Size()})");
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine($"⚠️ 디버깅 캡처 저장 실패: {e.Message}");
                        }
                    }

                    return img;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ 화면 캡처 오류: {e.Message}");
                return null;
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero && memoryDC != IntPtr.Zero)
                    SelectObject(memoryDC, oldBitmap);
                if (bitmap != IntPtr.Zero)
                    DeleteObject(bitmap);
                if (memoryDC != IntPtr.Zero)
                    DeleteObject(memoryDC);
                if (desktopDC != IntPtr.Zero)
                    ReleaseDC(GetDesktopWindow(), desktopDC);
            }
        }

        public Mat GetFrame()
        {
            try
            {
                if (frameQueue.TryTake(out var frame, 100))
                {
                    prevFrame?.Dispose();
                    prevFrame = frame.Clone();

                    int logInterval = Convert.ToInt32(config.GetValueOrDefault("log_interval", 100));
                    if (frameCount % logInterval == 0)
                    {
                        Console.WriteLine($"📸 화면 캡처: 프레임 #{frameCount}, 크기: {frame.Size()}");
                    }

                    return frame;
                }

                if (prevFrame != null && !prevFrame.Empty())
                {
                    return prevFrame.Clone();
                }

                return CaptureScreen();
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ 프레임 가져오기 오류: {e.Message}");
                return prevFrame?.Clone();
            }
        }

        public void Dispose()
        {
            StopCaptureThread();
            
            while (frameQueue.TryTake(out var frame))
            {
                frame?.Dispose();
            }
            
            prevFrame?.Dispose();
            frameQueue?.Dispose();
            cancellationTokenSource?.Dispose();
        }
    }
}