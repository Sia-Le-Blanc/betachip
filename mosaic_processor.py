import cv2
import numpy as np
import os
import time
import threading
from collections import deque
import torch

class MosaicProcessor:
    """실제 객체 감지 및 모자이크 처리를 수행하는 프로세서"""
    def __init__(self, model_path=None, strength=25, method="pixel", targets=None, device="auto"):
        """
        모자이크 프로세서 초기화
        
        Args:
            model_path: YOLO 모델 경로
            strength: 모자이크 강도 (높을수록 더 큰 모자이크)
            method: 모자이크 방법 ('pixel' 또는 'blur')
            targets: 감지 대상 클래스 목록
            device: 모델 실행 디바이스 ('cpu', 'cuda', 'auto')
        """
        self.strength = strength
        self.method = method
        self.targets = targets or ["얼굴", "가슴", "보지", "팬티"]
        
        # 모델 로드
        self.model = None
        self.confidence_threshold = 0.5
        if model_path and os.path.exists(model_path):
            self._load_model(model_path, device)
        else:
            print("⚠️ 모델 파일이 없어 기본 객체 감지를 사용합니다")
            
        # 디바이스 설정
        if device == "auto":
            self.device = "cuda" if torch.cuda.is_available() else "cpu"
        else:
            self.device = device
            
        # 매핑 테이블: YOLO 클래스 ID -> 한글 레이블
        self.class_mapping = {
            0: "얼굴",
            1: "눈",
            2: "손",
            3: "가슴",
            4: "보지",
            5: "팬티",
            6: "겨드랑이",
            7: "자지",
            8: "몸 전체",
            9: "교미",
            10: "신발",
            11: "가슴_옷",
            12: "보지_옷",
            13: "여성"
        }
        
        # 캐시 설정
        self.mosaic_cache = {}
        self.cache_lifetime = 45
        self.cache_cleanup_interval = 150
        
        # 성능 측정
        self.frame_count = 0
        self.avg_processing_time = 5.0  # 초기값 (ms)
        
        # 이전 감지 결과 (간단한 트래킹용)
        self.prev_regions = []
        self.region_history = deque(maxlen=5)
        
        # 디버그 디렉토리
        self.debug_dir = "debug_captures"
        os.makedirs(self.debug_dir, exist_ok=True)
        
        print(f"🎯 모자이크 대상: {self.targets}")
        print(f"✅ 모자이크 프로세서 초기화 완료 (디바이스: {self.device})")
    
    def _load_model(self, model_path, device):
        """YOLO 모델 로드"""
        try:
            # YOLO 모델 로드 시도
            try:
                from ultralytics import YOLO
                print(f"🔍 모델 로드 중: {model_path}")
                self.model = YOLO(model_path)
                print(f"✅ YOLO 모델 로드 완료")
            except ImportError:
                print("⚠️ ultralytics 모듈을 찾을 수 없습니다")
                self.model = None
                
            # 모델 로드 실패 시 OpenCV DNN으로 시도
            if self.model is None:
                try:
                    print("🔍 OpenCV DNN으로 모델 로드 시도...")
                    self.model = cv2.dnn.readNetFromONNX(model_path)
                    print("✅ OpenCV DNN으로 모델 로드 완료")
                except Exception as e:
                    print(f"❌ OpenCV DNN 모델 로드 실패: {e}")
                    self.model = None
                    
        except Exception as e:
            print(f"❌ 모델 로드 실패: {e}")
            self.model = None
    
    def cleanup_cache(self):
        """오래된 모자이크 캐시 제거"""
        current_frame = self.frame_count
        expired_keys = [k for k, (frame, _) in self.mosaic_cache.items() 
                       if current_frame - frame > self.cache_lifetime]
        for key in expired_keys:
            del self.mosaic_cache[key]
        
        if expired_keys:
            print(f"🧹 {len(expired_keys)}개의 모자이크 캐시 제거됨")
    
    def get_cached_mosaic(self, region, roi_key):
        """캐시된 모자이크 영역 반환 또는 새로 계산"""
        h, w = region.shape[:2]
        
        # 크기가 너무 작으면 원본 반환
        if h < 5 or w < 5:
            return region
            
        # 캐시에서 확인
        if roi_key in self.mosaic_cache:
            cached_frame, mosaic_img = self.mosaic_cache[roi_key]
            # 크기 일치하면 캐시 사용
            if mosaic_img.shape[:2] == (h, w):
                return mosaic_img
        
        # 새로 모자이크 계산
        if self.method == "pixel":
            mosaic_img = self.pixelate(region)
        elif self.method == "blur":
            mosaic_img = self.blur(region)
        else:
            # 기본 값은 픽셀화
            mosaic_img = self.pixelate(region)
            
        # 빨간색 오버레이 추가 (모자이크가 잘 보이도록)
        red_overlay = np.zeros_like(mosaic_img)
        red_overlay[:, :] = [0, 0, 200]  # BGR 빨간색 (약간 투명하게)
        mosaic_with_overlay = cv2.addWeighted(mosaic_img, 0.7, red_overlay, 0.3, 0)
        
        # 캐시에 저장
        self.mosaic_cache[roi_key] = (self.frame_count, mosaic_with_overlay)
        
        return mosaic_with_overlay
    
    def detect_objects(self, image):
        """이미지에서 객체 감지"""
        if image is None or image.size == 0:
            return []

        detected_regions = []
        self.frame_count += 1
        start_time = time.time()
        
        # 캐시 주기적 정리
        if self.frame_count % self.cache_cleanup_interval == 0:
            self.cleanup_cache()
        
        try:
            # 1. 모델이 있으면 실제 객체 감지 수행
            if self.model is not None:
                regions = self._detect_with_model(image)
                
                for (x, y, w, h, label, confidence) in regions:
                    # 좌표 유효성 검사 및 경계 확인
                    x = max(0, x)
                    y = max(0, y)
                    w = min(image.shape[1] - x, w)
                    h = min(image.shape[0] - y, h)
                    
                    if w > 0 and h > 0:
                        # 영역 추출
                        region = image[y:y+h, x:x+w].copy()
                        
                        # 고유 ROI 키 생성
                        roi_key = f"{label}_{int(x/10)}_{int(y/10)}_{int(w/10)}_{int(h/10)}"
                        
                        # 모자이크 처리
                        mosaic_region = self.get_cached_mosaic(region, roi_key)
                        
                        # 테두리 추가
                        cv2.rectangle(mosaic_region, (0, 0), (w-1, h-1), (0, 0, 255), 2)
                        
                        # 텍스트 추가
                        cv2.putText(mosaic_region, f"{label} {confidence:.2f}", (10, 30), 
                                  cv2.FONT_HERSHEY_SIMPLEX, 1, (255, 255, 255), 2)
                        
                        # 결과 추가
                        detected_regions.append((x, y, w, h, label, mosaic_region))
            
            # 2. 모델이 없으면 간단한 얼굴 인식 사용
            else:
                regions = self._detect_with_opencv(image)
                
                for (x, y, w, h) in regions:
                    # 좌표 유효성 검사 및 경계 확인
                    x = max(0, x)
                    y = max(0, y)
                    w = min(image.shape[1] - x, w)
                    h = min(image.shape[0] - y, h)
                    
                    if w > 0 and h > 0:
                        # 영역 추출
                        region = image[y:y+h, x:x+w].copy()
                        
                        # 고유 ROI 키 생성
                        roi_key = f"face_{int(x/10)}_{int(y/10)}_{int(w/10)}_{int(h/10)}"
                        
                        # 모자이크 처리
                        mosaic_region = self.get_cached_mosaic(region, roi_key)
                        
                        # 테두리 추가
                        cv2.rectangle(mosaic_region, (0, 0), (w-1, h-1), (0, 0, 255), 2)
                        
                        # 텍스트 추가
                        cv2.putText(mosaic_region, "얼굴", (10, 30), 
                                  cv2.FONT_HERSHEY_SIMPLEX, 1, (255, 255, 255), 2)
                        
                        # 결과 추가
                        detected_regions.append((x, y, w, h, "얼굴", mosaic_region))
            
            # 3. 객체가 감지되지 않을 경우 이전 결과 활용 (간단한 추적)
            if not detected_regions and self.prev_regions:
                for prev_x, prev_y, prev_w, prev_h, prev_label, _ in self.prev_regions:
                    # 이전 위치에서 영역 추출
                    if (prev_y+prev_h <= image.shape[0] and 
                        prev_x+prev_w <= image.shape[1]):
                        
                        region = image[prev_y:prev_y+prev_h, prev_x:prev_x+prev_w].copy()
                        roi_key = f"{prev_label}_{int(prev_x/10)}_{int(prev_y/10)}_{int(prev_w/10)}_{int(prev_h/10)}"
                        
                        # 모자이크 처리
                        mosaic_region = self.get_cached_mosaic(region, roi_key)
                        
                        # 테두리 추가 (추적 중임을 표시하는 다른 색상)
                        cv2.rectangle(mosaic_region, (0, 0), (prev_w-1, prev_h-1), (255, 0, 0), 2)
                        
                        # 텍스트 추가
                        cv2.putText(mosaic_region, f"{prev_label} (추적)", (10, 30), 
                                  cv2.FONT_HERSHEY_SIMPLEX, 1, (255, 255, 255), 2)
                        
                        # 결과 추가
                        detected_regions.append((prev_x, prev_y, prev_w, prev_h, prev_label, mosaic_region))
            
            # 결과 저장 (다음 프레임 추적용)
            if detected_regions:
                self.prev_regions = detected_regions
                self.region_history.append(detected_regions)
                
                # 디버깅용 로그 출력
                if self.frame_count % 30 == 0:
                    print(f"✅ 모자이크 영역 {len(detected_regions)}개 감지 (프레임 #{self.frame_count})")
                    
            # 처리 시간 측정
            elapsed_ms = (time.time() - start_time) * 1000
            self.avg_processing_time = 0.9 * self.avg_processing_time + 0.1 * elapsed_ms
            
        except Exception as e:
            print(f"❌ 객체 감지 오류: {e}")
            import traceback
            traceback.print_exc()
        
        return detected_regions
    
    def _detect_with_model(self, image):
        """YOLO 모델을 사용한 객체 감지"""
        results = []
        
        try:
            # YOLO 모델 사용
            if hasattr(self.model, 'predict'):  # ultralytics YOLO
                predictions = self.model.predict(
                    source=image,
                    conf=self.confidence_threshold,
                    verbose=False
                )
                
                # 결과 처리
                for r in predictions:
                    boxes = r.boxes
                    for box in boxes:
                        # 박스 좌표
                        x1, y1, x2, y2 = box.xyxy[0].cpu().numpy()
                        x, y = int(x1), int(y1)
                        w, h = int(x2 - x1), int(y2 - y1)
                        
                        # 클래스 및 신뢰도
                        conf = float(box.conf[0])
                        cls_id = int(box.cls[0])
                        
                        # 클래스 ID를 한글 레이블로 변환
                        if cls_id in self.class_mapping:
                            label = self.class_mapping[cls_id]
                        else:
                            label = f"클래스_{cls_id}"
                            
                        # 타겟 리스트에 있는 클래스만 처리
                        if label in self.targets:
                            results.append((x, y, w, h, label, conf))
            
            # OpenCV DNN 모델 사용
            elif hasattr(self.model, 'forward'):  # OpenCV DNN
                blob = cv2.dnn.blobFromImage(
                    image, 
                    1/255.0, 
                    (640, 640), 
                    swapRB=True, 
                    crop=False
                )
                self.model.setInput(blob)
                outputs = self.model.forward()
                
                # 결과 처리 (OpenCV DNN 예시, 실제 모델에 맞게 조정 필요)
                for output in outputs:
                    for detection in output:
                        scores = detection[5:]
                        class_id = np.argmax(scores)
                        confidence = scores[class_id]
                        
                        if confidence > self.confidence_threshold:
                            # YOLO 형식 출력은 상대 좌표
                            center_x = int(detection[0] * image.shape[1])
                            center_y = int(detection[1] * image.shape[0])
                            width = int(detection[2] * image.shape[1])
                            height = int(detection[3] * image.shape[0])
                            
                            # 왼쪽 상단 좌표 계산
                            x = int(center_x - width / 2)
                            y = int(center_y - height / 2)
                            
                            # 클래스 ID를 한글 레이블로 변환
                            if class_id in self.class_mapping:
                                label = self.class_mapping[class_id]
                            else:
                                label = f"클래스_{class_id}"
                                
                            # 타겟 리스트에 있는 클래스만 처리
                            if label in self.targets:
                                results.append((x, y, width, height, label, confidence))
        
        except Exception as e:
            print(f"❌ 모델 감지 오류: {e}")
            import traceback
            traceback.print_exc()
            
        return results
    
    def _detect_with_opencv(self, image):
        """OpenCV 기본 함수를 사용한 얼굴 인식"""
        try:
            # 얼굴 검출기 로드 (한 번만 로드하도록 개선 필요)
            face_cascade_path = cv2.data.haarcascades + 'haarcascade_frontalface_default.xml'
            face_cascade = cv2.CascadeClassifier(face_cascade_path)
            
            # 그레이스케일 변환
            gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
            
            # 얼굴 검출
            faces = face_cascade.detectMultiScale(
                gray,
                scaleFactor=1.1,
                minNeighbors=5,
                minSize=(30, 30)
            )
            
            return faces
            
        except Exception as e:
            print(f"❌ OpenCV 감지 오류: {e}")
            return []
    
    def pixelate(self, region):
        """픽셀화 모자이크 적용"""
        try:
            h, w = region.shape[:2]
            if h < 5 or w < 5:
                return region
                
            # 모자이크 픽셀 크기 - 강도에 비례
            block_size = max(3, self.strength // 3)
            
            # 작은 크기로 축소 후 다시 원래 크기로 확대
            small = cv2.resize(region, (w // block_size, h // block_size), 
                             interpolation=cv2.INTER_LINEAR)
            
            # 확대 시 픽셀 부각을 위해 NEAREST 사용
            return cv2.resize(small, (w, h), interpolation=cv2.INTER_NEAREST)
        except Exception as e:
            print(f"❌ 픽셀화 오류: {str(e)}")
            return region
    
    def blur(self, region):
        """블러 모자이크 적용"""
        try:
            h, w = region.shape[:2]
            if h < 5 or w < 5:
                return region
                
            # 블러 커널 크기 계산 - 강도에 비례
            ksize = max(5, min(self.strength, 51) // 2 * 2 + 1)
            
            # 가우시안 블러 적용
            return cv2.GaussianBlur(region, (ksize, ksize), 0)
        except Exception as e:
            print(f"❌ 블러 오류: {str(e)}")
            return region
    
    def set_strength(self, strength):
        """모자이크 강도 설정"""
        self.strength = max(5, min(strength, 50))
        print(f"✅ 모자이크 강도 설정: {self.strength}")
    
    def set_targets(self, targets):
        """감지 대상 설정"""
        self.targets = targets
        print(f"✅ 감지 대상 설정: {self.targets}")
    
    def set_method(self, method):
        """모자이크 방법 설정"""
        if method in ["pixel", "blur"]:
            self.method = method
            print(f"✅ 모자이크 방법 설정: {self.method}")
        else:
            print(f"❌ 지원하지 않는 모자이크 방법: {method}")
    
    def save_debug_image(self, image, regions, filename=None):
        """디버깅용 이미지 저장"""
        try:
            if image is None or not regions:
                return
                
            # 표시된 모자이크 영역 시각화
            debug_image = image.copy()
            for x, y, w, h, label, _ in regions:
                # 원본 이미지에 박스 표시
                cv2.rectangle(debug_image, (x, y), (x+w, y+h), (0, 0, 255), 2)
                cv2.putText(debug_image, label, (x, y-5), 
                          cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 0, 255), 1)
            
            # 이미지 저장
            if filename is None:
                filename = f"detect_{time.strftime('%Y%m%d_%H%M%S')}.jpg"
                
            debug_path = os.path.join(self.debug_dir, filename)
            cv2.imwrite(debug_path, debug_image)
            print(f"📸 디버깅용 이미지 저장: {debug_path}")
            return debug_path
        except Exception as e:
            print(f"⚠️ 디버깅 이미지 저장 실패: {e}")
            return None