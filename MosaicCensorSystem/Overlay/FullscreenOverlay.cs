using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using OpenCvSharp;

namespace MosaicCensorSystem.Overlay
{
    /// <summary>
    /// 풀스크린 + 캡처 방지 + 클릭 투과 모자이크 오버레이 윈도우
    /// </summary>
    public class FullscreenOverlay : Form, IOverlay
    {
        #region Windows API
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int GWL_EXSTYLE = -20;
        private const int LWA_ALPHA = 0x00000002;
        private const int HWND_TOPMOST = -1;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOACTIVATE = 0x0010;
        private const int SWP_SHOWWINDOW = 0x0040;
        private const int WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        // Windows Hook 상수
        private const int WH_CBT = 5;
        private const int HCBT_ACTIVATE = 5;

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll")]
        private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint pdwAffinity);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hInstance, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        #endregion

        // 설정
        private readonly Dictionary<string, object> config;
        public bool ShowDebugInfo { get; set; }
        private int fpsLimit = 30;

        // 상태 변수
        private bool isVisible = false;
        private bool isRunning = false;
        private Mat currentFrame = null;

        // 성능 통계
        private int fpsCounter = 0;
        private DateTime fpsStartTime = DateTime.Now;
        private double currentFps = 0;

        // 스레드 관련
        private Thread displayThread;
        private Thread topmostThread;
        private readonly object frameLock = new object();
        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private bool forceTopmost = false;

        // Windows Hook
        private IntPtr hookHandle = IntPtr.Zero;
        private HookProc hookCallback;
        private bool hookInstalled = false;

        // Graphics
        private BufferedGraphicsContext graphicsContext;
        private BufferedGraphics bufferedGraphics;
        private Font debugFont;

        public FullscreenOverlay(Dictionary<string, object> config = null)
        {
            this.config = config ?? Config.GetSection("overlay");
            
            ShowDebugInfo = Convert.ToBoolean(this.config.GetValueOrDefault("show_debug_info", false));
            fpsLimit = Convert.ToInt32(this.config.GetValueOrDefault("fps_limit", 30));

            InitializeForm();
            Console.WriteLine("🛡️ 화면 검열 시스템 초기화 완료");
        }

        private void InitializeForm()
        {
            // 폼 기본 설정
            Text = "Mosaic Fullscreen - Click Through Protected";
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            ShowInTaskbar = false;
            TopMost = true;
            
            // 전체 화면 크기
            var screen = Screen.PrimaryScreen;
            Bounds = screen.Bounds;

            // 더블 버퍼링 활성화
            SetStyle(ControlStyles.AllPaintingInWmPaint | 
                    ControlStyles.UserPaint | 
                    ControlStyles.DoubleBuffer | 
                    ControlStyles.ResizeRedraw, true);

            // Graphics 컨텍스트 초기화
            graphicsContext = BufferedGraphicsManager.Current;
            
            // 디버그 폰트
            if (ShowDebugInfo)
            {
                debugFont = new Font("Arial", 12);
            }

            // 키 이벤트 핸들러
            KeyDown += OnKeyDown;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                Console.WriteLine("🔑 ESC 키 감지됨 - 종료 중...");
                isRunning = false;
                Hide();
            }
            else if (e.KeyCode == Keys.F1)
            {
                ToggleDebugInfo();
            }
        }

        public new bool Show()
        {
            if (isVisible)
                return true;

            Console.WriteLine("🛡️ 풀스크린 캡처 방지 + 클릭 투과 오버레이 표시 시작...");

            try
            {
                // 윈도우 표시
                base.Show();
                
                // Windows 스타일 설정
                SetWindowClickThroughAndCaptureProtected();

                isVisible = true;
                isRunning = true;

                // 디스플레이 스레드 시작
                displayThread = new Thread(DisplayLoop)
                {
                    Name = "OverlayDisplayThread",
                    IsBackground = true
                };
                displayThread.Start();

                Console.WriteLine("✅ 풀스크린 캡처 방지 + 클릭 투과 오버레이 표시됨");
                Console.WriteLine("💡 ESC 키를 누르면 종료됩니다");
                Console.WriteLine("💡 바탕화면을 자유롭게 클릭/드래그할 수 있습니다");
                Console.WriteLine("📌 pygame 창이 항상 최상단에 고정됩니다");

                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ 오버레이 표시 실패: {e.Message}");
                return false;
            }
        }

        private void SetWindowClickThroughAndCaptureProtected()
        {
            try
            {
                IntPtr hwnd = Handle;
                Console.WriteLine($"🔍 윈도우 핸들 획득: {hwnd}");

                // 1단계: 캡처에서 완전 제외 (피드백 루프 방지)
                try
                {
                    bool result = SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                    if (result)
                    {
                        Console.WriteLine("🛡️ 캡처 방지 설정 성공! (100% 피드백 루프 방지)");
                    }
                    else
                    {
                        Console.WriteLine("⚠️ 캡처 방지 설정 실패 (Windows 10+ 필요)");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"⚠️ 캡처 방지 설정 오류: {e.Message}");
                }

                // 2단계: 클릭 투과 설정
                try
                {
                    int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                    Console.WriteLine($"🔍 현재 Extended Style: 0x{exStyle:X8}");

                    // 클릭 투과 및 레이어드 윈도우 스타일 추가
                    int newExStyle = exStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW;
                    Console.WriteLine($"🔍 새로운 Extended Style: 0x{newExStyle:X8}");

                    SetWindowLong(hwnd, GWL_EXSTYLE, newExStyle);
                    
                    // 완전 불투명 설정
                    SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
                    
                    Console.WriteLine("🖱️ 클릭 투과 설정 성공! (마우스 클릭이 바탕화면으로 전달됩니다)");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"⚠️ 클릭 투과 설정 오류: {e.Message}");
                }

                // 3단계: 창을 최상단으로 설정
                SetWindowPos(hwnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                Console.WriteLine("✅ 최상단 설정 완료");

                // 4단계: 클릭 투과 테스트
                if (TestClickThroughImmediately())
                {
                    Console.WriteLine("✅ 클릭 투과 즉시 테스트 성공!");
                }
                else
                {
                    Console.WriteLine("⚠️ 클릭 투과 즉시 테스트 실패 - 재시도 중...");
                    Thread.Sleep(500);
                    RetryClickThroughSetup();
                }

                // 5단계: 강제 최상단 모드 활성화
                forceTopmost = true;

                // 6단계: Windows Hook 설치
                InstallActivationHook();

                // 7단계: 지속적인 최상단 유지 스레드 시작
                StartTopmostKeeper();

                Console.WriteLine("🎉 풀스크린이 캡처 방지 + 클릭 투과로 설정되었습니다!");
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ 창 설정 실패: {e.Message}");
            }
        }

        private void RetryClickThroughSetup()
        {
            try
            {
                IntPtr hwnd = Handle;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                
                // 기존 스타일 제거 후 다시 설정
                int cleanStyle = exStyle & ~(WS_EX_LAYERED | WS_EX_TRANSPARENT);
                SetWindowLong(hwnd, GWL_EXSTYLE, cleanStyle);
                
                Thread.Sleep(100);
                
                // 다시 클릭 투과 스타일 적용
                int newStyle = cleanStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW;
                SetWindowLong(hwnd, GWL_EXSTYLE, newStyle);
                
                // 레이어드 윈도우 속성 재설정
                SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ 클릭 투과 재설정 실패: {e.Message}");
            }
        }

        private bool TestClickThroughImmediately()
        {
            try
            {
                IntPtr hwnd = Handle;
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                
                bool hasTransparent = (exStyle & WS_EX_TRANSPARENT) != 0;
                bool hasLayered = (exStyle & WS_EX_LAYERED) != 0;
                
                return hasTransparent && hasLayered;
            }
            catch
            {
                return false;
            }
        }

        private void InstallActivationHook()
        {
            try
            {
                hookCallback = new HookProc(ActivationHookProc);
                IntPtr hInstance = GetModuleHandle(null);
                
                hookHandle = SetWindowsHookEx(WH_CBT, hookCallback, hInstance, 0);
                
                if (hookHandle != IntPtr.Zero)
                {
                    hookInstalled = true;
                    Console.WriteLine("🛡️ Windows Hook 설치 성공: 창 활성화 시도를 즉시 감지합니다");
                }
                else
                {
                    Console.WriteLine("⚠️ Windows Hook 설치 실패");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ Windows Hook 설치 오류: {e.Message}");
            }
        }

        private IntPtr ActivationHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && forceTopmost && nCode == HCBT_ACTIVATE)
                {
                    IntPtr activatedHwnd = wParam;
                    
                    if (activatedHwnd != Handle)
                    {
                        InstantForceTopmost();
                        Console.WriteLine($"🛡️ 즉시 차단: 창(hwnd:{activatedHwnd}) 활성화 시도를 감지, 오버레이 창 즉시 복구");
                    }
                }
            }
            catch { }
            
            return CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }

        private void UninstallActivationHook()
        {
            if (hookInstalled && hookHandle != IntPtr.Zero)
            {
                try
                {
                    UnhookWindowsHookEx(hookHandle);
                    hookInstalled = false;
                    hookHandle = IntPtr.Zero;
                    Console.WriteLine("🛡️ Windows Hook 제거됨");
                }
                catch { }
            }
        }

        private void InstantForceTopmost()
        {
            try
            {
                IntPtr hwnd = Handle;
                SetWindowPos(hwnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
        }

        private void StartTopmostKeeper()
        {
            topmostThread = new Thread(TopmostKeeperLoop)
            {
                Name = "TopmostKeeperThread",
                IsBackground = true
            };
            topmostThread.Start();
        }

        private void TopmostKeeperLoop()
        {
            Console.WriteLine("🔄 강화된 최상단 유지 루프 시작");
            
            try
            {
                int checkCount = 0;
                while (!cancellationTokenSource.Token.IsCancellationRequested && forceTopmost)
                {
                    try
                    {
                        checkCount++;
                        
                        IntPtr foregroundHwnd = GetForegroundWindow();
                        if (foregroundHwnd != Handle)
                        {
                            ForceToTopmost();
                        }
                        
                        Thread.Sleep(50); // 0.05초 간격
                    }
                    catch
                    {
                        Thread.Sleep(100);
                    }
                }
            }
            catch { }
            
            Console.WriteLine("🛑 강화된 최상단 유지 루프 종료");
        }

        private void ForceToTopmost()
        {
            try
            {
                IntPtr hwnd = Handle;
                
                // 여러 방법으로 최상단 강제
                SetWindowPos(hwnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                
                BringWindowToTop(hwnd);
            }
            catch { }
        }

        public new void Hide()
        {
            if (!isVisible)
                return;

            Console.WriteLine("🛑 화면 검열 시스템 종료 중...");

            isVisible = false;
            isRunning = false;
            forceTopmost = false;

            // Windows Hook 제거
            UninstallActivationHook();

            // 스레드 종료 대기
            cancellationTokenSource.Cancel();
            
            if (displayThread?.IsAlive == true)
            {
                displayThread.Join(1000);
            }
            
            if (topmostThread?.IsAlive == true)
            {
                topmostThread.Join(1000);
            }

            // 폼 닫기
            if (InvokeRequired)
            {
                Invoke(new Action(() => base.Hide()));
            }
            else
            {
                base.Hide();
            }

            Console.WriteLine("✅ 화면 검열 시스템 종료됨");
        }

        public void UpdateFrame(Mat processedFrame)
        {
            lock (frameLock)
            {
                currentFrame?.Dispose();
                currentFrame = processedFrame?.Clone();
            }
        }

        private void DisplayLoop()
        {
            Console.WriteLine("🔄 풀스크린 디스플레이 루프 시작");

            try
            {
                while (isRunning)
                {
                    try
                    {
                        // 화면 그리기
                        if (InvokeRequired)
                        {
                            Invoke(new Action(() => Invalidate()));
                        }
                        else
                        {
                            Invalidate();
                        }

                        UpdateFps();

                        // FPS 제한
                        Thread.Sleep(1000 / fpsLimit);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"❌ 디스플레이 오류: {e.Message}");
                    }
                }
            }
            catch { }

            Console.WriteLine("🛑 풀스크린 디스플레이 루프 종료");
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.SmoothingMode = SmoothingMode.HighSpeed;
            g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            g.CompositingQuality = CompositingQuality.HighSpeed;

            // 버퍼 그래픽스 생성
            bufferedGraphics = graphicsContext.Allocate(g, DisplayRectangle);
            var bufferGraphics = bufferedGraphics.Graphics;

            // 배경을 검은색으로
            bufferGraphics.Clear(Color.Black);

            // 현재 프레임 그리기
            lock (frameLock)
            {
                if (currentFrame != null && !currentFrame.Empty())
                {
                    try
                    {
                        using (var bitmap = MatToBitmap(currentFrame))
                        {
                            bufferGraphics.DrawImage(bitmap, 0, 0, Width, Height);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ 프레임 그리기 오류: {ex.Message}");
                    }
                }
            }

            // 디버그 정보 그리기
            if (ShowDebugInfo && debugFont != null)
            {
                DrawDebugInfo(bufferGraphics);
            }

            // 버퍼를 화면에 그리기
            bufferedGraphics.Render(g);
            bufferedGraphics.Dispose();
        }

        private void DrawDebugInfo(Graphics g)
        {
            try
            {
                using (var brush = new SolidBrush(Color.White))
                using (var bgBrush = new SolidBrush(Color.FromArgb(128, 0, 0, 0)))
                {
                    int y = 10;
                    
                    // FPS
                    string fpsText = $"FPS: {currentFps:F1}";
                    var fpsSize = g.MeasureString(fpsText, debugFont);
                    g.FillRectangle(bgBrush, 10, y, fpsSize.Width, fpsSize.Height);
                    g.DrawString(fpsText, debugFont, brush, 10, y);
                    y += 30;

                    // 해상도
                    string resText = $"Resolution: {Width}x{Height}";
                    var resSize = g.MeasureString(resText, debugFont);
                    g.FillRectangle(bgBrush, 10, y, resSize.Width, resSize.Height);
                    g.DrawString(resText, debugFont, brush, 10, y);
                    y += 30;

                    // 상태
                    string statusText = "🛡️ PROTECTED + CLICK THROUGH + HOOK GUARD";
                    var statusSize = g.MeasureString(statusText, debugFont);
                    g.FillRectangle(bgBrush, 10, y, statusSize.Width, statusSize.Height);
                    g.DrawString(statusText, debugFont, Brushes.LightGreen, 10, y);
                    y += 30;

                    // Hook 상태
                    string hookText = hookInstalled ? "Hook: ACTIVE" : "Hook: INACTIVE";
                    var hookSize = g.MeasureString(hookText, debugFont);
                    g.FillRectangle(bgBrush, 10, y, hookSize.Width, hookSize.Height);
                    g.DrawString(hookText, debugFont, Brushes.Yellow, 10, y);
                    y += 30;

                    // 안내
                    string guideText = "Click anything! ZERO flickering guaranteed!";
                    var guideSize = g.MeasureString(guideText, debugFont);
                    g.FillRectangle(bgBrush, 10, y, guideSize.Width, guideSize.Height);
                    g.DrawString(guideText, debugFont, Brushes.Cyan, 10, y);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ 디버그 정보 표시 오류: {e.Message}");
            }
        }

        private void UpdateFps()
        {
            fpsCounter++;
            var currentTime = DateTime.Now;
            
            if ((currentTime - fpsStartTime).TotalSeconds >= 1.0)
            {
                currentFps = fpsCounter / (currentTime - fpsStartTime).TotalSeconds;
                fpsCounter = 0;
                fpsStartTime = currentTime;
            }
        }

        public bool IsWindowVisible()
        {
            return isVisible && isRunning;
        }

        public void ToggleDebugInfo()
        {
            ShowDebugInfo = !ShowDebugInfo;
            if (ShowDebugInfo && debugFont == null)
            {
                debugFont = new Font("Arial", 12);
            }
            Console.WriteLine($"🔍 디버그 정보: {(ShowDebugInfo ? "켜짐" : "꺼짐")}");
        }

        public void SetFpsLimit(int fps)
        {
            fpsLimit = Math.Max(10, Math.Min(60, fps));
            Console.WriteLine($"🎮 FPS 제한: {fpsLimit}");
        }

        public bool TestCaptureProtection()
        {
            try
            {
                uint affinity;
                bool result = GetWindowDisplayAffinity(Handle, out affinity);
                
                if (result && affinity == WDA_EXCLUDEFROMCAPTURE)
                {
                    Console.WriteLine("✅ 캡처 방지 테스트 성공: 창이 캡처에서 제외됨");
                    return true;
                }
                else
                {
                    Console.WriteLine($"⚠️ 캡처 방지 테스트 실패: affinity={affinity}");
                    return false;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ 캡처 방지 테스트 오류: {e.Message}");
                return false;
            }
        }

        public bool TestClickThrough()
        {
            try
            {
                int exStyle = GetWindowLong(Handle, GWL_EXSTYLE);
                
                bool hasTransparent = (exStyle & WS_EX_TRANSPARENT) != 0;
                bool hasLayered = (exStyle & WS_EX_LAYERED) != 0;
                
                if (hasTransparent && hasLayered)
                {
                    Console.WriteLine("✅ 클릭 투과 테스트 성공: 마우스 클릭이 바탕화면으로 전달됩니다");
                    return true;
                }
                else
                {
                    Console.WriteLine($"⚠️ 클릭 투과 테스트 실패: transparent={hasTransparent}, layered={hasLayered}");
                    return false;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ 클릭 투과 테스트 오류: {e.Message}");
                return false;
            }
        }

        public new void Dispose()
        {
            Hide();
            
            currentFrame?.Dispose();
            bufferedGraphics?.Dispose();
            debugFont?.Dispose();
            cancellationTokenSource?.Dispose();
        }

        // BitmapConverter.ToBitmap의 대체 구현
        private Bitmap MatToBitmap(Mat mat)
        {
            if (mat.Type() != MatType.CV_8UC3)
            {
                throw new ArgumentException("Only CV_8UC3 type is supported");
            }

            Bitmap bitmap = new Bitmap(mat.Width, mat.Height, PixelFormat.Format24bppRgb);
            BitmapData bmpData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly,
                bitmap.PixelFormat);

            try
            {
                unsafe
                {
                    byte* src = (byte*)mat.DataPointer;
                    byte* dst = (byte*)bmpData.Scan0.ToPointer();
                    
                    for (int y = 0; y < mat.Height; y++)
                    {
                        for (int x = 0; x < mat.Width; x++)
                        {
                            int srcIdx = (y * mat.Width + x) * 3;
                            int dstIdx = y * bmpData.Stride + x * 3;
                            
                            // BGR 순서 유지
                            dst[dstIdx] = src[srcIdx];         // B
                            dst[dstIdx + 1] = src[srcIdx + 1]; // G
                            dst[dstIdx + 2] = src[srcIdx + 2]; // R
                        }
                    }
                }
                return bitmap;
            }
            finally
            {
                bitmap.UnlockBits(bmpData);
            }
        }
    }
}