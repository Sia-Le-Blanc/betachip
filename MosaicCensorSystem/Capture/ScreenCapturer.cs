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
    /// <summary>
    /// 화면 캡처 인터페이스
    /// </summary>
    public interface ICapturer
    {
        /// <summary>
        /// 프레임 가져오기
        /// </summary>
        Mat? GetFrame();

        /// <summary>
        /// 캡처 스레드 시작
        /// </summary>
        void StartCaptureThread();

        /// <summary>
        /// 캡처 스레드 중지
        /// </summary>
        void StopCaptureThread();

        /// <summary>
        /// 캡처에서 제외할 윈도우 핸들 설정
        /// </summary>
        void SetExcludeHwnd(IntPtr hwnd);

        /// <summary>
        /// 캡처에서 제외할 영역 추가
        /// </summary>
        void AddExcludeRegion(int x, int y, int width, int height);

        /// <summary>
        /// 제외 영역 모두 제거
        /// </summary>
        void ClearExcludeRegions();
    }

    public class ScreenCapturer : ICapturer, IDisposable
    {
        #region Windows API
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
        #endregion

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
        private Mat? prevFrame;
        private int frameCount = 0;

        private readonly BlockingCollection<Mat> frameQueue;
        private readonly CancellationTokenSource cancellationTokenSource;
        private Thread? captureThread;

        private IntPtr excludeHwnd = IntPtr.Zero;
        private readonly List<Rectangle> excludeRegions = new List<Rectangle>();
        private readonly string debugDir = "debug_captures";

        public ScreenCapturer(Dictionary<string, object>? config = null)
        {
            this.config = config ?? new Dictionary<string, object>();
            
            // 안전한 타입 변환
            captureDownscale = Convert.ToDouble(this.config.GetValueOrDefault("downscale", 1.0));
            debugMode = Convert.ToBoolean(this.config.GetValueOrDefault("debug_mode", false));
            debugSaveInterval = Convert.ToInt32(this.config.GetValueOrDefault("debug_save_interval", 300));

            // 전체 화면 크기 가져오기 (멀티 모니터 지원)
            screenLeft = SystemInformation.VirtualScreen.Left;
            screenTop = SystemInformation.VirtualScreen.Top;
            screenWidth = SystemInformation.VirtualScreen.Width;
            screenHeight = SystemInformation.VirtualScreen.Height;

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
            int consecutiveErrors = 0;
            const int maxConsecutiveErrors = 10;

            while (!cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    // 프레임 레이트 제한 (최대 100 FPS)
                    var elapsed = (DateTime.Now - lastFrameTime).TotalMilliseconds;
                    if (elapsed < 10) // 10ms = 100fps
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    var frame = CaptureScreen();
                    lastFrameTime = DateTime.Now;

                    if (frame != null && !frame.Empty())
                    {
                        frameCount++;
                        
                        // 큐가 가득 차면 이전 프레임 제거 (논블로킹)
                        while (frameQueue.Count >= frameQueue.BoundedCapacity)
                        {
                            if (frameQueue.TryTake(out var oldFrame, 1))
                            {
                                oldFrame?.Dispose();
                            }
                            else
                            {
                                break; // 타임아웃 시 루프 탈출
                            }
                        }

                        // 새 프레임 추가 (논블로킹)
                        if (!frameQueue.TryAdd(frame, 1))
                        {
                            // 큐에 추가 실패시 프레임 폐기
                            frame?.Dispose();
                        }
                        
                        retryCount = 0;
                        consecutiveErrors = 0;
                    }
                    else
                    {
                        retryCount++;
                        consecutiveErrors++;
                        
                        if (retryCount > 5)
                        {
                            Console.WriteLine($"⚠️ 연속 {retryCount}회 캡처 실패");
                            retryCount = 0;
                        }
                        
                        if (consecutiveErrors > maxConsecutiveErrors)
                        {
                            Console.WriteLine($"❌ 연속 {consecutiveErrors}회 오류 발생 - 캡처 스레드 일시 정지");
                            Thread.Sleep(1000); // 1초 대기 후 재시도
                            consecutiveErrors = 0;
                        }
                        else
                        {
                            Thread.Sleep(50); // 50ms 대기
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    // 정상적인 종료 상황
                    Console.WriteLine("🛑 캡처 객체가 해제됨 - 스레드 종료");
                    break;
                }
                catch (Exception e)
                {
                    consecutiveErrors++;
                    Console.WriteLine($"❌ 캡처 스레드 오류: {e.Message}");
                    
                    if (consecutiveErrors > maxConsecutiveErrors)
                    {
                        Console.WriteLine($"❌ 치명적 오류 - 캡처 스레드 종료");
                        break;
                    }
                    
                    Thread.Sleep(Math.Min(consecutiveErrors * 100, 1000)); // 점진적 대기
                }
            }

            Console.WriteLine("🛑 캡처 스레드 종료");
            
            // 남은 프레임들 정리
            try
            {
                while (frameQueue.TryTake(out var frame, 100))
                {
                    frame?.Dispose();
                }
            }
            catch (Exception cleanupEx)
            {
                Console.WriteLine($"⚠️ 캡처 스레드 정리 중 오류: {cleanupEx.Message}");
            }
        }

        private Mat? CaptureScreen()
        {
            IntPtr desktopDC = IntPtr.Zero;
            IntPtr memoryDC = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            Bitmap? screenBitmap = null;

            try
            {
                desktopDC = GetWindowDC(GetDesktopWindow());
                memoryDC = CreateCompatibleDC(desktopDC);

                hBitmap = CreateCompatibleBitmap(desktopDC, screenWidth, screenHeight);
                oldBitmap = SelectObject(memoryDC, hBitmap);

                BitBlt(memoryDC, 0, 0, screenWidth, screenHeight, desktopDC, screenLeft, screenTop, SRCCOPY);

                // 올바른 Bitmap 생성 방법
                screenBitmap = Bitmap.FromHbitmap(hBitmap);
                
                // Bitmap을 Mat로 변환
                Mat img = BitmapConverter.ToMat(screenBitmap);

                // BGRA -> BGR 변환 (필요한 경우)
                if (img.Channels() == 4)
                {
                    Mat bgr = new Mat();
                    Cv2.CvtColor(img, bgr, ColorConversionCodes.BGRA2BGR);
                    img.Dispose();
                    img = bgr;
                }

                // 다운스케일 (필요한 경우)
                if (Math.Abs(captureDownscale - 1.0) > 0.001)
                {
                    Mat resized = new Mat();
                    Cv2.Resize(img, resized, new OpenCvSharp.Size(captureWidth, captureHeight), 
                        interpolation: InterpolationFlags.Nearest);
                    img.Dispose();
                    img = resized;
                }

                // 제외 영역 마스킹
                foreach (var region in excludeRegions)
                {
                    if (region.X >= 0 && region.Y >= 0 && 
                        region.X < img.Width && region.Y < img.Height)
                    {
                        int endX = Math.Min(region.X + region.Width, img.Width);
                        int endY = Math.Min(region.Y + region.Height, img.Height);

                        if (endX > region.X && endY > region.Y)
                        {
                            var rect = new Rect(region.X, region.Y, endX - region.X, endY - region.Y);
                            img[rect].SetTo(new Scalar(0, 0, 0));
                        }
                    }
                }

                // 디버깅 모드: 주기적으로 화면 캡처 저장
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
            catch (Exception e)
            {
                Console.WriteLine($"❌ 화면 캡처 오류: {e.Message}");
                return null;
            }
            finally
            {
                // 리소스 정리
                screenBitmap?.Dispose();
                
                if (oldBitmap != IntPtr.Zero && memoryDC != IntPtr.Zero)
                    SelectObject(memoryDC, oldBitmap);
                if (hBitmap != IntPtr.Zero)
                    DeleteObject(hBitmap);
                if (memoryDC != IntPtr.Zero)
                    DeleteObject(memoryDC);
                if (desktopDC != IntPtr.Zero)
                    ReleaseDC(GetDesktopWindow(), desktopDC);
            }
        }

        public Mat? GetFrame()
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

                // 큐가 비었으면 이전 프레임 반환
                if (prevFrame != null && !prevFrame.Empty())
                {
                    return prevFrame.Clone();
                }

                // 이전 프레임도 없으면 직접 캡처 시도
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
            
            // 큐에 남은 프레임들 정리
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