import os
import sys
import cv2
import torch
import time
import threading
import queue
from threading import Event
import argparse
import numpy as np

from PyQt5.QtWidgets import QApplication, QMessageBox
from PyQt5.QtCore import QThread, pyqtSignal, QTimer, QElapsedTimer, QT_VERSION_STR

from gui_korean import MainWindow
from cv_overlay_window import TransparentOverlayWindow
from mss_capture import ScreenCapturer
from mosaic_processor import MosaicProcessor

class ParallelProcessingPipeline:
    def __init__(self, capturer, processor, renderer):
        self.capturer = capturer
        self.processor = processor
        self.renderer = renderer
        
        # 각 단계 간 큐
        self.capture_to_detect = queue.Queue(maxsize=1)
        self.detect_to_render = queue.Queue(maxsize=1)
        
        # 스레드
        self.capture_thread = None
        self.detect_thread = None
        self.render_thread = None
        
        # 제어 이벤트
        self.stop_event = threading.Event()
        
        # 성능 측정 변수
        self.frame_count = 0
        self.start_time = None
        self.last_fps_print = 0
        self.processing_times = []
        
    def start(self):
        self.stop_event.clear()
        self.frame_count = 0
        self.start_time = time.time()
        self.last_fps_print = self.start_time
        
        # 캡처 스레드
        self.capture_thread = threading.Thread(
            target=self._capture_loop, 
            daemon=True,
            name="Capture-Thread"
        )
        
        # 감지 스레드
        self.detect_thread = threading.Thread(
            target=self._detect_loop, 
            daemon=True,
            name="Detect-Thread"
        )
        
        # 렌더링 스레드
        self.render_thread = threading.Thread(
            target=self._render_loop, 
            daemon=True,
            name="Render-Thread"
        )
        
        # 스레드 시작
        print("🚀 병렬 처리 파이프라인 시작")
        self.capture_thread.start()
        self.detect_thread.start()
        self.render_thread.start()
        
    def _capture_loop(self):
        """캡처 루프 - 가장 높은 우선순위"""
        # 스레드 우선순위 높게 설정
        self._set_high_priority()
        print("✅ 캡처 스레드 시작됨")
        
        # 메인 루프
        while not self.stop_event.is_set():
            try:
                # 프레임 캡처
                frame = self.capturer.get_frame()
                
                if frame is None:
                    time.sleep(0.01)
                    continue
                
                # 큐가 가득 차면 이전 프레임 제거하고 새 프레임 넣기
                try:
                    if self.capture_to_detect.full():
                        self.capture_to_detect.get_nowait()
                    self.capture_to_detect.put(frame, block=False)
                except queue.Full:
                    pass  # 무시하고 계속
                
                # 프레임 레이트 제한
                time.sleep(0.01)  # 최대 약 100fps
                
            except Exception as e:
                print(f"❌ 캡처 루프 오류: {e}")
                import traceback
                traceback.print_exc()
                time.sleep(0.1)
                
    def _detect_loop(self):
        """감지 루프"""
        print("✅ 감지 스레드 시작됨")
        
        while not self.stop_event.is_set():
            try:
                # 캡처 큐에서 프레임 가져오기
                try:
                    frame = self.capture_to_detect.get(timeout=0.1)
                except queue.Empty:
                    continue
                
                # 객체 감지 수행
                detect_start = time.time()
                regions = self.processor.detect_objects(frame)
                detect_time = (time.time() - detect_start) * 1000
                
                # 처리 시간 업데이트
                self.processing_times.append(detect_time)
                if len(self.processing_times) > 30:
                    self.processing_times.pop(0)
                
                if not hasattr(self.processor, 'avg_processing_time'):
                    self.processor.avg_processing_time = detect_time
                else:
                    # 이동 평균
                    self.processor.avg_processing_time = (
                        0.9 * self.processor.avg_processing_time + 
                        0.1 * detect_time
                    )
                
                # 렌더링 큐로 결과 전달
                try:
                    if self.detect_to_render.full():
                        self.detect_to_render.get_nowait()
                    self.detect_to_render.put((frame, regions), block=False)
                except queue.Full:
                    pass
                    
                # 성능 측정 및 로깅
                self.frame_count += 1
                current_time = time.time()
                if current_time - self.last_fps_print >= 1.0:
                    fps = self.frame_count / (current_time - self.start_time)
                    avg_process_time = np.mean(self.processing_times) if self.processing_times else 0
                    print(f"⚡️ FPS: {fps:.1f}, 평균 처리 시간: {avg_process_time:.1f}ms, 프레임: {self.frame_count}")
                    self.last_fps_print = current_time
                    
            except Exception as e:
                print(f"❌ 감지 루프 오류: {e}")
                import traceback
                traceback.print_exc()
                time.sleep(0.1)
                
    def _render_loop(self):
        """렌더링 루프"""
        print("✅ 렌더링 스레드 시작됨")
        
        while not self.stop_event.is_set():
            try:
                # 감지 큐에서 결과 가져오기
                try:
                    frame, regions = self.detect_to_render.get(timeout=0.1)
                except queue.Empty:
                    continue
                
                # 렌더링
                self.renderer.update_regions(frame, regions)
                
                # 모자이크 정보 로깅
                if len(regions) > 0 and self.frame_count % 100 == 0:
                    print(f"📬 모자이크 영역 {len(regions)}개 처리 중")
                
            except Exception as e:
                print(f"❌ 렌더링 루프 오류: {e}")
                import traceback
                traceback.print_exc()
                time.sleep(0.1)
                
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
                import win32api
                import win32process
                import win32con
                win32process.SetThreadPriority(
                    win32api.GetCurrentThread(),
                    win32con.THREAD_PRIORITY_HIGHEST
                )
        except Exception as e:
            print(f"⚠️ 스레드 우선순위 설정 실패: {e}")
            
    def stop(self):
        """파이프라인 중지"""
        print("🛑 병렬 처리 파이프라인 중지 중...")
        self.stop_event.set()
        
        # 스레드 종료 대기
        if self.capture_thread and self.capture_thread.is_alive():
            self.capture_thread.join(timeout=1.0)
        if self.detect_thread and self.detect_thread.is_alive():
            self.detect_thread.join(timeout=1.0)
        if self.render_thread and self.render_thread.is_alive():
            self.render_thread.join(timeout=1.0)
            
        print("✅ 파이프라인 정상 종료됨")

def check_gpu_availability():
    """GPU 사용 가능 여부와 종류를 확인"""
    gpu_available = False
    gpu_info = "CPU 모드"
    
    # CUDA 가용성 확인
    try:
        if torch.cuda.is_available():
            gpu_available = True
            gpu_info = f"CUDA GPU: {torch.cuda.get_device_name(0)}"
            print(f"✅ {gpu_info} 감지됨")
            return gpu_available, "cuda", gpu_info
    except Exception as e:
        print(f"CUDA 확인 중 오류: {e}")
    
    # DirectX/GPU 가속 확인 (Windows 환경)
    try:
        import ctypes
        from ctypes import windll
        if hasattr(windll, 'dxgi') and windll.dxgi.CreateDXGIFactory != None:  # DirectX 사용 가능 여부
            # 간단한 DirectX 기능 테스트
            result = windll.d3d11.D3D11CreateDevice(None, 0, None, 0, None, 0, None, None, None)
            if result == 0:  # 성공
                gpu_available = True
                gpu_info = "DirectX GPU 가속 가능"
                print(f"✅ {gpu_info} 감지됨")
                return gpu_available, "directx", gpu_info
    except Exception as e:
        print(f"DirectX 확인 중 오류: {e}")
    
    print(f"ℹ️ {gpu_info}로 실행됩니다")
    return gpu_available, "cpu", gpu_info

if __name__ == "__main__":
    try:
        # 파싱은 한 번만 수행
        parser = argparse.ArgumentParser(description='실시간 화면 모자이크 처리 프로그램')
        parser.add_argument('--debug', action='store_true', help='디버깅 모드 활성화')
        parser.add_argument('--force-cpu', action='store_true', help='CPU 모드 강제 사용')
        parser.add_argument('--speed', action='store_true', help='속도 우선 모드 (품질 저하)')
        args = parser.parse_args()

        from PyQt5.QtCore import QT_VERSION_STR
        print(f"🚀 Qt 버전: {QT_VERSION_STR}")
        print(f"🚀 OpenCV 버전: {cv2.__version__}")
        
        # GPU 확인
        gpu_available, render_mode, gpu_info = check_gpu_availability()
        if args.force_cpu:
            render_mode = "cpu"
            gpu_info = "CPU 모드 (강제 설정)"
            print("ℹ️ CPU 모드로 강제 전환됨")
        
        # QApplication은 한 번만 생성 (GUI 용도로만 사용)
        app = QApplication(sys.argv)
        window = MainWindow()
        
        # 렌더 모드에 따라 적절한 오버레이 생성
        if render_mode == "directx":
            from directx_overlay_window import DirectXOverlayWindow
            overlay = DirectXOverlayWindow()
        elif render_mode == "cuda":
            from cuda_overlay_window import CudaOverlayWindow
            overlay = CudaOverlayWindow()
        else:  # CPU 모드
            # 고성능 직접 렌더링 방식 사용
            try:
                from direct_screen_overlay import DirectScreenOverlay
                overlay = DirectScreenOverlay()
                print("✅ DirectScreenOverlay 사용 중")
            except ImportError:
                # 직접 렌더링 구현이 없으면 기본 오버레이 사용
                overlay = TransparentOverlayWindow()
                print("⚠️ 기본 TransparentOverlayWindow로 대체됨")
        
        # UI에 현재 모드 표시
        if hasattr(window, 'set_render_mode_info'):
            window.set_render_mode_info(gpu_info)
        
        # 컴포넌트 초기화
        capturer = ScreenCapturer(debug_mode=args.debug)
        
        # 속도 우선 모드 설정
        if args.speed:
            print("⚡️ 속도 우선 모드 활성화 (품질 저하)")
            capturer.capture_downscale = 0.75  # 캡처 해상도 축소
        
        # 검열 모델 초기화
        processor = MosaicProcessor("resources/best_weights_only.pt")
        
        # 병렬 파이프라인 설정
        pipeline = ParallelProcessingPipeline(capturer, processor, overlay)
        
        def start():
            # 파라미터 업데이트
            processor.targets = window.get_selected_targets()
            processor.strength = window.get_strength()
            print(f"🔄 모자이크 파라미터 업데이트: 대상={processor.targets}, 강도={processor.strength}")
            
            # 오버레이 표시
            overlay.show()
            
            # 파이프라인 시작
            pipeline.start()

        def stop():
            # 오버레이 숨기기
            overlay.hide()
            
            # 파이프라인 중지
            pipeline.stop()

        # 시그널 연결
        window.start_censoring_signal.connect(start)
        window.stop_censoring_signal.connect(stop)

        # GUI 표시
        window.show()
        sys.exit(app.exec_())

    except Exception as e:
        print(f"❌ 에프 시작 오류: {e}")
        import traceback
        traceback.print_exc()