using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace MosaicCensorSystem.Detection
{
    /// <summary>
    /// 최적화된 모자이크 프로세서 - 피드백 루프 해결 버전
    /// 원본 프레임에서만 감지하고, 개별 영역 모자이크 정보 제공
    /// </summary>
    public class MosaicProcessor : IProcessor, IDisposable
    {
        private readonly Dictionary<string, object> config;
        private InferenceSession model;
        private readonly List<string> classNames;
        
        // 설정값들
        public float ConfThreshold { get; set; }
        public List<string> Targets { get; private set; }
        public int Strength { get; private set; }

        // 성능 통계
        private readonly List<double> detectionTimes = new List<double>();
        private List<Detection> lastDetections = new List<Detection>();

        public MosaicProcessor(string modelPath = null, Dictionary<string, object> config = null)
        {
            this.config = config ?? Config.GetSection("mosaic");

            // 모델 경로 설정
            if (string.IsNullOrEmpty(modelPath))
            {
                modelPath = this.config.GetValueOrDefault("model_path", "resources/best.onnx") as string;
            }

            // YOLO 모델 로드
            try
            {
                Console.WriteLine($"🤖 YOLO 모델 로딩 중: {modelPath}");
                
                // ONNX Runtime 세션 옵션
                var options = new SessionOptions();
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                
                // GPU 사용 가능 시 GPU 사용 (선택사항)
                // options.AppendExecutionProvider_CUDA(0);
                
                model = new InferenceSession(modelPath, options);
                Console.WriteLine("✅ YOLO 모델 로드 성공");
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ YOLO 모델 로드 실패: {e.Message}");
                model = null;
            }

            // 클래스 이름 목록
            var modelConfig = Config.GetSection("models");
            classNames = (modelConfig.GetValueOrDefault("class_names", new List<string>()) as List<string>) 
                ?? new List<string>();

            // 설정값들
            ConfThreshold = Convert.ToSingle(this.config.GetValueOrDefault("conf_threshold", 0.1));
            Targets = (this.config.GetValueOrDefault("default_targets", new List<string> { "여성" }) as List<string>)
                ?? new List<string> { "여성" };
            Strength = Convert.ToInt32(this.config.GetValueOrDefault("default_strength", 15));

            Console.WriteLine($"🎯 기본 타겟: {string.Join(", ", Targets)}");
            Console.WriteLine($"⚙️ 기본 설정: 강도={Strength}, 신뢰도={ConfThreshold}");
        }

        public void SetTargets(List<string> targets)
        {
            Targets = targets;
            Console.WriteLine($"🎯 타겟 변경: {string.Join(", ", targets)}");
        }

        public void SetStrength(int strength)
        {
            Strength = Math.Max(1, Math.Min(50, strength));
            Console.WriteLine($"💪 강도 변경: {Strength}");
        }

        public List<Detection> DetectObjects(Mat frame)
        {
            if (model == null || frame == null || frame.Empty())
            {
                return new List<Detection>();
            }

            try
            {
                var stopwatch = Stopwatch.StartNew();

                // YOLO 입력 크기 (640x640)
                const int inputSize = 640;

                // 이미지 전처리
                Mat resized = new Mat();
                Cv2.Resize(frame, resized, new Size(inputSize, inputSize));

                // BGR to RGB
                Mat rgb = new Mat();
                Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

                // 정규화 및 텐서 변환
                var inputTensor = new DenseTensor<float>(new[] { 1, 3, inputSize, inputSize });
                for (int y = 0; y < inputSize; y++)
                {
                    for (int x = 0; x < inputSize; x++)
                    {
                        var pixel = rgb.At<Vec3b>(y, x);
                        inputTensor[0, 0, y, x] = pixel[0] / 255.0f; // R
                        inputTensor[0, 1, y, x] = pixel[1] / 255.0f; // G
                        inputTensor[0, 2, y, x] = pixel[2] / 255.0f; // B
                    }
                }

                // 추론 실행
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("images", inputTensor)
                };

                using (var results = model.Run(inputs))
                {
                    var output = results.First().AsEnumerable<float>().ToArray();
                    var detections = ParseYoloOutput(output, frame.Width, frame.Height);

                    // 성능 통계 업데이트
                    stopwatch.Stop();
                    detectionTimes.Add(stopwatch.Elapsed.TotalSeconds);
                    if (detectionTimes.Count > 100)
                    {
                        detectionTimes.RemoveRange(0, 50);
                    }

                    lastDetections = detections;

                    // 리소스 정리
                    resized.Dispose();
                    rgb.Dispose();

                    return detections;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"❌ 객체 감지 오류: {e.Message}");
                return new List<Detection>();
            }
        }

        private List<Detection> ParseYoloOutput(float[] output, int originalWidth, int originalHeight)
        {
            var detections = new List<Detection>();
            
            // YOLO 출력 형식에 따라 파싱 (이 부분은 모델에 따라 조정 필요)
            // 일반적인 YOLO 출력: [1, 25200, 85] 또는 [1, num_detections, 5 + num_classes]
            int numClasses = classNames.Count;
            int stride = 5 + numClasses; // x, y, w, h, conf + class scores
            int numDetections = output.Length / stride;

            for (int i = 0; i < numDetections; i++)
            {
                int baseIdx = i * stride;
                
                // 신뢰도 확인
                float confidence = output[baseIdx + 4];
                if (confidence < ConfThreshold)
                    continue;

                // 바운딩 박스 좌표 (중심점 x, y, 너비, 높이)
                float cx = output[baseIdx + 0];
                float cy = output[baseIdx + 1];
                float w = output[baseIdx + 2];
                float h = output[baseIdx + 3];

                // 클래스 확률
                int bestClassIdx = -1;
                float bestClassScore = 0;
                for (int j = 0; j < numClasses; j++)
                {
                    float classScore = output[baseIdx + 5 + j];
                    if (classScore > bestClassScore)
                    {
                        bestClassScore = classScore;
                        bestClassIdx = j;
                    }
                }

                if (bestClassIdx < 0 || bestClassScore * confidence < ConfThreshold)
                    continue;

                // YOLO 좌표를 실제 이미지 좌표로 변환
                int x1 = (int)((cx - w / 2) * originalWidth / 640);
                int y1 = (int)((cy - h / 2) * originalHeight / 640);
                int x2 = (int)((cx + w / 2) * originalWidth / 640);
                int y2 = (int)((cy + h / 2) * originalHeight / 640);

                // 좌표 검증
                x1 = Math.Max(0, Math.Min(originalWidth - 1, x1));
                y1 = Math.Max(0, Math.Min(originalHeight - 1, y1));
                x2 = Math.Max(0, Math.Min(originalWidth - 1, x2));
                y2 = Math.Max(0, Math.Min(originalHeight - 1, y2));

                if (x2 > x1 && y2 > y1)
                {
                    detections.Add(new Detection
                    {
                        ClassName = bestClassIdx < classNames.Count ? classNames[bestClassIdx] : $"class_{bestClassIdx}",
                        Confidence = confidence * bestClassScore,
                        BBox = new[] { x1, y1, x2, y2 },
                        ClassId = bestClassIdx
                    });
                }
            }

            // NMS (Non-Maximum Suppression) 적용
            return ApplyNMS(detections, 0.5f);
        }

        private List<Detection> ApplyNMS(List<Detection> detections, float iouThreshold)
        {
            if (detections.Count == 0)
                return detections;

            // 신뢰도 기준 내림차순 정렬
            detections.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

            var keep = new List<Detection>();
            var suppress = new HashSet<int>();

            for (int i = 0; i < detections.Count; i++)
            {
                if (suppress.Contains(i))
                    continue;

                keep.Add(detections[i]);

                for (int j = i + 1; j < detections.Count; j++)
                {
                    if (suppress.Contains(j))
                        continue;

                    float iou = CalculateIoU(detections[i].BBox, detections[j].BBox);
                    if (iou > iouThreshold)
                    {
                        suppress.Add(j);
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

            if (x2 < x1 || y2 < y1)
                return 0;

            int intersection = (x2 - x1) * (y2 - y1);
            int area1 = (box1[2] - box1[0]) * (box1[3] - box1[1]);
            int area2 = (box2[2] - box2[0]) * (box2[3] - box2[1]);
            int union = area1 + area2 - intersection;

            return (float)intersection / union;
        }

        public (Mat processedFrame, List<Detection> detections) DetectObjectsDetailed(Mat frame)
        {
            var detections = DetectObjects(frame);

            // 전체 프레임에 모자이크 적용
            Mat processedFrame = frame.Clone();

            foreach (var detection in detections)
            {
                if (Targets.Contains(detection.ClassName))
                {
                    int x1 = detection.BBox[0];
                    int y1 = detection.BBox[1];
                    int x2 = detection.BBox[2];
                    int y2 = detection.BBox[3];

                    // 해당 영역에 모자이크 적용
                    using (Mat region = new Mat(processedFrame, new Rect(x1, y1, x2 - x1, y2 - y1)))
                    {
                        if (!region.Empty())
                        {
                            Mat mosaicRegion = ApplyMosaic(region, Strength);
                            mosaicRegion.CopyTo(region);
                            mosaicRegion.Dispose();
                        }
                    }
                }
            }

            return (processedFrame, detections);
        }

        public Mat ApplyMosaic(Mat image, int? strength = null)
        {
            if (strength == null)
                strength = Strength;

            if (image.Empty())
                return image.Clone();

            try
            {
                int h = image.Height;
                int w = image.Width;

                // 최소 크기 보장
                int smallH = Math.Max(1, h / strength.Value);
                int smallW = Math.Max(1, w / strength.Value);

                // 축소 후 확대
                Mat small = new Mat();
                Mat mosaic = new Mat();
                
                Cv2.Resize(image, small, new Size(smallW, smallH), interpolation: InterpolationFlags.Linear);
                Cv2.Resize(small, mosaic, new Size(w, h), interpolation: InterpolationFlags.Nearest);

                small.Dispose();
                return mosaic;
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ 모자이크 적용 오류: {e.Message}");
                return image.Clone();
            }
        }

        public Mat CreateMosaicForRegion(Mat frame, int x1, int y1, int x2, int y2, int? strength = null)
        {
            try
            {
                // 영역 추출
                using (Mat region = new Mat(frame, new Rect(x1, y1, x2 - x1, y2 - y1)))
                {
                    if (region.Empty())
                        return null;

                    // 모자이크 적용
                    return ApplyMosaic(region, strength);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"⚠️ 영역 모자이크 생성 오류: {e.Message}");
                return null;
            }
        }

        public PerformanceStats GetPerformanceStats()
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
                        Targets = kvp.Value as List<string> ?? new List<string>();
                        break;
                    case "strength":
                        Strength = Math.Max(1, Math.Min(50, Convert.ToInt32(kvp.Value)));
                        break;
                }
            }

            Console.WriteLine($"⚙️ 설정 업데이트: {string.Join(", ", kwargs.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
        }

        public bool IsModelLoaded()
        {
            return model != null;
        }

        public List<string> GetAvailableClasses()
        {
            return model != null ? new List<string>(classNames) : new List<string>();
        }

        public void ResetStats()
        {
            detectionTimes.Clear();
            lastDetections.Clear();
            Console.WriteLine("📊 성능 통계 초기화됨");
        }

        public void Dispose()
        {
            model?.Dispose();
        }
    }
}