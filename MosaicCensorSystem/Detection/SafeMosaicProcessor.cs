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
    /// 크래시 방지 안전 프로세서 (단계별 안전성 확인)
    /// </summary>
    public class SafeMosaicProcessor : IProcessor, IDisposable
    {
        private InferenceSession model;
        private readonly string modelPath;
        private volatile bool isDisposed = false;
        private volatile bool isModelLoaded = false;
        private volatile bool isModelTested = false;
        private readonly object modelLock = new object();
        
        // 안전 설정
        private const int MAX_DETECTIONS = 10; // 메모리 보호
        private const int SAFE_INPUT_SIZE = 320; // 작은 입력 크기로 시작
        private int currentInputSize = SAFE_INPUT_SIZE;
        private bool useOptimizedMode = false;
        
        // 기본 설정
        public float ConfThreshold { get; set; } = 0.5f;
        public List<string> Targets { get; private set; } = new List<string> { "얼굴" };
        public int Strength { get; private set; } = 15;
        public CensorType CurrentCensorType { get; private set; } = CensorType.Mosaic;
        
        private readonly List<double> detectionTimes = new List<double>();
        private readonly object statsLock = new object();
        
        public SafeMosaicProcessor(string modelPath = null, Dictionary<string, object> config = null)
        {
            Console.WriteLine("🛡️ 안전 프로세서 초기화 시작");
            
            this.modelPath = FindModelPath(modelPath);
            Console.WriteLine($"📁 모델 경로: {this.modelPath}");
            
            // 단계별 안전 초기화
            InitializeSafely();
        }
        
        private string FindModelPath(string providedPath)
        {
            var candidates = new[]
            {
                providedPath,
                Program.ONNX_MODEL_PATH,
                "best.onnx",
                "Resources/best.onnx",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "best.onnx"),
                Path.Combine(Environment.CurrentDirectory, "Resources", "best.onnx")
            };
            
            foreach (var path in candidates)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var fileInfo = new FileInfo(path);
                    if (fileInfo.Length > 1024 * 1024) // 최소 1MB
                    {
                        Console.WriteLine($"✅ 유효한 모델 발견: {path} ({fileInfo.Length / (1024 * 1024):F1} MB)");
                        return path;
                    }
                }
            }
            
            Console.WriteLine("❌ 유효한 모델 파일을 찾을 수 없음");
            return candidates.FirstOrDefault() ?? "best.onnx";
        }
        
        private void InitializeSafely()
        {
            try
            {
                Console.WriteLine("🔍 1단계: 모델 파일 존재 확인");
                if (!File.Exists(modelPath))
                {
                    Console.WriteLine("❌ 모델 파일이 존재하지 않음 - 안전 모드로 계속");
                    return;
                }
                
                Console.WriteLine("🔍 2단계: 기본 세션 생성 테스트");
                if (!TryCreateBasicSession())
                {
                    Console.WriteLine("❌ 기본 세션 생성 실패");
                    return;
                }
                
                Console.WriteLine("🔍 3단계: 간단한 추론 테스트");
                if (!TrySimpleInference())
                {
                    Console.WriteLine("❌ 간단한 추론 실패 - 모델 로드만 유지");
                    return;
                }
                
                Console.WriteLine("🔍 4단계: 최적화 모드 활성화");
                TryOptimizedMode();
                
                isModelLoaded = true;
                isModelTested = true;
                Console.WriteLine("✅ 안전 프로세서 초기화 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 안전 초기화 실패: {ex.Message}");
                CleanupModel();
            }
        }
        
        private bool TryCreateBasicSession()
        {
            try
            {
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
                }
                
                Console.WriteLine("✅ 기본 세션 생성 성공");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 기본 세션 생성 실패: {ex.Message}");
                return false;
            }
        }
        
        private bool TrySimpleInference()
        {
            if (model == null) return false;
            
            try
            {
                Console.WriteLine($"🧪 {currentInputSize}x{currentInputSize} 입력으로 추론 테스트");
                
                // 작은 입력으로 시작
                var inputTensor = new DenseTensor<float>(new[] { 1, 3, currentInputSize, currentInputSize });
                
                // 안전한 값으로 채우기
                for (int i = 0; i < inputTensor.Length; i++)
                {
                    inputTensor.SetValue(i, 0.5f);
                }
                
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("images", inputTensor)
                };
                
                lock (modelLock)
                {
                    if (model == null || isDisposed) return false;
                    
                    using var results = model.Run(inputs);
                    var output = results.First().AsTensor<float>();
                    
                    Console.WriteLine($"✅ 추론 성공: 출력 크기 {output.Length}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 간단한 추론 실패: {ex.Message}");
                
                // 더 작은 입력 크기로 재시도
                if (currentInputSize > 160)
                {
                    currentInputSize = 160;
                    Console.WriteLine($"🔄 입력 크기를 {currentInputSize}로 줄여서 재시도");
                    return TrySimpleInference();
                }
                
                return false;
            }
        }
        
        private void TryOptimizedMode()
        {
            try
            {
                Console.WriteLine("🚀 최적화 모드 테스트");
                
                // 640x640으로 테스트
                var testInputSize = 640;
                var inputTensor = new DenseTensor<float>(new[] { 1, 3, testInputSize, testInputSize });
                
                // 패턴으로 채우기
                for (int i = 0; i < inputTensor.Length; i++)
                {
                    inputTensor.SetValue(i, (float)(Math.Sin(i * 0.001) * 0.5 + 0.5));
                }
                
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("images", inputTensor)
                };
                
                lock (modelLock)
                {
                    if (model == null || isDisposed) return;
                    
                    var startTime = DateTime.Now;
                    using var results = model.Run(inputs);
                    var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                    
                    var output = results.First().AsTensor<float>();
                    Console.WriteLine($"✅ 최적화 테스트 성공: {elapsed:F1}ms, 출력 {output.Length}");
                    
                    if (elapsed < 2000) // 2초 이내
                    {
                        currentInputSize = testInputSize;
                        useOptimizedMode = true;
                        Console.WriteLine("🚀 최적화 모드 활성화");
                    }
                    else
                    {
                        Console.WriteLine("⚠️ 느린 성능 - 안전 모드 유지");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 최적화 모드 실패 (안전 모드 유지): {ex.Message}");
            }
        }
        
        public List<Detection> DetectObjects(Mat frame)
        {
            if (isDisposed || !isModelLoaded || frame == null || frame.Empty())
            {
                return new List<Detection>();
            }
            
            try
            {
                var startTime = DateTime.Now;
                Console.WriteLine($"🔍 안전 감지 시작 ({currentInputSize}x{currentInputSize})");
                
                // 안전한 전처리
                var preprocessed = SafePreprocess(frame);
                if (preprocessed.inputData == null)
                {
                    Console.WriteLine("❌ 전처리 실패");
                    return new List<Detection>();
                }
                
                // 안전한 추론
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
                        var inputTensor = new DenseTensor<float>(preprocessed.inputData, 
                            new[] { 1, 3, currentInputSize, currentInputSize });
                        
                        var inputs = new List<NamedOnnxValue>
                        {
                            NamedOnnxValue.CreateFromTensor("images", inputTensor)
                        };
                        
                        using var results = model.Run(inputs);
                        var tensorOutput = results.First().AsTensor<float>();
                        
                        output = ConvertToArraySafely(tensorOutput);
                        Console.WriteLine($"✅ 추론 완료");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ 추론 오류: {ex.Message}");
                        return new List<Detection>();
                    }
                }
                
                if (output == null)
                {
                    Console.WriteLine("❌ 추론 결과가 null");
                    return new List<Detection>();
                }
                
                // 안전한 후처리
                var detections = SafePostprocess(output, preprocessed, frame.Width, frame.Height);
                
                var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                lock (statsLock)
                {
                    detectionTimes.Add(elapsed);
                    if (detectionTimes.Count > 50)
                    {
                        detectionTimes.RemoveRange(0, 25);
                    }
                }
                
                Console.WriteLine($"✅ 안전 감지 완료: {detections.Count}개 ({elapsed:F1}ms)");
                return detections;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 감지 중 예외: {ex.Message}");
                return new List<Detection>();
            }
        }
        
        private (float[] inputData, float scale, int padX, int padY) SafePreprocess(Mat frame)
        {
            try
            {
                int originalWidth = frame.Width;
                int originalHeight = frame.Height;
                
                Console.WriteLine($"🔧 안전 전처리: {originalWidth}x{originalHeight} -> {currentInputSize}x{currentInputSize}");
                
                // 스케일 계산
                float scale = Math.Min((float)currentInputSize / originalWidth, (float)currentInputSize / originalHeight);
                int newWidth = (int)(originalWidth * scale);
                int newHeight = (int)(originalHeight * scale);
                
                int padX = (currentInputSize - newWidth) / 2;
                int padY = (currentInputSize - newHeight) / 2;
                
                Mat resized = null;
                Mat padded = null;
                Mat rgb = null;
                
                try
                {
                    // 리사이즈
                    resized = new Mat();
                    Cv2.Resize(frame, resized, new OpenCvSharp.Size(newWidth, newHeight));
                    
                    // 패딩
                    padded = new Mat();
                    Cv2.CopyMakeBorder(resized, padded, padY, padY, padX, padX, 
                        BorderTypes.Constant, new Scalar(114, 114, 114));
                    
                    // BGR to RGB
                    rgb = new Mat();
                    Cv2.CvtColor(padded, rgb, ColorConversionCodes.BGR2RGB);
                    
                    // 정규화
                    var inputData = new float[3 * currentInputSize * currentInputSize];
                    var indexer = rgb.GetGenericIndexer<Vec3b>();
                    
                    int idx = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        for (int h = 0; h < currentInputSize; h++)
                        {
                            for (int w = 0; w < currentInputSize; w++)
                            {
                                var pixel = indexer[h, w];
                                float value = c == 0 ? pixel.Item0 : (c == 1 ? pixel.Item1 : pixel.Item2);
                                inputData[idx++] = value / 255.0f;
                            }
                        }
                    }
                    
                    Console.WriteLine($"✅ 전처리 완료: scale={scale:F3}, pad=({padX},{padY})");
                    return (inputData, scale, padX, padY);
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
                return (null, 1.0f, 0, 0);
            }
        }
        
        private float[,,] ConvertToArraySafely(Tensor<float> tensor)
        {
            try
            {
                var dimensions = tensor.Dimensions.ToArray();
                Console.WriteLine($"📊 텐서 차원: {string.Join("x", dimensions)}");
                
                if (dimensions.Length != 3)
                {
                    throw new Exception($"예상치 못한 차원 수: {dimensions.Length}");
                }
                
                int batch = dimensions[0];
                int channels = dimensions[1];
                int anchors = dimensions[2];
                
                var result = new float[batch, channels, anchors];
                
                for (int b = 0; b < batch; b++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        for (int a = 0; a < anchors; a++)
                        {
                            result[b, c, a] = tensor[b, c, a];
                        }
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
        
        private List<Detection> SafePostprocess(float[,,] output, 
            (float[] inputData, float scale, int padX, int padY) preprocessed,
            int originalWidth, int originalHeight)
        {
            var detections = new List<Detection>();
            
            try
            {
                int channels = output.GetLength(1);
                int anchors = output.GetLength(2);
                
                Console.WriteLine($"🔧 후처리: {channels}채널, {anchors}앵커");
                
                // 안전한 클래스 매핑 (기본 클래스만)
                var safeClasses = new Dictionary<int, string>
                {
                    {0, "얼굴"}, {8, "눈"}, {9, "손"}, {11, "신발"}
                };
                
                int validDetections = 0;
                
                for (int i = 0; i < anchors && validDetections < MAX_DETECTIONS; i++)
                {
                    if (isDisposed) break;
                    
                    try
                    {
                        // bbox 좌표 (center format)
                        float centerX = output[0, 0, i];
                        float centerY = output[0, 1, i];
                        float width = output[0, 2, i];
                        float height = output[0, 3, i];
                        
                        // 안전한 범위 확인
                        if (centerX < 0 || centerY < 0 || width <= 0 || height <= 0)
                            continue;
                        
                        // 클래스 확률 확인 (안전한 범위만)
                        float maxScore = 0;
                        int maxClass = -1;
                        
                        int maxClassCheck = Math.Min(14, channels - 4);
                        for (int c = 0; c < maxClassCheck; c++)
                        {
                            if (4 + c >= channels) break;
                            
                            float score = output[0, 4 + c, i];
                            if (score > maxScore && safeClasses.ContainsKey(c))
                            {
                                maxScore = score;
                                maxClass = c;
                            }
                        }
                        
                        // 신뢰도 및 타겟 필터링
                        if (maxScore > ConfThreshold && maxClass != -1)
                        {
                            string className = safeClasses[maxClass];
                            
                            if (!Targets.Contains(className))
                                continue;
                            
                            // 좌표 변환
                            float x1 = (centerX - width / 2 - preprocessed.padX) / preprocessed.scale;
                            float y1 = (centerY - height / 2 - preprocessed.padY) / preprocessed.scale;
                            float x2 = (centerX + width / 2 - preprocessed.padX) / preprocessed.scale;
                            float y2 = (centerY + height / 2 - preprocessed.padY) / preprocessed.scale;
                            
                            // 경계 확인
                            x1 = Math.Max(0, Math.Min(x1, originalWidth - 1));
                            y1 = Math.Max(0, Math.Min(y1, originalHeight - 1));
                            x2 = Math.Max(x1 + 1, Math.Min(x2, originalWidth));
                            y2 = Math.Max(y1 + 1, Math.Min(y2, originalHeight));
                            
                            int boxWidth = (int)(x2 - x1);
                            int boxHeight = (int)(y2 - y1);
                            
                            // 최소 크기 확인
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
                                validDetections++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠️ 앵커 {i} 처리 오류: {ex.Message}");
                        continue;
                    }
                }
                
                Console.WriteLine($"✅ 후처리 완료: {validDetections}개 유효 감지");
                
                // 간단한 NMS
                if (detections.Count > 1)
                {
                    detections = ApplySafeNMS(detections);
                }
                
                return detections;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 후처리 오류: {ex.Message}");
                return new List<Detection>();
            }
        }
        
        private List<Detection> ApplySafeNMS(List<Detection> detections)
        {
            try
            {
                detections = detections.OrderByDescending(d => d.Confidence).ToList();
                var keep = new List<Detection>();
                
                while (detections.Count > 0 && keep.Count < MAX_DETECTIONS)
                {
                    var current = detections[0];
                    keep.Add(current);
                    detections.RemoveAt(0);
                    
                    for (int i = detections.Count - 1; i >= 0; i--)
                    {
                        if (detections[i].ClassName == current.ClassName)
                        {
                            float iou = CalculateIoU(current.BBox, detections[i].BBox);
                            if (iou > 0.5f) // 보수적인 NMS
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
                return detections.Take(MAX_DETECTIONS).ToList();
            }
        }
        
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
        
        // 검열 효과 메서드들
        public void ApplySingleCensorOptimized(Mat processedFrame, Detection detection)
        {
            if (isDisposed || processedFrame == null || detection == null) return;
            
            try
            {
                var bbox = detection.BBox;
                int x1 = bbox[0], y1 = bbox[1], x2 = bbox[2], y2 = bbox[3];
                
                if (x2 <= x1 || y2 <= y1 || x1 < 0 || y1 < 0 || 
                    x2 > processedFrame.Width || y2 > processedFrame.Height)
                    return;
                
                using var region = new Mat(processedFrame, new Rect(x1, y1, x2 - x1, y2 - y1));
                if (!region.Empty())
                {
                    using var censoredRegion = ApplyCensorEffect(region, Strength);
                    if (censoredRegion != null && !censoredRegion.Empty())
                    {
                        censoredRegion.CopyTo(region);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 검열 효과 적용 오류: {ex.Message}");
            }
        }
        
        private Mat ApplyCensorEffect(Mat image, int strength)
        {
            return CurrentCensorType switch
            {
                CensorType.Mosaic => ApplyMosaicEffect(image, strength),
                CensorType.Blur => ApplyBlurEffect(image, strength),
                _ => ApplyMosaicEffect(image, strength)
            };
        }
        
        private Mat ApplyMosaicEffect(Mat image, int mosaicSize)
        {
            if (image == null || image.Empty()) return image?.Clone() ?? new Mat();
            
            try
            {
                int h = image.Height;
                int w = image.Width;
                int smallH = Math.Max(1, h / mosaicSize);
                int smallW = Math.Max(1, w / mosaicSize);

                using var smallImage = new Mat();
                using var mosaicImage = new Mat();
                
                Cv2.Resize(image, smallImage, new OpenCvSharp.Size(smallW, smallH));
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
        
        private Mat ApplyBlurEffect(Mat image, int blurStrength)
        {
            if (image == null || image.Empty()) return image?.Clone() ?? new Mat();
            
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
        
        private void CleanupModel()
        {
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
                isModelLoaded = false;
            }
        }
        
        // IProcessor 인터페이스 구현
        public void SetTargets(List<string> targets)
        {
            Targets = targets ?? new List<string> { "얼굴" };
            Console.WriteLine($"🎯 타겟 변경: {string.Join(", ", Targets)}");
        }
        
        public void SetStrength(int strength)
        {
            Strength = Math.Max(5, Math.Min(50, strength));
            Console.WriteLine($"💪 강도 변경: {Strength}");
        }
        
        public void SetCensorType(CensorType censorType)
        {
            CurrentCensorType = censorType;
            Console.WriteLine($"🎨 검열 타입 변경: {censorType}");
        }
        
        public (Mat processedFrame, List<Detection> detections) DetectObjectsDetailed(Mat frame)
        {
            var detections = DetectObjects(frame);
            var processedFrame = frame?.Clone() ?? new Mat();
            
            foreach (var detection in detections)
            {
                ApplySingleCensorOptimized(processedFrame, detection);
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
            try
            {
                if (x2 <= x1 || y2 <= y1) return null;
                
                using var region = new Mat(frame, new Rect(x1, y1, x2 - x1, y2 - y1));
                return ApplyCensorEffect(region, strength ?? Strength);
            }
            catch
            {
                return null;
            }
        }
        
        public PerformanceStats GetPerformanceStats()
        {
            lock (statsLock)
            {
                if (detectionTimes.Count == 0)
                {
                    return new PerformanceStats();
                }
                
                double avgTime = detectionTimes.Average() / 1000.0;
                return new PerformanceStats
                {
                    AvgDetectionTime = avgTime,
                    Fps = avgTime > 0 ? 1.0 / avgTime : 0,
                    LastDetectionsCount = 0
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
        }
        
        public bool IsModelLoaded()
        {
            return !isDisposed && isModelLoaded && isModelTested;
        }
        
        public List<string> GetAvailableClasses()
        {
            return new List<string> { "얼굴", "눈", "손", "신발" };
        }
        
        public void ResetStats()
        {
            lock (statsLock)
            {
                detectionTimes.Clear();
            }
        }
        
        public void Dispose()
        {
            if (!isDisposed)
            {
                isDisposed = true;
                CleanupModel();
                Console.WriteLine("🧹 안전 프로세서 정리됨");
            }
        }
    }
}