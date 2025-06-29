using System;
using System.IO;
using System.Windows.Forms;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace MosaicCensorSystem
{
    internal static class Program
    {
        // Windows API for ultra-high priority
        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

        [DllImport("kernel32.dll")]
        static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        // Priority constants
        const int THREAD_PRIORITY_TIME_CRITICAL = 15;
        const uint REALTIME_PRIORITY_CLASS = 0x00000100;

        // PyInstaller 환경과 유사하게 리소스 경로 처리
        public static string ONNX_MODEL_PATH { get; private set; } = "";

        [STAThread]
        static void Main()
        {
            Console.WriteLine("🚀 초고속 반응성 모드로 프로그램 시작");

            try
            {
                // 최고 우선순위 설정 (즉시 반응을 위해)
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                
                // 프로세스 우선순위를 RealTime으로 설정
                var currentProcess = Process.GetCurrentProcess();
                currentProcess.PriorityClass = ProcessPriorityClass.RealTime;
                
                // Windows 네이티브 API로 더 높은 우선순위 설정
                SetPriorityClass(GetCurrentProcess(), REALTIME_PRIORITY_CLASS);
                SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_TIME_CRITICAL);
                
                Console.WriteLine("✅ 최고 우선순위 설정 완료 - 즉시 반응 모드");

                // GC 최적화 (지연 최소화)
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                
                // 워킹셋 최적화
                SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
                
                Console.WriteLine("✅ 메모리 최적화 완료");
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ 우선순위 설정 실패 (관리자 권한 필요): {e.Message}");
                Console.WriteLine("💡 관리자 권한으로 실행하면 더 빠른 반응성을 얻을 수 있습니다");
            }

            // 리소스 경로 설정
            ONNX_MODEL_PATH = ResourcePath("Resources/best.onnx");

            // ONNX 모델 로딩 테스트
            Console.WriteLine("📡 ONNX 모델 로딩 시도");
            try
            {
                using (var session = new InferenceSession(ONNX_MODEL_PATH))
                {
                    Console.WriteLine("✅ 모델 로딩 성공");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ 모델 로딩 실패: {e.Message}");
            }

            Console.WriteLine("🪟 초고속 GUI 루프 진입 준비됨");

            // Windows Forms 설정 (최고 성능)
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            
            // 고성능 렌더링 모드
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            
            // 메인 앱 실행
            var app = new MosaicApp();
            Application.Run(app.Root);
        }

        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr hProcess, int dwMinimumWorkingSetSize, int dwMaximumWorkingSetSize);

        /// <summary>
        /// PyInstaller 환경에서도 리소스 경로를 안전하게 불러오기
        /// </summary>
        public static string ResourcePath(string relativePath)
        {
            // 실행 파일이 있는 디렉토리를 기준으로 경로 설정
            string basePath = Environment.CurrentDirectory;
            return Path.Combine(basePath, relativePath);
        }
    }
}