import numpy as np
from collections import deque

class KalmanBoxTracker:
    count = 0

    def __init__(self, bbox):
        self.bbox = bbox  # (x1, y1, x2, y2)
        self.id = KalmanBoxTracker.count
        KalmanBoxTracker.count += 1
        self.hits = 0
        self.no_losses = 0
        self.trace = deque(maxlen=5)
        self.class_id = None  # 클래스 ID 저장

    def update(self, bbox):
        self.bbox = bbox
        self.hits += 1
        self.no_losses = 0
        self.trace.append(bbox)

    def predict(self):
        self.no_losses += 1
        # 간단한 선형 예측 (이전 위치 기반)
        if len(self.trace) >= 2:
            prev1 = self.trace[-1]
            prev2 = self.trace[-2]
            # 속도 계산 및 예측
            dx = (prev1[2] - prev1[0]) - (prev2[2] - prev2[0])
            dy = (prev1[3] - prev1[1]) - (prev2[3] - prev2[1])
            
            # 새 위치 예측
            new_x1 = self.bbox[0] + dx * 0.5
            new_y1 = self.bbox[1] + dy * 0.5
            new_x2 = self.bbox[2] + dx * 0.5
            new_y2 = self.bbox[3] + dy * 0.5
            
            self.bbox = (new_x1, new_y1, new_x2, new_y2)
        
        return self.bbox


class Sort:
    def __init__(self, max_age=3, min_hits=1):
        self.trackers = []
        self.frame_count = 0
        self.max_age = max_age
        self.min_hits = min_hits
        self.last_matched_trackers = []  # 최근 매칭된 트래커 저장

    def update(self, dets):  # dets: [[x1, y1, x2, y2, conf, class_id]]
        self.frame_count += 1
        updated_trackers = []
        result_boxes = []

        # 트래커 예측
        for tracker in self.trackers:
            tracker.predict()

        # 감지 결과가 없는 경우
        if len(dets) == 0:
            # 예측만 진행 - 오래된 트래커 제거
            remaining_trackers = []
            for tracker in self.trackers:
                if tracker.no_losses <= self.max_age:
                    remaining_trackers.append(tracker)
                    if tracker.hits >= self.min_hits:
                        result_boxes.append((*tracker.bbox, tracker.class_id))
            
            self.trackers = remaining_trackers
            return np.array(result_boxes) if result_boxes else np.empty((0, 5))

        # 매칭 및 업데이트
        matched_indices = []
        unmatched_dets = list(range(len(dets)))
        unmatched_trks = list(range(len(self.trackers)))

        # 간단한 IOU 기반 매칭
        for t, tracker in enumerate(self.trackers):
            for d, det in enumerate(dets):
                if d in unmatched_dets and t in unmatched_trks:
                    iou_val = self._iou(det[:4], tracker.bbox)
                    if iou_val > 0.3:  # IOU 임계값
                        matched_indices.append((d, t))
                        unmatched_dets.remove(d)
                        unmatched_trks.remove(t)
                        break

        # 매칭된 트래커 업데이트
        for d, t in matched_indices:
            self.trackers[t].update(dets[d][:4])
            self.trackers[t].class_id = int(dets[d][5])

        # 새로운 트래커 생성
        for d in unmatched_dets:
            new_tracker = KalmanBoxTracker(dets[d][:4])
            new_tracker.class_id = int(dets[d][5])
            self.trackers.append(new_tracker)

        # 최종 결과 생성 및 트래커 정리
        remaining_trackers = []
        for t, tracker in enumerate(self.trackers):
            if tracker.no_losses <= self.max_age:
                remaining_trackers.append(tracker)
                if tracker.hits >= self.min_hits:
                    result_boxes.append((*tracker.bbox, tracker.class_id))
        
        self.trackers = remaining_trackers
        self.last_matched_trackers = matched_indices
        
        return np.array(result_boxes) if result_boxes else np.empty((0, 5))

    def _iou(self, bb1, bb2):
        """
        두 박스 간의 IOU 계산
        bb1, bb2: [x1, y1, x2, y2] 형식
        """
        x1 = max(bb1[0], bb2[0])
        y1 = max(bb1[1], bb2[1])
        x2 = min(bb1[2], bb2[2])
        y2 = min(bb1[3], bb2[3])
        
        # 교차 영역 계산
        w = max(0, x2 - x1)
        h = max(0, y2 - y1)
        intersection = w * h
        
        # 합집합 영역 계산
        area1 = (bb1[2] - bb1[0]) * (bb1[3] - bb1[1])
        area2 = (bb2[2] - bb2[0]) * (bb2[3] - bb2[1])
        union = area1 + area2 - intersection
        
        # IOU 계산
        iou = intersection / (union + 1e-6)  # 나누기 0 방지
        
        return iou