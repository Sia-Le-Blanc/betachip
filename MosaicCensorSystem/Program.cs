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
        
        private static StreamWriter logWriter;

        [STAThread]
        static void Main()
        {
            // 로그 파일 설정
            var logFile = Path.Combine(Environment.CurrentDirectory, "debug_log.txt");
            
            try
            {
                logWriter = new StreamWriter(logFile, false) { AutoFlush = true };
                
                // 콘솔과 파일에 동시 출력
                var multiWriter = new MultiTextWriter(Console.Out, logWriter);
                Console.SetOut(multiWriter);
                
                Console.WriteLine("=".PadRight(60, '='));
                Console.WriteLine($"🚀 프로그램 시작 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"📄 로그 파일: {logFile}");
                Console.WriteLine("=".PadRight(60, '='));
                
                RunMainProgram();
            }
            catch (Exception e)
            {
                var errorMsg = $"💥 최상위 오류: {e.Message}\nStack Trace:\n{e.StackTrace}";
                
                try
                {
                    Console.WriteLine(errorMsg);
                    File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "crash_log.txt"), errorMsg);
                }
                catch { }
                
                // 오류 발생 시 10초 대기
                Console.WriteLine("\n❌ 프로그램 오류가 발생했습니다. 10초 후 종료됩니다...");
                Thread.Sleep(10000);
            }
            finally
            {
                try
                {
                    Console.WriteLine($"\n🏁 프로그램 종료 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine("로그 파일을 확인하세요: debug_log.txt");
                    logWriter?.Close();
                }
                catch { }
                
                // 정상 종료 시에도 5초 대기
                Console.WriteLine("\n⏰ 5초 후 종료됩니다... (아무 키나 누르면 즉시 종료)");
                
                var waitTask = Task.Run(() => Thread.Sleep(5000));
                var keyTask = Task.Run(() => Console.ReadKey());
                
                Task.WaitAny(waitTask, keyTask);
            }
        }
        
        private static void RunMainProgram()
        {
            Console.WriteLine($"📍 현재 작업 디렉토리: {Environment.CurrentDirectory}");
            Console.WriteLine($"📍 실행 파일 위치: {AppDomain.CurrentDomain.BaseDirectory}");
            
            // 리소스 경로 설정
            ONNX_MODEL_PATH = ResourcePath("Resources/best.onnx");
            Console.WriteLine($"📂 ONNX 모델 경로: {ONNX_MODEL_PATH}");
            Console.WriteLine($"📂 파일 존재 여부: {File.Exists(ONNX_MODEL_PATH)}");
            
            if (!File.Exists(ONNX_MODEL_PATH))
            {
                Console.WriteLine("❌ ONNX 모델 파일이 없습니다!");
                Console.WriteLine("📋 현재 디렉토리의 파일들:");
                try
                {
                    var files = Directory.GetFiles(Environment.CurrentDirectory, "*", SearchOption.AllDirectories);
                    foreach (var file in files.Take(20)) // 처음 20개만 출력
                    {
                        Console.WriteLine($"  📄 {file}");
                    }
                    if (files.Length > 20)
                    {
                        Console.WriteLine($"  ... 그 외 {files.Length - 20}개 파일 더 있음");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"파일 목록 조회 실패: {ex.Message}");
                }
                
                Console.WriteLine("⚠️ 모델 파일 없이 계속 진행합니다...");
            }

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
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ 우선순위 설정 실패 (관리자 권한 필요): {e.Message}");
                Console.WriteLine("💡 관리자 권한으로 실행하면 더 빠른 반응성을 얻을 수 있습니다");
            }

            try
            {
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
                Console.WriteLine($"⚠️ 메모리 최적화 실패: {e.Message}");
            }

            // ONNX 모델 로딩 테스트
            Console.WriteLine("📡 ONNX 모델 로딩 시도");
            try
            {
                if (File.Exists(ONNX_MODEL_PATH))
                {
                    using (var session = new InferenceSession(ONNX_MODEL_PATH))
                    {
                        Console.WriteLine("✅ 모델 로딩 성공");
                    }
                }
                else
                {
                    Console.WriteLine("⚠️ 모델 파일이 없어서 로딩 테스트 건너뜀");
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ 모델 로딩 실패: {e.Message}");
                Console.WriteLine($"Stack Trace: {e.StackTrace}");
            }

            Console.WriteLine("🪟 초고속 GUI 루프 진입 준비됨");

            // Windows Forms 설정 (최고 성능)
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                
                // 고성능 렌더링 모드
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                
                Console.WriteLine("✅ Windows Forms 초기화 완료");
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ Windows Forms 초기화 실패: {e.Message}");
                Console.WriteLine($"Stack Trace: {e.StackTrace}");
                throw; // 이 경우 프로그램을 계속할 수 없음
            }
            
            // 메인 앱 실행
            Console.WriteLine("🚀 MosaicApp 인스턴스 생성 중...");
            try
            {
                var app = new MosaicApp();
                Console.WriteLine("✅ MosaicApp 인스턴스 생성 완료");
                
                Console.WriteLine("🏃 Application.Run 시작...");
                Application.Run(app.Root);
                Console.WriteLine("🏁 Application.Run 종료됨");
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ MosaicApp 실행 중 오류: {e.Message}");
                Console.WriteLine($"Stack Trace: {e.StackTrace}");
                throw;
            }
        }

        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr hProcess, int dwMinimumWorkingSetSize, int dwMaximumWorkingSetSize);

        /// <summary>
        /// PyInstaller 환경에서도 리소스 경로를 안전하게 불러오기
        /// </summary>
        public static string ResourcePath(string relativePath)
        {
            try
            {
                // 1순위: 실행 파일이 있는 디렉토리
                string basePath1 = AppDomain.CurrentDomain.BaseDirectory;
                string path1 = Path.Combine(basePath1, relativePath);
                Console.WriteLine($"🔍 경로 1 시도: {path1} (존재: {File.Exists(path1)})");
                if (File.Exists(path1)) return path1;
                
                // 2순위: 현재 작업 디렉토리
                string basePath2 = Environment.CurrentDirectory;
                string path2 = Path.Combine(basePath2, relativePath);
                Console.WriteLine($"🔍 경로 2 시도: {path2} (존재: {File.Exists(path2)})");
                if (File.Exists(path2)) return path2;
                
                // 3순위: 상위 디렉토리들 검색
                var currentDir = new DirectoryInfo(Environment.CurrentDirectory);
                while (currentDir != null && currentDir.Parent != null)
                {
                    string path3 = Path.Combine(currentDir.FullName, relativePath);
                    Console.WriteLine($"🔍 경로 3 시도: {path3} (존재: {File.Exists(path3)})");
                    if (File.Exists(path3)) return path3;
                    currentDir = currentDir.Parent;
                }
                
                Console.WriteLine($"⚠️ 모든 경로에서 파일을 찾을 수 없음: {relativePath}");
                return path1; // 기본값 반환
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ ResourcePath 오류: {e.Message}");
                return Path.Combine(Environment.CurrentDirectory, relativePath);
            }
        }
    }
    
    // 콘솔과 파일에 동시 출력하는 클래스
    public class MultiTextWriter : TextWriter
    {
        private readonly TextWriter[] writers;
        
        public MultiTextWriter(params TextWriter[] writers)
        {
            this.writers = writers;
        }
        
        public override void Write(char value)
        {
            foreach (var writer in writers)
            {
                try { writer.Write(value); } catch { }
            }
        }
        
        public override void Write(string value)
        {
            foreach (var writer in writers)
            {
                try { writer.Write(value); } catch { }
            }
        }
        
        public override void WriteLine(string value)
        {
            foreach (var writer in writers)
            {
                try { writer.WriteLine(value); } catch { }
            }
        }
        
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
    }
}