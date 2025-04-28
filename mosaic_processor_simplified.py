import cv2
import numpy as np
import os
import time
import threading

class MosaicProcessor:
    """간소화된 모자이크 프로세서 클래스"""
    def __init__(self, model_path=None, strength=30, method="pixel", targets=None, device="cpu"):
        print("🎯 모자이크 대상:", targets)
        
        self.strength = strength
        self.method = method
        self.targets = targets or ["얼굴", "가슴", "보지", "팬티"]  # 기본 검열 대상
        
        # 테스트용 영역 설정 - 화면에 모자이크가 보이도록 큰 사이즈로 지정
        self.test_regions = [
            (300, 200, 300, 300, "테스트1"),
            (800, 300, 250, 250, "테스트2"),
            (500, 500, 400, 200, "테스트3")
        ]
        
        # 캐시 설정
        self.mosaic_cache = {}
        self.cache_lifetime = 45
        self.cache_cleanup_interval = 150
        
        # 프레임 카운터
        self.frame_count = 0
        self.avg_processing_time = 5.0  # 초기값 (ms)
        
        # 디버그 디렉토리
        self.debug_dir = "debug_captures"
        os.makedirs(self.debug_dir, exist_ok=True)
        
        print("✅ 간소화된 모자이크 프로세서 초기화 완료")
    
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
        else:
            mosaic_img = self.blur(region)
            
        # 캐시에 저장
        self.mosaic_cache[roi_key] = (self.frame_count, mosaic_img)
        
        return mosaic_img
    
    def detect_objects(self, image):
        """이미지에서 객체 감지 (테스트용 더미 구현)"""
        if image is None or image.size == 0:
            return []

        detected_regions = []
        self.frame_count += 1
        
        # 캐시 주기적 정리
        if self.frame_count % self.cache_cleanup_interval == 0:
            self.cleanup_cache()
        
        # 테스트용 모자이크 영역 생성 - 실제로 화면에 보이게 하기 위해
        for (x, y, w, h, label) in self.test_regions:
            try:
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
                    
                    # 강력한 색상 추가 (빨간색 오버레이) - 모자이크가 잘 보이도록
                    red_overlay = np.zeros_like(mosaic_region)
                    red_overlay[:, :] = [0, 0, 200]  # BGR 빨간색 (약간 투명하게)
                    
                    # 모자이크 위에 반투명 빨간색 추가
                    mosaic_with_overlay = cv2.addWeighted(mosaic_region, 0.7, red_overlay, 0.3, 0)
                    
                    # 테두리 추가
                    cv2.rectangle(mosaic_with_overlay, (0, 0), (w-1, h-1), (0, 0, 255), 2)
                    
                    # 텍스트 추가
                    cv2.putText(mosaic_with_overlay, label, (10, 30), 
                               cv2.FONT_HERSHEY_SIMPLEX, 1, (255, 255, 255), 2)
                    
                    # 결과 추가
                    detected_regions.append((x, y, w, h, label, mosaic_with_overlay))
            except Exception as e:
                print(f"❌ 모자이크 영역 처리 오류: {e} @ ({x},{y},{w},{h})")
        
        # 디버깅용 로그 출력
        if self.frame_count % 100 == 0:
            print(f"✅ 모자이크 영역 {len(detected_regions)}개 생성 (프레임 #{self.frame_count})")
        
        return detected_regions
    
    def pixelate(self, region):
        """픽셀화 모자이크 적용"""
        try:
            h, w = region.shape[:2]
            if h < 5 or w < 5:
                return region
                
            # 모자이크 픽셀 크기 - 더 큰 픽셀 모자이크 사용
            size = max(5, self.strength // 3)
            
            # 작은 크기로 축소 후 다시 원래 크기로 확대
            small = cv2.resize(region, (size, size), interpolation=cv2.INTER_LINEAR)
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
                
            # 블러 커널 크기 계산 - 더 강한 블러 적용
            ksize = max(21, min(self.strength, 51) // 2 * 2 + 1)
            
            # 가우시안 블러 적용
            return cv2.GaussianBlur(region, (ksize, ksize), 0)
        except Exception as e:
            print(f"❌ 블러 오류: {str(e)}")
            return region