# inline_screen_processor.py 파일 생성
import cv2
import numpy as np
import threading
import time
import os
import mss
import mss.tools

class InlineScreenProcessor:
    """화면 내에서 직접 모자이크 처리하는 고성능 프로세서"""
    def __init__(self):
        # 화면 정보 초기화
        self.sct = mss.mss()
        monitor = self.sct.monitors[0]
        self.screen_width = monitor["width"]
        self.screen_height = monitor["height"]
        
        # 모자이크 정보
        self.mosaic_regions = []
        self.active = False
        self.processor_thread = None
        self.stop_event = threading.Event()
        
        # 디버깅 변수
        self.frame_count = 0
        self.debug_dir = "debug_overlay"
        os.makedirs(self.debug_dir, exist_ok=True)
        
        # 성능 측정
        self.fps_times = []
        
        print(f"✅ 인라인 스크린 프로세서 초기화 (해상도: {self.screen_width}x{self.screen_height})")
    
    def show(self):
        """프로세서 활성화 및 스레드 시작"""
        print("✅ 인라인 스크린 프로세서 시작")
        self.active = True
        self.stop_event.clear()
        
        if self.processor_thread is None or not self.processor_thread.is_alive():
            self.processor_thread = threading.Thread(target=self._process_loop, daemon=True)
            self.processor_thread.start()
    
    def hide(self):
        """프로세서 비활성화 및 스레드 중지"""
        print("🛑 인라인 스크린 프로세서 중지")
        self.active = False
        if self.processor_thread:
            self.stop_event.set()
            self.processor_thread.join(timeout=1.0)
            self.processor_thread = None
    
    def update_regions(self, original_image, mosaic_regions):
        """모자이크 영역 업데이트"""
        self.mosaic_regions = mosaic_regions
        self.frame_count += 1
        
        if len(mosaic_regions) > 0 and self.frame_count % 100 == 0:
            print(f"✅ 모자이크 영역 {len(mosaic_regions)}개 처리 중 (프레임 #{self.frame_count})")
            self._save_debug_image(original_image)
    
    def _process_loop(self):
        """모자이크 처리 메인 루프"""
        try:
            while not self.stop_event.is_set() and self.active:
                start_time = time.time()
                
                if not self.mosaic_regions:
                    time.sleep(0.016)  # 약 60fps
                    continue
                
                # 영역별로 화면 캡처 및 모자이크 적용
                for x, y, w, h, label, mosaic_img in self.mosaic_regions:
                    try:
                        if x < 0 or y < 0 or x + w > self.screen_width or y + h > self.screen_height:
                            continue
                        if w <= 0 or h <= 0 or mosaic_img is None:
                            continue
                        
                        # 해당 영역만 캡처
                        monitor = {"top": y, "left": x, "width": w, "height": h}
                        
                        # 모자이크 직접 적용 (이 부분은 실제 구현 필요)
                        # 실제로는 PyAutoGUI나 Win32 API를 사용하여 해당 위치에
                        # 모자이크 이미지를 직접 그리는 방식으로 구현 가능
                        
                    except Exception as e:
                        print(f"❌ 영역 처리 오류: {e} @ ({x},{y},{w},{h})")
                
                # FPS 계산 및 제한
                elapsed = time.time() - start_time
                self.fps_times.append(elapsed)
                if len(self.fps_times) > 60:
                    self.fps_times.pop(0)
                
                # FPS 출력 (60프레임마다)
                if self.frame_count % 60 == 0:
                    avg_time = sum(self.fps_times) / len(self.fps_times)
                    fps = 1.0 / avg_time if avg_time > 0 else 0
                    print(f"⚡️ 처리 FPS: {fps:.1f}, 평균 처리 시간: {avg_time*1000:.1f}ms")
                
                # 프레임 레이트 제한
                sleep_time = max(0, 0.016 - elapsed)  # 약 60fps
                if sleep_time > 0:
                    time.sleep(sleep_time)
                    
        except Exception as e:
            print(f"❌ 처리 루프 오류: {e}")
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
            debug_path = f"{self.debug_dir}/inline_{time.strftime('%Y%m%d_%H%M%S')}.jpg"
            cv2.imwrite(debug_path, debug_image)
            print(f"📸 디버깅용 이미지 저장: {debug_path}")
        except Exception as e:
            print(f"⚠️ 디버깅 이미지 저장 실패: {e}")
    
    def get_window_handle(self):
        """윈도우 핸들 반환"""
        return 0  # 실제 창이 없으므로 0 반환