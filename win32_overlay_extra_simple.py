# win32_overlay_extra_simple.py
import cv2
import numpy as np
import time
import os
import threading
import win32gui
import win32con
import win32api
from PIL import Image, ImageDraw, ImageFont
from PIL import ImageGrab

class Win32OverlayWindow:
    def __init__(self):
        self.classname = "MosaicOverlayWindowClass"
        self.title = "Mosaic Overlay"
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
        elif msg == win32con.WM_PAINT:
            self._on_paint(hwnd)
            return 0
        return win32gui.DefWindowProc(hwnd, msg, wparam, lparam)
    
    def _on_paint(self, hwnd):
        """WM_PAINT 메시지 처리"""
        # Validation region으로 Paint 구조체 생성
        ps = win32gui.PAINTSTRUCT()
        hdc = win32gui.BeginPaint(hwnd, ps)
        
        # 화면 업데이트 처리 (렌더링 스레드에서 처리되므로 여기서는 생략)
        
        # 페인트 종료
        win32gui.EndPaint(hwnd, ps)
    
    def _create_window(self):
        """오버레이 윈도우 생성"""
        # 창 스타일 설정 (투명 윈도우)
        style = win32con.WS_POPUP
        ex_style = (win32con.WS_EX_LAYERED | 
                    win32con.WS_EX_TRANSPARENT | 
                    win32con.WS_EX_TOPMOST)
        
        try:
            # 윈도우 생성
            self.hwnd = win32gui.CreateWindowEx(
                ex_style,
                self.classname,
                self.title,
                style,
                0, 0, self.width, self.height,
                0, 0, win32api.GetModuleHandle(None), None
            )
            
            # 윈도우 투명도 설정 (기본 투명)
            win32gui.SetLayeredWindowAttributes(
                self.hwnd, 0, 0, win32con.LWA_ALPHA
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
        
        # 기본 폰트 설정
        try:
            font = ImageFont.truetype("arial.ttf", 14)
        except:
            font = ImageFont.load_default()
        
        while not self.stop_event.is_set():
            try:
                if self.shown and self.mosaic_regions:
                    start_time = time.time()
                    
                    # 투명 이미지 생성 (RGBA 모드)
                    overlay_img = Image.new('RGBA', (self.width, self.height), (0, 0, 0, 0))
                    draw = ImageDraw.Draw(overlay_img)
                    
                    # 모자이크 영역 그리기
                    for x, y, w, h, label, _ in self.mosaic_regions:
                        # 빨간색 반투명 사각형
                        draw.rectangle(
                            [(x, y), (x+w, y+h)], 
                            fill=(255, 0, 0, 180),  # 빨간색 반투명
                            outline=(255, 255, 255, 255)  # 흰색 테두리
                        )
                        
                        # 텍스트 그리기 (흰색)
                        draw.text((x+5, y+5), label, fill=(255, 255, 255, 255), font=font)
                    
                    # 비트맵 저장
                    temp_file = "temp_overlay.png"
                    overlay_img.save(temp_file)
                    
                    # 비트맵 로드 및 윈도우 업데이트
                    try:
                        self._update_layered_window(temp_file)
                    except Exception as e:
                        print(f"❌ 윈도우 업데이트 실패: {e}")
                    
                    # 임시 파일 삭제
                    try:
                        os.remove(temp_file)
                    except:
                        pass
                    
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
                import traceback
                traceback.print_exc()
                time.sleep(0.1)
    
    def _update_layered_window(self, image_path):
        """레이어드 윈도우 업데이트 (더 단순한 방식)"""
        try:
            # 활성화된 경우만 보이도록 설정
            win32gui.SetLayeredWindowAttributes(
                self.hwnd, 0, 255, win32con.LWA_ALPHA
            )
            
            # 윈도우 업데이트 요청
            win32gui.InvalidateRect(self.hwnd, None, True)
            win32gui.UpdateWindow(self.hwnd)
        except Exception as e:
            print(f"❌ 레이어드 윈도우 업데이트 실패: {e}")
            import traceback
            traceback.print_exc()
    
    def show(self):
        """오버레이 창 표시"""
        print("✅ Win32 오버레이 창 표시")
        if self.hwnd:
            # 창 표시
            win32gui.SetLayeredWindowAttributes(self.hwnd, 0, 255, win32con.LWA_ALPHA)
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