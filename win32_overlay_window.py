import cv2
import numpy as np
import time
import os
import threading
import ctypes
from ctypes import windll, wintypes, byref, Structure, POINTER, c_void_p, c_int, c_uint, c_bool
from ctypes import windll, wintypes, byref, Structure, POINTER, WINFUNCTYPE
from ctypes import c_void_p, c_int, c_uint, c_bool, c_char_p, c_wchar_p

WNDPROC = WINFUNCTYPE(c_int, c_int, c_uint, c_uint, c_int)

class WNDCLASSEXW(Structure):
    _fields_ = [
        ("cbSize", c_uint),
        ("style", c_uint),
        ("lpfnWndProc", WNDPROC),  # 올바른 함수 포인터 타입 사용
        ("cbClsExtra", c_int),
        ("cbWndExtra", c_int),
        ("hInstance", c_void_p),  # c_int에서 c_void_p로 변경
        ("hIcon", c_void_p),      # c_int에서 c_void_p로 변경
        ("hCursor", c_void_p),    # c_int에서 c_void_p로 변경
        ("hbrBackground", c_void_p),  # c_int에서 c_void_p로 변경
        ("lpszMenuName", c_wchar_p),
        ("lpszClassName", c_wchar_p),
        ("hIconSm", c_void_p)     # c_int에서 c_void_p로 변경
    ]

class RECT(Structure):
    _fields_ = [("left", c_int),
                ("top", c_int),
                ("right", c_int),
                ("bottom", c_int)]

class Win32OverlayWindow:
    """Win32 API를 사용하는 모자이크 오버레이 윈도우"""
    def __init__(self):
        self.width = windll.user32.GetSystemMetrics(0)  # SM_CXSCREEN
        self.height = windll.user32.GetSystemMetrics(1)  # SM_CYSCREEN
        self.shown = False
        self.mosaic_regions = []
        self.original_image = None
        self.frame_count = 0
        self.debug_dir = "debug_overlay"
        os.makedirs(self.debug_dir, exist_ok=True)
        
        print("✅ Win32 API 기반 오버레이 창 초기화 완료 (시뮬레이션 모드)")
    
    def show(self):
        """오버레이 창 표시"""
        print("✅ 오버레이 창 표시 (시뮬레이션)")
        self.shown = True
    
    def hide(self):
        """오버레이 창 숨기기"""
        print("🛑 오버레이 창 숨기기 (시뮬레이션)")
        self.shown = False
    
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
        return 0  # 시뮬레이션용 더미 핸들
    
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