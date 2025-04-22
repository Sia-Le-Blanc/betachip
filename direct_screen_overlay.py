# direct_screen_overlay.py
import cv2
import numpy as np
import threading
import time
import os
import mss
import ctypes
from ctypes import windll, wintypes, byref, c_int

# Windows 상수 정의
WS_EX_LAYERED = 0x80000
WS_EX_TRANSPARENT = 0x20
WS_EX_TOPMOST = 0x8
WS_EX_TOOLWINDOW = 0x80
WS_POPUP = 0x80000000
WS_VISIBLE = 0x10000000
HWND_TOPMOST = -1
SWP_NOSIZE = 0x1
SWP_NOMOVE = 0x2
SWP_NOACTIVATE = 0x10
ULW_ALPHA = 0x2
AC_SRC_OVER = 0x0
AC_SRC_ALPHA = 0x1

class DirectScreenOverlay:
    """Direct Win32 API와 MSS를 사용한 고성능 오버레이"""
    def __init__(self):
        # 화면 정보 초기화
        with mss.mss() as sct:
            monitor = sct.monitors[0]  # 전체 화면
            self.screen_width = monitor["width"]
            self.screen_height = monitor["height"]
        
        # 모자이크 정보
        self.mosaic_regions = []
        self.active = False
        self.render_thread = None
        self.stop_event = threading.Event()
        
        # 디버깅 변수
        self.frame_count = 0
        self.debug_dir = "debug_overlay"
        os.makedirs(self.debug_dir, exist_ok=True)
        
        # 성능 측정
        self.last_render_time = time.time()
        self.fps_times = []
        
        # Win32 윈도우 관련
        self.hwnd = None
        self.hdc = None
        
        # 윈도우 클래스 등록 및 생성
        self._register_window_class()
        self._create_overlay_window()
        
        print(f"✅ 직접 스크린 오버레이 초기화 (해상도: {self.screen_width}x{self.screen_height})")
    
    def _register_window_class(self):
        """윈도우 클래스 등록"""
        self.wc = wintypes.WNDCLASSEXW()
        self.wc.cbSize = ctypes.sizeof(wintypes.WNDCLASSEXW)
        self.wc.style = 0
        self.wc.lpfnWndProc = ctypes.WINFUNCTYPE(ctypes.c_int, ctypes.c_int, ctypes.c_uint, 
                                                ctypes.c_uint, ctypes.c_int)(self._window_proc)
        self.wc.cbClsExtra = 0
        self.wc.cbWndExtra = 0
        self.wc.hInstance = windll.kernel32.GetModuleHandleW(None)
        self.wc.hIcon = 0
        self.wc.hCursor = windll.user32.LoadCursorW(0, 32512)  # IDC_ARROW
        self.wc.hbrBackground = 0
        self.wc.lpszMenuName = None
        self.wc.lpszClassName = "DirectOverlayClass"
        self.wc.hIconSm = 0
        
        windll.user32.RegisterClassExW(byref(self.wc))
    
    def _window_proc(self, hwnd, msg, wparam, lparam):
        """윈도우 프로시저"""
        if msg == 0x10:  # WM_CLOSE
            windll.user32.DestroyWindow(hwnd)
        elif msg == 0x2:  # WM_DESTROY
            windll.user32.PostQuitMessage(0)
        return windll.user32.DefWindowProcW(hwnd, msg, wparam, lparam)
    
    def _create_overlay_window(self):
        """투명 오버레이 윈도우 생성"""
        self.hwnd = windll.user32.CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
            self.wc.lpszClassName,
            "Direct Overlay",
            WS_POPUP | WS_VISIBLE,
            0, 0, self.screen_width, self.screen_height,
            None, None, self.wc.hInstance, None
        )
        
        if not self.hwnd:
            print("❌ 윈도우 생성 실패")
            return
        
        # 투명도 설정
        windll.user32.SetLayeredWindowAttributes(self.hwnd, 0, 255, 2)  # LWA_ALPHA = 2
        
        # 항상 최상위
        windll.user32.SetWindowPos(
            self.hwnd, HWND_TOPMOST,
            0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
        )
        
        # 클릭 통과 설정
        extendedStyle = windll.user32.GetWindowLongA(self.hwnd, -20)  # GWL_EXSTYLE
        windll.user32.SetWindowLongA(self.hwnd, -20, extendedStyle | WS_EX_TRANSPARENT)
    
    def show(self):
        """오버레이 활성화 및 렌더링 스레드 시작"""
        print("✅ 직접 스크린 오버레이 표시")
        self.active = True
        self.stop_event.clear()
        
        if self.hwnd:
            windll.user32.ShowWindow(self.hwnd, 5)  # SW_SHOW
        
        if self.render_thread is None or not self.render_thread.is_alive():
            self.render_thread = threading.Thread(target=self._render_loop, daemon=True)
            self.render_thread.start()
    
    def hide(self):
        """오버레이 비활성화 및 렌더링 스레드 중지"""
        print("🛑 직접 스크린 오버레이 숨기기")
        self.active = False
        
        if self.hwnd:
            windll.user32.ShowWindow(self.hwnd, 0)  # SW_HIDE
        
        if self.render_thread:
            self.stop_event.set()
            self.render_thread.join(timeout=1.0)
            self.render_thread = None
    
    def update_regions(self, original_image, mosaic_regions):
        """모자이크 영역 업데이트"""
        self.mosaic_regions = mosaic_regions
        self.frame_count += 1
        
        if len(mosaic_regions) > 0 and self.frame_count % 100 == 0:
            print(f"✅ 모자이크 영역 {len(mosaic_regions)}개 처리 중 (프레임 #{self.frame_count})")
            self._save_debug_image(original_image)
    
    def _render_loop(self):
        """렌더링 메인 루프"""
        try:
            while not self.stop_event.is_set() and self.active:
                start_time = time.time()
                
                if not self.mosaic_regions:
                    time.sleep(0.016)  # 약 60fps
                    continue
                
                # 모자이크 렌더링
                self._draw_mosaic_regions()
                
                # FPS 계산 및 제한
                elapsed = time.time() - start_time
                self.fps_times.append(elapsed)
                if len(self.fps_times) > 60:
                    self.fps_times.pop(0)
                
                # FPS 출력 (60프레임마다)
                if self.frame_count % 60 == 0:
                    avg_time = sum(self.fps_times) / len(self.fps_times)
                    fps = 1.0 / avg_time if avg_time > 0 else 0
                    print(f"⚡️ 렌더링 FPS: {fps:.1f}, 평균 처리 시간: {avg_time*1000:.1f}ms")
                
                # 프레임 레이트 제한
                sleep_time = max(0, 0.016 - elapsed)  # 약 60fps
                if sleep_time > 0:
                    time.sleep(sleep_time)
            
        except Exception as e:
            print(f"❌ 렌더링 루프 오류: {e}")
            import traceback
            traceback.print_exc()
    
    def _draw_mosaic_regions(self):
        """모자이크 영역을 화면에 그리기"""
        try:
            # 디바이스 컨텍스트 가져오기
            hdc = windll.user32.GetDC(self.hwnd)
            if not hdc:
                return
            
            # 메모리 DC 생성
            mem_dc = windll.gdi32.CreateCompatibleDC(hdc)
            if not mem_dc:
                windll.user32.ReleaseDC(self.hwnd, hdc)
                return
            
            # 비트맵 생성
            bitmap = windll.gdi32.CreateCompatibleBitmap(hdc, self.screen_width, self.screen_height)
            if not bitmap:
                windll.gdi32.DeleteDC(mem_dc)
                windll.user32.ReleaseDC(self.hwnd, hdc)
                return
            
            old_bitmap = windll.gdi32.SelectObject(mem_dc, bitmap)
            
            # 투명한 배경으로 초기화
            windll.gdi32.BitBlt(mem_dc, 0, 0, self.screen_width, self.screen_height,
                               None, 0, 0, 0x42)  # BLACKNESS
            
            # 모자이크 영역 그리기
            for x, y, w, h, label, mosaic_img in self.mosaic_regions:
                if mosaic_img is None:
                    continue
                
                try:
                    height, width = mosaic_img.shape[:2]
                    
                    # BGR to BGRA 변환
                    bgra = cv2.cvtColor(mosaic_img, cv2.COLOR_BGR2BGRA)
                    bgra[:, :, 3] = 255  # 알파 채널 설정
                    
                    # BITMAPINFO 구조체 생성
                    class BITMAPINFOHEADER(ctypes.Structure):
                        _fields_ = [
                            ('biSize', ctypes.c_uint32),
                            ('biWidth', ctypes.c_int32),
                            ('biHeight', ctypes.c_int32),
                            ('biPlanes', ctypes.c_uint16),
                            ('biBitCount', ctypes.c_uint16),
                            ('biCompression', ctypes.c_uint32),
                            ('biSizeImage', ctypes.c_uint32),
                            ('biXPelsPerMeter', ctypes.c_int32),
                            ('biYPelsPerMeter', ctypes.c_int32),
                            ('biClrUsed', ctypes.c_uint32),
                            ('biClrImportant', ctypes.c_uint32)
                        ]
                    
                    bmi = BITMAPINFOHEADER()
                    bmi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
                    bmi.biWidth = width
                    bmi.biHeight = -height  # 상하 반전
                    bmi.biPlanes = 1
                    bmi.biBitCount = 32
                    bmi.biCompression = 0  # BI_RGB
                    
                    # 이미지 그리기
                    windll.gdi32.SetDIBitsToDevice(
                        mem_dc, x, y, width, height,
                        0, 0, 0, height,
                        bgra.ctypes.data,
                        byref(bmi),
                        0  # DIB_RGB_COLORS
                    )
                except Exception as e:
                    print(f"❌ 모자이크 그리기 오류: {e} @ ({x},{y},{w},{h})")
            
            # 투명 윈도우 업데이트
            blend_function = ctypes.c_ulonglong(0x01FF0000)  # AC_SRC_OVER, 255 alpha
            pos = wintypes.POINT(0, 0)
            size = wintypes.SIZE(self.screen_width, self.screen_height)
            
            windll.user32.UpdateLayeredWindow(
                self.hwnd,
                hdc,
                None,
                byref(size),
                mem_dc,
                byref(pos),
                0,
                byref(blend_function),
                ULW_ALPHA
            )
            
            # 리소스 정리
            windll.gdi32.SelectObject(mem_dc, old_bitmap)
            windll.gdi32.DeleteObject(bitmap)
            windll.gdi32.DeleteDC(mem_dc)
            windll.user32.ReleaseDC(self.hwnd, hdc)
            
        except Exception as e:
            print(f"❌ 모자이크 그리기 오류: {e}")
            import traceback
            traceback.print_exc()
    
    def _save_debug_image(self, original_image):
        """디버깅용 이미지 저장"""
        try:
            if original_image is None or not self.mosaic_regions:
                return
                
            # 표시된 모자이크 영역 시각화
            debug_image = original_image.copy()
            for x, y, w, h, label, _ in self.mosaic_regions:
                cv2.rectangle(debug_image, (x, y), (x+w, y+h), (0, 255, 0), 2)
                cv2.putText(debug_image, label, (x, y-5), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 255, 0), 1)
            
            # 이미지 저장
            debug_path = f"{self.debug_dir}/overlay_direct_{time.strftime('%Y%m%d_%H%M%S')}.jpg"
            cv2.imwrite(debug_path, debug_image)
            print(f"📸 디버깅용 이미지 저장: {debug_path}")
        except Exception as e:
            print(f"⚠️ 디버깅 이미지 저장 실패: {e}")
    
    def get_window_handle(self):
        """윈도우 핸들 반환"""
        return self.hwnd
    
    def __del__(self):
        """소멸자"""
        self.hide()
        if self.hwnd:
            windll.user32.DestroyWindow(self.hwnd)