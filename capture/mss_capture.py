"""
MSS 라이브러리를 사용한 고성능 화면 캡처 모듈
"""

import numpy as np
import cv2
import os
import time
import threading
import queue
import mss
from config import CONFIG

class ScreenCapturer:
    """화면 캡처 클래스"""
    
    def __init__(self, config=None):
        # 설정 가져오기
        if config is None:
            config = CONFIG.get('capture', {})
        self.config = config
        
        # 화면 캡처 관련 설정
        self.capture_downscale = config.get('downscale', 1.0)
        self.debug_mode = config.get('debug_mode', False)
        self.debug_save_interval = config.get('debug_save_interval', 300)
        
        # 화면 정보 초기화
        with mss.mss() as sct:
            monitor = sct.monitors[0]  # 전체 화면 (모든 모니터 포함)
            self.screen_width = monitor["width"]
            self.screen_height = monitor["height"]
            self.screen_left = monitor["left"]
            self.screen_top = monitor["top"]
        
        # 캡처 크기 계산
        self.capture_width = int(self.screen_width * self.capture_downscale)
        self.capture_height = int(self.screen_height * self.capture_downscale)
        
        print(f"✅ 화면 해상도: {self.screen_width}x{self.screen_height}, 캡처 크기: {self.capture_width}x{self.capture_height}")
        
        # 캡처 영역 설정
        self.monitor = {
            "top": self.screen_top,
            "left": self.screen_left,
            "width": self.screen_width,
            "height": self.screen_height,
            "mon": 0,  # 0 = 전체 화면
        }
        
        # 이전 프레임 정보 저장
        self.prev_frame = None
        
        # 프레임 카운터
        self.frame_count = 0
        
        # 프레임 큐 및 스레드 관련 설정
        self.frame_queue = queue.Queue(maxsize=self.config.get('queue_size', 2))
        self.stop_event = threading.Event()
        self.capture_thread = None
        
        # 제외 영역 설정 (오버레이 창 등)
        self.exclude_hwnd = None
        self.exclude_regions = []
        
        # 디버깅 디렉토리 생성
        if self.debug_mode:
            self.debug_dir = "debug_captures"
            os.makedirs(self.debug_dir, exist_ok=True)
        
        # 캡처 스레드 시작
        self.start_capture_thread()
    
    def set_exclude_hwnd(self, hwnd):
        """캡처에서 제외할 윈도우 핸들 설정"""
        self.exclude_hwnd = hwnd
        print(f"✅ 제외 윈도우 핸들 설정: {hwnd}")
        
    def add_exclude_region(self, x, y, width, height):
        """캡처에서 제외할 영역 추가"""
        self.exclude_regions.append((x, y, width, height))
        print(f"✅ 제외 영역 추가: ({x}, {y}, {width}, {height})")
    
    def clear_exclude_regions(self):
        """제외 영역 모두 제거"""
        self.exclude_regions = []
    
    def start_capture_thread(self):
        """캡처 스레드 시작"""
        if self.capture_thread is not None and self.capture_thread.is_alive():
            print("⚠️ 캡처 스레드가 이미 실행 중입니다.")
            return
            
        self.stop_event.clear()
        self.capture_thread = threading.Thread(target=self._capture_thread_func, daemon=True)
        self.capture_thread.start()
        print("✅ 캡처 스레드 시작됨")
    
    def stop_capture_thread(self):
        """캡처 스레드 중지"""
        if self.capture_thread and self.capture_thread.is_alive():
            self.stop_event.set()
            self.capture_thread.join(timeout=1.0)
            print("✅ 캡처 스레드 중지됨")
    
    def _capture_thread_func(self):
        """캡처 스레드 함수"""
        print("🔄 캡처 스레드 시작")
        frame_time = time.time()
        retry_count = 0
        
        # 스레드 우선순위 높이기
        self._set_high_priority()
        
        # MSS 컨텍스트 생성
        with mss.mss() as sct:
            while not self.stop_event.is_set():
                try:
                    # 캡처 간격 제어 (최대 FPS 제한)
                    elapsed = time.time() - frame_time
                    if elapsed < 0.01:  # 최대 약 100 FPS
                        time.sleep(0.001)
                        continue
                    
                    # 화면 캡처 시도
                    frame = self._capture_screen(sct)
                    frame_time = time.time()
                    
                    if frame is not None:
                        self.frame_count += 1
                        # 프레임 큐가 가득 차면 이전 프레임 제거
                        try:
                            if self.frame_queue.full():
                                self.frame_queue.get_nowait()
                            self.frame_queue.put(frame, block=False)
                            retry_count = 0  # 성공 시 재시도 카운트 초기화
                        except queue.Full:
                            pass  # 큐가 가득 차면 무시
                    else:
                        retry_count += 1
                        if retry_count > 5:
                            print(f"⚠️ 연속 {retry_count}회 캡처 실패")
                            retry_count = 0
                            time.sleep(0.1)
                        
                except Exception as e:
                    print(f"❌ 캡처 스레드 오류: {e}")
                    retry_count += 1
                    if retry_count > 5:
                        retry_count = 0
                    time.sleep(0.1)
        
        print("🛑 캡처 스레드 종료")
    
    def _set_high_priority(self):
        """스레드 우선순위 높이기"""
        try:
            if hasattr(os, 'sched_setaffinity'):
                # Linux
                try:
                    os.sched_setaffinity(0, {0, 1})  # CPU 코어 0, 1에 할당
                except:
                    pass
            else:
                # Windows
                try:
                    import win32api
                    import win32process
                    import win32con
                    win32process.SetThreadPriority(
                        win32api.GetCurrentThread(),
                        win32con.THREAD_PRIORITY_HIGHEST
                    )
                except ImportError:
                    print("⚠️ win32api 모듈을 찾을 수 없습니다. 스레드 우선순위 설정을 건너뜁니다.")
        except Exception as e:
            print(f"⚠️ 스레드 우선순위 설정 실패: {e}")
    
    def _capture_screen(self, sct):
        """실제 화면 캡처 로직 - 최적화 버전"""
        try:
            # MSS로 화면 캡처
            sct_img = sct.grab(self.monitor)
            
            # 빠른 변환을 위한 Numpy 배열 직접 접근
            img = np.array(sct_img, dtype=np.uint8)
            
            # Numpy 연산으로 BGR 변환 (최적화)
            if img.shape[2] == 4:  # BGRA
                img = img[:, :, :3]  # BGR만 추출 (알파 채널 제거)
            
            # 성능 최적화: 필요한 경우만 다운스케일
            if self.capture_downscale != 1.0:
                target_width = self.capture_width
                target_height = self.capture_height
                
                # 더 빠른 다운샘플링 (품질보다 속도 우선)
                img = cv2.resize(
                    img, 
                    (target_width, target_height), 
                    interpolation=cv2.INTER_NEAREST
                )
            
            # 제외 영역 마스킹 (필요한 경우)
            if self.exclude_regions:
                for x, y, w, h in self.exclude_regions:
                    # 좌표 유효성 검사
                    if (x >= 0 and y >= 0 and 
                        x < img.shape[1] and y < img.shape[0]):
                        # 실제 그릴 영역 계산 (범위 초과 방지)
                        end_x = min(x + w, img.shape[1])
                        end_y = min(y + h, img.shape[0])
                        
                        # 검은색 사각형으로 채움 (성능 최적화: 직접 배열 접근)
                        img[y:end_y, x:end_x] = 0
            
            # 디버깅 모드: 주기적으로 화면 캡처 저장
            if self.debug_mode and self.frame_count % self.debug_save_interval == 0:
                try:
                    debug_path = f"{self.debug_dir}/screen_{time.strftime('%Y%m%d_%H%M%S')}.jpg"
                    
                    # 고품질 저장 필요 없는 경우 압축률 높이기
                    cv2.imwrite(debug_path, img, [cv2.IMWRITE_JPEG_QUALITY, 80])
                    print(f"📸 디버깅용 화면 캡처 저장: {debug_path} (크기: {img.shape})")
                except Exception as e:
                    print(f"⚠️ 디버깅 캡처 저장 실패: {e}")
            
            return img
            
        except Exception as e:
            print(f"❌ 화면 캡처 오류: {e}")
            import traceback
            traceback.print_exc()
            return None
    
    def get_frame(self):
        """외부에서 호출하는 프레임 가져오기 함수"""
        try:
            # 큐에서 프레임 가져오기
            frame = self.frame_queue.get(block=True, timeout=0.1)
            
            # 프레임 저장
            self.prev_frame = frame
            
            # 주기적인 로그 출력
            log_interval = self.config.get('log_interval', 100)
            if self.frame_count % log_interval == 0:
                print(f"📸 화면 캡처: 프레임 #{self.frame_count}, 크기: {frame.shape}")
            
            return frame
            
        except queue.Empty:
            # 큐가 비었으면 이전 프레임 반환
            if self.prev_frame is not None:
                return self.prev_frame
            
            # 이전 프레임도 없으면 직접 캡처 시도
            with mss.mss() as sct:
                return self._capture_screen(sct)
                
        except Exception as e:
            print(f"❌ 프레임 가져오기 오류: {e}")
            if self.prev_frame is not None:
                return self.prev_frame
            return None
    
    def __del__(self):
        """소멸자: 자원 정리"""
        self.stop_capture_thread()