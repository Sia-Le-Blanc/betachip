# win32_overlay_window.py
import cv2
import numpy as np
import time
import os
import threading
import ctypes
from ctypes import windll, wintypes, byref, c_int, c_uint, c_void_p, c_byte, c_long

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
ULW_COLORKEY = 0x1
AC_SRC_OVER = 0x0
AC_SRC_ALPHA = 0x1

# BLENDFUNCTION 구조체 정의
class BLENDFUNCTION(ctypes.Structure):
    _fields_ = [
        ('BlendOp', c_byte),
        ('BlendFlags', c_byte),
        ('SourceConstantAlpha', c_byte),
        ('AlphaFormat', c_byte)
    ]

# POINT 구조체 정의 
class POINT(ctypes.Structure):
    _fields_ = [('x', c_long), ('y', c_long)]

# SIZE 구조체 정의
class SIZE(ctypes.Structure):
    _fields_ = [('cx', c_long), ('cy', c_long)]

# BITMAPINFOHEADER 구조체 정의
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
        self.render_interval = 1/30  # 30fps
        
        # 윈도우 클래스 등록 및 생성
        self._register_window_class()
        self._create_window()
        
        print(f"✅ Win32 API 기반 오버레이 창 초기화 완료 (해상도: {self.width}x{self.height})")
    
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
        
        # 윈도우 프로시저 콜백 저장 (참조를 유지하기 위해)
        self._wndproc_callback = WNDPROC(self._window_proc)
        
        # WNDCLASSEX 구조체 정의 및 설정
        class WNDCLASSEX(ctypes.Structure):
            _fields_ = [
                ('cbSize', c_uint),
                ('style', c_uint),
                ('lpfnWndProc', WNDPROC),
                ('cbClsExtra', c_int),
                ('cbWndExtra', c_int),
                ('hInstance', c_void_p),
                ('hIcon', c_void_p),
                ('hCursor', c_void_p),
                ('hbrBackground', c_void_p),
                ('lpszMenuName', ctypes.c_char_p),
                ('lpszClassName', ctypes.c_char_p),
                ('hIconSm', c_void_p)
            ]
        
        wc = WNDCLASSEX()
        wc.cbSize = ctypes.sizeof(WNDCLASSEX)
        wc.style = 0
        wc.lpfnWndProc = self._wndproc_callback
        wc.cbClsExtra = 0
        wc.cbWndExtra = 0
        wc.hInstance = windll.kernel32.GetModuleHandleA(None)
        wc.hIcon = 0
        wc.hCursor = windll.user32.LoadCursorA(0, 32512)  # IDC_ARROW
        wc.hbrBackground = 0
        wc.lpszMenuName = None
        wc.lpszClassName = b"MosaicOverlayClass"
        wc.hIconSm = 0
        
        # 클래스 등록
        if not windll.user32.RegisterClassExA(byref(wc)):
            error = ctypes.GetLastError()
            print(f"❌ 윈도우 클래스 등록 실패. 오류 코드: {error}")
            
        self.wc = wc
    
    def _window_proc(self, hwnd, msg, wparam, lparam):
        """윈도우 프로시저"""
        try:
            if msg == 0x10:  # WM_CLOSE
                windll.user32.DestroyWindow(hwnd)
                return 0
            elif msg == 0x2:  # WM_DESTROY
                windll.user32.PostQuitMessage(0)
                return 0
            
            # DefWindowProc 호출
            return windll.user32.DefWindowProcA(
                c_void_p(hwnd), 
                c_uint(msg), 
                c_void_p(wparam), 
                c_void_p(lparam)
            )
        except Exception as e:
            print(f"❌ 윈도우 프로시저 오류: {e}")
            return 0
    
    def _create_window(self):
        """투명 오버레이 윈도우 생성"""
        try:
            # 윈도우 생성
            self.hwnd = windll.user32.CreateWindowExA(
                WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
                b"MosaicOverlayClass",
                b"Mosaic Overlay",
                WS_POPUP,  # WS_VISIBLE 제거하고 나중에 ShowWindow로 표시
                0, 0, self.width, self.height,
                None, None, self.wc.hInstance, None
            )
            
            if not self.hwnd:
                error = ctypes.GetLastError()
                print(f"❌ 윈도우 생성 실패. 오류 코드: {error}")
                return
            
            # 투명도 설정 - 완전 투명으로 시작
            windll.user32.SetLayeredWindowAttributes(self.hwnd, 0, 0, ULW_ALPHA)
            
            # 항상 최상위로 설정
            windll.user32.SetWindowPos(
                self.hwnd, HWND_TOPMOST,
                0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
            )
            
            print(f"✅ 오버레이 윈도우 생성 완료: 핸들={self.hwnd}")
        except Exception as e:
            print(f"❌ 윈도우 생성 오류: {e}")
            import traceback
            traceback.print_exc()
    
    def _render_thread_func(self):
        """렌더링 스레드 함수"""
        last_render_time = time.time()
        frame_count = 0
        
        while not self.stop_event.is_set():
            try:
                if self.shown:
                    start_time = time.time()
                    
                    # 렌더링 수행
                    if self.mosaic_regions:
                        self._render_overlay()
                    else:
                        # 모자이크 영역이 없으면 완전 투명하게
                        self._clear_overlay()
                    
                    # FPS 계산 및 출력
                    frame_count += 1
                    elapsed = time.time() - start_time
                    if frame_count % 30 == 0:  # 30프레임마다 FPS 출력
                        fps = 30 / (time.time() - last_render_time)
                        print(f"⚡️ 오버레이 렌더링 FPS: {fps:.1f}, 시간: {elapsed*1000:.1f}ms")
                        last_render_time = time.time()
                        frame_count = 0
                    
                    # 프레임 타이밍 조절
                    sleep_time = max(0.001, self.render_interval - elapsed)
                    time.sleep(sleep_time)
                else:
                    time.sleep(0.1)  # 비활성화 상태에서는 CPU 사용 줄이기
            except Exception as e:
                print(f"❌ 렌더링 스레드 오류: {e}")
                time.sleep(0.1)
    
    def _render_overlay(self):
        """모자이크 오버레이 렌더링"""
        try:
            # 디바이스 컨텍스트 가져오기
            hdc = windll.user32.GetDC(self.hwnd)
            if not hdc:
                print("❌ 디바이스 컨텍스트 가져오기 실패")
                return
            
            # 메모리 DC 생성
            mem_dc = windll.gdi32.CreateCompatibleDC(hdc)
            if not mem_dc:
                windll.user32.ReleaseDC(self.hwnd, hdc)
                print("❌ 메모리 DC 생성 실패")
                return
            
            # 비트맵 생성
            bitmap = windll.gdi32.CreateCompatibleBitmap(hdc, self.width, self.height)
            if not bitmap:
                windll.gdi32.DeleteDC(mem_dc)
                windll.user32.ReleaseDC(self.hwnd, hdc)
                print("❌ 비트맵 생성 실패")
                return
            
            old_bitmap = windll.gdi32.SelectObject(mem_dc, bitmap)
            
            # 투명한 배경으로 초기화
            windll.gdi32.PatBlt(mem_dc, 0, 0, self.width, self.height, 0x00000042)  # BLACKNESS
            
            # 모자이크 영역 그리기
            for x, y, w, h, label, mosaic_img in self.mosaic_regions:
                if mosaic_img is None:
                    continue
                    
                try:
                    # OpenCV 이미지를 Windows 비트맵으로 변환
                    height, width = mosaic_img.shape[:2]
                    
                    # BGR to BGRA 변환
                    bgra = cv2.cvtColor(mosaic_img, cv2.COLOR_BGR2BGRA)
                    bgra[:, :, 3] = 255  # 알파 채널 설정 (완전 불투명)
                    
                    # BITMAPINFO 생성
                    bmi = BITMAPINFOHEADER()
                    bmi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
                    bmi.biWidth = width
                    bmi.biHeight = -height  # 상하 반전
                    bmi.biPlanes = 1
                    bmi.biBitCount = 32
                    bmi.biCompression = 0  # BI_RGB
                    
                    # 이미지 그리기를 위한 데이터 포인터 설정
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
                    print(f"❌ 모자이크 그리기 오류: {e}")
            
            # 블렌드 함수 설정
            blend_function = BLENDFUNCTION()
            blend_function.BlendOp = AC_SRC_OVER
            blend_function.BlendFlags = 0
            blend_function.SourceConstantAlpha = 255  # 완전 불투명 (값이 낮을수록 더 투명)
            blend_function.AlphaFormat = AC_SRC_ALPHA  # 소스 이미지의 알파 채널 사용
            
            # 투명 윈도우 업데이트
            point_zero = POINT(0, 0)
            size = SIZE(self.width, self.height)
            
            # UpdateLayeredWindow 호출
            result = windll.user32.UpdateLayeredWindow(
                self.hwnd,
                hdc,
                None,  # 위치 유지 (NULL)
                byref(size),
                mem_dc,
                byref(point_zero),
                0,  # RGB 색상 (0)
                byref(blend_function),
                ULW_ALPHA
            )
            
            if not result:
                error = ctypes.GetLastError()
                print(f"❌ UpdateLayeredWindow 실패. 오류 코드: {error}")
            
            # 리소스 정리
            windll.gdi32.SelectObject(mem_dc, old_bitmap)
            windll.gdi32.DeleteObject(bitmap)
            windll.gdi32.DeleteDC(mem_dc)
            windll.user32.ReleaseDC(self.hwnd, hdc)
            
        except Exception as e:
            print(f"❌ 렌더링 오류: {e}")
            import traceback
            traceback.print_exc()
    
    def _clear_overlay(self):
        """오버레이 창을 완전히 투명하게 설정"""
        try:
            # 투명한 배경으로 설정
            windll.user32.SetLayeredWindowAttributes(self.hwnd, 0, 0, ULW_ALPHA)
        except Exception as e:
            print(f"❌ 오버레이 투명화 오류: {e}")
    
    def show(self):
        """오버레이 창 표시"""
        print("✅ Win32 오버레이 창 표시")
        if self.hwnd:
            # 창 표시
            result = windll.user32.ShowWindow(self.hwnd, 5)  # SW_SHOW
            if not result:
                error = ctypes.GetLastError()
                print(f"⚠️ ShowWindow 반환값: {result}")  # 에러가 아닐 수 있음
            
            self.shown = True
            
            # 최상위 윈도우로 설정
            windll.user32.SetWindowPos(
                self.hwnd, HWND_TOPMOST,
                0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE
            )
            
            # 렌더링 스레드 시작
            if self.render_thread is None or not self.render_thread.is_alive():
                self.stop_event.clear()
                self.render_thread = threading.Thread(target=self._render_thread_func, daemon=True)
                self.render_thread.start()
                print("✅ 렌더링 스레드 시작됨")
        else:
            print("❌ 윈도우 핸들이 없습니다")
    
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
                return
            
            self.frame_count += 1
            
            # 모자이크 정보 저장
            self.original_image = original_image
            self.mosaic_regions = mosaic_regions
            
            # 모자이크 영역 조회 및 로그 출력
            if len(mosaic_regions) > 0:
                if self.frame_count % 30 == 0:  # 30프레임마다 로그 출력
                    print(f"✅ 모자이크 영역 {len(mosaic_regions)}개 처리 중 (프레임 #{self.frame_count})")
                    self._save_debug_image()
            else:
                if self.frame_count % 100 == 0:  # 100프레임마다 로그 출력
                    print(f"📢 모자이크 영역 없음 (프레임 #{self.frame_count})")
        
        except Exception as e:
            print(f"❌ 오버레이 업데이트 실패: {e}")
    
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
                cv2.rectangle(debug_image, (x, y), (x+w, y+h), (0, 0, 255), 2)  # 빨간색
                cv2.putText(debug_image, label, (x, y-5), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 0, 255), 1)
            
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