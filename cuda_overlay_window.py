import cv2
import numpy as np
import threading
import time
import os

class CudaOverlayWindow:
    """CUDA 가속 기반 모자이크 오버레이 윈도우"""
    def __init__(self):
        print("✅ CUDA 기반 오버레이 초기화")
        # CUDA 구현은 향후 개발 예정
        self.mosaic_regions = []
        self.original_image = None
        self.frame_count = 0
        self.debug_dir = "debug_overlay"
        os.makedirs(self.debug_dir, exist_ok=True)
        
        # GPU 정보 출력
        self._print_cuda_info()
        
    def _print_cuda_info(self):
        """CUDA 정보 출력"""
        try:
            import torch
            if torch.cuda.is_available():
                device_name = torch.cuda.get_device_name(0)
                device_count = torch.cuda.device_count()
                cuda_version = torch.version.cuda
                
                print(f"🔥 CUDA 장치: {device_name}")
                print(f"🔥 CUDA 장치 수: {device_count}")
                print(f"🔥 CUDA 버전: {cuda_version}")
                print(f"🔥 현재 메모리 사용량: {torch.cuda.memory_allocated(0)/1024**2:.1f} MB")
                print(f"🔥 최대 메모리 사용량: {torch.cuda.max_memory_allocated(0)/1024**2:.1f} MB")
        except Exception as e:
            print(f"⚠️ CUDA 정보 확인 중 오류: {e}")
    
    def show(self):
        """오버레이 창 표시"""
        print("✅ CUDA 오버레이 표시 (향후 구현 예정)")
    
    def hide(self):
        """오버레이 창 숨기기"""
        print("🛑 CUDA 오버레이 숨기기 (향후 구현 예정)")
    
    def update_regions(self, original_image, mosaic_regions):
        """모자이크 영역 업데이트"""
        self.original_image = original_image
        self.mosaic_regions = mosaic_regions
        self.frame_count += 1
        
        # 로그 출력
        if len(mosaic_regions) > 0 and self.frame_count % 100 == 0:
            print(f"✅ CUDA: 모자이크 영역 {len(mosaic_regions)}개 처리 중 (프레임 #{self.frame_count})")
            # 실제 구현 전까지는 OpenCV로 임시 처리
            self._temp_save_debug_image()
    
    def _temp_save_debug_image(self):
        """디버깅용 이미지 저장 (임시 구현)"""
        try:
            if self.original_image is None or not self.mosaic_regions:
                return
                
            # 표시된 모자이크 영역 시각화
            debug_image = self.original_image.copy()
            for x, y, w, h, label, _ in self.mosaic_regions:
                # 원본 이미지에 박스 표시
                cv2.rectangle(debug_image, (x, y), (x+w, y+h), (0, 0, 255), 2)  # 빨간색으로 표시 (CUDA 구분)
                cv2.putText(debug_image, f"CUDA:{label}", (x, y-5), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 0, 255), 1)
            
            # 이미지 저장
            debug_path = f"{self.debug_dir}/overlay_cuda_{time.strftime('%Y%m%d_%H%M%S')}.jpg"
            cv2.imwrite(debug_path, debug_image)
            print(f"📸 CUDA 디버깅용 이미지 저장: {debug_path}")
        except Exception as e:
            print(f"⚠️ 디버깅 이미지 저장 실패: {e}")
    
    def get_window_handle(self):
        """윈도우 핸들 반환"""
        return 0  # 임시 구현