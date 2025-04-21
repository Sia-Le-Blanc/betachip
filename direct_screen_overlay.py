# direct_screen_overlay.py 파일 생성
import cv2
import numpy as np
import threading
import time
import os
import mss
import mss.tools
from PIL import Image, ImageDraw
import win32gui
import win32con
import win32api

class DirectScreenOverlay:
    """MSS와 직접 스크린 조작을 사용한 고성능 오버레이"""
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
        
        print(f"✅ 직접 스크린 오버레이 초기화 (해상도: {self.screen_width}x{self.screen_height})")
    
    def show(self):
        """오버레이 활성화 및 렌더링 스레드 시작"""
        print("✅ 직접 스크린 오버레이 표시")
        self.active = True
        self.stop_event.clear()
        
        if self.render_thread is None or not self.render_thread.is_alive():
            self.render_thread = threading.Thread(target=self._render_loop, daemon=True)
            self.render_thread.start()
    
    def hide(self):
        """오버레이 비활성화 및 렌더링 스레드 중지"""
        print("🛑 직접 스크린 오버레이 숨기기")
        self.active = False
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
            # 창 생성
            self._create_overlay_window()
            
            # MSS 캡처 객체 생성
            with mss.mss() as sct:
                while not self.stop_event.is_set() and self.active:
                    start_time = time.time()
                    
                    if not self.mosaic_regions:
                        time.sleep(0.016)  # 약 60fps
                        continue
                    
                    # 현재 화면 캡처
                    screen = sct.grab(sct.monitors[0])
                    screen_np = np.array(screen)
                    
                    # 모자이크 영역 합성
                    for x, y, w, h, label, mosaic_img in self.mosaic_regions:
                        try:
                            if x < 0 or y < 0 or x + w > self.screen_width or y + h > self.screen_height:
                                continue
                            if w <= 0 or h <= 0 or mosaic_img is None:
                                continue
                            
                            # 좌표 변환 (MSS는 상단 왼쪽 시작)
                            screen_np[y:y+h, x:x+w] = mosaic_img
                        except Exception as e:
                            pass
                    
                    # PIL 이미지로 변환
                    screen_pil = Image.fromarray(screen_np)
                    
                    # 창에 이미지 표시
                    self._update_window_image(screen_pil)
                    
                    # FPS 계산 및 제한
                    elapsed = time.time() - start_time
                    self.fps_times.append(elapsed)
                    if len(self.fps_times) > 60:
                        self.fps_times.pop(0)
                    
                    # FPS 출력 (10프레임마다)
                    if self.frame_count % 60 == 0:
                        avg_time = sum(self.fps_times) / len(self.fps_times)
                        fps = 1.0 / avg_time if avg_time > 0 else 0
                        print(f"⚡️ 렌더링 FPS: {fps:.1f}, 평균 처리 시간: {avg_time*1000:.1f}ms")
                    
                    # 프레임 레이트 제한
                    sleep_time = max(0, 0.016 - elapsed)  # 약 60fps
                    if sleep_time > 0:
                        time.sleep(sleep_time)
            
            # 창 정리
            self._destroy_overlay_window()
            
        except Exception as e:
            print(f"❌ 렌더링 루프 오류: {e}")
            import traceback
            traceback.print_exc()
    
    def _create_overlay_window(self):
        """오버레이 창 생성 (Win32 API 사용)"""
        try:
            # 창 클래스 및 스타일 설정은 실제 구현 시 추가
            pass
        except Exception as e:
            print(f"❌ 오버레이 창 생성 오류: {e}")
    
    def _update_window_image(self, pil_image):
        """창에 이미지 업데이트"""
        try:
            # 창에 이미지 표시 로직 구현
            pass
        except Exception as e:
            print(f"❌ 창 이미지 업데이트 오류: {e}")
    
    def _destroy_overlay_window(self):
        """오버레이 창 제거"""
        try:
            # 창 정리 로직 구현
            pass
        except Exception as e:
            print(f"❌ 오버레이 창 제거 오류: {e}")
    
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
        return 0  # 실제 구현에서는 실제 핸들 반환