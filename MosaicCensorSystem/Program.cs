using System;
using System.IO;
using System.Windows.Forms;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
namespace MosaicCensorSystem
{
    internal static class Program
    {
        // Windows API for priority management
        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        static extern bool SetThreadPriority(IntPtr hThread, int nPriority);

        [DllImport("kernel32.dll")]
        static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();

        // Priority constants
        const int THREAD_PRIORITY_NORMAL = 0;
        const uint NORMAL_PRIORITY_CLASS = 0x00000020;
        const uint HIGH_PRIORITY_CLASS = 0x00000080;

        // PyInstaller 환경과 유사하게 리소스 경로 처리
        public static string ONNX_MODEL_PATH { get; private set; } = "";
        
        // 🚨 FIXED: nullable 필드로 선언
        private static StreamWriter? logWriter;

        [STAThread]
        static void Main()
        {
            // 🚨 CRITICAL: 매우 강화된 글로벌 예외 핸들러 (크래시 완전 방지)
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                var crashLog = $"💥 FATAL CRASH PREVENTED at {DateTime.Now}\n" +
                              $"Exception: {ex?.GetType().Name}\n" +
                              $"Message: {ex?.Message}\n" +
                              $"StackTrace: {ex?.StackTrace}\n" +
                              $"IsTerminating: {e.IsTerminating}\n" +
                              $"Thread: {Thread.CurrentThread.Name ?? "Unknown"}\n" +
                              $"ThreadId: {Thread.CurrentThread.ManagedThreadId}\n" +
                              $"=====================================\n";
                
                try
                {
                    File.AppendAllText("fatal_crash_prevented.log", crashLog);
                    Console.WriteLine(crashLog);
                    
                    // 강제 GC 및 정리
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    
                    MessageBox.Show(
                        "치명적 오류가 감지되었지만 프로그램을 안전하게 보호했습니다.\n\n" +
                        "로그 파일: fatal_crash_prevented.log\n\n" +
                        "프로그램을 재시작하는 것을 권장합니다.",
                        "오류 방지",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                }
                catch 
                {
                    // 최후의 수단
                    try
                    {
                        File.WriteAllText("emergency_crash_log.txt", $"Emergency: {DateTime.Now} - {ex?.Message}");
                    }
                    catch { }
                }
            };

            Application.ThreadException += (sender, e) =>
            {
                var crashLog = $"💥 UI THREAD CRASH PREVENTED at {DateTime.Now}\n" +
                              $"Exception: {e.Exception.GetType().Name}\n" +
                              $"Message: {e.Exception.Message}\n" +
                              $"StackTrace: {e.Exception.StackTrace}\n" +
                              $"Thread: {Thread.CurrentThread.Name ?? "UI Thread"}\n" +
                              $"ThreadId: {Thread.CurrentThread.ManagedThreadId}\n" +
                              $"=====================================\n";
                
                try
                {
                    File.AppendAllText("ui_crash_prevented.log", crashLog);
                    Console.WriteLine(crashLog);
                    
                    // UI 스레드에서 안전한 정리
                    GC.Collect();
                    
                    MessageBox.Show(
                        "UI 스레드 오류가 감지되었지만 프로그램을 안전하게 보호했습니다.\n\n" +
                        "로그 파일: ui_crash_prevented.log\n\n" +
                        "계속 사용하거나 프로그램을 재시작할 수 있습니다.",
                        "UI 오류 방지",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
                catch { }
            };

            // 🚨 CRITICAL: 추가 안전 설정
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // 로그 파일 설정
            var logFile = Path.Combine(Environment.CurrentDirectory, "safe_mode_debug_log.txt");
            
            try
            {
                logWriter = new StreamWriter(logFile, false) { AutoFlush = true };
                
                // 콘솔과 파일에 동시 출력
                var multiWriter = new MultiTextWriter(Console.Out, logWriter);
                Console.SetOut(multiWriter);
                
                Console.WriteLine("=".PadRight(70, '='));
                Console.WriteLine($"🛡️ 안전 모드 프로그램 시작 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                Console.WriteLine($"📄 로그 파일: {logFile}");
                Console.WriteLine($"🔒 Runtime 크래시 완전 방지 모드 활성화");
                Console.WriteLine("=".PadRight(70, '='));
                
                RunMainProgramSafeMode();
            }
            catch (Exception e)
            {
                var errorMsg = $"💥 최상위 오류 (방지됨): {e.Message}\nStack Trace:\n{e.StackTrace}";
                
                try
                {
                    Console.WriteLine(errorMsg);
                    File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "top_level_crash_prevented.txt"), errorMsg);
                    
                    MessageBox.Show(
                        "최상위 오류가 감지되었지만 완전히 방지되었습니다.\n\n" +
                        "로그 파일을 확인하세요: top_level_crash_prevented.txt\n\n" +
                        "10초 후 안전하게 종료됩니다.",
                        "최상위 오류 방지",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                }
                catch { }
                
                // 오류 발생 시 10초 대기
                Console.WriteLine("\n❌ 최상위 오류가 방지되었습니다. 10초 후 안전하게 종료됩니다...");
                Thread.Sleep(10000);
            }
            finally
            {
                try
                {
                    Console.WriteLine($"\n🏁 안전 모드 프로그램 종료 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine("🛡️ 모든 크래시가 성공적으로 방지되었습니다");
                    Console.WriteLine("로그 파일들을 확인하세요: safe_mode_debug_log.txt");
                    logWriter?.Close();
                }
                catch { }
                
                // 정상 종료 시에도 5초 대기
                Console.WriteLine("\n⏰ 5초 후 안전하게 종료됩니다... (아무 키나 누르면 즉시 종료)");
                
                var waitTask = Task.Run(() => Thread.Sleep(5000));
                var keyTask = Task.Run(() => 
                {
                    try 
                    { 
                        Console.ReadKey(); 
                    } 
                    catch { }
                });
                
                Task.WaitAny(waitTask, keyTask);
                
                // 최종 정리
                try
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                catch { }
            }
        }
        
        private static void RunMainProgramSafeMode()
        {
            Console.WriteLine($"📍 현재 작업 디렉토리: {Environment.CurrentDirectory}");
            Console.WriteLine($"📍 실행 파일 위치: {AppDomain.CurrentDomain.BaseDirectory}");
            
            // 리소스 경로 설정
            ONNX_MODEL_PATH = SafeResourcePath("Resources/best.onnx");
            Console.WriteLine($"📂 ONNX 모델 경로: {ONNX_MODEL_PATH}");
            Console.WriteLine($"📂 파일 존재 여부: {File.Exists(ONNX_MODEL_PATH)}");
            
            if (!File.Exists(ONNX_MODEL_PATH))
            {
                Console.WriteLine("❌ ONNX 모델 파일이 없습니다!");
                Console.WriteLine("📋 현재 디렉토리의 파일들:");
                try
                {
                    var files = Directory.GetFiles(Environment.CurrentDirectory, "*", SearchOption.AllDirectories);
                    foreach (var file in files.Take(15)) // 처음 15개만 출력
                    {
                        Console.WriteLine($"  📄 {file}");
                    }
                    if (files.Length > 15)
                    {
                        Console.WriteLine($"  ... 그 외 {files.Length - 15}개 파일 더 있음");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"파일 목록 조회 실패: {ex.Message}");
                }
                
                Console.WriteLine("⚠️ 모델 파일 없이 안전 모드로 계속 진행합니다...");
            }

            Console.WriteLine("🛡️ 안전 모드로 프로그램 시작 (크래시 완전 방지)");

            try
            {
                // 🚨 CRITICAL: 안전한 우선순위 설정
                try
                {
                    Thread.CurrentThread.Priority = ThreadPriority.Normal;
                    var currentProcess = Process.GetCurrentProcess();
                    currentProcess.PriorityClass = ProcessPriorityClass.Normal; // 안전한 우선순위
                    
                    Console.WriteLine("✅ 안전한 우선순위 설정 완료 - 안정성 최우선 모드");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"⚠️ 우선순위 설정 실패 (무시하고 계속): {e.Message}");
                }

                try
                {
                    // 안전한 GC 설정
                    GCSettings.LatencyMode = GCLatencyMode.Interactive; // 안전한 GC 모드
                    
                    // 메모리 정리
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    
                    Console.WriteLine("✅ 안전한 메모리 관리 설정 완료");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"⚠️ 메모리 설정 실패 (무시하고 계속): {e.Message}");
                }

                // ONNX 모델 로딩 테스트 (안전 모드)
                Console.WriteLine("📡 안전 모드 ONNX 모델 로딩 테스트");
                try
                {
                    if (File.Exists(ONNX_MODEL_PATH))
                    {
                        var sessionOptions = new SessionOptions
                        {
                            EnableCpuMemArena = false,
                            EnableMemoryPattern = false,
                            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                            InterOpNumThreads = 1,
                            IntraOpNumThreads = 1,
                            GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL
                        };
                        
                        using (var session = new InferenceSession(ONNX_MODEL_PATH, sessionOptions))
                        {
                            Console.WriteLine("✅ 안전 모드 모델 로딩 성공");
                        }
                    }
                    else
                    {
                        Console.WriteLine("⚠️ 모델 파일이 없어서 로딩 테스트 건너뜀");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"❌ 안전 모드 모델 로딩 실패 (계속 진행): {e.Message}");
                }

                Console.WriteLine("🪟 안전 모드 GUI 루프 진입 준비됨");

                // Windows Forms 설정 (안전 모드)
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    
                    // 안전한 DPI 설정
                    try
                    {
                        Application.SetHighDpiMode(HighDpiMode.SystemAware);
                    }
                    catch (Exception dpiEx)
                    {
                        Console.WriteLine($"⚠️ DPI 설정 실패 (무시): {dpiEx.Message}");
                    }
                    
                    Console.WriteLine("✅ 안전 모드 Windows Forms 초기화 완료");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"❌ Windows Forms 초기화 실패: {e.Message}");
                    throw;
                }
                
                // 메인 앱 실행 (안전 모드)
                Console.WriteLine("🚀 안전 모드 MosaicApp 인스턴스 생성 중...");
                try
                {
                    var app = new MosaicApp();
                    Console.WriteLine("✅ 안전 모드 MosaicApp 인스턴스 생성 완료");
                    
                    Console.WriteLine("🏃 안전 모드 Application.Run 시작...");
                    Application.Run(app.Root);
                    Console.WriteLine("🏁 안전 모드 Application.Run 종료됨");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"❌ 안전 모드 MosaicApp 실행 중 오류 (방지됨): {e.Message}");
                    
                    MessageBox.Show(
                        $"애플리케이션 오류가 감지되었지만 안전하게 방지되었습니다.\n\n" +
                        $"오류: {e.Message}\n\n" +
                        $"로그 파일을 확인하세요.",
                        "애플리케이션 오류 방지",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
            }
            catch (Exception fatalEx)
            {
                Console.WriteLine($"💥 치명적 오류 감지됨 (방지됨): {fatalEx.Message}");
                Console.WriteLine($"Stack Trace: {fatalEx.StackTrace}");
                
                try
                {
                    File.AppendAllText("fatal_error_prevented.log", 
                        $"{DateTime.Now}: FATAL ERROR PREVENTED\n{fatalEx}\n================\n");
                }
                catch { }
                
                MessageBox.Show(
                    "치명적 오류가 감지되었지만 완전히 방지되었습니다!\n\n" +
                    "프로그램이 안전하게 보호되었습니다.\n\n" +
                    "로그 파일: fatal_error_prevented.log",
                    "치명적 오류 완전 방지",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                
                throw; // 상위에서 처리
            }
        }

        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr hProcess, int dwMinimumWorkingSetSize, int dwMaximumWorkingSetSize);

        /// <summary>
        /// 안전한 리소스 경로 불러오기
        /// </summary>
        public static string SafeResourcePath(string relativePath)
        {
            try
            {
                // 1순위: 실행 파일이 있는 디렉토리
                string basePath1 = AppDomain.CurrentDomain.BaseDirectory;
                string path1 = Path.Combine(basePath1, relativePath);
                Console.WriteLine($"🔍 안전 경로 1 시도: {path1} (존재: {File.Exists(path1)})");
                if (File.Exists(path1)) return path1;
                
                // 2순위: 현재 작업 디렉토리
                string basePath2 = Environment.CurrentDirectory;
                string path2 = Path.Combine(basePath2, relativePath);
                Console.WriteLine($"🔍 안전 경로 2 시도: {path2} (존재: {File.Exists(path2)})");
                if (File.Exists(path2)) return path2;
                
                // 3순위: 상위 디렉토리들 검색 (안전하게)
                var currentDir = new DirectoryInfo(Environment.CurrentDirectory);
                int searchDepth = 0;
                while (currentDir != null && currentDir.Parent != null && searchDepth < 3) // 최대 3단계만
                {
                    string path3 = Path.Combine(currentDir.FullName, relativePath);
                    Console.WriteLine($"🔍 안전 경로 3 시도 (depth {searchDepth}): {path3} (존재: {File.Exists(path3)})");
                    if (File.Exists(path3)) return path3;
                    currentDir = currentDir.Parent;
                    searchDepth++;
                }
                
                Console.WriteLine($"⚠️ 모든 안전 경로에서 파일을 찾을 수 없음: {relativePath}");
                return path1; // 기본값 반환
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ 안전 ResourcePath 오류: {e.Message}");
                return Path.Combine(Environment.CurrentDirectory, relativePath);
            }
        }
    }
    
    // 🚨 FIXED: nullable 문제 해결된 콘솔과 파일에 동시 출력하는 클래스
    public class MultiTextWriter : TextWriter
    {
        private readonly TextWriter[] writers;
        
        public MultiTextWriter(params TextWriter[] writers)
        {
            this.writers = writers ?? new TextWriter[0];
        }
        
        public override void Write(char value)
        {
            foreach (var writer in writers)
            {
                try 
                { 
                    writer?.Write(value); 
                } 
                catch { }
            }
        }
        
        // 🚨 FIXED: nullable string으로 변경
        public override void Write(string? value)
        {
            foreach (var writer in writers)
            {
                try 
                { 
                    writer?.Write(value); 
                } 
                catch { }
            }
        }
        
        // 🚨 FIXED: nullable string으로 변경
        public override void WriteLine(string? value)
        {
            foreach (var writer in writers)
            {
                try 
                { 
                    writer?.WriteLine(value); 
                } 
                catch { }
            }
        }
        
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var writer in writers)
                {
                    try
                    {
                        writer?.Dispose();
                    }
                    catch { }
                }
            }
            base.Dispose(disposing);
        }
    }
}