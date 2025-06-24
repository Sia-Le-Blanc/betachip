using OpenCvSharp;

namespace MosaicCensorSystem.Capture
{
    /// <summary>
    /// 화면 캡처 인터페이스
    /// </summary>
    public interface ICapturer
    {
        /// <summary>
        /// 프레임 가져오기
        /// </summary>
        Mat GetFrame();

        /// <summary>
        /// 캡처 스레드 시작
        /// </summary>
        void StartCaptureThread();

        /// <summary>
        /// 캡처 스레드 중지
        /// </summary>
        void StopCaptureThread();

        /// <summary>
        /// 캡처에서 제외할 윈도우 핸들 설정
        /// </summary>
        void SetExcludeHwnd(IntPtr hwnd);

        /// <summary>
        /// 캡처에서 제외할 영역 추가
        /// </summary>
        void AddExcludeRegion(int x, int y, int width, int height);

        /// <summary>
        /// 제외 영역 모두 제거
        /// </summary>
        void ClearExcludeRegions();
    }
}