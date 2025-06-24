using System;
using OpenCvSharp;

namespace MosaicCensorSystem.Overlay
{
    /// <summary>
    /// 오버레이 윈도우 인터페이스
    /// </summary>
    public interface IOverlay : IDisposable
    {
        /// <summary>
        /// 풀스크린 오버레이 표시
        /// </summary>
        bool Show();

        /// <summary>
        /// 오버레이 숨기기
        /// </summary>
        void Hide();

        /// <summary>
        /// 전체 화면 프레임 업데이트
        /// </summary>
        void UpdateFrame(Mat processedFrame);

        /// <summary>
        /// 창이 표시되고 있는지 확인
        /// </summary>
        bool IsWindowVisible();

        /// <summary>
        /// 디버그 정보 표시 토글
        /// </summary>
        void ToggleDebugInfo();

        /// <summary>
        /// FPS 제한 설정
        /// </summary>
        void SetFpsLimit(int fps);

        /// <summary>
        /// 캡처 방지 기능 테스트
        /// </summary>
        bool TestCaptureProtection();

        /// <summary>
        /// 클릭 투과 기능 테스트
        /// </summary>
        bool TestClickThrough();

        /// <summary>
        /// 디버그 정보 표시 여부
        /// </summary>
        bool ShowDebugInfo { get; set; }
    }
}