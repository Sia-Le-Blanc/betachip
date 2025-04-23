# win32_overlay_window.py
import cv2
import numpy as np
import time
import os
import threading
import ctypes
from ctypes import windll, wintypes, byref, c_int, c_uint, c_void_p

# Windows 상수 정의
WS_EX_LAYERED = 0x80000
WS_EX_TRANSPARENT = 0x20
WS_EX_TOPMOST = 0x8
WS_EX_TOOLWINDOW = 0x80
WS_POPUP = 0x80000000
WS_VISIBLE = 0x10000000

GWL_EXSTYLE = -20
HWND_TOPMOST = -1
SWP_NOSIZE = 0x1
SWP_NOMOVE = 0x2
SWP_NOACTIVATE = 0x10
ULW_ALPHA = 0x2
AC_SRC_OVER = 0x0
AC_SRC_ALPHA = 0x1

class Win32OverlayWindow:
    """Win32 API를 사용하는 모자이크 오버레이 윈도우"""
    def __init__(self):
        self.hwnd = None
        self.width = windll.user32.GetSystemMetrics(0)  # SM_CXSCREEN
        self.height = windll.user32.GetSystemMetrics(1)  # SM_CYSCREEN
        self.shown = False
        self.mosaic_regions = []
        self.original_image = None
        self.frame_count = 0
        self.debug_dir = "debug_overlay"
        os.makedirs(self.debug_dir, exist_ok=True)
        
        # 렌더링 스레드 관련
        self.render_thread = None
        self.stop_event = threading.Event()
        self.render_interval = 1/60  # 60fps
        
        # 윈도우 클래스 등록 및 생성
        if self._register_window_class():
            self._create_window()
        
        print("✅ Win32 API 기반 오버레이 창 초기화 완료")
    
    def _register_window_class(self):
        """윈도우 클래스 등록"""
        # WNDPROC 타입 정의 (윈도우 프로시저 함수 포인터 타입)
        WNDPROC = ctypes.WINFUNCTYPE(
            ctypes.c_long, 
            ctypes.c_void_p, 
            ctypes.c_uint, 
            ctypes.c_void_p, 
            ctypes.c_void_p
        )
        
        # WNDCLASSEXW 구조체 정의
        class WNDCLASSEXW(ctypes.Structure):
            _fields_ = [
                ('cbSize', ctypes.c_uint),
                ('style', ctypes.c_uint),
                ('lpfnWndProc', WNDPROC),
                ('cbClsExtra', ctypes.c_int),
                ('cbWndExtra', ctypes.c_int),
                ('hInstance', ctypes.c_void_p),
                ('hIcon', ctypes.c_void_p),
                ('hCursor', ctypes.c_void_p),
                ('hbrBackground', ctypes.c_void_p),
                ('lpszMenuName', ctypes.c_wchar_p),
                ('lpszClassName', ctypes.c_wchar_p),
                ('hIconSm', ctypes.c_void_p)
            ]
        
        # 윈도우 프로시저 콜백 저장 (참조를 유지하기 위해)
        self._wndproc_callback = WNDPROC(self._window_proc)
        
        # 윈도우 클래스 설정
        self.wc = WNDCLASSEXW()
        self.wc.cbSize = ctypes.sizeof(WNDCLASSEXW)
        self.wc.style = 0
        self.wc.lpfnWndProc = self._wndproc_callback
        self.wc.cbClsExtra = 0
        self.wc.cbWndExtra = 0
        self.wc.hInstance = windll.kernel32.GetModuleHandleW(None)
        self.wc.hIcon = 0
        self.wc.hCursor = windll.user32.LoadCursorW(0, 32512)  # IDC_ARROW
        self.wc.hbrBackground = 0
        self.wc.lpszMenuName = None
        self.wc.lpszClassName = "MosaicOverlayClass"
        self.wc.hIconSm = 0
        
        # 클래스 등록
        if not windll.user32.RegisterClassExW(byref(self.wc)):
            error = ctypes.GetLastError()
            print(f"❌ 윈도우 클래스 등록 실패. 오류 코드: {error}")
            return False
        
        return True
    
    def _window_proc(self, hwnd, msg, wparam, lparam):
        """윈도우 프로시저"""
        if msg == 0x10:  # WM_CLOSE
            windll.user32.DestroyWindow(hwnd)
        elif msg == 0x2:  # WM_DESTROY
            windll.user32.PostQuitMessage(0)
        
        # DefWindowProcW 호출 시 매개변수 타입 수정
        return windll.user32.DefWindowProcW(
            ctypes.c_void_p(hwnd), 
            ctypes.c_uint(msg), 
            ctypes.c_void_p(wparam), 
            ctypes.c_void_p(lparam)
        )
    
    def _create_window(self):
        """투명 오버레이 윈도우 생성"""
        # 함수 원형 선언
        CreateWindowExW = windll.user32.CreateWindowExW
        CreateWindowExW.argtypes = [
            ctypes.c_uint, ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_uint,
            ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
            ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p
        ]
        CreateWindowExW.restype = ctypes.c_void_p
        
        # 윈도우 생성
        self.hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
            "MosaicOverlayClass",  # 직접 문자열 전달
            "Mosaic Overlay",
            WS_POPUP | WS_VISIBLE,
            0, 0, self.width, self.height,
            None, None, self.wc.hInstance, None
        )
        
        if not self.hwnd:
            error = ctypes.GetLastError()
            print(f"❌ 윈도우 생성 실패. 오류 코드: {error}")
            return
        
        # 윈도우를 투명하게 설정
        windll.user32.SetLayeredWindowAttributes(self.hwnd, 0, 255, 2)  # LWA_ALPHA = 2
        
        # 항상 최상위로 설정
        windll.user32.SetWindowPos(
            self.hwnd, HWND_TOPMOST,
            0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
        )
    
    def _render_thread_func(self):
        """렌더링 스레드 함수"""
        while not self.stop_event.is_set():
            if self.shown and self.mosaic_regions:
                self._render_overlay()
            time.sleep(self.render_interval)
    
    def _render_overlay(self):
        """모자이크 오버레이 렌더링"""
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
            bitmap = windll.gdi32.CreateCompatibleBitmap(hdc, self.width, self.height)
            if not bitmap:
                windll.gdi32.DeleteDC(mem_dc)
                windll.user32.ReleaseDC(self.hwnd, hdc)
                return
            
            old_bitmap = windll.gdi32.SelectObject(mem_dc, bitmap)
            
            # 투명한 배경으로 초기화
            windll.gdi32.SelectObject(mem_dc, windll.gdi32.GetStockObject(5))  # NULL_BRUSH
            windll.gdi32.Rectangle(mem_dc, 0, 0, self.width, self.height)
            
            # 모자이크 영역 그리기
            for x, y, w, h, label, mosaic_img in self.mosaic_regions:
                if mosaic_img is None:
                    continue
                    
                try:
                    # OpenCV 이미지를 Windows 비트맵으로 변환
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
                    
                    # 이미지 그리기를 위한 데이터 포인터 설정 (오버플로우 해결)
                    data_ptr = bgra.ctypes.data_as(ctypes.POINTER(ctypes.c_ubyte))
                    
                    # 이미지 그리기
                    windll.gdi32.SetDIBitsToDevice(
                        mem_dc, x, y, width, height,
                        0, 0, 0, height,
                        data_ptr,
                        byref(bmi),
                        0  # DIB_RGB_COLORS
                    )
                except Exception as e:
                    print(f"❌ 모자이크 그리기 오류: {e} @ ({x},{y},{w},{h})")
            
            # BLENDFUNCTION 구조체 정의
            class BLENDFUNCTION(ctypes.Structure):
                _fields_ = [
                    ('BlendOp', ctypes.c_byte),
                    ('BlendFlags', ctypes.c_byte),
                    ('SourceConstantAlpha', ctypes.c_byte),
                    ('AlphaFormat', ctypes.c_byte)
                ]
            
            # 블렌드 함수 설정
            bf = BLENDFUNCTION()
            bf.BlendOp = AC_SRC_OVER
            bf.BlendFlags = 0
            bf.SourceConstantAlpha = 255
            bf.AlphaFormat = AC_SRC_ALPHA
            
            # 투명 윈도우 업데이트
            pos = wintypes.POINT(0, 0)
            size = wintypes.SIZE(self.width, self.height)
            srcpos = wintypes.POINT(0, 0)
            
            windll.user32.UpdateLayeredWindow(
                self.hwnd,
                hdc,
                byref(pos),
                byref(size),
                mem_dc,
                byref(srcpos),
                0,
                byref(bf),
                ULW_ALPHA
            )
            
            # 리소스 정리
            windll.gdi32.SelectObject(mem_dc, old_bitmap)
            windll.gdi32.DeleteObject(bitmap)
            windll.gdi32.DeleteDC(mem_dc)
            windll.user32.ReleaseDC(self.hwnd, hdc)
            
        except Exception as e:
            print(f"❌ 렌더링 오류: {e}")
            import traceback
            traceback.print_exc()
    
    def show(self):
        """오버레이 창 표시"""
        print("✅ Win32 오버레이 창 표시")
        if self.hwnd:
            windll.user32.ShowWindow(self.hwnd, 5)  # SW_SHOW
            self.shown = True
            
            # 렌더링 스레드 시작
            if self.render_thread is None or not self.render_thread.is_alive():
                self.stop_event.clear()
                self.render_thread = threading.Thread(target=self._render_thread_func, daemon=True)
                self.render_thread.start()
                print("✅ 렌더링 스레드 시작됨")
    
    def hide(self):
        """오버레이 창 숨기기"""
        print("🛑 Win32 오버레이 창 숨기기")
        if self.hwnd:
            windll.user32.ShowWindow(self.hwnd, 0)  # SW_HIDE
            self.shown = False
            
            # 렌더링 스레드 중지
            if self.render_thread and self.render_thread.is_alive():
                self.stop_event.set()
                self.render_thread.join(timeout=1.0)
                self.render_thread = None
                print("🛑 렌더링 스레드 중지됨")
    
    def update_regions(self, original_image, mosaic_regions):
        """모자이크 영역 업데이트"""
        try:
            if original_image is None:
                print("❌ 오버레이 업데이트: 원본 이미지가 없습니다.")
                return
            
            self.frame_count += 1
            
            # 원본 이미지와 모자이크 영역 저장
            self.original_image = original_image
            self.mosaic_regions = mosaic_regions
            
            # 로그 출력
            if len(mosaic_regions) > 0 and self.frame_count % 100 == 0:
                print(f"✅ 모자이크 영역 {len(mosaic_regions)}개 처리 중 (프레임 #{self.frame_count})")
                self._save_debug_image()
            
        except Exception as e:
            print(f"❌ 오버레이 업데이트 실패: {e}")
            import traceback
            traceback.print_exc()
    
    def get_window_handle(self):
        """윈도우 핸들 반환"""
        return self.hwnd
    
    def _save_debug_image(self):
        """디버깅용 이미지 저장"""
        try:
            if self.original_image is None or not self.mosaic_regions:
                return
                
            # 표시된 모자이크 영역 시각화
            debug_image = self.original_image.copy()
            for x, y, w, h, label, _ in self.mosaic_regions:
                # 원본 이미지에 박스 표시
                cv2.rectangle(debug_image, (x, y), (x+w, y+h), (0, 255, 0), 2)
                cv2.putText(debug_image, label, (x, y-5), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 255, 0), 1)
            
            # 이미지 저장
            debug_path = f"{self.debug_dir}/overlay_win32_{time.strftime('%Y%m%d_%H%M%S')}.jpg"
            cv2.imwrite(debug_path, debug_image)
            print(f"📸 디버깅용 오버레이 이미지 저장: {debug_path}")
        except Exception as e:
            print(f"⚠️ 디버깅 이미지 저장 실패: {e}")
    
    def __del__(self):
        """소멸자"""
        self.hide()
        if self.hwnd:
            windll.user32.DestroyWindow(self.hwnd)