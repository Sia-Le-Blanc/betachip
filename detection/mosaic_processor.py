"""
최적화된 모자이크 프로세서 - 피드백 루프 해결 버전
원본 프레임에서만 감지하고, 개별 영역 모자이크 정보 제공
"""

import cv2
import numpy as np
from ultralytics import YOLO
import time

class MosaicProcessor:
    """최적화된 모자이크 프로세서"""
    
    def __init__(self, model_path=None, config=None):
        """초기화"""
        self.config = config or {}
        
        # 모델 경로 설정
        if model_path is None:
            model_path = self.config.get("model_path", "resources/best.onnx")
        
        # YOLO 모델 로드
        try:
            print(f"🤖 YOLO 모델 로딩 중: {model_path}")
            self.model = YOLO(model_path)
            print("✅ YOLO 모델 로드 성공")
        except Exception as e:
            print(f"❌ YOLO 모델 로드 실패: {e}")
            self.model = None
        
        # 설정값들
        self.conf_threshold = self.config.get("conf_threshold", 0.1)
        self.targets = self.config.get("default_targets", ["여성"])
        self.strength = self.config.get("default_strength", 15)
        
        # 성능 통계
        self.detection_times = []
        self.last_detections = []
        
        print(f"🎯 기본 타겟: {self.targets}")
        print(f"⚙️ 기본 설정: 강도={self.strength}, 신뢰도={self.conf_threshold}")
    
    def set_targets(self, targets):
        """모자이크 대상 설정"""
        self.targets = targets
        print(f"🎯 타겟 변경: {targets}")
    
    def set_strength(self, strength):
        """모자이크 강도 설정"""
        self.strength = max(1, min(50, strength))
        print(f"💪 강도 변경: {self.strength}")
    
    def detect_objects(self, frame):
        """객체 감지만 수행 (모자이크 적용 없이)"""
        if self.model is None:
            return []
        
        try:
            start_time = time.time()
            
            # YOLO 추론
            results = self.model(frame, conf=self.conf_threshold, verbose=False)
            
            detections = []
            
            for result in results:
                if result.boxes is not None and len(result.boxes) > 0:
                    boxes = result.boxes
                    
                    for i in range(len(boxes)):
                        # 바운딩 박스 좌표
                        xyxy = boxes.xyxy[i].cpu().numpy()
                        x1, y1, x2, y2 = map(int, xyxy)
                        
                        # 신뢰도
                        confidence = float(boxes.conf[i].cpu().numpy())
                        
                        # 클래스 이름
                        class_id = int(boxes.cls[i].cpu().numpy())
                        class_name = self.model.names[class_id]
                        
                        # 유효한 바운딩 박스인지 확인
                        if x2 > x1 and y2 > y1 and confidence >= self.conf_threshold:
                            detection = {
                                'class_name': class_name,
                                'confidence': confidence,
                                'bbox': [x1, y1, x2, y2],
                                'class_id': class_id
                            }
                            detections.append(detection)
            
            # 성능 통계 업데이트
            detection_time = time.time() - start_time
            self.detection_times.append(detection_time)
            if len(self.detection_times) > 100:
                self.detection_times = self.detection_times[-50:]
            
            self.last_detections = detections
            
            # 리스트로 반환 (배열 오류 방지)
            return detections
            
        except Exception as e:
            print(f"❌ 객체 감지 오류: {e}")
            return []
    
    def detect_objects_detailed(self, frame):
        """객체 감지 + 모자이크 적용된 전체 프레임 반환 (호환성용)"""
        detections = self.detect_objects(frame)
        
        # 전체 프레임에 모자이크 적용
        processed_frame = frame.copy()
        
        for detection in detections:
            class_name = detection['class_name']
            
            if class_name in self.targets:
                x1, y1, x2, y2 = detection['bbox']
                
                # 해당 영역에 모자이크 적용
                region = processed_frame[y1:y2, x1:x2]
                if region.size > 0:
                    mosaic_region = self.apply_mosaic(region, self.strength)
                    processed_frame[y1:y2, x1:x2] = mosaic_region
        
        return processed_frame, detections
    
    def apply_mosaic(self, image, strength=None):
        """이미지에 모자이크 효과 적용"""
        if strength is None:
            strength = self.strength
        
        if image.size == 0:
            return image
        
        try:
            h, w = image.shape[:2]
            
            # 최소 크기 보장
            small_h = max(1, h // strength)
            small_w = max(1, w // strength)
            
            # 축소 후 확대
            small = cv2.resize(image, (small_w, small_h), interpolation=cv2.INTER_LINEAR)
            mosaic = cv2.resize(small, (w, h), interpolation=cv2.INTER_NEAREST)
            
            return mosaic
            
        except Exception as e:
            print(f"⚠️ 모자이크 적용 오류: {e}")
            return image
    
    def create_mosaic_for_region(self, frame, x1, y1, x2, y2, strength=None):
        """특정 영역에 대한 모자이크 이미지 생성"""
        try:
            # 영역 추출
            region = frame[y1:y2, x1:x2]
            
            if region.size == 0:
                return None
            
            # 모자이크 적용
            mosaic_region = self.apply_mosaic(region, strength)
            
            return mosaic_region
            
        except Exception as e:
            print(f"⚠️ 영역 모자이크 생성 오류: {e}")
            return None
    
    def get_performance_stats(self):
        """성능 통계 반환"""
        if not self.detection_times:
            return {
                'avg_detection_time': 0,
                'fps': 0,
                'last_detections_count': 0
            }
        
        avg_time = sum(self.detection_times) / len(self.detection_times)
        fps = 1.0 / avg_time if avg_time > 0 else 0
        
        return {
            'avg_detection_time': avg_time,
            'fps': fps,
            'last_detections_count': len(self.last_detections)
        }
    
    def update_config(self, **kwargs):
        """설정 업데이트"""
        for key, value in kwargs.items():
            if key == 'conf_threshold':
                self.conf_threshold = max(0.01, min(0.99, value))
            elif key == 'targets':
                self.targets = value
            elif key == 'strength':
                self.strength = max(1, min(50, value))
        
        print(f"⚙️ 설정 업데이트: {kwargs}")
    
    def is_model_loaded(self):
        """모델이 로드되었는지 확인"""
        return self.model is not None
    
    def get_available_classes(self):
        """사용 가능한 클래스 목록 반환"""
        if self.model is None:
            return []
        
        return list(self.model.names.values())
    
    def reset_stats(self):
        """통계 초기화"""
        self.detection_times = []
        self.last_detections = []
        print("📊 성능 통계 초기화됨")