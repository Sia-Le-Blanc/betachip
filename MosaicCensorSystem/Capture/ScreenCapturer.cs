using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using OpenCvSharp;

namespace MosaicCensorSystem.Capture
{
    /// <summary>
    /// MSS 라이브러리를 사용한 고성능 화면 캡처 모듈
    /// </summary>
    public class ScreenCapturer : ICapturer, IDisposable
    {
        // Windows API
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

        // 설정
        private readonly Dictionary<string, object> config;
        private readonly double captureDownscale;
        private readonly bool debugMode;
        private readonly int debugSaveInterval;

        // 화면 정보
        private readonly int screenWidth;
        private readonly int screenHeight;
        private readonly int screenLeft;
        private readonly int screenTop;
        private readonly int captureWidth;
        private readonly int captureHeight;

        // 캡처 영역
        private readonly Rectangle monitor;

        // 이전 프레임
        private Mat prevFrame;

        // 프레임 카운터
        private int frameCount = 0;

        // 프레임 큐 및 스레드
        private readonly BlockingCollection<Mat> frameQueue;
        private readonly CancellationTokenSource cancellationTokenSource;
        private Thread captureThread;

        // 제외 영역
        private IntPtr excludeHwnd = IntPtr.Zero;
        private readonly List<Rectangle> excludeRegions = new List<Rectangle>();

        // 디버깅
        private readonly string debugDir = "debug_captures";

        public ScreenCapturer(Dictionary<string, object> config = null)
        {
            // 설정 가져오기
            this.config = config ?? Config.GetSection("capture");
            
            captureDownscale = Convert.ToDouble(this.config.GetValueOrDefault("downscale", 1.0));
            debugMode = Convert.ToBoolean(this.config.GetValueOrDefault("debug_mode", false));
            debugSaveInterval = Convert.ToInt32(this.config.GetValueOrDefault("debug_save_interval", 300));

            // 화면 정보 초기화
            screenWidth = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
            screenHeight = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
            screenLeft = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Left;
            screenTop = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Top;

            // 캡처 크기 계산
            captureWidth = (int)(screenWidth * captureDownscale);
            captureHeight = (int)(screenHeight * captureDownscale);

            Console.WriteLine($"✅ 화면 해상도: {screenWidth}x{screenHeight}, 캡처 크기: {captureWidth}x{captureHeight}");

            // 캡처 영역 설정
            monitor = new Rectangle(screenLeft, screenTop, screenWidth, screenHeight);

            // 프레임 큐 및 스레드 설정
            int queueSize = Convert.ToInt32(this.config.GetValueOrDefault("queue_size", 2));
            frameQueue = new BlockingCollection<Mat>(queueSize);
            cancellationTokenSource = new CancellationTokenSource();

            // 디버깅 디렉토리 생성
            if (debugMode)
            {
                Directory.CreateDirectory(debugDir);
            }

        // BitmapConverter.ToMat의 대체 구현
        private Mat BitmapToMat(Bitmap bitmap)
        {
            BitmapData bmpData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly,
                bitmap.PixelFormat);

            try
            {
                Mat mat = new Mat(bitmap.Height, bitmap.Width, MatType.CV_8UC3);
                unsafe
                {
                    byte* src = (byte*)bmpData.Scan0.ToPointer();
                    byte* dst = (byte*)mat.DataPointer;
                    
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            int srcIdx = y * bmpData.Stride + x * 3;
                            int dstIdx = (y * bitmap.Width + x) * 3;
                            
                            // BGR 순서 유지
                            dst[dstIdx] = src[srcIdx];         // B
                            dst[dstIdx + 1] = src[srcIdx + 1]; // G
                            dst[dstIdx + 2] = src[srcIdx + 2]; // R
                        }
                    }
                }
                return mat;
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
        }

            // 캡처 스레드 시작
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
                    // 캡처 간격 제어 (최대 FPS 제한)
                    var elapsed = (DateTime.Now - lastFrameTime).TotalSeconds;
                    if (elapsed < 0.01) // 최대 약 100 FPS
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    // 화면 캡처 시도
                    var frame = CaptureScreen();
                    lastFrameTime = DateTime.Now;

                    if (frame != null && !frame.Empty())
                    {
                        frameCount++;
                        
                        // 프레임 큐가 가득 차면 이전 프레임 제거
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
                // 데스크톱 DC 가져오기
                desktopDC = GetWindowDC(GetDesktopWindow());
                memoryDC = CreateCompatibleDC(desktopDC);

                // 비트맵 생성
                bitmap = CreateCompatibleBitmap(desktopDC, screenWidth, screenHeight);
                oldBitmap = SelectObject(memoryDC, bitmap);

                // 화면 캡처
                BitBlt(memoryDC, 0, 0, screenWidth, screenHeight, desktopDC, screenLeft, screenTop, SRCCOPY);

                // Bitmap으로 변환
                using (var screenBitmap = Image.FromHbitmap(bitmap))
                {
                    // OpenCV Mat으로 변환 - BitmapConverter 대신 수동 변환
                    Bitmap bmp = (Bitmap)screenBitmap;
                    Mat img = BitmapToMat(bmp);

                    // BGR 형식으로 변환 (필요한 경우)
                    if (img.Channels() == 4)
                    {
                        Cv2.CvtColor(img, img, ColorConversionCodes.BGRA2BGR);
                    }

                    // 성능 최적화: 필요한 경우만 다운스케일
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

                            // 검은색으로 채우기
                            img[new Rect(region.X, region.Y, endX - region.X, endY - region.Y)] = new Scalar(0, 0, 0);
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
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ 화면 캡처 오류: {e.Message}");
                return null;
            }
            finally
            {
                // 리소스 정리
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
                // 큐에서 프레임 가져오기
                if (frameQueue.TryTake(out var frame, 100))
                {
                    // 프레임 저장
                    prevFrame?.Dispose();
                    prevFrame = frame.Clone();

                    // 주기적인 로그 출력
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
            
            // 큐에 남은 프레임 정리
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