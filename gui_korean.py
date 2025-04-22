# gui_korean.py
import ctypes
from ctypes import wintypes
import win32gui
import win32con
import win32api
import numpy as np

# 트랙바(슬라이더) 스타일 상수 정의
TBS_HORZ = 0x0000
TBS_AUTOTICKS = 0x0001
TBM_SETRANGE = 0x0406
TBM_SETPOS = 0x0405
TBM_GETPOS = 0x0400
BM_SETCHECK = 0x00F1
BM_GETCHECK = 0x00F0
BS_AUTOCHECKBOX = 0x0003
BS_GROUPBOX = 0x0007
BS_PUSHBUTTON = 0x0000

class MainWindow:
    def __init__(self):
        self.hwnd = None
        self.strength = 25
        self.targets = ["얼굴", "가슴", "보지", "팬티"]
        self.checkboxes = {}
        self.running = False
        
        # 윈도우 클래스 등록
        self.wc = self._register_window_class()
        
        # 윈도우 생성
        self._create_window()
        
        # 컨트롤 생성
        self._create_controls()
        
        # 시그널 핸들러
        self.start_callback = None
        self.stop_callback = None
    
    def _register_window_class(self):
        """윈도우 클래스 등록"""
        wc = win32gui.WNDCLASS()
        wc.lpfnWndProc = self._window_proc
        wc.lpszClassName = "MosaicControlWindow"
        wc.hInstance = win32api.GetModuleHandle(None)
        wc.hbrBackground = win32gui.GetStockObject(win32con.WHITE_BRUSH)
        wc.hCursor = win32gui.LoadCursor(0, win32con.IDC_ARROW)
        
        win32gui.RegisterClass(wc)
        return wc
    
    def _create_window(self):
        """메인 윈도우 생성"""
        self.hwnd = win32gui.CreateWindow(
            self.wc.lpszClassName,
            "실시간 화면 검열 시스템",
            win32con.WS_OVERLAPPEDWINDOW,
            win32con.CW_USEDEFAULT, win32con.CW_USEDEFAULT,
            400, 500,
            0, 0, self.wc.hInstance, None
        )
    
    def _create_controls(self):
        """컨트롤 생성"""
        # 모자이크 강도 라벨
        self.strength_label = win32gui.CreateWindow(
            "STATIC", "모자이크 강도: 25",
            win32con.WS_CHILD | win32con.WS_VISIBLE,
            20, 20, 150, 20,
            self.hwnd, 0, None, None
        )
        
        # 슬라이더 (트랙바)
        self.strength_slider = win32gui.CreateWindow(
            "msctls_trackbar32", "",
            win32con.WS_CHILD | win32con.WS_VISIBLE | TBS_HORZ | TBS_AUTOTICKS,
            20, 50, 350, 30,
            self.hwnd, 1001, None, None
        )
        win32gui.SendMessage(self.strength_slider, TBM_SETRANGE, 1, (50 << 16) | 5)
        win32gui.SendMessage(self.strength_slider, TBM_SETPOS, 1, self.strength)
        
        # 검열 대상 그룹박스
        self.group_box = win32gui.CreateWindow(
            "BUTTON", "검열 대상",
            win32con.WS_CHILD | win32con.WS_VISIBLE | BS_GROUPBOX,
            20, 100, 350, 250,
            self.hwnd, 0, None, None
        )
        
        # 체크박스들
        options = [
            "얼굴", "눈", "손", "가슴", "보지", "팬티",
            "겨드랑이", "자지", "몸 전체", "교미", "신발",
            "가슴_옷", "보지_옷", "여성"
        ]
        
        for i, option in enumerate(options):
            checkbox = win32gui.CreateWindow(
                "BUTTON", option,
                win32con.WS_CHILD | win32con.WS_VISIBLE | BS_AUTOCHECKBOX,
                40, 130 + i * 25, 150, 20,
                self.hwnd, 2000 + i, None, None
            )
            if option in self.targets:
                win32gui.SendMessage(checkbox, BM_SETCHECK, 1, 0)
            self.checkboxes[option] = checkbox
        
        # 시작/중지 버튼
        self.start_button = win32gui.CreateWindow(
            "BUTTON", "검열 시작",
            win32con.WS_CHILD | win32con.WS_VISIBLE | BS_PUSHBUTTON,
            70, 400, 100, 30,
            self.hwnd, 3001, None, None
        )
        
        self.stop_button = win32gui.CreateWindow(
            "BUTTON", "검열 중지",
            win32con.WS_CHILD | win32con.WS_VISIBLE | BS_PUSHBUTTON,
            230, 400, 100, 30,
            self.hwnd, 3002, None, None
        )
    
    def _window_proc(self, hwnd, msg, wparam, lparam):
        """윈도우 메시지 처리"""
        if msg == win32con.WM_COMMAND:
            control_id = win32api.LOWORD(wparam)
            
            # 시작 버튼
            if control_id == 3001:
                self._on_start_clicked()
            # 중지 버튼
            elif control_id == 3002:
                self._on_stop_clicked()
            # 체크박스
            elif 2000 <= control_id < 2100:
                self._on_checkbox_clicked(control_id - 2000)
        
        elif msg == win32con.WM_HSCROLL:
            # 슬라이더 값 변경
            if lparam == self.strength_slider:
                self.strength = win32gui.SendMessage(self.strength_slider, TBM_GETPOS, 0, 0)
                win32gui.SetWindowText(self.strength_label, f"모자이크 강도: {self.strength}")
        
        elif msg == win32con.WM_DESTROY:
            win32gui.PostQuitMessage(0)
            return 0
        
        return win32gui.DefWindowProc(hwnd, msg, wparam, lparam)
    
    def _on_start_clicked(self):
        """시작 버튼 클릭 처리"""
        self.targets = self.get_selected_targets()
        if self.start_callback:
            self.start_callback()
    
    def _on_stop_clicked(self):
        """중지 버튼 클릭 처리"""
        if self.stop_callback:
            self.stop_callback()
    
    def _on_checkbox_clicked(self, index):
        """체크박스 클릭 처리"""
        # 체크박스 상태 업데이트는 자동으로 됨
        pass
    
    def get_selected_targets(self):
        """선택된 검열 대상 반환"""
        selected = []
        for label, checkbox in self.checkboxes.items():
            if win32gui.SendMessage(checkbox, BM_GETCHECK, 0, 0):
                selected.append(label)
        return selected
    
    def get_strength(self):
        """모자이크 강도 반환"""
        return self.strength
    
    def set_start_callback(self, callback):
        """시작 콜백 설정"""
        self.start_callback = callback
    
    def set_stop_callback(self, callback):
        """중지 콜백 설정"""
        self.stop_callback = callback
    
    def show(self):
        """윈도우 표시"""
        win32gui.ShowWindow(self.hwnd, win32con.SW_SHOW)
        win32gui.UpdateWindow(self.hwnd)
    
    def run(self):
        """메시지 루프 실행"""
        msg = wintypes.MSG()
        while win32gui.GetMessage(ctypes.byref(msg), None, 0, 0):
            win32gui.TranslateMessage(ctypes.byref(msg))
            win32gui.DispatchMessage(ctypes.byref(msg))


# PyQt 호환성을 위한 시그널 에뮬레이션
class Signal:
    def __init__(self):
        self.callbacks = []
    
    def connect(self, callback):
        self.callbacks.append(callback)
    
    def emit(self):
        for callback in self.callbacks:
            callback()


# GUI 컨트롤러 클래스 (PyQt와 비슷한 인터페이스)
class GUIController(MainWindow):
    def __init__(self):
        super().__init__()
        self.start_censoring_signal = Signal()
        self.stop_censoring_signal = Signal()
        
        # 콜백 설정
        self.set_start_callback(self.start_censoring_signal.emit)
        self.set_stop_callback(self.stop_censoring_signal.emit)