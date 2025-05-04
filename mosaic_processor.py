import cv2
import numpy as np
import onnxruntime as ort

class MosaicProcessor:
    def __init__(self, model_path='resources/best.onnx', mosaic_strength=15):
        self.session = ort.InferenceSession(model_path, providers=['CPUExecutionProvider'])
        self.input_name = self.session.get_inputs()[0].name
        self.mosaic_strength = mosaic_strength

    def preprocess(self, frame):
        img = cv2.resize(frame, (640, 640))
        img_rgb = cv2.cvtColor(img, cv2.COLOR_BGR2RGB)
        img_rgb = img_rgb.transpose(2, 0, 1).astype(np.float32) / 255.0
        return np.expand_dims(img_rgb, axis=0), img.shape[1], img.shape[0]

    def postprocess(self, outputs, original_w, original_h, conf_thres=0.5):
        preds = outputs[0]  # (1, num_boxes, 85)
        boxes = []
        for pred in preds[0]:
            conf = pred[4]
            if conf < conf_thres:
                continue
            class_id = int(pred[5:].argmax())
            cx, cy, w, h = pred[0:4]
            x1 = int((cx - w / 2) * original_w / 640)
            y1 = int((cy - h / 2) * original_h / 640)
            x2 = int((cx + w / 2) * original_w / 640)
            y2 = int((cy + h / 2) * original_h / 640)
            boxes.append((x1, y1, x2, y2, conf, class_id))
        return boxes

    def apply_mosaic(self, frame, boxes):
        for (x1, y1, x2, y2, conf, class_id) in boxes:
            x1, y1 = max(0, x1), max(0, y1)
            x2, y2 = min(frame.shape[1], x2), min(frame.shape[0], y2)
            mosaic_area = frame[y1:y2, x1:x2]
            if mosaic_area.size == 0:
                continue
            small = cv2.resize(mosaic_area, (self.mosaic_strength, self.mosaic_strength), interpolation=cv2.INTER_LINEAR)
            mosaic = cv2.resize(small, (x2 - x1, y2 - y1), interpolation=cv2.INTER_NEAREST)
            frame[y1:y2, x1:x2] = mosaic
        return frame

    def process(self, frame):
        input_tensor, w, h = self.preprocess(frame)
        outputs = self.session.run(None, {self.input_name: input_tensor})
        boxes = self.postprocess(outputs, w, h)
        return self.apply_mosaic(frame, boxes)
