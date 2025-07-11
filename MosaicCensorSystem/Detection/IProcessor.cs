using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace MosaicCensorSystem.Detection
{
    /// <summary>
    /// 검열 처리 인터페이스
    /// </summary>
    public interface IProcessor : IDisposable
    {
        /// <summary>
        /// 검열 대상 설정
        /// </summary>
        /// <param name="targets">검열할 객체 클래스 목록</param>
        void SetTargets(List<string> targets);

        /// <summary>
        /// 검열 강도 설정
        /// </summary>
        /// <param name="strength">검열 강도 (5-50)</param>
        void SetStrength(int strength);

        /// <summary>
        /// 검열 타입 설정
        /// </summary>
        /// <param name="censorType">모자이크 또는 블러</param>
        void SetCensorType(CensorType censorType);

        /// <summary>
        /// 프레임에서 객체 감지
        /// </summary>
        /// <param name="frame">입력 프레임</param>
        /// <returns>감지된 객체 목록</returns>
        List<Detection> DetectObjects(Mat frame);

        /// <summary>
        /// 객체 감지와 검열 효과를 동시에 적용
        /// </summary>
        /// <param name="frame">입력 프레임</param>
        /// <returns>처리된 프레임과 감지 결과</returns>
        (Mat processedFrame, List<Detection> detections) DetectObjectsDetailed(Mat frame);

        /// <summary>
        /// 이미지에 검열 효과 적용
        /// </summary>
        /// <param name="image">입력 이미지</param>
        /// <param name="strength">검열 강도 (선택적)</param>
        /// <returns>검열된 이미지</returns>
        Mat ApplyCensor(Mat image, int? strength = null);

        /// <summary>
        /// 모자이크 효과 적용
        /// </summary>
        /// <param name="image">입력 이미지</param>
        /// <param name="strength">모자이크 강도 (선택적)</param>
        /// <returns>모자이크 처리된 이미지</returns>
        Mat ApplyMosaic(Mat image, int? strength = null);

        /// <summary>
        /// 블러 효과 적용
        /// </summary>
        /// <param name="image">입력 이미지</param>
        /// <param name="strength">블러 강도 (선택적)</param>
        /// <returns>블러 처리된 이미지</returns>
        Mat ApplyBlur(Mat image, int? strength = null);

        /// <summary>
        /// 특정 영역에 대한 검열 효과 생성
        /// </summary>
        /// <param name="frame">원본 프레임</param>
        /// <param name="x1">영역 시작 X 좌표</param>
        /// <param name="y1">영역 시작 Y 좌표</param>
        /// <param name="x2">영역 끝 X 좌표</param>
        /// <param name="y2">영역 끝 Y 좌표</param>
        /// <param name="strength">검열 강도 (선택적)</param>
        /// <returns>검열된 영역 이미지</returns>
        Mat CreateCensorForRegion(Mat frame, int x1, int y1, int x2, int y2, int? strength = null);

        /// <summary>
        /// 성능 통계 조회
        /// </summary>
        /// <returns>성능 통계 객체</returns>
        PerformanceStats GetPerformanceStats();

        /// <summary>
        /// 설정 업데이트
        /// </summary>
        /// <param name="kwargs">설정 키-값 딕셔너리</param>
        void UpdateConfig(Dictionary<string, object> kwargs);

        /// <summary>
        /// 모델 로드 상태 확인
        /// </summary>
        /// <returns>모델이 성공적으로 로드되었는지 여부</returns>
        bool IsModelLoaded();

        /// <summary>
        /// 사용 가능한 클래스 목록 조회
        /// </summary>
        /// <returns>지원하는 객체 클래스 목록</returns>
        List<string> GetAvailableClasses();

        /// <summary>
        /// 성능 통계 초기화
        /// </summary>
        void ResetStats();

        /// <summary>
        /// 감지 신뢰도 임계값
        /// </summary>
        float ConfThreshold { get; set; }

        /// <summary>
        /// 현재 검열 대상 목록
        /// </summary>
        List<string> Targets { get; }

        /// <summary>
        /// 현재 검열 강도
        /// </summary>
        int Strength { get; }

        /// <summary>
        /// 현재 검열 타입
        /// </summary>
        CensorType CurrentCensorType { get; }
    }
}