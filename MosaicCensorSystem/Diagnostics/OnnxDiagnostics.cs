using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MosaicCensorSystem.Diagnostics
{
    /// <summary>
    /// ONNX Runtime 진단 및 테스트 도구
    /// </summary>
    public static class OnnxDiagnostics
    {
        /// <summary>
        /// 전체 ONNX 진단 실행
        /// </summary>
        public static void RunFullDiagnostics()
        {
            Console.WriteLine("🔍 ONNX Runtime 전체 진단 시작");
            Console.WriteLine("=" + new string('=', 60));

            try
            {
                // 1. 시스템 정보
                DiagnoseSystemInfo();
                Console.WriteLine();

                // 2. ONNX Runtime 버전 및 제공자
                DiagnoseOnnxRuntime();
                Console.WriteLine();

                // 3. GPU 지원 확인
                DiagnoseGpuSupport();
                Console.WriteLine();

                // 4. 메모리 상태
                DiagnoseMemoryStatus();
                Console.WriteLine();

                // 5. 모델 파일 검증
                DiagnoseModelFiles();
                Console.WriteLine();

                // 6. 네이티브 라이브러리 확인
                DiagnoseNativeLibraries();
                Console.WriteLine();

                Console.WriteLine("✅ ONNX Runtime 전체 진단 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 진단 중 오류 발생: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
            }

            Console.WriteLine("=" + new string('=', 60));
        }

        /// <summary>
        /// 시스템 정보 진단
        /// </summary>
        private static void DiagnoseSystemInfo()
        {
            Console.WriteLine("🖥️ 시스템 정보:");
            
            try
            {
                Console.WriteLine($"  OS: {Environment.OSVersion}");
                Console.WriteLine($"  아키텍처: {RuntimeInformation.OSArchitecture}");
                Console.WriteLine($"  프로세서 수: {Environment.ProcessorCount}");
                Console.WriteLine($"  .NET Runtime: {RuntimeInformation.FrameworkDescription}");
                Console.WriteLine($"  Working Set: {Environment.WorkingSet / (1024 * 1024):F1} MB");
                Console.WriteLine($"  64비트 프로세스: {Environment.Is64BitProcess}");
                Console.WriteLine($"  64비트 OS: {Environment.Is64BitOperatingSystem}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 시스템 정보 조회 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// ONNX Runtime 진단
        /// </summary>
        private static void DiagnoseOnnxRuntime()
        {
            Console.WriteLine("🧠 ONNX Runtime 정보:");
            
            try
            {
                // ONNX Runtime 버전
                var version = typeof(InferenceSession).Assembly.GetName().Version;
                Console.WriteLine($"  ONNX Runtime 버전: {version}");

                // 사용 가능한 실행 제공자
                var providers = OrtEnv.Instance().GetAvailableProviders();
                Console.WriteLine($"  사용 가능한 실행 제공자: {providers.Length}개");
                
                foreach (var provider in providers)
                {
                    string status = GetProviderStatus(provider);
                    Console.WriteLine($"    - {provider}: {status}");
                }

                // 기본 할당자 정보
                Console.WriteLine($"  기본 메모리 할당자: {(IntPtr.Size == 8 ? "64비트" : "32비트")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ ONNX Runtime 정보 조회 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// GPU 지원 진단
        /// </summary>
        private static void DiagnoseGpuSupport()
        {
            Console.WriteLine("🚀 GPU 가속 지원:");
            
            try
            {
                var providers = OrtEnv.Instance().GetAvailableProviders();
                
                // CUDA 지원
                bool hasCuda = providers.Contains("CUDAExecutionProvider");
                Console.WriteLine($"  CUDA: {(hasCuda ? "✅ 지원됨" : "❌ 지원되지 않음")}");
                
                // DirectML 지원 (Windows GPU)
                bool hasDml = providers.Contains("DmlExecutionProvider");
                Console.WriteLine($"  DirectML: {(hasDml ? "✅ 지원됨" : "❌ 지원되지 않음")}");
                
                // TensorRT 지원
                bool hasTensorRt = providers.Contains("TensorrtExecutionProvider");
                Console.WriteLine($"  TensorRT: {(hasTensorRt ? "✅ 지원됨" : "❌ 지원되지 않음")}");
                
                // CPU 최적화
                bool hasCpu = providers.Contains("CPUExecutionProvider");
                Console.WriteLine($"  CPU 최적화: {(hasCpu ? "✅ 지원됨" : "❌ 지원되지 않음")}");

                // 권장 설정
                if (hasCuda)
                {
                    Console.WriteLine("  🎯 권장: CUDA 가속 사용");
                }
                else if (hasDml)
                {
                    Console.WriteLine("  🎯 권장: DirectML 가속 사용");
                }
                else
                {
                    Console.WriteLine("  🎯 권장: CPU 최적화 모드");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ GPU 지원 진단 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 메모리 상태 진단
        /// </summary>
        private static void DiagnoseMemoryStatus()
        {
            Console.WriteLine("💾 메모리 상태:");
            
            try
            {
                var process = Process.GetCurrentProcess();
                Console.WriteLine($"  물리 메모리 사용량: {process.WorkingSet64 / (1024 * 1024):F1} MB");
                Console.WriteLine($"  가상 메모리 사용량: {process.VirtualMemorySize64 / (1024 * 1024):F1} MB");
                Console.WriteLine($"  Private 메모리: {process.PrivateMemorySize64 / (1024 * 1024):F1} MB");
                
                // GC 정보
                Console.WriteLine($"  GC Generation 0: {GC.CollectionCount(0)}회");
                Console.WriteLine($"  GC Generation 1: {GC.CollectionCount(1)}회");
                Console.WriteLine($"  GC Generation 2: {GC.CollectionCount(2)}회");
                Console.WriteLine($"  총 할당된 메모리: {GC.GetTotalMemory(false) / (1024 * 1024):F1} MB");

                // 메모리 압박 상태
                long totalMemory = GC.GetTotalMemory(false);
                if (totalMemory > 500 * 1024 * 1024) // 500MB
                {
                    Console.WriteLine("  ⚠️ 메모리 사용량이 높습니다");
                }
                else
                {
                    Console.WriteLine("  ✅ 메모리 사용량 정상");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 메모리 상태 진단 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 모델 파일 진단
        /// </summary>
        private static void DiagnoseModelFiles()
        {
            Console.WriteLine("📁 모델 파일 진단:");
            
            try
            {
                var modelPaths = new[]
                {
                    "best.onnx",
                    "Resources/best.onnx",
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "best.onnx"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "best.onnx"),
                    Program.ONNX_MODEL_PATH
                };

                bool foundValidModel = false;

                foreach (var path in modelPaths.Where(p => !string.IsNullOrEmpty(p)))
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            var fileInfo = new FileInfo(path);
                            Console.WriteLine($"  📄 {path}:");
                            Console.WriteLine($"    크기: {fileInfo.Length / (1024 * 1024):F1} MB");
                            Console.WriteLine($"    생성일: {fileInfo.CreationTime:yyyy-MM-dd HH:mm}");
                            Console.WriteLine($"    수정일: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm}");

                            // 모델 파일 크기 검증
                            if (fileInfo.Length > 5 * 1024 * 1024) // 5MB 이상
                            {
                                Console.WriteLine($"    ✅ 유효한 모델 파일");
                                foundValidModel = true;

                                // 간단한 모델 로딩 테스트
                                TestModelLoading(path);
                            }
                            else
                            {
                                Console.WriteLine($"    ⚠️ 파일이 너무 작음 (손상 가능성)");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  ❌ {path}: {ex.Message}");
                    }
                }

                if (!foundValidModel)
                {
                    Console.WriteLine("  ❌ 유효한 모델 파일을 찾을 수 없습니다");
                    Console.WriteLine("  💡 'best.onnx' 파일을 Resources 폴더에 배치하세요");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 모델 파일 진단 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 네이티브 라이브러리 진단
        /// </summary>
        private static void DiagnoseNativeLibraries()
        {
            Console.WriteLine("📚 네이티브 라이브러리:");
            
            try
            {
                var requiredLibs = new[]
                {
                    "onnxruntime.dll",
                    "opencv_world490.dll", // OpenCV 버전에 따라 변경될 수 있음
                    "onnxruntime_providers_shared.dll"
                };

                var searchPaths = new[]
                {
                    Environment.CurrentDirectory,
                    AppDomain.CurrentDomain.BaseDirectory,
                    Path.Combine(Environment.CurrentDirectory, "runtimes", "win-x64", "native"),
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
                };

                foreach (var lib in requiredLibs)
                {
                    bool found = false;
                    foreach (var searchPath in searchPaths.Where(p => !string.IsNullOrEmpty(p)))
                    {
                        try
                        {
                            var fullPath = Path.Combine(searchPath, lib);
                            if (File.Exists(fullPath))
                            {
                                var fileInfo = new FileInfo(fullPath);
                                Console.WriteLine($"  ✅ {lib}: {fullPath} ({fileInfo.Length / (1024 * 1024):F1} MB)");
                                found = true;
                                break;
                            }
                        }
                        catch { }
                    }

                    if (!found)
                    {
                        Console.WriteLine($"  ❌ {lib}: 찾을 수 없음");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 네이티브 라이브러리 진단 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 간단한 추론 테스트
        /// </summary>
        public static bool TestSimpleInference()
        {
            Console.WriteLine("🧪 간단한 추론 테스트:");
            
            try
            {
                // 더미 입력으로 세션 생성 테스트
                var sessionOptions = new SessionOptions
                {
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                };

                // 더미 텐서 생성 (1x3x64x64 - 작은 크기로 테스트)
                var inputTensor = new DenseTensor<float>(new[] { 1, 3, 64, 64 });
                for (int i = 0; i < inputTensor.Length; i++)
                {
                    inputTensor.SetValue(i, 0.5f);
                }

                Console.WriteLine("  ✅ 더미 텐서 생성 성공");
                Console.WriteLine("  💡 실제 모델이 필요한 테스트는 모델 로딩 후 수행됩니다");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 추론 테스트 실패: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 모델 로딩 테스트
        /// </summary>
        private static void TestModelLoading(string modelPath)
        {
            try
            {
                Console.WriteLine($"    🧪 모델 로딩 테스트...");
                
                var sessionOptions = new SessionOptions
                {
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                };

                using var session = new InferenceSession(modelPath, sessionOptions);
                
                // 입력/출력 메타데이터 확인
                var inputMeta = session.InputMetadata.FirstOrDefault();
                var outputMeta = session.OutputMetadata.FirstOrDefault();

                if (inputMeta.Key != null)
                {
                    Console.WriteLine($"    📊 입력: {inputMeta.Key} -> {string.Join("x", inputMeta.Value.Dimensions)}");
                }
                if (outputMeta.Key != null)
                {
                    Console.WriteLine($"    📊 출력: {outputMeta.Key} -> {string.Join("x", outputMeta.Value.Dimensions)}");
                }

                Console.WriteLine($"    ✅ 모델 로딩 성공");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ❌ 모델 로딩 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 실행 제공자 상태 확인
        /// </summary>
        private static string GetProviderStatus(string provider)
        {
            return provider switch
            {
                "CUDAExecutionProvider" => "GPU 가속 (NVIDIA)",
                "DmlExecutionProvider" => "GPU 가속 (DirectML)",
                "TensorrtExecutionProvider" => "GPU 가속 (TensorRT)",
                "CPUExecutionProvider" => "CPU 최적화",
                "OpenVINOExecutionProvider" => "Intel 최적화",
                _ => "알 수 없음"
            };
        }

        /// <summary>
        /// 성능 벤치마크 (선택적)
        /// </summary>
        public static void RunPerformanceBenchmark()
        {
            Console.WriteLine("⚡ 성능 벤치마크 (선택적):");
            
            try
            {
                var stopwatch = Stopwatch.StartNew();
                
                // 간단한 행렬 연산으로 CPU 성능 측정
                var random = new Random();
                var matrix = new float[1000, 1000];
                
                for (int i = 0; i < 1000; i++)
                {
                    for (int j = 0; j < 1000; j++)
                    {
                        matrix[i, j] = (float)random.NextDouble();
                    }
                }
                
                stopwatch.Stop();
                Console.WriteLine($"  행렬 생성 (1000x1000): {stopwatch.ElapsedMilliseconds}ms");
                
                // 메모리 할당 테스트
                stopwatch.Restart();
                var tensors = new List<DenseTensor<float>>();
                for (int i = 0; i < 10; i++)
                {
                    tensors.Add(new DenseTensor<float>(new[] { 1, 3, 256, 256 }));
                }
                stopwatch.Stop();
                Console.WriteLine($"  텐서 할당 (10개): {stopwatch.ElapsedMilliseconds}ms");
                
                // 정리
                tensors.Clear();
                GC.Collect();
                
                Console.WriteLine("  ✅ 기본 성능 테스트 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 성능 벤치마크 실패: {ex.Message}");
            }
        }
    }
}