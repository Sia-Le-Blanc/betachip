#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace MosaicCensorSystem.Detection
{
    /// <summary>
    /// 검열 효과 타입 열거형
    /// </summary>
    public enum CensorType
    {
        Mosaic,  // 모자이크
        Blur     // 블러
    }

    public class DetectionResult
    {
        public string ClassName { get; set; } = "";
        public float Confidence { get; set; }
        public int[] BBox { get; set; } = new int[4]; // [x1, y1, x2, y2]
        public int ClassId { get; set; }
    }

    /// <summary>
    /// 객체 감지 결과를 나타내는 클래스 (트래킹 ID 추가)
    /// </summary>
    public class Detection
    {
        public string ClassName { get; set; } = "";
        public float Confidence { get; set; }
        public int[] BBox { get; set; } = new int[4]; // [x1, y1, x2, y2]
        public int ClassId { get; set; }
        public int TrackId { get; set; } = -1; // 트래킹 ID 추가
        public bool IsStable { get; set; } = false; // 안정적인 감지인지 여부
        
        // 편의 속성
        public int X1 => BBox[0];
        public int Y1 => BBox[1];
        public int X2 => BBox[2];
        public int Y2 => BBox[3];
        public int Width => X2 - X1;
        public int Height => Y2 - Y1;
    }

    /// <summary>
    /// 트래킹된 객체 정보 (검열 효과 캐싱용)
    /// </summary>
    public class TrackedObject
    {
        public int TrackId { get; set; }
        public string ClassName { get; set; } = "";
        public Rect2d BoundingBox { get; set; }
        public float LastConfidence { get; set; }
        public int StableFrameCount { get; set; } = 0;
        public Mat CachedCensorRegion { get; set; } // 모자이크/블러 캐싱
        public CensorType LastCensorType { get; set; } = CensorType.Mosaic;
        public int LastStrength { get; set; } = 15;
        public DateTime LastUpdated { get; set; } = DateTime.Now;
        
        public void Dispose()
        {
            try
            {
                CachedCensorRegion?.Dispose();
            }
            catch { }
        }
    }

    /// <summary>
    /// 성능 통계를 나타내는 클래스
    /// </summary>
    public class PerformanceStats
    {
        public double AvgDetectionTime { get; set; }
        public double Fps { get; set; }
        public int LastDetectionsCount { get; set; }
        public int CacheHits { get; set; }
        public int CacheMisses { get; set; }
        public int TrackedObjects { get; set; }
    }

    /// <summary>
    /// 검열 처리 인터페이스
    /// </summary>
    public interface IProcessor
    {
        void SetTargets(List<string> targets);
        void SetStrength(int strength);
        void SetCensorType(CensorType censorType);
        List<Detection> DetectObjects(Mat frame);
        (Mat processedFrame, List<Detection> detections) DetectObjectsDetailed(Mat frame);
        Mat ApplyCensor(Mat image, int? strength = null);
        Mat ApplyMosaic(Mat image, int? strength = null);
        Mat ApplyBlur(Mat image, int? strength = null);
        Mat CreateCensorForRegion(Mat frame, int x1, int y1, int x2, int y2, int? strength = null);
        PerformanceStats GetPerformanceStats();
        void UpdateConfig(Dictionary<string, object> kwargs);
        bool IsModelLoaded();
        List<string> GetAvailableClasses();
        void ResetStats();
        float ConfThreshold { get; set; }
        List<string> Targets { get; }
        int Strength { get; }
        CensorType CurrentCensorType { get; }
    }

    /// <summary>
    /// ONNX 가이드 기반 완전 개선된 검열 프로세서
    /// </summary>
    public class MosaicProcessor : IProcessor, IDisposable
    {
        private readonly Dictionary<string, object> config;
        private InferenceSession model;
        private readonly string modelPath;
        private string accelerationMode = "Unknown";
        private volatile bool isDisposed = false;
        private volatile bool isModelLoaded = false;

        // 트래킹 시스템
        private readonly SortTracker tracker = new SortTracker();
        private readonly Dictionary<int, TrackedObject> trackedObjects = new Dictionary<int, TrackedObject>();
        private readonly object trackingLock = new object();
        private readonly object modelLock = new object();

        // 성능 최적화 설정
        private const int STABLE_FRAME_THRESHOLD = 3;
        private const int CACHE_CLEANUP_INTERVAL = 50;
        private const double CACHE_REGION_THRESHOLD = 0.15;
        private int frameCounter = 0;

        // 설정값들
        public float ConfThreshold { get; set; }
        public List<string> Targets { get; private set; }
        public int Strength { get; private set; }
        public CensorType CurrentCensorType { get; private set; }

        // ONNX 가이드 기반 정확한 클래스 매핑 (14개 클래스)
        private static readonly Dictionary<int, string> ClassNames = new Dictionary<int, string>
        {
            {0, "얼굴"}, {1, "가슴"}, {2, "겨드랑이"}, {3, "보지"}, {4, "발"},
            {5, "몸 전체"}, {6, "자지"}, {7, "팬티"}, {8, "눈"}, {9, "손"},
            {10, "교미"}, {11, "신발"}, {12, "가슴_옷"}, {13, "여성"}
        };

        // 각 클래스별 최적화된 NMS 임계값
        private static readonly Dictionary<string, float> NmsThresholds = new Dictionary<string, float>
        {
            ["얼굴"] = 0.3f, ["가슴"] = 0.4f, ["겨드랑이"] = 0.4f, ["보지"] = 0.3f, ["발"] = 0.5f,
            ["몸 전체"] = 0.6f, ["자지"] = 0.3f, ["팬티"] = 0.4f, ["눈"] = 0.2f, ["손"] = 0.5f,
            ["교미"] = 0.3f, ["신발"] = 0.5f, ["가슴_옷"] = 0.4f, ["여성"] = 0.7f
        };

        // 성능 통계
        private readonly List<double> detectionTimes = new List<double>();
        private List<Detection> lastDetections = new List<Detection>();
        private readonly object statsLock = new object();
        private int cacheHits = 0;
        private int cacheMisses = 0;

        // 전처리 버퍼 재사용 (메모리 최적화)
        private float[] reuseInputBuffer = new float[3 * 640 * 640];

        public MosaicProcessor(string modelPath = null, Dictionary<string, object> config = null)
        {
            Console.WriteLine("🔍 ONNX 가이드 기반 검열 프로세서 초기화");
            this.config = config ?? new Dictionary<string, object>();
            
            // 모델 경로 설정
            this.modelPath = FindBestModelPath(modelPath);
            Console.WriteLine($"🔍 최종 모델 경로: {this.modelPath}");

            // 설정값들 초기화
            ConfThreshold = 0.3f; // 가이드 권장값
            Targets = new List<string> { "얼굴", "눈", "손" }; // 가이드 기본 타겟
            Strength = 15; // 가이드 기본값
            CurrentCensorType = CensorType.Mosaic;

            // 모델 로딩
            LoadModelWithBestStrategy();

            Console.WriteLine($"🎯 타겟 클래스: {string.Join(", ", Targets)}");
            Console.WriteLine($"⚙️ 설정: 강도={Strength}, 신뢰도={ConfThreshold}, 타입={CurrentCensorType}");
            Console.WriteLine($"🚀 가속 모드: {accelerationMode}");
            Console.WriteLine($"📊 모델 상태: {(isModelLoaded ? "로드됨" : "로드 실패")}");
        }

        /// <summary>
        /// 가이드 기반 최적 모델 경로 찾기
        /// </summary>
        private string FindBestModelPath(string providedPath)
        {
            var candidates = new List<string>();
            
            // 제공된 경로 우선
            if (!string.IsNullOrEmpty(providedPath))
            {
                candidates.Add(providedPath);
            }
            
            // Program.ONNX_MODEL_PATH
            if (!string.IsNullOrEmpty(Program.ONNX_MODEL_PATH))
            {
                candidates.Add(Program.ONNX_MODEL_PATH);
            }
            
            // 가이드 기본 경로들
            candidates.AddRange(new[]
            {
                "best.onnx",
                "Resources/best.onnx",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "best.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "best.onnx"),
                Path.Combine(Environment.CurrentDirectory, "best.onnx"),
                Path.Combine(Environment.CurrentDirectory, "Resources", "best.onnx")
            });
            
            // 상위 디렉토리 검색
            var currentDir = new DirectoryInfo(Environment.CurrentDirectory);
            for (int i = 0; i < 3 && currentDir?.Parent != null; i++)
            {
                currentDir = currentDir.Parent;
                candidates.Add(Path.Combine(currentDir.FullName, "best.onnx"));
                candidates.Add(Path.Combine(currentDir.FullName, "Resources", "best.onnx"));
            }
            
            // 첫 번째 유효한 파일 반환
            foreach (var path in candidates)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var fileInfo = new FileInfo(path);
                        // 가이드 기준: 11.6MB 근처여야 함
                        if (fileInfo.Length > 10 * 1024 * 1024) // 10MB 이상
                        {
                            Console.WriteLine($"✅ 유효한 모델 파일 발견: {path} ({fileInfo.Length / (1024 * 1024):F1} MB)");
                            return path;
                        }
                        else
                        {
                            Console.WriteLine($"⚠️ 모델 파일이 너무 작음: {path} ({fileInfo.Length / (1024 * 1024):F1} MB)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ 경로 체크 오류 ({path}): {ex.Message}");
                }
            }
            
            Console.WriteLine("❌ 유효한 모델 파일을 찾을 수 없습니다!");
            return candidates.FirstOrDefault() ?? "best.onnx";
        }

        /// <summary>
        /// 최적 전략으로 모델 로딩
        /// </summary>
        private void LoadModelWithBestStrategy()
        {
            try
            {
                Console.WriteLine($"🤖 ONNX 모델 로딩 시작: {modelPath}");
                
                if (!File.Exists(modelPath))
                {
                    Console.WriteLine("❌ 모델 파일이 존재하지 않습니다");
                    accelerationMode = "No Model";
                    isModelLoaded = false;
                    return;
                }

                // 파일 크기 검증
                var fileInfo = new FileInfo(modelPath);
                Console.WriteLine($"📊 모델 파일 크기: {fileInfo.Length / (1024 * 1024):F1} MB");

                // 가이드 기준: 11.6MB 근처여야 함
                if (fileInfo.Length < 5 * 1024 * 1024)
                {
                    Console.WriteLine("❌ 모델 파일이 너무 작습니다 (손상되었을 가능성)");
                    accelerationMode = "Corrupted Model";
                    isModelLoaded = false;
                    return;
                }

                // GPU 먼저 시도
                if (TryLoadGpuModel())
                {
                    accelerationMode = "GPU Accelerated";
                    isModelLoaded = true;
                    Console.WriteLine("✅ GPU 가속 모델 로딩 성공!");
                    return;
                }

                // CPU 폴백
                if (TryLoadCpuModel())
                {
                    accelerationMode = "CPU Optimized";
                    isModelLoaded = true;
                    Console.WriteLine("✅ CPU 최적화 모델 로딩 성공!");
                    return;
                }

                // 안전 모드 폴백
                if (TryLoadSafeModel())
                {
                    accelerationMode = "Safe Mode";
                    isModelLoaded = true;
                    Console.WriteLine("✅ 안전 모드 로딩 성공!");
                    return;
                }
                
                accelerationMode = "Load Failed";
                isModelLoaded = false;
                Console.WriteLine("❌ 모든 로딩 전략 실패");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 모델 로딩 중 예외: {ex.Message}");
                accelerationMode = "Exception";
                isModelLoaded = false;
                
                lock (modelLock)
                {
                    model?.Dispose();
                    model = null;
                }
            }
        }

        /// <summary>
        /// GPU 가속 모델 로딩 시도
        /// </summary>
        private bool TryLoadGpuModel()
        {
            try
            {
                Console.WriteLine("⚡ GPU 가속 모델 로딩 시도...");
                
                var sessionOptions = new SessionOptions
                {
                    EnableCpuMemArena = true,
                    EnableMemoryPattern = true,
                    ExecutionMode = ExecutionMode.ORT_PARALLEL,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                };
                
                // GPU 실행 제공자 추가
                sessionOptions.AppendExecutionProvider_CUDA(0);
                sessionOptions.AppendExecutionProvider_CPU(); // 폴백
                
                lock (modelLock)
                {
                    model = new InferenceSession(modelPath, sessionOptions);
                    ValidateModelStructure();
                    TestInferencePerformance();
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ GPU 로딩 실패: {ex.Message}");
                
                lock (modelLock)
                {
                    model?.Dispose();
                    model = null;
                }
                
                return false;
            }
        }

        /// <summary>
        /// CPU 최적화 모델 로딩 시도
        /// </summary>
        private bool TryLoadCpuModel()
        {
            try
            {
                Console.WriteLine("🔥 CPU 최적화 모델 로딩 시도...");
                
                var sessionOptions = new SessionOptions
                {
                    EnableCpuMemArena = true,
                    EnableMemoryPattern = true,
                    ExecutionMode = ExecutionMode.ORT_PARALLEL,
                    InterOpNumThreads = Environment.ProcessorCount,
                    IntraOpNumThreads = Environment.ProcessorCount,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                };
                
                lock (modelLock)
                {
                    model = new InferenceSession(modelPath, sessionOptions);
                    ValidateModelStructure();
                    TestInferencePerformance();
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ CPU 최적화 로딩 실패: {ex.Message}");
                
                lock (modelLock)
                {
                    model?.Dispose();
                    model = null;
                }
                
                return false;
            }
        }

        /// <summary>
        /// 안전 모드 로딩 시도
        /// </summary>
        private bool TryLoadSafeModel()
        {
            try
            {
                Console.WriteLine("🛡️ 안전 모드 로딩 시도...");
                
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
                
                lock (modelLock)
                {
                    model = new InferenceSession(modelPath, sessionOptions);
                    ValidateModelStructure();
                    TestInferencePerformance();
                }
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 안전 모드 로딩 실패: {ex.Message}");
                
                lock (modelLock)
                {
                    model?.Dispose();
                    model = null;
                }
                
                return false;
            }
        }

        /// <summary>
        /// 가이드 기반 모델 구조 검증
        /// </summary>
        private void ValidateModelStructure()
        {
            if (model == null) return;
            
            try
            {
                Console.WriteLine("🔍 모델 구조 검증 중...");
                
                // 입력 메타데이터 확인
                var inputMeta = model.InputMetadata;
                Console.WriteLine($"📊 입력 메타데이터: {inputMeta.Count}개");
                
                foreach (var input in inputMeta)
                {
                    Console.WriteLine($"  - {input.Key}: {string.Join("x", input.Value.Dimensions)}");
                }
                
                // 출력 메타데이터 확인
                var outputMeta = model.OutputMetadata;
                Console.WriteLine($"📊 출력 메타데이터: {outputMeta.Count}개");
                
                foreach (var output in outputMeta)
                {
                    Console.WriteLine($"  - {output.Key}: {string.Join("x", output.Value.Dimensions)}");
                }
                
                // 가이드 기준 검증
                var expectedInput = new[] { 1, 3, 640, 640 };
                var expectedOutput = new[] { 1, 18, 8400 };
                
                var actualInput = inputMeta.First().Value.Dimensions;
                var actualOutput = outputMeta.First().Value.Dimensions;
                
                bool inputValid = actualInput.SequenceEqual(expectedInput);
                bool outputValid = actualOutput.SequenceEqual(expectedOutput);
                
                if (inputValid && outputValid)
                {
                    Console.WriteLine("✅ 모델 구조 검증 통과!");
                }
                else
                {
                    Console.WriteLine($"⚠️ 모델 구조 불일치:");
                    Console.WriteLine($"  입력 - 예상: {string.Join("x", expectedInput)}, 실제: {string.Join("x", actualInput)}");
                    Console.WriteLine($"  출력 - 예상: {string.Join("x", expectedOutput)}, 실제: {string.Join("x", actualOutput)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 모델 구조 검증 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 추론 성능 테스트
        /// </summary>
        private void TestInferencePerformance()
        {
            if (model == null) return;
            
            try
            {
                Console.WriteLine("🧪 추론 성능 테스트 시작...");
                
                // 더미 입력 생성
                var inputTensor = new DenseTensor<float>(new[] { 1, 3, 640, 640 });
                var random = new Random();
                
                // 정규화된 랜덤 값 (0~1)
                for (int i = 0; i < inputTensor.Length; i++)
                {
                    inputTensor.SetValue(i, (float)random.NextDouble());
                }
                
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("images", inputTensor)
                };
                
                // 워밍업
                Console.WriteLine("🔥 모델 워밍업 중...");
                using (var results = model.Run(inputs))
                {
                    var output = results.First().AsTensor<float>();
                    Console.WriteLine($"✅ 워밍업 완료: 출력 크기 {output.Length}");
                }
                
                // 성능 측정
                var times = new List<double>();
                const int testRuns = 5;
                
                for (int i = 0; i < testRuns; i++)
                {
                    var start = DateTime.Now;
                    
                    using (var results = model.Run(inputs))
                    {
                        var output = results.First().AsTensor<float>();
                        // 출력 유효성 검사
                        if (output.Length != 18 * 8400)
                        {
                            throw new Exception($"출력 크기 불일치: {output.Length}, 예상: {18 * 8400}");
                        }
                    }
                    
                    var elapsed = (DateTime.Now - start).TotalMilliseconds;
                    times.Add(elapsed);
                    Console.WriteLine($"  테스트 {i + 1}: {elapsed:F1}ms");
                }
                
                double avgTime = times.Average();
                double fps = 1000.0 / avgTime;
                
                Console.WriteLine($"📊 성능 결과:");
                Console.WriteLine($"  평균 추론 시간: {avgTime:F1}ms");
                Console.WriteLine($"  예상 FPS: {fps:F1}");
                
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 성능 테스트 실패: {ex.Message}");
                throw;
            }
        }

        public void SetTargets(List<string> targets)
        {
            if (isDisposed) return;
            
            Targets = targets ?? new List<string>();
            Console.WriteLine($"🎯 타겟 변경: {string.Join(", ", Targets)}");
        }

        public void SetStrength(int strength)
        {
            if (isDisposed) return;
            
            Strength = Math.Max(5, Math.Min(50, strength)); // 가이드 기준 확장
            Console.WriteLine($"💪 강도 변경: {Strength}");
        }

        public void SetCensorType(CensorType censorType)
        {
            if (isDisposed) return;
            
            CurrentCensorType = censorType;
            
            // 검열 타입 변경시 캐시 무효화
            lock (trackingLock)
            {
                foreach (var trackedObj in trackedObjects.Values)
                {
                    if (trackedObj.LastCensorType != censorType)
                    {
                        trackedObj.CachedCensorRegion?.Dispose();
                        trackedObj.CachedCensorRegion = null;
                    }
                }
            }
            
            Console.WriteLine($"🎨 검열 타입 변경: {censorType}");
        }

        /// <summary>
        /// 가이드 기반 정확한 객체 감지
        /// </summary>
        public List<Detection> DetectObjects(Mat frame)
        {
            if (isDisposed)
            {
                Console.WriteLine("⚠️ 프로세서가 해제된 상태입니다");
                return new List<Detection>();
            }

            if (frame == null || frame.Empty())
            {
                Console.WriteLine("⚠️ 입력 프레임이 null이거나 비어있습니다");
                return new List<Detection>();
            }

            if (!isModelLoaded)
            {
                Console.WriteLine("⚠️ 모델이 로드되지 않았습니다");
                return new List<Detection>();
            }

            try
            {
                var startTime = DateTime.Now;
                frameCounter++;

                Console.WriteLine($"🔍 객체 감지 시작 (프레임 #{frameCounter})");

                // 가이드 기반 전처리
                var preprocessResult = PreprocessImageOptimized(frame);
                if (preprocessResult.inputData == null)
                {
                    Console.WriteLine("❌ 전처리 실패");
                    return new List<Detection>();
                }

                // 가이드 기반 추론
                float[,,] output = null;
                lock (modelLock)
                {
                    if (model == null || isDisposed)
                    {
                        Console.WriteLine("❌ 모델이 null이거나 해제됨");
                        return new List<Detection>();
                    }

                    try
                    {
                        var inputTensor = new DenseTensor<float>(preprocessResult.inputData, new[] { 1, 3, 640, 640 });
                        var inputs = new List<NamedOnnxValue>
                        {
                            NamedOnnxValue.CreateFromTensor("images", inputTensor)
                        };

                        using var results = model.Run(inputs);
                        var tensorOutput = results.First().AsTensor<float>();
                        
                        // (1, 18, 8400) 형태로 변환
                        output = ConvertToArray(tensorOutput);
                        
                        Console.WriteLine($"✅ 추론 완료: {output.GetLength(0)}x{output.GetLength(1)}x{output.GetLength(2)}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ 추론 오류: {ex.Message}");
                        return new List<Detection>();
                    }
                }

                if (output == null)
                {
                    Console.WriteLine("❌ 추론 결과가 null입니다");
                    return new List<Detection>();
                }

                // 가이드 기반 후처리
                var rawDetections = ProcessOutputOptimized(output, 
                    preprocessResult.scale, preprocessResult.padX, preprocessResult.padY, 
                    preprocessResult.originalWidth, preprocessResult.originalHeight);

                Console.WriteLine($"🎯 원시 감지 결과: {rawDetections.Count}개");

                // 트래킹 적용
                var trackedDetections = ApplyTrackingOptimized(rawDetections);

                Console.WriteLine($"🎯 최종 감지 결과: {trackedDetections.Count}개");

                // 성능 통계 업데이트
                var detectionTime = (DateTime.Now - startTime).TotalMilliseconds;
                lock (statsLock)
                {
                    detectionTimes.Add(detectionTime);
                    if (detectionTimes.Count > 100) // 더 많은 샘플 보관
                    {
                        detectionTimes.RemoveRange(0, 50);
                    }
                    lastDetections = trackedDetections;
                }

                // 주기적 캐시 정리
                if (frameCounter % CACHE_CLEANUP_INTERVAL == 0)
                {
                    CleanupExpiredTracks();
                }

                Console.WriteLine($"✅ 감지 완료 ({detectionTime:F1}ms)");
                return trackedDetections;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 감지 중 예외 발생: {ex.Message}");
                return new List<Detection>();
            }
        }

        /// <summary>
        /// 가이드 기반 최적화된 이미지 전처리
        /// </summary>
        private (float[] inputData, float scale, int padX, int padY, int originalWidth, int originalHeight) PreprocessImageOptimized(Mat frame)
        {
            try
            {
                if (frame == null || frame.Empty() || isDisposed)
                {
                    Console.WriteLine("❌ 전처리: 입력 프레임 문제");
                    return (null, 1.0f, 0, 0, 0, 0);
                }

                const int inputSize = 640;
                int originalWidth = frame.Width;
                int originalHeight = frame.Height;

                Console.WriteLine($"🔧 전처리 시작: {originalWidth}x{originalHeight} -> {inputSize}x{inputSize}");

                // 가이드 기반: 비율 유지 리사이즈 계산
                float scale = Math.Min((float)inputSize / originalWidth, (float)inputSize / originalHeight);
                int newWidth = (int)(originalWidth * scale);
                int newHeight = (int)(originalHeight * scale);

                // letterbox 패딩 계산
                int padX = (inputSize - newWidth) / 2;
                int padY = (inputSize - newHeight) / 2;

                Mat resized = null;
                Mat padded = null;
                Mat rgb = null;

                try
                {
                    // 1. 비율 유지 리사이즈
                    resized = new Mat();
                    Cv2.Resize(frame, resized, new OpenCvSharp.Size(newWidth, newHeight), interpolation: InterpolationFlags.Linear);

                    // 2. letterbox 패딩 추가
                    padded = new Mat();
                    Cv2.CopyMakeBorder(resized, padded, padY, padY, padX, padX, 
                        BorderTypes.Constant, new Scalar(114, 114, 114));

                    // 3. BGR to RGB 변환
                    rgb = new Mat();
                    Cv2.CvtColor(padded, rgb, ColorConversionCodes.BGR2RGB);

                    // 4. 정규화 및 NCHW 형식으로 변환 (버퍼 재사용)
                    var indexer = rgb.GetGenericIndexer<Vec3b>();
                    
                    for (int h = 0; h < 640; h++)
                    {
                        for (int w = 0; w < 640; w++)
                        {
                            var pixel = indexer[h, w];
                            // NCHW 형식: [batch, channel, height, width]
                            reuseInputBuffer[0 * 640 * 640 + h * 640 + w] = pixel.Item0 / 255.0f; // R
                            reuseInputBuffer[1 * 640 * 640 + h * 640 + w] = pixel.Item1 / 255.0f; // G  
                            reuseInputBuffer[2 * 640 * 640 + h * 640 + w] = pixel.Item2 / 255.0f; // B
                        }
                    }

                    Console.WriteLine($"✅ 전처리 완료: scale={scale:F3}, pad=({padX},{padY})");
                    return (reuseInputBuffer, scale, padX, padY, originalWidth, originalHeight);
                }
                finally
                {
                    resized?.Dispose();
                    padded?.Dispose();
                    rgb?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 전처리 오류: {ex.Message}");
                return (null, 1.0f, 0, 0, 0, 0);
            }
        }

        /// <summary>
        /// 텐서를 3차원 배열로 변환
        /// </summary>
        private float[,,] ConvertToArray(Tensor<float> tensor)
        {
            try
            {
                var dimensions = tensor.Dimensions.ToArray(); // ReadOnlySpan을 배열로 변환
                if (dimensions.Length != 3 || dimensions[0] != 1 || dimensions[1] != 18 || dimensions[2] != 8400)
                {
                    // string.Join 수정: 배열을 객체 배열로 변환
                    var dimensionStrings = dimensions.Select(d => d.ToString()).ToArray();
                    throw new Exception($"예상치 못한 텐서 크기: {string.Join("x", dimensionStrings)}");
                }

                var result = new float[1, 18, 8400];
                
                for (int i = 0; i < 18; i++)
                {
                    for (int j = 0; j < 8400; j++)
                    {
                        result[0, i, j] = tensor[0, i, j];
                    }
                }
                
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 텐서 변환 오류: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 가이드 기반 최적화된 후처리
        /// </summary>
        private List<Detection> ProcessOutputOptimized(float[,,] output, float scale, int padX, int padY, 
                                                       int originalWidth, int originalHeight)
        {
            var detections = new List<Detection>();

            try
            {
                const int numClasses = 14;
                const int numDetections = 8400;
                
                Console.WriteLine($"🔧 후처리 시작: {numDetections}개 앵커 처리");

                int validDetections = 0;
                
                // 가이드 기준: 8400개 앵커 순회
                for (int i = 0; i < numDetections; i++)
                {
                    if (isDisposed) break;

                    // bbox 좌표 (center format)
                    float centerX = output[0, 0, i];
                    float centerY = output[0, 1, i];
                    float width = output[0, 2, i];
                    float height = output[0, 3, i];

                    // 클래스 확률 (4~17번 채널, 14개 클래스)
                    float maxScore = 0;
                    int maxClass = -1;
                    
                    for (int c = 0; c < numClasses; c++)
                    {
                        float score = output[0, 4 + c, i];
                        if (score > maxScore)
                        {
                            maxScore = score;
                            maxClass = c;
                        }
                    }

                    // 신뢰도 필터링
                    if (maxScore > ConfThreshold && ClassNames.ContainsKey(maxClass))
                    {
                        string className = ClassNames[maxClass];
                        
                        // 타겟 클래스 필터링
                        if (!Targets.Contains(className))
                            continue;

                        // 좌표 변환 (center -> corner + 패딩 보정 + 스케일링)
                        float x1 = (centerX - width / 2 - padX) / scale;
                        float y1 = (centerY - height / 2 - padY) / scale;
                        float x2 = (centerX + width / 2 - padX) / scale;
                        float y2 = (centerY + height / 2 - padY) / scale;

                        // 경계 확인
                        x1 = Math.Max(0, Math.Min(x1, originalWidth - 1));
                        y1 = Math.Max(0, Math.Min(y1, originalHeight - 1));
                        x2 = Math.Max(0, Math.Min(x2, originalWidth - 1));
                        y2 = Math.Max(0, Math.Min(y2, originalHeight - 1));

                        int boxWidth = (int)(x2 - x1);
                        int boxHeight = (int)(y2 - y1);
                        
                        // 최소 크기 확인
                        if (boxWidth > 5 && boxHeight > 5)
                        {
                            var detection = new Detection
                            {
                                ClassName = className,
                                Confidence = maxScore,
                                BBox = new int[] { (int)x1, (int)y1, (int)x2, (int)y2 },
                                ClassId = maxClass
                            };
                            
                            detections.Add(detection);
                            validDetections++;
                        }
                    }
                }

                Console.WriteLine($"✅ 후처리 완료: {validDetections}개 유효 감지");

                // 가이드 기준: NMS 적용
                if (detections.Count > 0)
                {
                    detections = ApplyOptimizedNMS(detections);
                    Console.WriteLine($"✅ NMS 적용 후: {detections.Count}개 감지");
                }

                return detections;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 후처리 오류: {ex.Message}");
                return new List<Detection>();
            }
        }

        /// <summary>
        /// 가이드 기반 최적화된 NMS
        /// </summary>
        private List<Detection> ApplyOptimizedNMS(List<Detection> detections)
        {
            if (detections.Count == 0 || isDisposed) return detections;

            try
            {
                // 신뢰도 기준 정렬
                detections = detections.OrderByDescending(d => d.Confidence).ToList();
                var keep = new List<Detection>();

                while (detections.Count > 0)
                {
                    var current = detections[0];
                    keep.Add(current);
                    detections.RemoveAt(0);

                    // 클래스별 최적화된 NMS 임계값 사용
                    float nmsThreshold = NmsThresholds.GetValueOrDefault(current.ClassName, 0.45f);

                    // 같은 클래스의 겹치는 박스 제거
                    for (int i = detections.Count - 1; i >= 0; i--)
                    {
                        if (detections[i].ClassName == current.ClassName)
                        {
                            float iou = CalculateIoU(current.BBox, detections[i].BBox);
                            if (iou > nmsThreshold)
                            {
                                detections.RemoveAt(i);
                            }
                        }
                    }
                }

                return keep;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ NMS 오류: {ex.Message}");
                return detections;
            }
        }

        /// <summary>
        /// IoU 계산
        /// </summary>
        private float CalculateIoU(int[] box1, int[] box2)
        {
            try
            {
                int x1 = Math.Max(box1[0], box2[0]);
                int y1 = Math.Max(box1[1], box2[1]);
                int x2 = Math.Min(box1[2], box2[2]);
                int y2 = Math.Min(box1[3], box2[3]);

                if (x2 <= x1 || y2 <= y1) return 0;

                float intersection = (x2 - x1) * (y2 - y1);
                float area1 = (box1[2] - box1[0]) * (box1[3] - box1[1]);
                float area2 = (box2[2] - box2[0]) * (box2[3] - box2[1]);
                float union = area1 + area2 - intersection;

                return union > 0 ? intersection / union : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 최적화된 트래킹 적용
        /// </summary>
        private List<Detection> ApplyTrackingOptimized(List<Detection> rawDetections)
        {
            if (isDisposed) return rawDetections;

            lock (trackingLock)
            {
                try
                {
                    if (rawDetections.Count == 0)
                    {
                        return rawDetections;
                    }

                    // Detection을 Rect2d로 변환
                    var detectionBoxes = rawDetections.Select(d => new Rect2d(
                        d.BBox[0], d.BBox[1], 
                        d.BBox[2] - d.BBox[0], 
                        d.BBox[3] - d.BBox[1]
                    )).ToList();

                    // SortTracker 업데이트
                    var trackedResults = tracker.Update(detectionBoxes);

                    var trackedDetections = new List<Detection>();

                    for (int i = 0; i < Math.Min(rawDetections.Count, trackedResults.Count); i++)
                    {
                        var detection = rawDetections[i];
                        var (trackId, trackedBox) = trackedResults[i];

                        detection.TrackId = trackId;

                        // 트래킹된 객체 정보 업데이트
                        UpdateTrackedObject(trackId, detection, trackedBox);

                        // 안정성 플래그 설정
                        detection.IsStable = trackedObjects.ContainsKey(trackId) && 
                                           trackedObjects[trackId].StableFrameCount >= STABLE_FRAME_THRESHOLD;

                        trackedDetections.Add(detection);
                    }

                    return trackedDetections;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ 트래킹 오류: {ex.Message}");
                    return rawDetections;
                }
            }
        }

        /// <summary>
        /// 트래킹된 객체 정보 업데이트
        /// </summary>
        private void UpdateTrackedObject(int trackId, Detection detection, Rect2d trackedBox)
        {
            if (!trackedObjects.ContainsKey(trackId))
            {
                trackedObjects[trackId] = new TrackedObject
                {
                    TrackId = trackId,
                    ClassName = detection.ClassName,
                    BoundingBox = trackedBox,
                    LastConfidence = detection.Confidence,
                    StableFrameCount = 1,
                    LastCensorType = CurrentCensorType,
                    LastStrength = Strength,
                    LastUpdated = DateTime.Now
                };
            }
            else
            {
                var trackedObj = trackedObjects[trackId];
                
                // 영역 변화 계산
                double areaChange = Math.Abs(trackedBox.Width * trackedBox.Height - 
                                           trackedObj.BoundingBox.Width * trackedObj.BoundingBox.Height) /
                                  Math.Max(trackedObj.BoundingBox.Width * trackedObj.BoundingBox.Height, 1.0);

                // 안정성 판단
                if (areaChange < CACHE_REGION_THRESHOLD && detection.ClassName == trackedObj.ClassName)
                {
                    trackedObj.StableFrameCount++;
                }
                else
                {
                    trackedObj.StableFrameCount = 1;
                    trackedObj.CachedCensorRegion?.Dispose();
                    trackedObj.CachedCensorRegion = null;
                }

                // 검열 설정 변경시 캐시 무효화
                if (trackedObj.LastCensorType != CurrentCensorType || 
                    trackedObj.LastStrength != Strength)
                {
                    trackedObj.CachedCensorRegion?.Dispose();
                    trackedObj.CachedCensorRegion = null;
                    trackedObj.LastCensorType = CurrentCensorType;
                    trackedObj.LastStrength = Strength;
                }

                trackedObj.BoundingBox = trackedBox;
                trackedObj.LastConfidence = detection.Confidence;
                trackedObj.LastUpdated = DateTime.Now;
            }
        }

        /// <summary>
        /// 만료된 트랙 정리
        /// </summary>
        private void CleanupExpiredTracks()
        {
            if (isDisposed) return;

            lock (trackingLock)
            {
                try
                {
                    var expiredTracks = trackedObjects.Where(kvp => 
                        (DateTime.Now - kvp.Value.LastUpdated).TotalSeconds > 2.0
                    ).Select(kvp => kvp.Key).ToList();

                    foreach (var trackId in expiredTracks)
                    {
                        trackedObjects[trackId].Dispose();
                        trackedObjects.Remove(trackId);
                    }

                    if (expiredTracks.Count > 0)
                    {
                        Console.WriteLine($"🧹 만료된 트랙 정리: {expiredTracks.Count}개");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ 트랙 정리 오류: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 캐싱 최적화된 검열 효과 적용
        /// </summary>
        public void ApplySingleCensorOptimized(Mat processedFrame, Detection detection)
        {
            if (isDisposed || processedFrame == null || processedFrame.Empty() || detection == null)
                return;

            try
            {
                var bbox = detection.BBox;
                int x1 = bbox[0], y1 = bbox[1], x2 = bbox[2], y2 = bbox[3];
                
                // 경계 확인
                if (x2 <= x1 || y2 <= y1 || x1 < 0 || y1 < 0 || 
                    x2 > processedFrame.Width || y2 > processedFrame.Height)
                    return;

                lock (trackingLock)
                {
                    // 트래킹된 객체의 캐시 활용
                    if (detection.TrackId != -1 && trackedObjects.ContainsKey(detection.TrackId))
                    {
                        var trackedObj = trackedObjects[detection.TrackId];

                        // 캐시된 검열 효과 사용
                        if (detection.IsStable && trackedObj.CachedCensorRegion != null &&
                            trackedObj.LastCensorType == CurrentCensorType &&
                            trackedObj.LastStrength == Strength)
                        {
                            if (TryApplyCachedCensor(processedFrame, detection, trackedObj))
                            {
                                cacheHits++;
                                return;
                            }
                        }

                        // 캐시 미스 - 새로운 검열 효과 생성
                        ApplyFreshCensor(processedFrame, detection, trackedObj);
                        cacheMisses++;
                    }
                    else
                    {
                        // 트래킹되지 않은 객체 - 일반 검열
                        ApplyDirectCensor(processedFrame, detection);
                        cacheMisses++;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 검열 효과 적용 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 캐시된 검열 효과 적용 시도
        /// </summary>
        private bool TryApplyCachedCensor(Mat processedFrame, Detection detection, TrackedObject trackedObj)
        {
            try
            {
                using (var region = new Mat(processedFrame, new Rect(detection.X1, detection.Y1, detection.Width, detection.Height)))
                {
                    if (trackedObj.CachedCensorRegion.Width == detection.Width && 
                        trackedObj.CachedCensorRegion.Height == detection.Height)
                    {
                        trackedObj.CachedCensorRegion.CopyTo(region);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 캐시 적용 실패: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// 새로운 검열 효과 생성 및 캐싱
        /// </summary>
        private void ApplyFreshCensor(Mat processedFrame, Detection detection, TrackedObject trackedObj)
        {
            try
            {
                using (var region = new Mat(processedFrame, new Rect(detection.X1, detection.Y1, detection.Width, detection.Height)))
                {
                    if (!region.Empty())
                    {
                        using (var censoredRegion = ApplyCensorEffect(region, Strength))
                        {
                            if (censoredRegion != null && !censoredRegion.Empty())
                            {
                                censoredRegion.CopyTo(region);

                                // 안정적인 객체인 경우 캐싱
                                if (detection.IsStable)
                                {
                                    trackedObj.CachedCensorRegion?.Dispose();
                                    trackedObj.CachedCensorRegion = censoredRegion.Clone();
                                    trackedObj.LastCensorType = CurrentCensorType;
                                    trackedObj.LastStrength = Strength;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 새 검열 효과 생성 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 직접 검열 효과 적용
        /// </summary>
        private void ApplyDirectCensor(Mat processedFrame, Detection detection)
        {
            try
            {
                using (var region = new Mat(processedFrame, new Rect(detection.X1, detection.Y1, detection.Width, detection.Height)))
                {
                    if (!region.Empty())
                    {
                        using (var censoredRegion = ApplyCensorEffect(region, Strength))
                        {
                            if (censoredRegion != null && !censoredRegion.Empty())
                            {
                                censoredRegion.CopyTo(region);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 직접 검열 효과 적용 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 검열 효과 적용 (가이드 기반)
        /// </summary>
        private Mat ApplyCensorEffect(Mat image, int strength)
        {
            return CurrentCensorType switch
            {
                CensorType.Mosaic => ApplyMosaicEffect(image, strength),
                CensorType.Blur => ApplyBlurEffect(image, strength),
                _ => ApplyMosaicEffect(image, strength)
            };
        }

        /// <summary>
        /// 가이드 기반 모자이크 효과
        /// </summary>
        private Mat ApplyMosaicEffect(Mat image, int mosaicSize)
        {
            if (isDisposed || image == null || image.Empty())
                return image?.Clone() ?? new Mat();

            try
            {
                int h = image.Height;
                int w = image.Width;

                // 가이드 기준: 축소 -> 확대
                int smallH = Math.Max(1, h / mosaicSize);
                int smallW = Math.Max(1, w / mosaicSize);

                using var smallImage = new Mat();
                using var mosaicImage = new Mat();
                
                Cv2.Resize(image, smallImage, new OpenCvSharp.Size(smallW, smallH), 
                    interpolation: InterpolationFlags.Linear);
                Cv2.Resize(smallImage, mosaicImage, new OpenCvSharp.Size(w, h), 
                    interpolation: InterpolationFlags.Nearest);

                return mosaicImage.Clone();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 모자이크 효과 오류: {ex.Message}");
                return image.Clone();
            }
        }

        /// <summary>
        /// 블러 효과
        /// </summary>
        private Mat ApplyBlurEffect(Mat image, int blurStrength)
        {
            if (isDisposed || image == null || image.Empty())
                return image?.Clone() ?? new Mat();

            try
            {
                int kernelSize = Math.Max(3, blurStrength + 1);
                if (kernelSize % 2 == 0) kernelSize += 1;

                var blurred = new Mat();
                Cv2.GaussianBlur(image, blurred, new OpenCvSharp.Size(kernelSize, kernelSize), 0);

                return blurred;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 블러 효과 오류: {ex.Message}");
                return image.Clone();
            }
        }

        public (Mat processedFrame, List<Detection> detections) DetectObjectsDetailed(Mat frame)
        {
            if (isDisposed) return (frame?.Clone() ?? new Mat(), new List<Detection>());

            var detections = DetectObjects(frame);
            var processedFrame = frame?.Clone() ?? new Mat();

            if (!processedFrame.Empty())
            {
                foreach (var detection in detections)
                {
                    ApplySingleCensorOptimized(processedFrame, detection);
                }
            }

            return (processedFrame, detections);
        }

        public Mat ApplyCensor(Mat image, int? strength = null)
        {
            return ApplyCensorEffect(image, strength ?? Strength);
        }

        public Mat ApplyMosaic(Mat image, int? strength = null)
        {
            return ApplyMosaicEffect(image, strength ?? Strength);
        }

        public Mat ApplyBlur(Mat image, int? strength = null)
        {
            return ApplyBlurEffect(image, strength ?? Strength);
        }

        public Mat CreateCensorForRegion(Mat frame, int x1, int y1, int x2, int y2, int? strength = null)
        {
            if (isDisposed) return null;

            try
            {
                if (x2 <= x1 || y2 <= y1 || x1 < 0 || y1 < 0 || x2 > frame.Width || y2 > frame.Height)
                    return null;

                using var region = new Mat(frame, new Rect(x1, y1, x2 - x1, y2 - y1));
                if (region.Empty())
                    return null;

                return ApplyCensorEffect(region, strength ?? Strength);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 영역 검열 효과 생성 오류: {ex.Message}");
                return null;
            }
        }

        public PerformanceStats GetPerformanceStats()
        {
            if (isDisposed) return new PerformanceStats();

            lock (statsLock)
            {
                if (detectionTimes.Count == 0)
                {
                    return new PerformanceStats
                    {
                        AvgDetectionTime = 0,
                        Fps = 0,
                        LastDetectionsCount = 0,
                        CacheHits = cacheHits,
                        CacheMisses = cacheMisses,
                        TrackedObjects = trackedObjects.Count
                    };
                }

                double avgTime = detectionTimes.Average() / 1000.0; // ms to seconds
                double fps = avgTime > 0 ? 1.0 / avgTime : 0;

                return new PerformanceStats
                {
                    AvgDetectionTime = avgTime,
                    Fps = fps,
                    LastDetectionsCount = lastDetections.Count,
                    CacheHits = cacheHits,
                    CacheMisses = cacheMisses,
                    TrackedObjects = trackedObjects.Count
                };
            }
        }

        public void UpdateConfig(Dictionary<string, object> kwargs)
        {
            if (isDisposed) return;

            foreach (var kvp in kwargs)
            {
                switch (kvp.Key)
                {
                    case "conf_threshold":
                        ConfThreshold = Math.Max(0.1f, Math.Min(0.9f, Convert.ToSingle(kvp.Value)));
                        break;
                    case "targets":
                        if (kvp.Value is List<string> targets)
                            SetTargets(targets);
                        break;
                    case "strength":
                        SetStrength(Convert.ToInt32(kvp.Value));
                        break;
                    case "censor_type":
                        if (kvp.Value is CensorType censorType)
                            SetCensorType(censorType);
                        break;
                }
            }

            Console.WriteLine($"⚙️ 설정 업데이트: {string.Join(", ", kwargs.Keys)}");
        }

        public bool IsModelLoaded()
        {
            return !isDisposed && isModelLoaded && model != null;
        }

        public List<string> GetAvailableClasses()
        {
            return ClassNames.Values.ToList();
        }

        public void ResetStats()
        {
            if (isDisposed) return;

            lock (statsLock)
            {
                detectionTimes.Clear();
                lastDetections.Clear();
                cacheHits = 0;
                cacheMisses = 0;
            }
            
            lock (trackingLock)
            {
                foreach (var trackedObj in trackedObjects.Values)
                {
                    trackedObj.Dispose();
                }
                trackedObjects.Clear();
            }
            
            Console.WriteLine("📊 성능 통계 및 트래킹 캐시 초기화됨");
        }

        public string GetAccelerationMode()
        {
            return accelerationMode;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!isDisposed && disposing)
            {
                isDisposed = true;
                
                // 트래킹된 객체들 정리
                lock (trackingLock)
                {
                    foreach (var trackedObj in trackedObjects.Values)
                    {
                        trackedObj.Dispose();
                    }
                    trackedObjects.Clear();
                }
                
                // 모델 정리
                lock (modelLock)
                {
                    try
                    {
                        model?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ 모델 정리 오류: {ex.Message}");
                    }
                    model = null;
                }
                
                Console.WriteLine($"🧹 {accelerationMode} 검열 프로세서 리소스 정리됨");
            }
        }
    }
}