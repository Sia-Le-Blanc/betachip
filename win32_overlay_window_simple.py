# win32_overlay_window_simple.py
import cv2
import numpy as np
import time
import os
import threading
import win32gui
import win32con
import win32api
import win32ui
from ctypes import windll

class Win32OverlayWindow:
    def __init__(self):
        self.classname = "MosaicOverlayClass"
        self.width = win32api.GetSystemMetrics(0)  # SM_CXSCREEN
        self.height = win32api.GetSystemMetrics(1)  # SM_CYSCREEN
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
        self._register_class()
        self._create_window()
        
        print(f"✅ 단순화된 Win32 기반 오버레이 초기화 완료 (해상도: {self.width}x{self.height})")
    
    def _register_class(self):
        """윈도우 클래스 등록"""
        wc = win32gui.WNDCLASS()
        wc.lpfnWndProc = self._wnd_proc_stub
        wc.hInstance = win32api.GetModuleHandle(None)
        wc.hCursor = win32gui.LoadCursor(0, win32con.IDC_ARROW)
        wc.hbrBackground = win32gui.GetStockObject(win32con.BLACK_BRUSH)
        wc.lpszClassName = self.classname
        
        try:
            win32gui.RegisterClass(wc)
            print("✅ 윈도우 클래스 등록 성공")
        except Exception as e:
            print(f"⚠️ 윈도우 클래스 등록 실패 (이미 등록되었을 수 있음): {e}")
    
    def _wnd_proc_stub(self, hwnd, msg, wparam, lparam):
        """윈도우 프로시져"""
        if msg == win32con.WM_DESTROY:
            win32gui.PostQuitMessage(0)
            return 0
        return win32gui.DefWindowProc(hwnd, msg, wparam, lparam)
    
    def _create_window(self):
        """오버레이 윈도우 생성"""
        style = win32con.WS_POPUP
        ex_style = (win32con.WS_EX_LAYERED | 
                   win32con.WS_EX_TRANSPARENT | 
                   win32con.WS_EX_TOPMOST)
        
        try:
            self.hwnd = win32gui.CreateWindowEx(
                ex_style,
                self.classname,
                "Mosaic Overlay",
                style,
                0, 0, self.width, self.height,
                0, 0, win32api.GetModuleHandle(None), None
            )
            
            # 투명 속성 설정
            windll.user32.SetLayeredWindowAttributes(
                self.hwnd, 0, 0, win32con.LWA_ALPHA
            )
            
            print(f"✅ 오버레이 윈도우 생성 완료: 핸들={self.hwnd}")
        except Exception as e:
            print(f"❌ 윈도우 생성 오류: {e}")
    
    def _render_thread_func(self):
        """렌더링 스레드 함수"""
        last_render_time = time.time()
        frame_count = 0
        
        while not self.stop_event.is_set():
            try:
                if self.shown and self.mosaic_regions:
                    start_time = time.time()
                    
                    # 렌더링 수행
                    self._render_overlay()
                    
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
            # 화면 DC 가져오기
            hdc = win32gui.GetDC(self.hwnd)
            mfcDC = win32ui.CreateDCFromHandle(hdc)
            saveDC = mfcDC.CreateCompatibleDC()
            
            # 비트맵 생성
            saveBitMap = win32ui.CreateBitmap()
            saveBitMap.CreateCompatibleBitmap(mfcDC, self.width, self.height)
            saveDC.SelectObject(saveBitMap)
            
            # 비트맵 초기화 (검은색 배경)
            saveDC.PatBlt(0, 0, self.width, self.height, win32con.BLACKNESS)
            
            # 모자이크 영역 그리기 - 각 영역을 단순한 색상 사각형으로 그림
            for x, y, w, h, label, _ in self.mosaic_regions:
                # 빨간색 브러시 생성
                red_brush = win32gui.CreateSolidBrush(0x0000FF)  # BGR 색상 (빨간색)
                old_brush = saveDC.SelectObject(red_brush)
                
                # 사각형 그리기
                saveDC.Rectangle(x, y, x+w, y+h)
                
                # 브러시 해제
                saveDC.SelectObject(old_brush)
                win32gui.DeleteObject(red_brush)
                
                # 텍스트 출력
                saveDC.SetTextColor(0xFFFFFF)  # 흰색
                saveDC.SetBkMode(win32con.TRANSPARENT)
                saveDC.DrawText(label, (x+5, y+5, x+w, y+30), win32con.DT_SINGLELINE)
            
            # 비트맵을 윈도우로 전송
            windll.user32.SetLayeredWindowAttributes(
                self.hwnd, 0, 255, win32con.LWA_ALPHA
            )
            
            mfcDC.BitBlt((0, 0), (self.width, self.height), saveDC, (0, 0), win32con.SRCCOPY)
            
            # 리소스 정리
            saveDC.DeleteDC()
            mfcDC.DeleteDC()
            win32gui.ReleaseDC(self.hwnd, hdc)
            win32gui.DeleteObject(saveBitMap.GetHandle())
            
        except Exception as e:
            print(f"❌ 렌더링 오류: {e}")
            import traceback
            traceback.print_exc()
    
    def show(self):
        """오버레이 창 표시"""
        print("✅ Win32 오버레이 창 표시")
        if self.hwnd:
            # 창 표시
            win32gui.ShowWindow(self.hwnd, win32con.SW_SHOW)
            win32gui.UpdateWindow(self.hwnd)
            
            # 항상 최상위로 유지
            win32gui.SetWindowPos(
                self.hwnd, win32con.HWND_TOPMOST, 0, 0, 0, 0, 
                win32con.SWP_NOMOVE | win32con.SWP_NOSIZE | win32con.SWP_NOACTIVATE
            )
            
            self.shown = True
            
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
            win32gui.ShowWindow(self.hwnd, win32con.SW_HIDE)
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
            win32gui.DestroyWindow(self.hwnd)