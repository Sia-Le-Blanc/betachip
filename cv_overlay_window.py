import cv2
import numpy as np
import threading
import time
import os
import ctypes
from PyQt5.QtWidgets import QApplication, QWidget
from PyQt5.QtCore import Qt, QTimer
from PyQt5.QtGui import QPainter, QColor, QImage

class TransparentOverlayWindow(QWidget):
    """모자이크 영역만 오버레이하는 윈도우"""
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Mosaic Overlay")
        
        # 윈도우 스타일 설정
        self.setWindowFlags(
            Qt.FramelessWindowHint |
            Qt.WindowStaysOnTopHint |
            Qt.Tool
        )
        
        # 배경 투명 및 마우스 이벤트 통과 설정
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setAttribute(Qt.WA_NoSystemBackground, True)
        self.setAttribute(Qt.WA_TransparentForMouseEvents, True)

        # 화면 크기 설정
        self.screen_width = QApplication.primaryScreen().size().width()
        self.screen_height = QApplication.primaryScreen().size().height()
        self.setGeometry(0, 0, self.screen_width, self.screen_height)
        
        # 디버깅 변수
        self.frame_count = 0
        self.debug_dir = "debug_overlay"
        os.makedirs(self.debug_dir, exist_ok=True)
        
        # 모자이크 정보
        self.mosaic_regions = []
        self.original_image = None
        
        # 갱신 타이머
        self.refresh_timer = QTimer(self)
        self.refresh_timer.timeout.connect(self.update)
        self.refresh_timer.start(16)  # 약 60fps
    
    def show(self):
        """오버레이 창 표시"""
        print("✅ 오버레이 창 표시")
        super().show()
    
    def hide(self):
        """오버레이 창 숨기기"""
        print("🛑 오버레이 창 숨기기")
        super().hide()
    
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
            
            # 화면 갱신 요청
            self.update()
            
        except Exception as e:
            print(f"❌ 오버레이 업데이트 실패: {e}")
            import traceback
            traceback.print_exc()
    
    def paintEvent(self, event):
        """화면 그리기 이벤트"""
        try:
            painter = QPainter(self)
            painter.setRenderHint(QPainter.SmoothPixmapTransform)
            
            # 전체 배경을 완전 투명하게 설정
            painter.fillRect(self.rect(), QColor(0, 0, 0, 0))
            
            # 모자이크 영역 그리기
            if self.mosaic_regions:
                for x, y, w, h, label, mosaic_img in self.mosaic_regions:
                    try:
                        # 좌표 유효성 검사
                        if x < 0 or y < 0 or x + w > self.screen_width or y + h > self.screen_height:
                            continue
                            
                        if w <= 0 or h <= 0 or mosaic_img is None:
                            continue
                        
                        # QImage로 변환
                        bytes_per_line = 3 * w
                        rgb_image = cv2.cvtColor(mosaic_img, cv2.COLOR_BGR2RGB)
                        qimg = QImage(rgb_image.data, w, h, bytes_per_line, QImage.Format_RGB888).copy()
                        
                        # 해당 위치에 이미지 그리기
                        painter.drawImage(x, y, qimg)
                        
                    except Exception as e:
                        print(f"❌ 이미지 그리기 오류: {e} @ ({x},{y},{w},{h})")
            
        except Exception as e:
            print(f"❌ paintEvent 오류: {e}")
            import traceback
            traceback.print_exc()
    
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
            debug_path = f"{self.debug_dir}/overlay_{time.strftime('%Y%m%d_%H%M%S')}.jpg"
            cv2.imwrite(debug_path, debug_image)
            print(f"📸 디버깅용 오버레이 이미지 저장: {debug_path}")
        except Exception as e:
            print(f"⚠️ 디버깅 이미지 저장 실패: {e}")
    
    def get_window_handle(self):
        """윈도우 핸들 반환"""
        return int(self.winId())