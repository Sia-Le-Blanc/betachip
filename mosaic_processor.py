import cv2
import numpy as np
import torch
import os
import time
import imagehash
from PIL import Image
from ultralytics.nn.tasks import DetectionModel
from torch.serialization import add_safe_globals
from ultralytics.utils.ops import non_max_suppression
from sort import Sort

add_safe_globals([DetectionModel])

class MosaicProcessor:
    def __init__(self, model_path, strength=30, method="pixel", targets=None, device="cpu"):
        print("🎯 모자이크 대상:", targets)
        self.device = device
        print(f"💻 디바이스: {self.device.upper()}")

        try:
            if not os.path.exists(model_path):
                raise FileNotFoundError(f"모델 파일이 없습니다: {model_path}")

            print(f"📂 모델 파일 크기: {os.path.getsize(model_path) / (1024*1024):.2f} MB")
            print("🔄 YOLOv8 모델 초기화 중...")
            self.model = DetectionModel(cfg='yolov8n.yaml', ch=3, nc=15)
            state_dict = torch.load(model_path, map_location=self.device)
            self.model.load_state_dict(state_dict)
            self.model.to(self.device)
            self.model.eval()
            
            # GPU 사용 시 메모리 최적화
            if self.device == "cuda":
                torch.cuda.empty_cache()
                # 성능 개선: CUDA 최적화 설정
                if hasattr(torch.backends, 'cudnn'):
                    torch.backends.cudnn.benchmark = True
                    torch.backends.cudnn.deterministic = False
            
            print("✅ YOLO 모델 로드 완료")
        except Exception as e:
            print(f"❌ 모델 로드 실패: {e}")
            import traceback
            traceback.print_exc()
            raise RuntimeError(f"Model load failed: {e}")

        self.classes = {
            0: "얼굴", 1: "가슴", 2: "겨드랑이", 3: "보지", 4: "발",
            5: "몸 전체", 6: "자지", 7: "팬티", 8: "눈", 9: "손",
            10: "교미", 11: "신발", 12: "가슴_옷", 13: "보지_옷", 14: "여성"
        }

        self.strength = strength
        self.method = method
        self.targets = targets or []

        # 성능 개선: Sort 추적기 추가 및 추적 성능 향상
        self.tracker = Sort(max_age=45, min_hits=1)  # 객체 추적 시간 확장
        
        # 성능 개선: 감지 주기 조절 최적화
        self.detection_interval = 3  # 3프레임마다 YOLO 실행 (더 잦은 검출)
        self.frame_count = 0
        self.last_detection_frame = 0
        
        # 성능 개선: 추적과 예측을 위한 변수들 최적화
        self.tracked_objects = {}  # id를 키로 사용하는 추적 객체 저장
        self.confidence_threshold = 0.3  # 신뢰도 임계값 약간 증가
        self.prev_frame_hash = None
        self.phash_threshold = 8
        self.prev_frame = None
        
        # 성능 개선: 미리 계산된 모자이크 캐시 최적화
        self.mosaic_cache = {}
        self.cache_lifetime = 45  # 캐시 생존 시간 증가
        self.cache_cleanup_interval = 150
        
        # 성능 개선: 모델 입력 크기 최적화
        self.input_size = (384, 224)  # 더 작은 크기로 설정
        
        # 성능 개선: 모션 기반 감지 빈도 조절
        self.motion_threshold = 0.03
        self.last_motion_level = 0
        
        # 성능 개선: 색상 왜곡 방지를 위한 설정
        self.use_hsv_mosaic = False  # HSV 색 공간에서 모자이크 처리
        
        # 성능 개선: 객체 추적 안정성 향상
        self.stable_region_history = {}  # 안정적인 영역 이력
        self.region_stability_threshold = 5  # 안정성 임계값
        
        # 디버그 디렉토리
        self.debug_dir = "debug_captures"
        os.makedirs(self.debug_dir, exist_ok=True)

    def get_phash(self, image):
        try:
            if image is None:
                return None
            # 성능 개선: 이미지 다운샘플링으로 phash 계산 속도 향상
            small_img = cv2.resize(image, (64, 36))
            pil_img = Image.fromarray(cv2.cvtColor(small_img, cv2.COLOR_BGR2RGB))
            return imagehash.phash(pil_img)
        except Exception as e:
            print(f"❌ pHash 계산 실패: {e}")
            return None

    def detect_motion(self, current_frame):
        """프레임 간 모션 감지"""
        if self.prev_frame is None or current_frame is None:
            self.prev_frame = current_frame
            return 0.0
            
        # 성능 개선: 작은 크기로 다운샘플링하여 모션 감지 속도 향상
        prev_small = cv2.resize(self.prev_frame, (128, 72), interpolation=cv2.INTER_AREA)
        curr_small = cv2.resize(current_frame, (128, 72), interpolation=cv2.INTER_AREA)
        
        # 그레이스케일 변환
        prev_gray = cv2.cvtColor(prev_small, cv2.COLOR_BGR2GRAY)
        curr_gray = cv2.cvtColor(curr_small, cv2.COLOR_BGR2GRAY)
        
        # 차이 계산
        diff = cv2.absdiff(prev_gray, curr_gray)
        _, thresh = cv2.threshold(diff, 25, 255, cv2.THRESH_BINARY)
        motion_level = np.sum(thresh) / (128 * 72 * 255)
        
        # 이전 프레임 업데이트
        self.prev_frame = current_frame
        
        return motion_level

    def cleanup_cache(self):
        """오래된 모자이크 캐시 제거"""
        current_frame = self.frame_count
        expired_keys = [k for k, (frame, _) in self.mosaic_cache.items() 
                       if current_frame - frame > self.cache_lifetime]
        for key in expired_keys:
            del self.mosaic_cache[key]
        
        if expired_keys:
            print(f"🧹 {len(expired_keys)}개의 모자이크 캐시 제거됨")
    
    def update_region_stability(self, label, box):
        """영역 안정성 업데이트"""
        region_key = f"{label}_{int(box[0]/10)}_{int(box[1]/10)}_{int(box[2]/10)}_{int(box[3]/10)}"
        
        if region_key not in self.stable_region_history:
            self.stable_region_history[region_key] = 1
        else:
            self.stable_region_history[region_key] += 1
            
        # 오래된 키 제거
        if len(self.stable_region_history) > 100:
            min_count = min(self.stable_region_history.values())
            keys_to_remove = [k for k, v in self.stable_region_history.items() if v == min_count]
            for k in keys_to_remove[:10]:  # 최대 10개만 제거
                if k in self.stable_region_history:
                    del self.stable_region_history[k]
                    
        return self.stable_region_history[region_key] >= self.region_stability_threshold

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
        else:
            mosaic_img = self.blur(region)
            
        # 캐시에 저장
        self.mosaic_cache[roi_key] = (self.frame_count, mosaic_img)
        
        return mosaic_img

    def detect_objects(self, image):
        if image is None or image.size == 0:
            return []

        detected_regions = []
        self.frame_count += 1
        
        # 모션 레벨 계산
        motion_level = self.detect_motion(image)
        motion_changed = abs(motion_level - self.last_motion_level) > 0.02
        self.last_motion_level = motion_level
        
        # 성능 개선: 모션 수준에 따라 감지 간격 동적 조정
        if motion_level > 0.1:
            effective_interval = max(1, self.detection_interval // 2)
        else:
            effective_interval = self.detection_interval
            
        # 이미지 해시 계산 및 변화 감지
        force_detect = False
        if self.frame_count % 5 == 0:  # 더 자주 해시 계산 (5프레임마다)
            current_hash = self.get_phash(image)
            if self.prev_frame_hash is not None and current_hash is not None:
                hash_diff = abs(current_hash - self.prev_frame_hash)
                if hash_diff > self.phash_threshold:
                    force_detect = True
                    if self.frame_count % 30 == 0:  # 로그 빈도 제한
                        print(f"📸 화면 변화 감지 - hash 변화량: {hash_diff}")
            self.prev_frame_hash = current_hash

        # 감지 실행 조건: 간격, 강제 감지, 모션 변화
        should_detect = (self.frame_count - self.last_detection_frame >= effective_interval 
                        or force_detect 
                        or motion_changed)

        # 감지 및 추적
        detected_boxes = []
        try:
            if should_detect:
                self.last_detection_frame = self.frame_count
                
                # 성능 개선: 작은 크기로 리사이즈하여 YOLO 처리 속도 향상
                resized = cv2.resize(image, self.input_size, interpolation=cv2.INTER_AREA)
                
                # 입력 텐서 준비
                input_tensor = resized.transpose(2, 0, 1) / 255.0
                input_tensor = np.ascontiguousarray(input_tensor)
                tensor = torch.from_numpy(input_tensor).float().unsqueeze(0).to(self.device)

                # YOLO 추론
                with torch.no_grad():
                    preds = self.model(tensor)[0]
                    results = non_max_suppression(preds, conf_thres=self.confidence_threshold)[0]

                # 감지 결과 형식 변환
                if results is not None and len(results) > 0:
                    for i, det in enumerate(results):
                        x1, y1, x2, y2, conf, class_id = det.tolist()
                        label = self.classes.get(int(class_id), None)
                        
                        if label and label in self.targets:
                            # 원본 해상도로 좌표 변환
                            x1_orig = int(x1 / self.input_size[0] * image.shape[1])
                            y1_orig = int(y1 / self.input_size[1] * image.shape[0])
                            x2_orig = int(x2 / self.input_size[0] * image.shape[1])
                            y2_orig = int(y2 / self.input_size[1] * image.shape[0])
                            
                            # SORT 형식으로 변환
                            detected_boxes.append([x1_orig, y1_orig, x2_orig, y2_orig, conf, int(class_id)])
            
            # Sort 트래커 업데이트
            tracked_boxes = self.tracker.update(detected_boxes if detected_boxes else [])
                
            # 캐시 주기적 정리
            if self.frame_count % self.cache_cleanup_interval == 0:
                self.cleanup_cache()
                
            # 추적 결과로 모자이크 영역 생성
            for box in tracked_boxes:
                x1, y1, x2, y2, class_id = box
                label = self.classes.get(int(class_id), None)
                
                if label in self.targets:
                    try:
                        # 좌표 정수로 변환 및 경계 확인
                        x = max(0, int(x1))
                        y = max(0, int(y1))
                        w = min(image.shape[1] - x, int(x2 - x1))
                        h = min(image.shape[0] - y, int(y2 - y1))
                        
                        if w > 0 and h > 0:
                            # 영역이 안정적인지 확인 (색상 왜곡 및 깜빡임 방지)
                            is_stable = self.update_region_stability(label, (x, y, w, h))
                            
                            # 안정적인 영역만 처리 (영역 크기가 매우 작으면 무조건 처리)
                            if is_stable or (w < 50 and h < 50):
                                # 모자이크 영역 추출
                                region = image[y:y+h, x:x+w].copy()
                                
                                # 고유 ROI 키 생성
                                roi_key = f"{label}_{int(x/10)}_{int(y/10)}_{int(w/10)}_{int(h/10)}"
                                
                                # 캐시된 모자이크 받기 또는 새로 계산
                                mosaic_region = self.get_cached_mosaic(region, roi_key)
                                
                                # 결과 추가
                                detected_regions.append((x, y, w, h, label, mosaic_region))
                    except Exception as e:
                        print(f"❌ 모자이크 오류: {e} @ ({x},{y},{w},{h})")

        except Exception as e:
            print(f"❌ 감지 오류: {e}")
            import traceback
            traceback.print_exc()

        return detected_regions

    def pixelate(self, region):
        try:
            h, w = region.shape[:2]
            if h < 5 or w < 5:
                return region
                
            # 성능 개선: 모자이크 픽셀 크기와 크기에 따른 최적화
            size = max(3, min(self.strength, 20))
            
            # 색상 왜곡 방지를 위한 HSV 모드
            if self.use_hsv_mosaic:
                # HSV 색공간으로 변환
                hsv = cv2.cvtColor(region, cv2.COLOR_BGR2HSV)
                
                # H, S, V 채널 분리
                h_channel, s_channel, v_channel = cv2.split(hsv)
                
                # 픽셀화 처리 - 명도(V) 채널에만 적용
                small_v = cv2.resize(v_channel, (size, size), interpolation=cv2.INTER_LINEAR)
                v_pixelated = cv2.resize(small_v, (w, h), interpolation=cv2.INTER_NEAREST)
                
                # 채널 병합
                hsv_pixelated = cv2.merge([h_channel, s_channel, v_pixelated])
                
                # BGR로 변환하여 반환
                return cv2.cvtColor(hsv_pixelated, cv2.COLOR_HSV2BGR)
            else:
                # 기존 방식: BGR 픽셀화
                # 이미지 크기에 따라 최적의 인터폴레이션 방식 선택
                if h * w > 10000:
                    small = cv2.resize(region, (size, size), interpolation=cv2.INTER_NEAREST)
                    return cv2.resize(small, (w, h), interpolation=cv2.INTER_NEAREST)
                else:
                    small = cv2.resize(region, (size, size), interpolation=cv2.INTER_LINEAR)
                    return cv2.resize(small, (w, h), interpolation=cv2.INTER_NEAREST)
        except Exception as e:
            print(f"❌ 픽셀화 오류: {str(e)}")
            return region

    def blur(self, region):
        try:
            h, w = region.shape[:2]
            if h < 5 or w < 5:
                return region
                
            # 성능 개선: 이미지 크기에 따라 커널 크기 조정
            ksize = max(3, min(self.strength, 31) // 2 * 2 + 1)
            
            # 색상 왜곡 방지를 위한 HSV 모드
            if self.use_hsv_mosaic:
                # HSV 색공간으로 변환
                hsv = cv2.cvtColor(region, cv2.COLOR_BGR2HSV)
                
                # H, S, V 채널 분리
                h_channel, s_channel, v_channel = cv2.split(hsv)
                
                # 블러 처리 - 명도(V) 채널에만 적용
                v_blurred = cv2.GaussianBlur(v_channel, (ksize, ksize), 0)
                
                # 채널 병합
                hsv_blurred = cv2.merge([h_channel, s_channel, v_blurred])
                
                # BGR로 변환하여 반환
                return cv2.cvtColor(hsv_blurred, cv2.COLOR_HSV2BGR)
            else:
                # 기존 방식: 전체 BGR 블러
                # 큰 이미지는 다운샘플 후 블러 처리
                if h * w > 10000:
                    scale = 0.5
                    small = cv2.resize(region, (int(w*scale), int(h*scale)), interpolation=cv2.INTER_AREA)
                    blurred = cv2.GaussianBlur(small, (ksize, ksize), 0)
                    return cv2.resize(blurred, (w, h), interpolation=cv2.INTER_LINEAR)
                else:
                    return cv2.GaussianBlur(region, (ksize, ksize), 0)
        except Exception as e:
            print(f"❌ 블러 오류: {str(e)}")
            return region