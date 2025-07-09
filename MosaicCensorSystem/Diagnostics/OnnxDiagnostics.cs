using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;
using Microsoft.ML.OnnxRuntime;

namespace MosaicCensorSystem.Diagnostics
{
    /// <summary>
    /// ONNX Runtime 환경 진단 도구
    /// </summary>
    public static class OnnxDiagnostics
    {
        public static void RunFullDiagnostics()
        {
            Console.WriteLine("🔍 ONNX Runtime 환경 진단 시작");
            Console.WriteLine("=" + new string('=', 50));
            
            CheckOnnxRuntimeVersion();
            CheckAvailableProviders();
            CheckNativeLibraries();
            CheckModelCompatibility();
            CheckMemoryLimits();
            
            Console.WriteLine("=" + new string('=', 50));
            Console.WriteLine("✅ 진단 완료");
        }
        
        private static void CheckOnnxRuntimeVersion()
        {
            try
            {
                Console.WriteLine("📦 ONNX Runtime 버전 정보:");
                
                // Assembly 버전 확인
                var assembly = Assembly.GetAssembly(typeof(InferenceSession));
                var version = assembly?.GetName().Version;
                Console.WriteLine($"  Assembly 버전: {version}");
                
                // 파일 버전 확인
                var location = assembly?.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    var fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(location);
                    Console.WriteLine($"  파일 버전: {fileVersion.FileVersion}");
                    Console.WriteLine($"  제품 버전: {fileVersion.ProductVersion}");
                }
                
                Console.WriteLine($"  라이브러리 위치: {location}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 버전 확인 실패: {ex.Message}");
            }
        }
        
        private static void CheckAvailableProviders()
        {
            try
            {
                Console.WriteLine("\n🔧 사용 가능한 실행 제공자:");
                
                var providers = OrtEnv.Instance().GetAvailableProviders();
                foreach (var provider in providers)
                {
                    Console.WriteLine($"  ✅ {provider}");
                    
                    // 각 제공자별 상세 정보
                    switch (provider)
                    {
                        case "CUDAExecutionProvider":
                            CheckCudaSupport();
                            break;
                        case "DmlExecutionProvider":
                            CheckDirectMLSupport();
                            break;
                        case "CPUExecutionProvider":
                            CheckCpuSupport();
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 제공자 확인 실패: {ex.Message}");
            }
        }
        
        private static void CheckCudaSupport()
        {
            try
            {
                Console.WriteLine("    🚀 CUDA 지원 확인 중...");
                
                // CUDA 라이브러리 존재 확인
                var cudaFiles = new[] 
                {
                    "cudart64_110.dll", "cudart64_111.dll", "cudart64_112.dll",
                    "cublas64_11.dll", "cublasLt64_11.dll", "curand64_10.dll",
                    "cudnn64_8.dll", "cufft64_10.dll"
                };
                
                bool hasCudaLibs = false;
                foreach (var file in cudaFiles)
                {
                    if (File.Exists(file))
                    {
                        Console.WriteLine($"    ✅ {file} 발견");
                        hasCudaLibs = true;
                    }
                }
                
                if (!hasCudaLibs)
                {
                    Console.WriteLine("    ⚠️ CUDA 라이브러리를 찾을 수 없음");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ❌ CUDA 확인 실패: {ex.Message}");
            }
        }
        
        private static void CheckDirectMLSupport()
        {
            try
            {
                Console.WriteLine("    🎮 DirectML 지원 확인 중...");
                // DirectML은 Windows 10/11에 내장
                var osVersion = Environment.OSVersion;
                if (osVersion.Platform == PlatformID.Win32NT && osVersion.Version.Major >= 10)
                {
                    Console.WriteLine("    ✅ Windows 10/11 - DirectML 지원 가능");
                }
                else
                {
                    Console.WriteLine("    ❌ DirectML 미지원 OS");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ❌ DirectML 확인 실패: {ex.Message}");
            }
        }
        
        private static void CheckCpuSupport()
        {
            try
            {
                Console.WriteLine("    🔥 CPU 지원 정보:");
                Console.WriteLine($"    프로세서 코어: {Environment.ProcessorCount}");
                Console.WriteLine($"    64비트 프로세스: {Environment.Is64BitProcess}");
                Console.WriteLine($"    사용 가능한 메모리: {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    ❌ CPU 정보 확인 실패: {ex.Message}");
            }
        }
        
        private static void CheckNativeLibraries()
        {
            try
            {
                Console.WriteLine("\n📚 네이티브 라이브러리 확인:");
                
                var nativeFiles = new[]
                {
                    "onnxruntime.dll",
                    "onnxruntime_providers_shared.dll",
                    "onnxruntime_providers_cuda.dll",
                    "onnxruntime_providers_tensorrt.dll"
                };
                
                foreach (var file in nativeFiles)
                {
                    if (File.Exists(file))
                    {
                        var fileInfo = new FileInfo(file);
                        Console.WriteLine($"  ✅ {file} ({fileInfo.Length / (1024 * 1024):F1} MB)");
                    }
                    else
                    {
                        Console.WriteLine($"  ❌ {file} 없음");
                    }
                }
                
                // 현재 디렉토리의 모든 DLL 확인
                Console.WriteLine("\n  현재 디렉토리의 관련 DLL:");
                var currentDir = Environment.CurrentDirectory;
                var dllFiles = Directory.GetFiles(currentDir, "*.dll");
                
                foreach (var dll in dllFiles)
                {
                    var fileName = Path.GetFileName(dll);
                    if (fileName.Contains("onnx", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("cuda", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("tensorrt", StringComparison.OrdinalIgnoreCase))
                    {
                        var fileInfo = new FileInfo(dll);
                        Console.WriteLine($"  📄 {fileName} ({fileInfo.Length / 1024:F0} KB)");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 네이티브 라이브러리 확인 실패: {ex.Message}");
            }
        }
        
        private static void CheckModelCompatibility()
        {
            try
            {
                Console.WriteLine("\n🤖 모델 호환성 확인:");
                
                var modelPath = Program.ONNX_MODEL_PATH;
                if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
                {
                    Console.WriteLine("  ❌ 모델 파일을 찾을 수 없음");
                    return;
                }
                
                Console.WriteLine($"  📁 모델 경로: {modelPath}");
                
                var fileInfo = new FileInfo(modelPath);
                Console.WriteLine($"  📊 파일 크기: {fileInfo.Length / (1024 * 1024):F1} MB");
                
                // 안전한 세션 옵션으로 모델 로딩 테스트
                var sessionOptions = new SessionOptions
                {
                    EnableCpuMemArena = false,
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = 1,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL,
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                };
                
                using var session = new InferenceSession(modelPath, sessionOptions);
                Console.WriteLine("  ✅ 모델 로딩 성공 (안전 모드)");
                
                // 입출력 메타데이터 확인
                var inputMeta = session.InputMetadata;
                var outputMeta = session.OutputMetadata;
                
                Console.WriteLine($"  📥 입력: {inputMeta.Count}개");
                foreach (var input in inputMeta)
                {
                    Console.WriteLine($"    - {input.Key}: {string.Join("x", input.Value.Dimensions)}");
                }
                
                Console.WriteLine($"  📤 출력: {outputMeta.Count}개");
                foreach (var output in outputMeta)
                {
                    Console.WriteLine($"    - {output.Key}: {string.Join("x", output.Value.Dimensions)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 모델 호환성 확인 실패: {ex.Message}");
                Console.WriteLine($"  스택 트레이스: {ex.StackTrace}");
            }
        }
        
        private static void CheckMemoryLimits()
        {
            try
            {
                Console.WriteLine("\n💾 메모리 상태:");
                
                // GC 정보
                Console.WriteLine($"  전체 메모리: {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
                Console.WriteLine($"  Gen 0 수집: {GC.CollectionCount(0)}");
                Console.WriteLine($"  Gen 1 수집: {GC.CollectionCount(1)}");
                Console.WriteLine($"  Gen 2 수집: {GC.CollectionCount(2)}");
                
                // 프로세스 메모리
                var process = System.Diagnostics.Process.GetCurrentProcess();
                Console.WriteLine($"  작업 세트: {process.WorkingSet64 / (1024 * 1024)} MB");
                Console.WriteLine($"  개인 메모리: {process.PrivateMemorySize64 / (1024 * 1024)} MB");
                Console.WriteLine($"  가상 메모리: {process.VirtualMemorySize64 / (1024 * 1024)} MB");
                
                // 강제 GC 실행
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                
                Console.WriteLine($"  GC 후 메모리: {GC.GetTotalMemory(false) / (1024 * 1024)} MB");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 메모리 확인 실패: {ex.Message}");
            }
        }
        
        /// <summary>
        /// 간단한 추론 테스트
        /// </summary>
        public static bool TestSimpleInference()
        {
            try
            {
                Console.WriteLine("\n🧪 간단한 추론 테스트:");
                
                var modelPath = Program.ONNX_MODEL_PATH;
                if (!File.Exists(modelPath))
                {
                    Console.WriteLine("  ❌ 모델 파일 없음");
                    return false;
                }
                
                // 최소한의 세션 옵션
                var sessionOptions = new SessionOptions
                {
                    LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
                };
                
                using var session = new InferenceSession(modelPath, sessionOptions);
                
                // 더미 입력 생성 (640x640)
                var inputTensor = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(new[] { 1, 3, 640, 640 });
                
                // 간단한 패턴으로 채우기 (메모리 오류 방지)
                for (int i = 0; i < inputTensor.Length; i++)
                {
                    inputTensor.SetValue(i, 0.5f); // 중간 값
                }
                
                var inputs = new List<Microsoft.ML.OnnxRuntime.NamedOnnxValue>
                {
                    Microsoft.ML.OnnxRuntime.NamedOnnxValue.CreateFromTensor("images", inputTensor)
                };
                
                Console.WriteLine("  🔄 추론 실행 중...");
                using var results = session.Run(inputs);
                
                var output = results.First().AsTensor<float>();
                Console.WriteLine($"  ✅ 추론 성공: 출력 크기 {output.Length}");
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 추론 테스트 실패: {ex.Message}");
                return false;
            }
        }
    }
}