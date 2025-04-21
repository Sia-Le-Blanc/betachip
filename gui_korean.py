from PyQt5.QtWidgets import (
    QWidget, QPushButton, QVBoxLayout, QHBoxLayout,
    QLabel, QSlider, QCheckBox, QGroupBox
)
from PyQt5.QtCore import Qt, pyqtSignal


class MainWindow(QWidget):
    start_censoring_signal = pyqtSignal()
    stop_censoring_signal = pyqtSignal()

    def __init__(self):
        super().__init__()
        self.setWindowTitle("실시간 화면 검열 시스템")
        self.setFixedSize(400, 500)

        self.strength = 25
        self.targets = ["얼굴", "가슴", "보지", "팬티"]

        self.init_ui()

    def init_ui(self):
        layout = QVBoxLayout()

        # --- 강도 슬라이더 ---
        strength_layout = QVBoxLayout()
        strength_label = QLabel("모자이크 강도")
        self.strength_slider = QSlider(Qt.Horizontal)
        self.strength_slider.setMinimum(5)
        self.strength_slider.setMaximum(50)
        self.strength_slider.setValue(self.strength)
        self.strength_slider.valueChanged.connect(self.update_strength)
        strength_layout.addWidget(strength_label)
        strength_layout.addWidget(self.strength_slider)

        # --- 타겟 체크박스 ---
        self.checkboxes = []
        checkbox_group = QGroupBox("검열 대상")
        checkbox_layout = QVBoxLayout()
        options = [
            "얼굴", "눈", "손", "가슴", "보지", "팬티",
            "겨드랑이", "자지", "몸 전체", "교미", "신발",
            "가슴_옷", "보지_옷", "여성"
        ]
        for label in options:
            checkbox = QCheckBox(label)
            checkbox.setChecked(label in self.targets)
            checkbox_layout.addWidget(checkbox)
            self.checkboxes.append(checkbox)
        checkbox_group.setLayout(checkbox_layout)

        # --- 버튼 ---
        button_layout = QHBoxLayout()
        self.start_button = QPushButton("검열 시작")
        self.stop_button = QPushButton("검열 중지")
        self.start_button.clicked.connect(self.start_censoring)
        self.stop_button.clicked.connect(self.stop_censoring)
        button_layout.addWidget(self.start_button)
        button_layout.addWidget(self.stop_button)

        # 전체 레이아웃 구성
        layout.addLayout(strength_layout)
        layout.addWidget(checkbox_group)
        layout.addLayout(button_layout)
        self.setLayout(layout)

    def update_strength(self, value):
        self.strength = value

    def get_selected_targets(self):
        return [cb.text() for cb in self.checkboxes if cb.isChecked()]

    def start_censoring(self):
        self.targets = self.get_selected_targets()
        self.start_censoring_signal.emit()

    def stop_censoring(self):
        self.stop_censoring_signal.emit()

    def get_strength(self):
        return self.strength
    
    # MainWindow 클래스 수정
    def set_render_mode_info(self, mode_info):
        """렌더링 모드 정보 설정"""
        if not hasattr(self, 'render_mode_label'):
            # 레이블이 없으면 새로 생성
            self.render_mode_label = QLabel(mode_info)
            self.layout().addWidget(self.render_mode_label)
        else:
            # 기존 레이블 텍스트 업데이트
            self.render_mode_label.setText(mode_info)