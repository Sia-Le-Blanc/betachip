using System;
using System.Collections.Generic;
using System.Linq;
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
        public Mat? CachedCensorRegion { get; set; } // 모자이크/블러 캐싱
        public CensorType LastCensorType { get; set; } = CensorType.Mosaic;
        public int LastStrength { get; set; } = 15;
        public DateTime LastUpdated { get; set; } = DateTime.Now;
        
        public void Dispose()
        {
            CachedCensorRegion?.Dispose();
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
        Mat? CreateCensorForRegion(Mat frame, int x1, int y1, int x2, int y2, int? strength = null);
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
    /// CUDA 자동감지 및 최적화된 검열 프로세서 (SortTracker + 모자이크/블러 지원)
    /// </summary>
    public class MosaicProcessor : IProcessor, IDisposable
    {
        private readonly Dictionary<string, object> config;
        private InferenceSession? model;
        private readonly string modelPath;
        private string accelerationMode = "Unknown";

        // 트래킹 시스템 추가
        private readonly SortTracker tracker = new SortTracker();
        private readonly Dictionary<int, TrackedObject> trackedObjects = new Dictionary<int, TrackedObject>();
        private readonly object trackingLock = new object();

        // 성능 최적화 설정
        private const int STABLE_FRAME_THRESHOLD = 2; // 안정적 감지로 간주할 프레임 수
        private const int CACHE_CLEANUP_INTERVAL = 30; // 캐시 정리 간격 (프레임)
        private const double CACHE_REGION_THRESHOLD = 0.1; // 영역 변화 임계값
        private int frameCounter = 0;

        // 설정값들
        public float ConfThreshold { get; set; }
        public List<string> Targets { get; private set; }
        public int Strength { get; private set; }
        public CensorType CurrentCensorType { get; private set; }

        // 가이드의 클래스 이름 매핑 (정확히 14개 클래스)
        private readonly Dictionary<int, string> classNames = new Dictionary<int, string>
        {
            {0, "얼굴"}, {1, "가슴"}, {2, "겨드랑이"}, {3, "보지"}, {4, "발"},
            {5, "몸 전체"}, {6, "자지"}, {7, "팬티"}, {8, "눈"}, {9, "손"},
            {10, "교미"}, {11, "신발"}, {12, "가슴_옷"}, {13, "여성"}
        };

        // 성능 통계
        private readonly List<double> detectionTimes = new List<double>();
        private List<Detection> lastDetections = new List<Detection>();
        private readonly object statsLock = new object();
        private int cacheHits = 0;
        private int cacheMisses = 0;

        public MosaicProcessor(string? modelPath = null, Dictionary<string, object>? config = null)
        {
            Console.WriteLine("🔍 CUDA 자동감지 + 모자이크/블러 검열 프로세서 초기화");
            this.config = config ?? new Dictionary<string, object>();
            
            // 모델 경로 설정
            this.modelPath = modelPath ?? "Resources/best.onnx";
            if (!System.IO.File.Exists(this.modelPath))
            {
                this.modelPath = modelPath ?? Program.ONNX_MODEL_PATH;
            }

            // CUDA 우선, CPU 자동 폴백 모델 로드
            LoadModelWithAutoFallback();

            // 설정값들 초기화
            ConfThreshold = 0.3f; // 가이드 권장값
            Targets = new List<string> { "눈", "손" }; // 가이드의 기본 타겟
            Strength = 15; // 가이드 권장값
            CurrentCensorType = CensorType.Mosaic; // 기본값

            Console.WriteLine($"🎯 기본 타겟: {string.Join(", ", Targets)}");
            Console.WriteLine($"⚙️ 기본 설정: 강도={Strength}, 신뢰도={ConfThreshold}, 타입={CurrentCensorType}");
            Console.WriteLine($"🚀 가속 모드: {accelerationMode}");
            Console.WriteLine($"📊 SortTracker 활성화 - 검열 효과 캐싱으로 성능 향상");
        }

        private void LoadModelWithAutoFallback()
        {
            Console.WriteLine($"🤖 YOLO 모델 로딩 시작: {this.modelPath}");
            
            // 1순위: CUDA 시도
            if (TryLoadCudaModel())
            {
                accelerationMode = "CUDA GPU";
                Console.WriteLine("✅ CUDA GPU 가속 모델 로드 성공! (최고 성능)");
                return;
            }
            
            // 2순위: DirectML 시도 (Windows GPU 가속)
            if (TryLoadDirectMLModel())
            {
                accelerationMode = "DirectML GPU";
                Console.WriteLine("✅ DirectML GPU 가속 모델 로드 성공! (고성능)");
                return;
            }
            
            // 3순위: 최적화된 CPU
            if (TryLoadOptimizedCpuModel())
            {
                accelerationMode = "Optimized CPU";
                Console.WriteLine("✅ 최적화된 CPU 모델 로드 성공 (일반 성능)");
                return;
            }
            
            // 4순위: 기본 CPU
            if (TryLoadBasicCpuModel())
            {
                accelerationMode = "Basic CPU";
                Console.WriteLine("⚠️ 기본 CPU 모델 로드 성공 (저성능)");
                return;
            }
            
            // 모든 시도 실패
            accelerationMode = "Failed";
            Console.WriteLine("❌ 모든 모델 로딩 시도 실패");
            model = null;
        }

        private bool TryLoadCudaModel()
        {
            try
            {
                Console.WriteLine("🎯 CUDA GPU 가속 시도 중...");
                
                var sessionOptions = new SessionOptions();
                sessionOptions.AppendExecutionProvider_CUDA(0);
                sessionOptions.EnableCpuMemArena = true;
                sessionOptions.EnableMemoryPattern = true;
                sessionOptions.ExecutionMode = ExecutionMode.ORT_PARALLEL;
                sessionOptions.InterOpNumThreads = Environment.ProcessorCount;
                sessionOptions.IntraOpNumThreads = Environment.ProcessorCount;
                sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                
                model = new InferenceSession(this.modelPath, sessionOptions);
                TestModelInference();
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ CUDA 로딩 실패: {e.Message}");
                model?.Dispose();
                model = null;
                return false;
            }
        }

        private bool TryLoadDirectMLModel()
        {
            try
            {
                Console.WriteLine("🎯 DirectML GPU 가속 시도 중...");
                
                var sessionOptions = new SessionOptions();
                sessionOptions.AppendExecutionProvider_DML(0);
                sessionOptions.EnableCpuMemArena = true;
                sessionOptions.EnableMemoryPattern = true;
                sessionOptions.ExecutionMode = ExecutionMode.ORT_PARALLEL;
                sessionOptions.InterOpNumThreads = Environment.ProcessorCount;
                sessionOptions.IntraOpNumThreads = Environment.ProcessorCount;
                sessionOptions.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                
                model = new InferenceSession(this.modelPath, sessionOptions);
                TestModelInference();
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ DirectML 로딩 실패: {e.Message}");
                model?.Dispose();
                model = null;
                return false;
            }
        }

        private bool TryLoadOptimizedCpuModel()
        {
            try
            {
                Console.WriteLine("🎯 최적화된 CPU 시도 중...");
                
                var sessionOptions = new SessionOptions
                {
                    EnableCpuMemArena = true,
                    EnableMemoryPattern = true,
                    ExecutionMode = ExecutionMode.ORT_PARALLEL,
                    InterOpNumThreads = Environment.ProcessorCount,
                    IntraOpNumThreads = Environment.ProcessorCount,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
                };
                
                model = new InferenceSession(this.modelPath, sessionOptions);
                TestModelInference();
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ 최적화된 CPU 로딩 실패: {e.Message}");
                model?.Dispose();
                model = null;
                return false;
            }
        }

        private bool TryLoadBasicCpuModel()
        {
            try
            {
                Console.WriteLine("🎯 기본 CPU 시도 중...");
                
                var sessionOptions = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC
                };
                
                model = new InferenceSession(this.modelPath, sessionOptions);
                TestModelInference();
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ 기본 CPU 로딩 실패: {e.Message}");
                model?.Dispose();
                model = null;
                return false;
            }
        }

        private void TestModelInference()
        {
            if (model == null) return;
            
            // 가이드에 따른 정확한 입력 크기로 테스트
            var testInput = new DenseTensor<float>(new[] { 1, 3, 640, 640 });
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("images", testInput)
            };
            
            using var results = model.Run(inputs);
            var output = results.FirstOrDefault()?.AsEnumerable<float>().ToArray();
            
            // 가이드에 따른 출력 크기 검증: (1, 18, 8400)
            if (output == null || output.Length != 18 * 8400)
            {
                throw new Exception($"예상치 못한 모델 출력 크기: {output?.Length}, 예상: {18 * 8400}");
            }
            
            Console.WriteLine("✅ 모델 출력 형식 검증 완료: (1, 18, 8400)");
        }

        public void SetTargets(List<string> targets)
        {
            Targets = targets ?? new List<string>();
            Console.WriteLine($"🎯 타겟 변경: {string.Join(", ", Targets)}");
        }

        public void SetStrength(int strength)
        {
            Strength = Math.Max(1, Math.Min(50, strength));
            Console.WriteLine($"💪 강도 변경: {Strength}");
        }

        public void SetCensorType(CensorType censorType)
        {
            CurrentCensorType = censorType;
            
            // 검열 타입이 변경되면 모든 캐시 무효화
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

        public List<Detection> DetectObjects(Mat frame)
        {
            if (model == null || frame == null || frame.Empty())
                return new List<Detection>();

            try
            {
                var startTime = DateTime.Now;
                frameCounter++;

                // 가이드에 따른 전처리
                var (inputData, scale, padX, padY, originalWidth, originalHeight) = PreprocessImage(frame);
                if (inputData == null)
                    return new List<Detection>();

                // YOLO 추론
                var inputTensor = new DenseTensor<float>(inputData, new[] { 1, 3, 640, 640 });
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("images", inputTensor)
                };

                using var results = model.Run(inputs);
                var output = results.FirstOrDefault()?.AsEnumerable<float>().ToArray();

                if (output == null)
                    return new List<Detection>();

                // 가이드에 따른 후처리
                var rawDetections = PostprocessOutput(output, scale, padX, padY, originalWidth, originalHeight);

                // SortTracker 적용
                var trackedDetections = ApplyTracking(rawDetections);

                // 성능 통계 업데이트
                var detectionTime = (DateTime.Now - startTime).TotalSeconds;
                lock (statsLock)
                {
                    detectionTimes.Add(detectionTime);
                    if (detectionTimes.Count > 100)
                    {
                        detectionTimes.RemoveRange(0, detectionTimes.Count - 50);
                    }
                    lastDetections = trackedDetections;
                }

                // 주기적으로 캐시 정리
                if (frameCounter % CACHE_CLEANUP_INTERVAL == 0)
                {
                    CleanupExpiredTracks();
                }

                return trackedDetections;
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ 객체 감지 오류: {e.Message}");
                return new List<Detection>();
            }
        }

        /// <summary>
        /// 가이드에 따른 이미지 전처리 (letterbox 포함)
        /// </summary>
        private (float[]? inputData, float scale, int padX, int padY, int originalWidth, int originalHeight) PreprocessImage(Mat frame)
        {
            try
            {
                int inputSize = 640;
                int originalWidth = frame.Width;
                int originalHeight = frame.Height;

                // 비율 유지 리사이즈 계산
                float scale = Math.Min((float)inputSize / originalWidth, (float)inputSize / originalHeight);
                int newWidth = (int)(originalWidth * scale);
                int newHeight = (int)(originalHeight * scale);

                // 패딩 계산 (letterbox)
                int padX = (inputSize - newWidth) / 2;
                int padY = (inputSize - newHeight) / 2;

                // 리사이즈
                using var resized = new Mat();
                Cv2.Resize(frame, resized, new OpenCvSharp.Size(newWidth, newHeight));

                // 패딩 추가
                using var padded = new Mat();
                Cv2.CopyMakeBorder(resized, padded, padY, padY, padX, padX, BorderTypes.Constant, new Scalar(114, 114, 114));

                // BGR to RGB 변환
                using var rgb = new Mat();
                Cv2.CvtColor(padded, rgb, ColorConversionCodes.BGR2RGB);

                // 정규화 및 NCHW 형식으로 변환
                var inputData = new float[3 * 640 * 640];
                var indexer = rgb.GetGenericIndexer<Vec3b>();
                
                for (int h = 0; h < 640; h++)
                {
                    for (int w = 0; w < 640; w++)
                    {
                        var pixel = indexer[h, w];
                        // NCHW 형식: [batch, channel, height, width]
                        inputData[0 * 640 * 640 + h * 640 + w] = pixel.Item0 / 255.0f; // R
                        inputData[1 * 640 * 640 + h * 640 + w] = pixel.Item1 / 255.0f; // G  
                        inputData[2 * 640 * 640 + h * 640 + w] = pixel.Item2 / 255.0f; // B
                    }
                }

                return (inputData, scale, padX, padY, originalWidth, originalHeight);
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ 전처리 오류: {e.Message}");
                return (null, 1.0f, 0, 0, frame.Width, frame.Height);
            }
        }

        /// <summary>
        /// 가이드에 따른 후처리 (좌표 변환 포함)
        /// </summary>
        private List<Detection> PostprocessOutput(float[] output, float scale, int padX, int padY, int originalWidth, int originalHeight)
        {
            var detections = new List<Detection>();

            try
            {
                const int numClasses = 14;
                const int numDetections = 8400;
                
                if (output.Length != 18 * numDetections)
                {
                    Console.WriteLine($"❌ 예상치 못한 출력 크기: {output.Length}, 예상: {18 * numDetections}");
                    return detections;
                }

                for (int i = 0; i < numDetections; i++)
                {
                    // bbox 좌표 (center format)
                    float centerX = output[0 * numDetections + i];
                    float centerY = output[1 * numDetections + i];
                    float width = output[2 * numDetections + i];
                    float height = output[3 * numDetections + i];

                    // 클래스 확률 (4~17번 채널)
                    float maxScore = 0;
                    int maxClass = -1;
                    for (int c = 0; c < numClasses; c++)
                    {
                        float score = output[(4 + c) * numDetections + i];
                        if (score > maxScore)
                        {
                            maxScore = score;
                            maxClass = c;
                        }
                    }

                    // 신뢰도 필터링
                    if (maxScore > ConfThreshold && classNames.ContainsKey(maxClass))
                    {
                        string className = classNames[maxClass];
                        
                        // 타겟 필터링
                        if (!Targets.Contains(className))
                            continue;

                        // 좌표 변환 (패딩 보정 + 스케일링)
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
                        
                        if (boxWidth > 10 && boxHeight > 10)
                        {
                            var detection = new Detection
                            {
                                ClassName = className,
                                Confidence = maxScore,
                                BBox = new int[] { (int)x1, (int)y1, (int)x2, (int)y2 },
                                ClassId = maxClass
                            };
                            detections.Add(detection);
                        }
                    }
                }

                // NMS 적용
                if (detections.Count > 0)
                {
                    detections = ApplyNMS(detections);
                }

                return detections;
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ 후처리 오류: {e.Message}");
                return new List<Detection>();
            }
        }

        /// <summary>
        /// SortTracker를 사용한 트래킹 적용
        /// </summary>
        private List<Detection> ApplyTracking(List<Detection> rawDetections)
        {
            lock (trackingLock)
            {
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
                                          (trackedObj.BoundingBox.Width * trackedObj.BoundingBox.Height);

                        // 안정적인 감지인지 판단
                        if (areaChange < CACHE_REGION_THRESHOLD && 
                            detection.ClassName == trackedObj.ClassName)
                        {
                            trackedObj.StableFrameCount++;
                        }
                        else
                        {
                            trackedObj.StableFrameCount = 1;
                            // 영역이 많이 변했으면 캐시 무효화
                            trackedObj.CachedCensorRegion?.Dispose();
                            trackedObj.CachedCensorRegion = null;
                        }

                        // 검열 설정이 변경되었으면 캐시 무효화
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

                    // 안정성 플래그 설정
                    detection.IsStable = trackedObjects[trackId].StableFrameCount >= STABLE_FRAME_THRESHOLD;

                    trackedDetections.Add(detection);
                }

                return trackedDetections;
            }
        }

        /// <summary>
        /// NMS 적용
        /// </summary>
        private List<Detection> ApplyNMS(List<Detection> detections)
        {
            if (detections.Count == 0) return detections;

            var nmsThresholds = new Dictionary<string, float>
            {
                ["얼굴"] = 0.3f, ["가슴"] = 0.4f, ["겨드랑이"] = 0.4f, ["보지"] = 0.3f, ["발"] = 0.5f,
                ["몸 전체"] = 0.6f, ["자지"] = 0.3f, ["팬티"] = 0.4f, ["눈"] = 0.2f, ["손"] = 0.5f,
                ["교미"] = 0.3f, ["신발"] = 0.5f, ["가슴_옷"] = 0.4f, ["여성"] = 0.7f
            };

            detections = detections.OrderByDescending(d => d.Confidence).ToList();
            var keep = new List<Detection>();

            while (detections.Count > 0)
            {
                var current = detections[0];
                keep.Add(current);
                detections.RemoveAt(0);

                float nmsThreshold = nmsThresholds.GetValueOrDefault(current.ClassName, 0.45f);

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
        
        private float CalculateIoU(int[] box1, int[] box2)
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

        /// <summary>
        /// 만료된 트랙 정리
        /// </summary>
        private void CleanupExpiredTracks()
        {
            lock (trackingLock)
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
        }

        /// <summary>
        /// 캐싱 최적화된 검열 효과 적용 메서드 (MainForm.cs에서 사용)
        /// </summary>
        public void ApplySingleCensorOptimized(Mat processedFrame, Detection detection)
        {
            try
            {
                var bbox = detection.BBox;
                int x1 = bbox[0], y1 = bbox[1], x2 = bbox[2], y2 = bbox[3];
                
                if (x2 > x1 && y2 > y1 && x1 >= 0 && y1 >= 0 && x2 <= processedFrame.Width && y2 <= processedFrame.Height)
                {
                    lock (trackingLock)
                    {
                        // 트래킹된 객체이고 안정적인 경우 캐시 사용
                        if (detection.TrackId != -1 && trackedObjects.ContainsKey(detection.TrackId))
                        {
                            var trackedObj = trackedObjects[detection.TrackId];

                            // 안정적인 객체이고 캐시된 검열 효과가 있는 경우
                            if (detection.IsStable && trackedObj.CachedCensorRegion != null &&
                                trackedObj.LastCensorType == CurrentCensorType &&
                                trackedObj.LastStrength == Strength)
                            {
                                try
                                {
                                    using (var region = new Mat(processedFrame, new Rect(x1, y1, x2 - x1, y2 - y1)))
                                    {
                                        // 캐시된 검열 효과 크기가 현재 영역과 일치하는지 확인
                                        if (trackedObj.CachedCensorRegion.Width == (x2 - x1) && 
                                            trackedObj.CachedCensorRegion.Height == (y2 - y1))
                                        {
                                            trackedObj.CachedCensorRegion.CopyTo(region);
                                            cacheHits++;
                                            return;
                                        }
                                    }
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine($"⚠️ 캐시된 검열 효과 적용 실패: {e.Message}");
                                }
                            }

                            // 캐시 미스 - 새로운 검열 효과 생성 및 캐싱
                            using (var region = new Mat(processedFrame, new Rect(x1, y1, x2 - x1, y2 - y1)))
                            {
                                if (!region.Empty())
                                {
                                    using (var censoredRegion = ApplyCensor(region, Strength))
                                    {
                                        censoredRegion.CopyTo(region);

                                        // 안정적인 객체인 경우 검열 효과 캐싱
                                        if (detection.IsStable)
                                        {
                                            trackedObj.CachedCensorRegion?.Dispose();
                                            trackedObj.CachedCensorRegion = censoredRegion.Clone();
                                            trackedObj.LastCensorType = CurrentCensorType;
                                            trackedObj.LastStrength = Strength;
                                        }
                                    }
                                    cacheMisses++;
                                }
                            }
                        }
                        else
                        {
                            // 트래킹되지 않은 객체 - 일반 검열 효과 적용
                            using (var region = new Mat(processedFrame, new Rect(x1, y1, x2 - x1, y2 - y1)))
                            {
                                if (!region.Empty())
                                {
                                    using (var censoredRegion = ApplyCensor(region, Strength))
                                    {
                                        censoredRegion.CopyTo(region);
                                    }
                                }
                            }
                            cacheMisses++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 최적화된 검열 효과 적용 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// 기존 호환성을 위한 메서드
        /// </summary>
        public void ApplySingleMosaicOptimized(Mat processedFrame, Detection detection)
        {
            ApplySingleCensorOptimized(processedFrame, detection);
        }

        public (Mat processedFrame, List<Detection> detections) DetectObjectsDetailed(Mat frame)
        {
            var detections = DetectObjects(frame);
            var processedFrame = frame.Clone();

            foreach (var detection in detections)
            {
                ApplySingleCensorOptimized(processedFrame, detection);
            }

            return (processedFrame, detections);
        }

        /// <summary>
        /// 현재 설정에 따른 검열 효과 적용 (모자이크 또는 블러)
        /// </summary>
        public Mat ApplyCensor(Mat image, int? strength = null)
        {
            return CurrentCensorType switch
            {
                CensorType.Mosaic => ApplyMosaic(image, strength),
                CensorType.Blur => ApplyBlur(image, strength),
                _ => ApplyMosaic(image, strength)
            };
        }

        /// <summary>
        /// 모자이크 효과 적용
        /// </summary>
        public Mat ApplyMosaic(Mat image, int? strength = null)
        {
            int mosaicStrength = strength ?? Strength;
            
            if (image == null || image.Empty())
                return image?.Clone() ?? new Mat();

            try
            {
                int h = image.Height;
                int w = image.Width;

                int smallH = Math.Max(1, h / mosaicStrength);
                int smallW = Math.Max(1, w / mosaicStrength);

                using var small = new Mat();
                Cv2.Resize(image, small, new OpenCvSharp.Size(smallW, smallH), interpolation: InterpolationFlags.Linear);
                
                var mosaic = new Mat();
                Cv2.Resize(small, mosaic, new OpenCvSharp.Size(w, h), interpolation: InterpolationFlags.Nearest);

                return mosaic;
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ 모자이크 적용 오류: {e.Message}");
                return image.Clone();
            }
        }

        /// <summary>
        /// 블러 효과 적용
        /// </summary>
        public Mat ApplyBlur(Mat image, int? strength = null)
        {
            int blurStrength = strength ?? Strength;
            
            if (image == null || image.Empty())
                return image?.Clone() ?? new Mat();

            try
            {
                // 블러 강도를 커널 크기로 변환 (홀수로 만들기)
                int kernelSize = Math.Max(3, blurStrength * 2 + 1);
                if (kernelSize % 2 == 0) kernelSize += 1;

                var blurred = new Mat();
                
                // 가우시안 블러 적용
                Cv2.GaussianBlur(image, blurred, new OpenCvSharp.Size(kernelSize, kernelSize), 0);

                return blurred;
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ 블러 적용 오류: {e.Message}");
                return image.Clone();
            }
        }

        public Mat? CreateCensorForRegion(Mat frame, int x1, int y1, int x2, int y2, int? strength = null)
        {
            try
            {
                if (x2 <= x1 || y2 <= y1 || x1 < 0 || y1 < 0 || x2 > frame.Width || y2 > frame.Height)
                    return null;

                using var region = new Mat(frame, new Rect(x1, y1, x2 - x1, y2 - y1));
                if (region.Empty())
                    return null;

                return ApplyCensor(region, strength);
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ 영역 검열 효과 생성 오류: {e.Message}");
                return null;
            }
        }

        public PerformanceStats GetPerformanceStats()
        {
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

                double avgTime = detectionTimes.Average();
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
            foreach (var kvp in kwargs)
            {
                switch (kvp.Key)
                {
                    case "conf_threshold":
                        ConfThreshold = Math.Max(0.01f, Math.Min(0.99f, Convert.ToSingle(kvp.Value)));
                        break;
                    case "targets":
                        if (kvp.Value is List<string> targets)
                            Targets = targets;
                        break;
                    case "strength":
                        Strength = Math.Max(1, Math.Min(50, Convert.ToInt32(kvp.Value)));
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
            return model != null;
        }

        public List<string> GetAvailableClasses()
        {
            return classNames.Values.ToList();
        }

        public void ResetStats()
        {
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
            if (disposing)
            {
                // 트래킹된 객체들 정리
                lock (trackingLock)
                {
                    foreach (var trackedObj in trackedObjects.Values)
                    {
                        trackedObj.Dispose();
                    }
                    trackedObjects.Clear();
                }
                
                model?.Dispose();
                Console.WriteLine($"🧹 {accelerationMode} 검열 프로세서 + SortTracker 리소스 정리됨");
            }
        }
    }
}