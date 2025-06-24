using System.Collections.Generic;
using OpenCvSharp;

namespace MosaicCensorSystem.Detection
{
    /// <summary>
    /// 객체 감지 결과를 나타내는 클래스
    /// </summary>
    public class Detection
    {
        public string ClassName { get; set; }
        public float Confidence { get; set; }
        public int[] BBox { get; set; } // [x1, y1, x2, y2]
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
        /// <summary>
        /// 모자이크 대상 설정
        /// </summary>
        void SetTargets(List<string> targets);

        /// <summary>
        /// 모자이크 강도 설정
        /// </summary>
        void SetStrength(int strength);

        /// <summary>
        /// 객체 감지만 수행 (모자이크 적용 없이)
        /// </summary>
        List<Detection> DetectObjects(Mat frame);

        /// <summary>
        /// 객체 감지 + 모자이크 적용된 전체 프레임 반환
        /// </summary>
        (Mat processedFrame, List<Detection> detections) DetectObjectsDetailed(Mat frame);

        /// <summary>
        /// 이미지에 모자이크 효과 적용
        /// </summary>
        Mat ApplyMosaic(Mat image, int? strength = null);

        /// <summary>
        /// 특정 영역에 대한 모자이크 이미지 생성
        /// </summary>
        Mat CreateMosaicForRegion(Mat frame, int x1, int y1, int x2, int y2, int? strength = null);

        /// <summary>
        /// 성능 통계 반환
        /// </summary>
        PerformanceStats GetPerformanceStats();

        /// <summary>
        /// 설정 업데이트
        /// </summary>
        void UpdateConfig(Dictionary<string, object> kwargs);

        /// <summary>
        /// 모델이 로드되었는지 확인
        /// </summary>
        bool IsModelLoaded();

        /// <summary>
        /// 사용 가능한 클래스 목록 반환
        /// </summary>
        List<string> GetAvailableClasses();

        /// <summary>
        /// 통계 초기화
        /// </summary>
        void ResetStats();

        /// <summary>
        /// 신뢰도 임계값 속성
        /// </summary>
        float ConfThreshold { get; set; }

        /// <summary>
        /// 현재 타겟 목록
        /// </summary>
        List<string> Targets { get; }

        /// <summary>
        /// 현재 모자이크 강도
        /// </summary>
        int Strength { get; }
    }
}