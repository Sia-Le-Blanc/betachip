using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace MosaicCensorSystem.Detection
{
    /// <summary>
    /// 객체 감지 결과를 나타내는 클래스
    /// </summary>
    public class Detection
    {
        public string ClassName { get; set; } = "";
        public float Confidence { get; set; }
        public int[] BBox { get; set; } = new int[4]; // [x1, y1, x2, y2]
        public int ClassId { get; set; }
    }

    /// <summary>
    /// 성능 통계를 나타내는 클래스
    /// </summary>
    public class PerformanceStats
    {
        public double AvgDetectionTime { get; set; }
        public double Fps { get; set; }
        public int LastDetectionsCount { get; set; }
    }

    /// <summary>
    /// 모자이크 처리 인터페이스
    /// </summary>
    public interface IProcessor
    {
        void SetTargets(List<string> targets);
        void SetStrength(int strength);
        List<Detection> DetectObjects(Mat frame);
        (Mat processedFrame, List<Detection> detections) DetectObjectsDetailed(Mat frame);
        Mat ApplyMosaic(Mat image, int? strength = null);
        Mat? CreateMosaicForRegion(Mat frame, int x1, int y1, int x2, int y2, int? strength = null);
        PerformanceStats GetPerformanceStats();
        void UpdateConfig(Dictionary<string, object> kwargs);
        bool IsModelLoaded();
        List<string> GetAvailableClasses();
        void ResetStats();
        float ConfThreshold { get; set; }
        List<string> Targets { get; }
        int Strength { get; }
    }

    /// <summary>
    /// 최적화된 모자이크 프로세서 - 피드백 루프 해결 버전
    /// 원본 프레임에서만 감지하고, 개별 영역 모자이크 정보 제공
    /// </summary>
    public class MosaicProcessor : IProcessor, IDisposable
    {
        private readonly Dictionary<string, object> config;
        private InferenceSession? model;
        private readonly string modelPath;

        // 설정값들
        public float ConfThreshold { get; set; }
        public List<string> Targets { get; private set; }
        public int Strength { get; private set; }

        // 클래스 이름 매핑
        private readonly List<string> classNames = new List<string>
        {
            "얼굴", "가슴", "겨드랑이", "보지", "발", "몸 전체",
            "자지", "팬티", "눈", "손", "교미", "신발",
            "가슴_옷", "보지_옷", "여성"
        };

        // 성능 통계
        private readonly List<double> detectionTimes = new List<double>();
        private List<Detection> lastDetections = new List<Detection>();
        private readonly object statsLock = new object();

        public MosaicProcessor(string? modelPath = null, Dictionary<string, object>? config = null)
        {
            this.config = config ?? new Dictionary<string, object>();
            
            // 모델 경로 설정
            this.modelPath = modelPath ?? Program.ONNX_MODEL_PATH;
            if (!System.IO.File.Exists(this.modelPath))
            {
                Console.WriteLine($" 모델 파일을 찾을 수 없습니다: {this.modelPath}");
            }

            Console.WriteLine($"🔍 현재 작업 디렉토리: {Environment.CurrentDirectory}");
            Console.WriteLine($"🔍 실행 파일 디렉토리: {AppDomain.CurrentDomain.BaseDirectory}");
            Console.WriteLine($"🔍 모델 경로: {this.modelPath}");
            Console.WriteLine($"🔍 파일 존재 여부: {System.IO.File.Exists(this.modelPath)}");
            // YOLO 모델 로드
            try
            {
                Console.WriteLine($"🤖 YOLO 모델 로딩 중: {this.modelPath}");
                var sessionOptions = new SessionOptions
                {
                    EnableCpuMemArena = false,
                    EnableMemoryPattern = false,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED
                };

                model = new InferenceSession(this.modelPath, sessionOptions);
                Console.WriteLine("✅ YOLO 모델 로드 성공");
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ YOLO 모델 로드 실패: {e.Message}");
                model = null;
            }

            // 설정값들 초기화
            ConfThreshold = 0.1f;
            Targets = new List<string> { "여성" };
            Strength = 15;

            Console.WriteLine($"🎯 기본 타겟: {string.Join(", ", Targets)}");
            Console.WriteLine($"⚙️ 기본 설정: 강도={Strength}, 신뢰도={ConfThreshold}");
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

        public List<Detection> DetectObjects(Mat frame)
        {
            if (model == null || frame == null || frame.Empty())
                return new List<Detection>();

            try
            {
                var startTime = DateTime.Now;

                // 전처리
                var inputTensor = PreprocessFrame(frame);
                if (inputTensor == null)
                    return new List<Detection>();

                // YOLO 추론
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("images", inputTensor)
                };

                using var results = model.Run(inputs);
                var output = results.FirstOrDefault()?.AsEnumerable<float>().ToArray();

                if (output == null)
                    return new List<Detection>();

                // 후처리
                var detections = PostprocessOutput(output, frame.Width, frame.Height);

                // 성능 통계 업데이트
                var detectionTime = (DateTime.Now - startTime).TotalSeconds;
                lock (statsLock)
                {
                    detectionTimes.Add(detectionTime);
                    if (detectionTimes.Count > 100)
                    {
                        detectionTimes.RemoveRange(0, detectionTimes.Count - 50);
                    }
                    lastDetections = detections;
                }

                return detections;
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ 객체 감지 오류: {e.Message}");
                return new List<Detection>();
            }
        }

        private DenseTensor<float>? PreprocessFrame(Mat frame)
        {
            try
            {
                // 640x640으로 리사이즈
                using var resized = new Mat();
                Cv2.Resize(frame, resized, new OpenCvSharp.Size(640, 640));

                // BGR to RGB 변환
                using var rgb = new Mat();
                Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

                // 정규화 및 텐서 변환
                var tensor = new DenseTensor<float>(new[] { 1, 3, 640, 640 });

                // OpenCV Mat을 직접 사용한 안전한 픽셀 접근
                var indexer = rgb.GetGenericIndexer<Vec3b>();
                
                for (int h = 0; h < 640; h++)
                {
                    for (int w = 0; w < 640; w++)
                    {
                        var pixel = indexer[h, w];
                        // RGB 순서로 저장 (OpenCV는 BGR)
                        tensor[0, 0, h, w] = pixel.Item2 / 255.0f; // R
                        tensor[0, 1, h, w] = pixel.Item1 / 255.0f; // G  
                        tensor[0, 2, h, w] = pixel.Item0 / 255.0f; // B
                    }
                }

                return tensor;
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ 전처리 오류: {e.Message}");
                return null;
            }
        }

        private List<Detection> PostprocessOutput(float[] output, int originalWidth, int originalHeight)
        {
            var detections = new List<Detection>();

            try
            {
                // YOLO 출력 형태 추정: [1, 25200, 20] (20 = 4 bbox + 1 confidence + 15 classes)
                int numDetections = output.Length / 20;
                
                for (int i = 0; i < numDetections; i++)
                {
                    int baseIndex = i * 20;
                    
                    // 범위 확인
                    if (baseIndex + 19 >= output.Length) break;
                    
                    // 바운딩 박스 (중심점 + 크기)
                    float centerX = output[baseIndex + 0];
                    float centerY = output[baseIndex + 1];
                    float width = output[baseIndex + 2];
                    float height = output[baseIndex + 3];
                    float objectConfidence = output[baseIndex + 4];

                    // 클래스별 확률
                    float maxClassProb = 0;
                    int maxClassIndex = 0;
                    
                    for (int j = 0; j < 15; j++)
                    {
                        if (baseIndex + 5 + j < output.Length)
                        {
                            float classProb = output[baseIndex + 5 + j];
                            if (classProb > maxClassProb)
                            {
                                maxClassProb = classProb;
                                maxClassIndex = j;
                            }
                        }
                    }

                    float confidence = objectConfidence * maxClassProb;

                    if (confidence >= ConfThreshold)
                    {
                        // 좌표 변환 (640x640 -> 원본 크기)
                        float scaleX = originalWidth / 640.0f;
                        float scaleY = originalHeight / 640.0f;

                        int x1 = (int)((centerX - width / 2) * scaleX);
                        int y1 = (int)((centerY - height / 2) * scaleY);
                        int x2 = (int)((centerX + width / 2) * scaleX);
                        int y2 = (int)((centerY + height / 2) * scaleY);

                        // 경계 확인
                        x1 = Math.Max(0, Math.Min(x1, originalWidth - 1));
                        y1 = Math.Max(0, Math.Min(y1, originalHeight - 1));
                        x2 = Math.Max(0, Math.Min(x2, originalWidth - 1));
                        y2 = Math.Max(0, Math.Min(y2, originalHeight - 1));

                        if (x2 > x1 && y2 > y1 && maxClassIndex < classNames.Count)
                        {
                            var detection = new Detection
                            {
                                ClassName = classNames[maxClassIndex],
                                Confidence = confidence,
                                BBox = new int[] { x1, y1, x2, y2 },
                                ClassId = maxClassIndex
                            };
                            detections.Add(detection);
                        }
                    }
                }

                // NMS (Non-Maximum Suppression) 적용
                detections = ApplyNMS(detections, 0.4f);

                return detections;
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ 후처리 오류: {e.Message}");
                return new List<Detection>();
            }
        }

        private List<Detection> ApplyNMS(List<Detection> detections, float nmsThreshold)
        {
            if (detections.Count == 0) return detections;

            // 신뢰도 순으로 정렬
            detections = detections.OrderByDescending(d => d.Confidence).ToList();
            var keep = new List<Detection>();

            while (detections.Count > 0)
            {
                var current = detections[0];
                keep.Add(current);
                detections.RemoveAt(0);

                // IoU 계산하여 겹치는 박스 제거
                for (int i = detections.Count - 1; i >= 0; i--)
                {
                    float iou = CalculateIoU(current.BBox, detections[i].BBox);
                    if (iou > nmsThreshold)
                    {
                        detections.RemoveAt(i);
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

        public (Mat processedFrame, List<Detection> detections) DetectObjectsDetailed(Mat frame)
        {
            var detections = DetectObjects(frame);
            var processedFrame = frame.Clone();

            foreach (var detection in detections)
            {
                if (Targets.Contains(detection.ClassName))
                {
                    int x1 = detection.BBox[0], y1 = detection.BBox[1];
                    int x2 = detection.BBox[2], y2 = detection.BBox[3];

                    if (x2 > x1 && y2 > y1 && x1 >= 0 && y1 >= 0 && x2 <= frame.Width && y2 <= frame.Height)
                    {
                        using var region = new Mat(processedFrame, new Rect(x1, y1, x2 - x1, y2 - y1));
                        if (!region.Empty())
                        {
                            using var mosaicRegion = ApplyMosaic(region, Strength);
                            mosaicRegion.CopyTo(region);
                        }
                    }
                }
            }

            return (processedFrame, detections);
        }

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

        public Mat? CreateMosaicForRegion(Mat frame, int x1, int y1, int x2, int y2, int? strength = null)
        {
            try
            {
                if (x2 <= x1 || y2 <= y1 || x1 < 0 || y1 < 0 || x2 > frame.Width || y2 > frame.Height)
                    return null;

                using var region = new Mat(frame, new Rect(x1, y1, x2 - x1, y2 - y1));
                if (region.Empty())
                    return null;

                return ApplyMosaic(region, strength);
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ 영역 모자이크 생성 오류: {e.Message}");
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
                        LastDetectionsCount = 0
                    };
                }

                double avgTime = detectionTimes.Average();
                double fps = avgTime > 0 ? 1.0 / avgTime : 0;

                return new PerformanceStats
                {
                    AvgDetectionTime = avgTime,
                    Fps = fps,
                    LastDetectionsCount = lastDetections.Count
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
            return new List<string>(classNames);
        }

        public void ResetStats()
        {
            lock (statsLock)
            {
                detectionTimes.Clear();
                lastDetections.Clear();
            }
            Console.WriteLine("📊 성능 통계 초기화됨");
        }

        public void Dispose()
        {
            model?.Dispose();
            Console.WriteLine("🧹 MosaicProcessor 리소스 정리됨");
        }
    }
}