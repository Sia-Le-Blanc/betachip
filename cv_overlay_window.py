import cv2
import numpy as np
import threading
import time
import os
import ctypes
from PyQt5.QtWidgets import QApplication, QWidget
from PyQt5.QtCore import Qt, QTimer
from PyQt5.QtGui import QPainter, QColor, QImage, QCursor

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

            # 스크롤 추적용 변수 추가
        self.cursor_x = 0
        self.cursor_y = 0
        
        # 성능 개선: 렌더링 주기 조정
        self.refresh_timer.start(16)  # 약 60fps
        
        # 성능 개선: 더 강력한 방식으로 윈도우 투명도 설정
        if hasattr(self, 'setAttribute'):
            self.setAttribute(Qt.WA_TransparentForMouseEvents, True)
        
    def show(self):
        """오버레이 창 표시"""
        print("✅ 오버레이 창 표시")
        super().show()
    
    def hide(self):
        """오버레이 창 숨기기"""
        print("🛑 오버레이 창 숨기기")
        super().hide()
    
    def update_regions(self, original_image, mosaic_regions):
        """모자이크 영역 업데이트 (스크롤 처리 개선)"""
        try:
            if original_image is None:
                print("❌ 오버레이 업데이트: 원본 이미지가 없습니다.")
                return
            
            self.frame_count += 1
            
            # 현재 마우스 커서 위치 가져오기 - 스크롤 위치 추적용
            cursor_pos = QCursor.pos()
            self.cursor_x, self.cursor_y = cursor_pos.x(), cursor_pos.y()
            
            # 원본 이미지와 모자이크 영역 저장
            self.original_image = original_image
            
            # 최적화: 이전 프레임과 큰 차이가 없으면 처리 생략
            if len(mosaic_regions) == 0 and len(self.mosaic_regions) == 0:
                return
                
            # 성능 최적화: 목록 복사 최소화
            self.mosaic_regions = []
            for region in mosaic_regions:
                x, y, w, h, label, mosaic_img = region
                
                # 좌표 유효성 미리 검사 (paintEvent에서 반복 검사 방지)
                if x < 0 or y < 0 or x + w > self.screen_width or y + h > self.screen_height:
                    continue
                    
                if w <= 0 or h <= 0 or mosaic_img is None:
                    continue
                
                # 최적화: QImage 미리 생성 (paintEvent에서 변환 회피)
                try:
                    bytes_per_line = 3 * w
                    rgb_image = cv2.cvtColor(mosaic_img, cv2.COLOR_BGR2RGB)
                    qimg = QImage(rgb_image.data, w, h, bytes_per_line, QImage.Format_RGB888).copy()
                    
                    # 최적화된 정보 저장
                    self.mosaic_regions.append((x, y, w, h, label, qimg))
                except Exception as e:
                    print(f"❌ QImage 변환 오류: {e} @ ({x},{y},{w},{h})")
            
            # 로그 출력
            if len(mosaic_regions) > 0 and self.frame_count % 100 == 0:
                print(f"✅ 모자이크 영역 {len(mosaic_regions)}개 처리 중 (프레임 #{self.frame_count})")
                self._save_debug_image()
            
            # 화면 갱신 요청 - 최적화: 변경 있을 때만 업데이트
            self.update()
            
        except Exception as e:
            print(f"❌ 오버레이 업데이트 실패: {e}")
            import traceback
            traceback.print_exc()
    
    def paintEvent(self, event):
        """화면 그리기 이벤트 (성능 최적화 및 스크롤 보정)"""
        try:
            # 성능 최적화: 지역 변수로 저장하여 속성 접근 최소화
            regions = self.mosaic_regions
            if not regions:
                return
                
            # 현재 커서 위치 가져오기
            current_pos = QCursor.pos()
            dx = current_pos.x() - self.cursor_x  # 스크롤에 의한 X축 이동 보정
            dy = current_pos.y() - self.cursor_y  # 스크롤에 의한 Y축 이동 보정
            
            # 작은 움직임은 무시 (노이즈 방지)
            if abs(dx) < 3 and abs(dy) < 3:
                dx, dy = 0, 0
                
            # 성능 최적화: 페인터 설정
            painter = QPainter(self)
            painter.setRenderHint(QPainter.SmoothPixmapTransform, False)  # 렌더링 속도 우선
            
            # 전체 배경을 완전 투명하게 설정
            painter.fillRect(self.rect(), QColor(0, 0, 0, 0))
            
            # 성능 최적화: 한번에 많은 영역 그리기
            for x, y, w, h, label, qimg in regions:
                try:
                    # 스크롤 보정 적용
                    adjusted_x = x - dx
                    adjusted_y = y - dy
                    
                    # 좌표 유효성 검사
                    if adjusted_x < -w or adjusted_y < -h or adjusted_x > self.screen_width or adjusted_y > self.screen_height:
                        continue
                    
                    # 해당 위치에 이미지 그리기 - 미리 준비된 QImage 사용
                    painter.drawImage(adjusted_x, adjusted_y, qimg)
                    
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
    
        # cv_overlay_window.py에 다음 메서드 추가
    def limit_frame_rate(self):
        """프레임 레이트 제한"""
        now = time.time()
        if not hasattr(self, 'last_render_time'):
            self.last_render_time = now
            return False
            
        elapsed = now - self.last_render_time
        if elapsed < 0.016:  # 60fps (약 16ms)
            return True  # 프레임 드롭
            
        self.last_render_time = now
        return False