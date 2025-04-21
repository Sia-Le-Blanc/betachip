import os
import sys
import cv2
import torch
import time
from threading import Event
import argparse
import numpy as np

from PyQt5.QtWidgets import QApplication, QMessageBox
from PyQt5.QtCore import QThread, pyqtSignal, QTimer, QElapsedTimer

from gui_korean import MainWindow
from cv_overlay_window import TransparentOverlayWindow
from mss_capture import ScreenCapturer
from mosaic_processor import MosaicProcessor

class ProcessingThread(QThread):
    regions_ready = pyqtSignal(object, object)

    def __init__(self, capturer, processor):
        super().__init__()
        self.capturer = capturer
        self.processor = processor
        self.stop_event = Event()
        self.targets = []
        self.strength = 25
        self.fps_limit = 60
        self.frame_count = 0
        self.last_fps_print = 0
        self.is_paused = False
        self.pause_event = Event()
        self.timer = QElapsedTimer()
        self.processing_times = []
        self.skip_frames = 0
        self.frame_budget = 1000.0 / self.fps_limit
        self.current_frame = None
        self.previous_regions = []

    def update_params(self, targets, strength):
        if self.targets != targets or self.strength != strength:
            print(f"🔄 모자이크 파라미터 업데이트: 대상={targets}, 강도={strength}")
            self.targets = targets.copy()
            self.processor.targets = targets.copy()
            self.strength = strength
            self.processor.strength = strength

    def pause(self):
        self.is_paused = True
        self.pause_event.clear()

    def resume(self):
        self.is_paused = False
        self.pause_event.set()

    def run(self):
        self.timer.start()
        self.frame_count = 0
        start_time = time.time()
        self.last_fps_print = start_time
        self.pause_event.set()

        print("🚀 처리 스레드 시작됨")
        print(f"📋 초기 설정: 대상={self.targets}, 강도={self.strength}, FPS 한계={self.fps_limit}")

        while not self.stop_event.is_set():
            if self.is_paused:
                self.pause_event.wait()
                continue

            elapsed_ms = self.timer.elapsed()
            self.timer.restart()

            if elapsed_ms > self.frame_budget * 1.5:
                self.skip_frames += 1
                if self.skip_frames % 10 == 0:
                    print(f"⚠️ 성능 경고: 프레임 스핑됨 ({elapsed_ms:.1f}ms > {self.frame_budget:.1f}ms)")
                time.sleep(0.001)
                continue

            self.skip_frames = 0

            try:
                frame = self.capturer.get_frame()
                if frame is None:
                    print("⚠️ 화면 캡쳐 실패")
                    time.sleep(0.01)
                    continue

                self.current_frame = frame

                if self.frame_count % 100 == 0:
                    print(f"📸 화면 캡쳐: 프레임 #{self.frame_count}, 크기: {frame.shape}")

                if frame.size > 0:
                    process_start = time.time()
                    mosaic_regions = self.processor.detect_objects(frame)
                    process_time = (time.time() - process_start) * 1000
                    self.processing_times.append(process_time)
                    if len(self.processing_times) > 30:
                        self.processing_times.pop(0)

                    if len(mosaic_regions) == 0 and len(self.previous_regions) > 0:
                        motion_thresh = 0.15
                        if hasattr(self.processor, 'last_motion_level'):
                            motion_level = self.processor.last_motion_level
                            if motion_level < motion_thresh:
                                use_previous = True
                            else:
                                use_previous = all(w * h > 1600 for _, _, w, h, *_ in self.previous_regions)
                        else:
                            use_previous = True

                        if use_previous:
                            updated_regions = []
                            for x, y, w, h, label, mosaic_img in self.previous_regions:
                                try:
                                    if (x >= 0 and y >= 0 and 
                                        x + w <= frame.shape[1] and 
                                        y + h <= frame.shape[0] and
                                        w > 0 and h > 0):
                                        region = frame[y:y+h, x:x+w].copy()
                                        roi_key = f"{label}_{int(x/10)}_{int(y/10)}_{int(w/10)}_{int(h/10)}"
                                        mosaic_img = self.processor.get_cached_mosaic(region, roi_key)
                                        updated_regions.append((x, y, w, h, label, mosaic_img))
                                except Exception as e:
                                    print(f"❌ 이전 영역 업데이트 오류: {e} @ ({x},{y},{w},{h})")
                            if updated_regions:
                                mosaic_regions = updated_regions

                    if len(mosaic_regions) > 0:
                        self.previous_regions = mosaic_regions.copy()
                        self.regions_ready.emit(frame, mosaic_regions)
                        if self.frame_count % 100 == 0:
                            print(f"📬 모자이크 영역 {len(mosaic_regions)}개 전송됨")
                    elif len(self.previous_regions) == 0:
                        self.regions_ready.emit(frame, [])

                self.frame_count += 1
                current_time = time.time()
                if current_time - self.last_fps_print >= 1.0:
                    fps = self.frame_count / (current_time - start_time)
                    avg_process_time = np.mean(self.processing_times) if self.processing_times else 0
                    print(f"⚡️ FPS: {fps:.1f}, 평균 처리 시간: {avg_process_time:.1f}ms, 프레임: {self.frame_count}")
                    self.last_fps_print = current_time

                remaining_budget = self.frame_budget - self.timer.elapsed()
                if remaining_budget > 1:
                    time.sleep(remaining_budget / 1000.0)

            except Exception as e:
                print(f"❌ 프레임 처리 오류: {e}")
                import traceback
                traceback.print_exc()
                time.sleep(0.1)

    def stop(self):
        self.stop_event.set()
        self.pause_event.set()
        self.wait(1000)

if __name__ == "__main__":
    try:
        parser = argparse.ArgumentParser(description='실시간 화면 모자이크 처리 프로그램')
        parser.add_argument('--debug', action='store_true', help='디버깅 모드 활성화')
        args = parser.parse_args()

        from PyQt5.QtCore import QT_VERSION_STR
        print(f"🚀 Qt 버전: {QT_VERSION_STR}")
        print(f"🚀 OpenCV 버전: {cv2.__version__}")

        app = QApplication(sys.argv)
        window = MainWindow()
        overlay = TransparentOverlayWindow()
        capturer = ScreenCapturer(debug_mode=args.debug)
        processor = MosaicProcessor("resources/best_weights_only.pt")

        thread = ProcessingThread(capturer, processor)
        thread.regions_ready.connect(overlay.update_regions)

        def start():
            thread.update_params(window.get_selected_targets(), window.get_strength())
            overlay.show()
            if not thread.isRunning():
                thread.start()
            else:
                thread.resume()

        def stop():
            overlay.hide()
            thread.pause()

        window.start_censoring_signal.connect(start)
        window.stop_censoring_signal.connect(stop)

        window.show()
        sys.exit(app.exec_())

    except Exception as e:
        print(f"❌ 에프 시작 오류: {e}")
        import traceback
        traceback.print_exc()
