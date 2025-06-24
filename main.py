"""
풀스크린 + 캡처 방지 실시간 화면 검열 시스템
전체 화면을 매끄럽게 표시하여 끊김 없는 검열 효과 제공
"""

import tkinter as tk
from tkinter import ttk, messagebox
import threading
import time
import os
from datetime import datetime
from capture.mss_capture import ScreenCapturer
from detection.mosaic_processor import MosaicProcessor
from overlay.pygame_overlay import PygameOverlayWindow
from config import CONFIG
import cv2
import numpy as np
import sys
import onnxruntime

def resource_path(relative_path):
    """PyInstaller 환경에서도 리소스 경로를 안전하게 불러오기"""
    try:
        base_path = sys._MEIPASS
    except AttributeError:
        base_path = os.path.abspath(".")

    return os.path.join(base_path, relative_path)

# 예시: ONNX 모델 경로를 전역으로 사용하고 싶을 경우
ONNX_MODEL_PATH = resource_path("resources/best.onnx")

print("✅ 프로그램 시작됨")

# onnxruntime 직전
print("📡 ONNX 모델 로딩 시도")
# 예외 감싸기
try:
    session = onnxruntime.InferenceSession(ONNX_MODEL_PATH)
    print("✅ 모델 로딩 성공")
except Exception as e:
    print("❌ 모델 로딩 실패:", e)

# Tkinter 루프 진입 확인
print("🪟 Tkinter GUI 루프 진입 준비됨")


# 기존 import 구문들 아래에 추가할 것



class ScrollableFrame(tk.Frame):
    """스크롤 가능한 프레임 클래스"""
    
    def __init__(self, container, *args, **kwargs):
        super().__init__(container, *args, **kwargs)
        
        # Canvas와 Scrollbar 생성
        self.canvas = tk.Canvas(self, highlightthickness=0)
        self.scrollbar = ttk.Scrollbar(self, orient="vertical", command=self.canvas.yview)
        self.scrollable_frame = tk.Frame(self.canvas)
        
        # 스크롤 영역 설정
        self.scrollable_frame.bind(
            "<Configure>",
            lambda e: self.canvas.configure(scrollregion=self.canvas.bbox("all"))
        )
        
        # Canvas에 프레임 추가
        self.canvas_frame = self.canvas.create_window((0, 0), window=self.scrollable_frame, anchor="nw")
        
        # Canvas 크기 조정
        def configure_canvas(event):
            self.canvas.itemconfig(self.canvas_frame, width=event.width)
        
        self.canvas.bind('<Configure>', configure_canvas)
        self.canvas.configure(yscrollcommand=self.scrollbar.set)
        
        # 레이아웃
        self.canvas.pack(side="left", fill="both", expand=True)
        self.scrollbar.pack(side="right", fill="y")
        
        # 마우스 휠 바인딩
        self.bind_mousewheel()
    
    def bind_mousewheel(self):
        """마우스 휠 바인딩"""
        def _on_mousewheel(event):
            self.canvas.yview_scroll(int(-1*(event.delta/120)), "units")
        
        def _on_mousewheel_linux(event):
            if event.num == 4:
                self.canvas.yview_scroll(-1, "units")
            elif event.num == 5:
                self.canvas.yview_scroll(1, "units")
        
        # 바인딩 함수
        def bind_to_mousewheel(widget):
            widget.bind("<MouseWheel>", _on_mousewheel)  # Windows
            widget.bind("<Button-4>", _on_mousewheel_linux)  # Linux
            widget.bind("<Button-5>", _on_mousewheel_linux)  # Linux
            
            # 모든 자식 위젯에도 바인딩
            for child in widget.winfo_children():
                bind_to_mousewheel(child)
        
        # Canvas와 스크롤 가능한 프레임에 바인딩
        bind_to_mousewheel(self.canvas)
        bind_to_mousewheel(self.scrollable_frame)
        
        # 상위 윈도우에도 바인딩
        def bind_to_parent():
            parent = self.winfo_toplevel()
            if parent:
                bind_to_mousewheel(parent)
        
        # 약간의 지연 후 바인딩
        self.after(100, bind_to_parent)

class MosaicApp:
    """풀스크린 + 캡처 방지 실시간 화면 검열 애플리케이션"""
    
    def __init__(self):
        # 메인 윈도우 생성
        self.root = tk.Tk()
        self.root.title("실시간 화면 검열 시스템 v3.0 (풀스크린 + 캡처 방지)")
        self.root.geometry("500x600")  # 높이를 줄여서 스크롤 필요하게 만듦
        self.root.resizable(True, True)
        self.root.minsize(450, 400)  # 최소 크기 더 작게
        
        # 드래그 기능을 위한 변수들
        self.drag_start_x = 0
        self.drag_start_y = 0
        
        # 컴포넌트 초기화
        self.capturer = ScreenCapturer(CONFIG.get("capture", {}))
        self.processor = MosaicProcessor(None, CONFIG.get("mosaic", {}))
        self.overlay = PygameOverlayWindow(CONFIG.get("overlay", {}))
        
        # 상태 변수
        self.is_running = False
        self.process_thread = None
        self.debug_mode = False
        
        # 통계 변수
        self.stats = {
            'frames_processed': 0,
            'objects_detected': 0,
            'mosaic_applied': 0,
            'start_time': None
        }
        
        # GUI 생성
        self.create_gui()
        
        # 디버그 디렉토리 생성
        if self.debug_mode:
            os.makedirs("debug_detection", exist_ok=True)
    
    def setup_window_dragging(self, widget):
        """창 드래그 기능 설정"""
        def start_drag(event):
            """드래그 시작"""
            self.drag_start_x = event.x
            self.drag_start_y = event.y
        
        def do_drag(event):
            """드래그 진행"""
            # 현재 마우스 위치에서 드래그 시작 위치를 빼서 이동할 거리 계산
            dx = event.x - self.drag_start_x
            dy = event.y - self.drag_start_y
            
            # 현재 창 위치 가져오기
            current_x = self.root.winfo_x()
            current_y = self.root.winfo_y()
            
            # 새로운 위치로 창 이동
            self.root.geometry(f"+{current_x + dx}+{current_y + dy}")
        
        # 위젯에 드래그 기능 바인딩
        widget.bind("<Button-1>", start_drag)
        widget.bind("<B1-Motion>", do_drag)
    
    def create_gui(self):
        """GUI 생성 (스크롤 기능 추가)"""
        
        # 제목 (드래그 가능) - 고정 영역
        title_label = tk.Label(self.root, text="🛡️ 풀스크린 화면 검열 시스템", 
                              font=("Arial", 14, "bold"), bg="lightblue", 
                              relief="raised", cursor="hand2")
        title_label.pack(pady=5, fill="x", padx=5)
        
        # 제목 라벨에 드래그 기능 바인딩
        self.setup_window_dragging(title_label)
        
        # 스크롤 안내 - 고정 영역
        scroll_info = tk.Label(self.root, text="📜 마우스 휠로 스크롤하여 모든 설정을 확인하세요", 
                              font=("Arial", 9), fg="blue", bg="lightyellow")
        scroll_info.pack(pady=2, fill="x", padx=5)
        
        # 스크롤 가능한 메인 영역
        self.scrollable_container = ScrollableFrame(self.root)
        self.scrollable_container.pack(fill=tk.BOTH, expand=True, padx=5, pady=5)
        
        # 실제 내용을 스크롤 가능한 프레임에 추가
        self.create_content(self.scrollable_container.scrollable_frame)
    
    def create_content(self, parent):
        """실제 내용 생성"""
        
        # 드래그 안내
        drag_info = tk.Label(parent, text="💡 파란색 제목을 드래그해서 창을 이동하세요", 
                            font=("Arial", 9), fg="gray")
        drag_info.pack(pady=5)
        
        # 상태 표시
        self.status_label = tk.Label(parent, text="⭕ 대기 중", 
                                   font=("Arial", 12), fg="red")
        self.status_label.pack(pady=5)
        
        # 개선 안내
        info_frame = ttk.LabelFrame(parent, text="🚀 최종 완성 버전!", padding=10)
        info_frame.pack(pady=10, padx=20, fill="x")
        
        info_text = """🛡️ 화면 캡처에서 완전 제외로 피드백 루프 방지
🖥️ 전체 화면 매끄러운 모자이크 표시
🖱️ 클릭 투과로 바탕화면 상호작용 가능
📌 Windows Hook으로 창 활성화 즉시 차단
⚡ 플리커링 없는 안정적인 검열 시스템
✅ 실시간 객체 감지 및 모자이크 적용"""
        
        info_label = tk.Label(info_frame, text=info_text, justify="left", 
                            wraplength=450, fg="green")
        info_label.pack()
        
        # 중요 안내
        warning_frame = ttk.LabelFrame(parent, text="⚠️ 중요 안내", padding=10)
        warning_frame.pack(pady=10, padx=20, fill="x")
        
        warning_text = """풀스크린 모드에서는 모든 화면이 덮어집니다.
ESC 키를 눌러 종료하거나, Ctrl+Alt+Del로 강제 종료하세요.
F1 키로 디버그 정보를 켜고 끌 수 있습니다."""
        
        warning_label = tk.Label(warning_frame, text=warning_text, justify="left", 
                               wraplength=450, fg="red")
        warning_label.pack()
        
        # 모자이크 대상 선택
        targets_frame = ttk.LabelFrame(parent, text="🎯 모자이크 대상 선택", padding=10)
        targets_frame.pack(pady=10, padx=20, fill="x")
        
        # 체크박스 변수들
        self.target_vars = {}
        available_targets = ["얼굴", "가슴", "겨드랑이", "보지", "발", "몸 전체", 
                           "자지", "팬티", "눈", "손", "교미", "신발", 
                           "가슴_옷", "보지_옷", "여성"]
        
        # 2열로 배치
        for i, target in enumerate(available_targets):
            var = tk.BooleanVar()
            if target in CONFIG.get("mosaic", {}).get("default_targets", []):
                var.set(True)
            
            self.target_vars[target] = var
            
            row = i // 2
            col = i % 2
            
            checkbox = ttk.Checkbutton(targets_frame, text=target, variable=var)
            checkbox.grid(row=row, column=col, sticky="w", padx=5, pady=2)
        
        # 모자이크 설정
        settings_frame = ttk.LabelFrame(parent, text="⚙️ 모자이크 설정", padding=10)
        settings_frame.pack(pady=10, padx=20, fill="x")
        
        # 모자이크 강도
        tk.Label(settings_frame, text="모자이크 강도:").grid(row=0, column=0, sticky="w")
        self.strength_var = tk.IntVar(value=CONFIG.get("mosaic", {}).get("default_strength", 15))
        strength_scale = ttk.Scale(settings_frame, from_=5, to=50, 
                                 variable=self.strength_var, orient="horizontal")
        strength_scale.grid(row=0, column=1, sticky="ew", padx=5)
        
        self.strength_label = tk.Label(settings_frame, text="15")
        self.strength_label.grid(row=0, column=2)
        
        strength_scale.configure(command=self.update_strength_label)
        settings_frame.columnconfigure(1, weight=1)
        
        # 신뢰도 임계값
        tk.Label(settings_frame, text="감지 신뢰도:").grid(row=1, column=0, sticky="w")
        self.confidence_var = tk.DoubleVar(value=CONFIG.get("mosaic", {}).get("conf_threshold", 0.1))
        confidence_scale = ttk.Scale(settings_frame, from_=0.1, to=0.9, 
                                   variable=self.confidence_var, orient="horizontal")
        confidence_scale.grid(row=1, column=1, sticky="ew", padx=5)
        
        self.confidence_label = tk.Label(settings_frame, text="0.1")
        self.confidence_label.grid(row=1, column=2)
        
        confidence_scale.configure(command=self.update_confidence_label)
        
        # 성능 설정
        tk.Label(settings_frame, text="FPS 제한:").grid(row=2, column=0, sticky="w")
        self.fps_var = tk.IntVar(value=30)
        fps_scale = ttk.Scale(settings_frame, from_=15, to=60, 
                            variable=self.fps_var, orient="horizontal")
        fps_scale.grid(row=2, column=1, sticky="ew", padx=5)
        
        self.fps_label = tk.Label(settings_frame, text="30")
        self.fps_label.grid(row=2, column=2)
        
        fps_scale.configure(command=self.update_fps_label)
        
        # 컨트롤 버튼 (중요!)
        control_frame = tk.Frame(parent, bg="lightgray", relief="raised", bd=3)
        control_frame.pack(pady=20, padx=20, fill="x")
        
        button_label = tk.Label(control_frame, text="🎮 메인 컨트롤", 
                               font=("Arial", 12, "bold"), bg="lightgray")
        button_label.pack(pady=5)
        
        inner_control_frame = tk.Frame(control_frame, bg="lightgray")
        inner_control_frame.pack(pady=10)
        
        self.start_button = tk.Button(inner_control_frame, text="🚀 풀스크린 시작", 
                                    command=self.start_censoring,
                                    bg="green", fg="white", font=("Arial", 12, "bold"),
                                    width=15, height=2)
        self.start_button.pack(side="left", padx=5)
        
        self.stop_button = tk.Button(inner_control_frame, text="🛑 검열 중지", 
                                   command=self.stop_censoring,
                                   bg="red", fg="white", font=("Arial", 12, "bold"),
                                   width=15, height=2, state="disabled")
        self.stop_button.pack(side="left", padx=5)
        
        # 통계 표시
        stats_frame = ttk.LabelFrame(parent, text="📊 실시간 통계", padding=10)
        stats_frame.pack(pady=10, padx=20, fill="x")
        
        self.stats_labels = {}
        stats_items = [
            ("처리된 프레임", "frames_processed"),
            ("감지된 객체", "objects_detected"),
            ("모자이크 적용", "mosaic_applied"),
            ("실행 시간", "runtime")
        ]
        
        for i, (name, key) in enumerate(stats_items):
            tk.Label(stats_frame, text=f"{name}:").grid(row=i, column=0, sticky="w")
            label = tk.Label(stats_frame, text="0", font=("Arial", 10, "bold"))
            label.grid(row=i, column=1, sticky="e")
            self.stats_labels[key] = label
        
        # 로그 표시
        log_frame = ttk.LabelFrame(parent, text="📝 실시간 로그", padding=10)
        log_frame.pack(pady=10, padx=20, fill="x")
        
        # 텍스트 위젯과 스크롤바
        text_frame = tk.Frame(log_frame)
        text_frame.pack(fill="x")
        
        self.log_text = tk.Text(text_frame, height=4, wrap="word")  # 높이 줄임
        log_scrollbar = ttk.Scrollbar(text_frame, orient="vertical", command=self.log_text.yview)
        self.log_text.configure(yscrollcommand=log_scrollbar.set)
        
        self.log_text.pack(side="left", fill="x", expand=True)
        log_scrollbar.pack(side="right", fill="y")
        
        # 디버그 설정
        debug_frame = ttk.LabelFrame(parent, text="🐛 디버그 옵션", padding=10)
        debug_frame.pack(pady=10, padx=20, fill="x")
        
        self.debug_var = tk.BooleanVar()
        debug_check = ttk.Checkbutton(debug_frame, text="🐛 디버그 모드", 
                                    variable=self.debug_var)
        debug_check.pack(side="left", padx=5)
        
        self.show_debug_info_var = tk.BooleanVar(value=False)
        debug_info_check = ttk.Checkbutton(debug_frame, text="🔍 풀스크린 디버그 정보", 
                                         variable=self.show_debug_info_var)
        debug_info_check.pack(side="left", padx=5)
        
        # 스크롤 테스트 확인
        test_frame = ttk.LabelFrame(parent, text="✅ 스크롤 테스트", padding=10)
        test_frame.pack(pady=10, padx=20, fill="x")
        
        test_label = tk.Label(test_frame, text="여기까지 스크롤이 되었다면 성공! 위로 올라가서 버튼을 클릭하세요.", 
                             fg="green", font=("Arial", 10, "bold"))
        test_label.pack()
        
        # 마지막 여백
        spacer = tk.Frame(parent, height=30)
        spacer.pack()
    
    def update_strength_label(self, value):
        """모자이크 강도 라벨 업데이트"""
        self.strength_label.config(text=str(int(float(value))))
    
    def update_confidence_label(self, value):
        """신뢰도 라벨 업데이트"""
        self.confidence_label.config(text=f"{float(value):.2f}")
    
    def update_fps_label(self, value):
        """FPS 라벨 업데이트"""
        self.fps_label.config(text=str(int(float(value))))
    
    def log_message(self, message):
        """로그 메시지 출력"""
        timestamp = datetime.now().strftime("%H:%M:%S")
        full_message = f"[{timestamp}] {message}"
        
        # GUI 로그
        self.log_text.insert("end", full_message + "\n")
        self.log_text.see("end")
        
        # 콘솔 로그
        print(full_message)
    
    def update_stats(self):
        """통계 업데이트"""
        if self.stats['start_time']:
            runtime = int(time.time() - self.stats['start_time'])
            minutes, seconds = divmod(runtime, 60)
            self.stats_labels['runtime'].config(text=f"{minutes:02d}:{seconds:02d}")
        
        self.stats_labels['frames_processed'].config(text=str(self.stats['frames_processed']))
        self.stats_labels['objects_detected'].config(text=str(self.stats['objects_detected']))
        self.stats_labels['mosaic_applied'].config(text=str(self.stats['mosaic_applied']))
    
    def start_censoring(self):
        """풀스크린 검열 시작"""
        if self.is_running:
            return
        
        # 선택된 타겟 확인
        selected_targets = [target for target, var in self.target_vars.items() if var.get()]
        
        if not selected_targets:
            messagebox.showwarning("경고", "최소 하나의 모자이크 대상을 선택해주세요!")
            return
        
        # 최종 확인
        result = messagebox.askyesno(
            "화면 검열 시작 확인", 
            "화면 검열 시스템을 시작하시겠습니까?\n\n"
            "• 전체 화면에 모자이크가 적용됩니다\n"
            "• 바탕화면을 자유롭게 사용할 수 있습니다\n"
            "• ESC 키로 언제든 종료할 수 있습니다\n\n"
            "계속하시겠습니까?"
        )
        
        if not result:
            return
        
        # 설정 적용
        self.processor.set_targets(selected_targets)
        self.processor.set_strength(self.strength_var.get())
        self.processor.conf_threshold = self.confidence_var.get()
        self.debug_mode = self.debug_var.get()
        
        # 오버레이 설정
        self.overlay.show_debug_info = self.show_debug_info_var.get()
        self.overlay.set_fps_limit(self.fps_var.get())
        
        # 상태 변경
        self.is_running = True
        self.stats['start_time'] = time.time()
        for key in self.stats:
            if key != 'start_time':
                self.stats[key] = 0
        
        # GUI 업데이트
        self.status_label.config(text="✅ 풀스크린 검열 중", fg="green")
        self.start_button.config(state="disabled")
        self.stop_button.config(state="normal")
        
        # 풀스크린 오버레이 표시
        if not self.overlay.show():
            self.log_message("❌ 풀스크린 오버레이 시작 실패")
            self.stop_censoring()
            return
        
        # 처리 스레드 시작
        self.process_thread = threading.Thread(target=self.processing_loop, daemon=True)
        self.process_thread.start()
        
        # 로그 메시지
        self.log_message(f"🚀 화면 검열 시작! 대상: {', '.join(selected_targets)}")
        self.log_message(f"⚙️ 설정: 강도={self.strength_var.get()}, 신뢰도={self.confidence_var.get():.2f}, FPS={self.fps_var.get()}")
        
        # pywin32 설치 확인 및 기능 테스트
        try:
            import win32gui
            self.log_message("✅ Windows 기능 활성화됨")
            
            # 캡처 방지 및 클릭 투과 기능 테스트
            def test_window_features():
                time.sleep(3)  # 오버레이 초기화 대기
                
                # 캡처 방지 테스트
                if self.overlay.test_capture_protection():
                    self.log_message("🛡️ 캡처 방지 기능 활성화됨")
                # else:
                #     self.log_message("⚠️ 캡처 방지 테스트 실패 (Windows 10+ 에서만 지원)")
                
                # 클릭 투과 테스트 (더 자세히)
                # self.log_message("🔍 클릭 투과 기능 상세 테스트 시작...")
                if self.overlay.test_click_through():
                    # self.log_message("✅ 클릭 투과 스타일 확인 성공!")
                    
                    # 추가 테스트
                    if hasattr(self.overlay, 'test_click_through_immediately'):
                        if self.overlay.test_click_through_immediately():
                            self.log_message("🖱️ 클릭 투과 기능 활성화됨")
                            self.log_message("💡 바탕화면을 자유롭게 사용할 수 있습니다")
                            # self.log_message("🛡️ Windows Hook으로 창 활성화 시도를 즉시 차단합니다!")
                            # self.log_message("📌 어떤 클릭을 해도 pygame 창이 절대 깜빡이지 않습니다!")
                            # self.log_message("⚡ 0% 플리커링 보장: 모자이크가 순간도 풀리지 않습니다!")
                            # self.log_message("🎯 실제 테스트: 바탕화면 파일을 클릭해보세요!")
                        # else:
                        #     self.log_message("⚠️ 클릭 투과 즉시 테스트 실패")
                    # else:
                    #     self.log_message("💡 이제 바탕화면을 자유롭게 클릭/드래그할 수 있습니다!")
                # else:
                #     self.log_message("⚠️ 클릭 투과 테스트 실패 - 바탕화면 클릭이 제한될 수 있습니다")
            
            # 테스트를 별도 스레드에서 실행
            test_thread = threading.Thread(target=test_window_features, daemon=True)
            test_thread.start()
            
        except ImportError:
            self.log_message("⚠️ pywin32가 없어 일부 기능이 제한됩니다. pip install pywin32로 설치하세요")
    
    def stop_censoring(self):
        """풀스크린 검열 중지"""
        if not self.is_running:
            return
        
        self.log_message("🛑 화면 검열 중지 중...")
        
        self.is_running = False
        
        # 오버레이 숨기기
        self.overlay.hide()
        
        # 스레드 종료 대기
        if self.process_thread and self.process_thread.is_alive():
            self.process_thread.join(timeout=1.0)
        
        # GUI 업데이트
        self.status_label.config(text="⭕ 대기 중", fg="red")
        self.start_button.config(state="normal")
        self.stop_button.config(state="disabled")
        
        self.log_message("✅ 화면 검열 중지됨")
        
        # 최종 통계 (간소화)
        # runtime = int(time.time() - self.stats['start_time']) if self.stats['start_time'] else 0
        # if runtime > 0:
        #     fps = self.stats['frames_processed'] / runtime
        #     self.log_message(f"📊 최종 통계: {runtime}초, {self.stats['frames_processed']}프레임, "
        #                    f"평균 {fps:.1f}FPS")
    
    def processing_loop(self):
        """메인 처리 루프 - 전체 화면 모자이크 처리"""
        # self.log_message("🔄 전체 화면 모자이크 처리 루프 시작")
        frame_count = 0
        
        try:
            while self.is_running:
                # **원본 화면 캡처 (캡처 방지로 오버레이 영향 없음)**
                original_frame = self.capturer.get_frame()
                if original_frame is None:
                    time.sleep(0.01)
                    continue
                
                frame_count += 1
                self.stats['frames_processed'] = frame_count
                
                # **전체 화면 복사 (모자이크 처리용)**
                processed_frame = original_frame.copy()
                
                # **객체 감지는 원본 프레임에서 수행**
                detections = self.processor.detect_objects(original_frame)
                
                # **모자이크 적용**
                if detections is not None and len(detections) > 0:
                    for detection in detections:
                        class_name = detection['class_name']
                        confidence = detection['confidence']
                        bbox = detection['bbox']
                        x1, y1, x2, y2 = bbox
                        
                        self.stats['objects_detected'] += 1
                        
                        # 타겟인지 확인
                        if class_name in self.processor.targets:
                            self.stats['mosaic_applied'] += 1
                            
                            # **전체 화면에서 해당 영역에 모자이크 적용**
                            region = processed_frame[y1:y2, x1:x2]
                            if region.size > 0:
                                mosaic_region = self.processor.apply_mosaic(region, self.strength_var.get())
                                processed_frame[y1:y2, x1:x2] = mosaic_region
                                
                                # 너무 많은 로그 출력 방지 - 간헐적으로만 출력
                                if frame_count % 30 == 0:  # 30프레임마다 한 번씩만
                                    self.log_message(f"🎯 모자이크 적용: {class_name}")
                            # else:
                            #     self.log_message(f"⚠️ [ERROR] 빈 영역: {class_name}")
                        # else:
                        #     self.log_message(f"📌 [DETECT] {class_name} ({confidence:.3f}) - 타겟 아님")
                
                # **풀스크린에 전체 처리된 화면 표시**
                self.overlay.update_frame(processed_frame)
                
                # 디버그 이미지 저장
                if self.debug_mode and self.stats['mosaic_applied'] > 0:
                    timestamp = datetime.now().strftime("%Y%m%d_%H%M%S_%f")[:-3]
                    
                    # 원본과 처리된 이미지 저장
                    original_path = f"debug_detection/original_{timestamp}.jpg"
                    processed_path = f"debug_detection/processed_{timestamp}.jpg"
                    
                    cv2.imwrite(original_path, original_frame)
                    cv2.imwrite(processed_path, processed_frame)
                    
                    # self.log_message(f"디버그 저장: {processed_path}")
                
                # 통계 업데이트 (매 30프레임마다)
                if frame_count % 30 == 0:
                    self.root.after(0, self.update_stats)
                
                # 오버레이가 종료되었는지 확인 (ESC 키 등으로)
                if not self.overlay.is_window_visible():
                    # self.log_message("🔑 풀스크린이 종료되었습니다")
                    self.is_running = False
                    break
                
                # FPS 제한
                time.sleep(1.0 / self.fps_var.get())  # 동적 FPS 제한
        
        except Exception as e:
            self.log_message(f"❌ 처리 오류: {e}")
            # import traceback
            # traceback.print_exc()
        
        finally:
            # self.log_message("🛑 전체 화면 모자이크 처리 루프 종료")
            # 메인 스레드에서 정리 작업 수행
            self.root.after(0, self.stop_censoring)
    
    def run(self):
        """애플리케이션 실행"""
        print("🛡️ 화면 검열 시스템 시작")
        print("="*40)
        
        try:
            self.root.protocol("WM_DELETE_WINDOW", self.on_closing)
            self.root.mainloop()
        except KeyboardInterrupt:
            print("\n🛑 키보드 인터럽트")
        finally:
            self.cleanup()
    
    def on_closing(self):
        """윈도우 닫기 이벤트"""
        if self.is_running:
            self.stop_censoring()
        
        self.cleanup()
        self.root.destroy()
    
    def cleanup(self):
        """리소스 정리"""
        # print("🧹 리소스 정리 중...")
        
        if self.is_running:
            self.is_running = False
        
        if self.process_thread and self.process_thread.is_alive():
            self.process_thread.join(timeout=1.0)
        
        self.overlay.hide()
        self.capturer.stop_capture_thread()
        
        # print("✅ 리소스 정리 완료")

def main():
    """메인 함수"""
    import sys
    
    # 명령행 인수 처리
    debug_mode = "--debug" in sys.argv
    
    if debug_mode:
        print("🐛 디버그 모드 활성화")
        os.makedirs("debug_detection", exist_ok=True)
    
    # 애플리케이션 생성 및 실행
    app = MosaicApp()
    if debug_mode:
        app.debug_var.set(True)
    
    app.run()

if __name__ == "__main__":
    main()