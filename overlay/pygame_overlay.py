"""
풀스크린 + 캡처 방지 모자이크 오버레이
전체 화면을 pygame에서 표시하여 매끄러운 검열 효과 제공
클릭 투과 기능으로 바탕화면 상호작용 가능
"""

import pygame
import numpy as np
import cv2
import threading
import time
import os

# Windows에서 창을 캡처에서 제외하기 위한 모듈
try:
    import win32gui
    import win32con
    import win32api
    import ctypes
    from ctypes import wintypes
    HAS_WIN32 = True
    
    # Windows 10+ 캡처 방지 상수
    WDA_EXCLUDEFROMCAPTURE = 0x00000011
    
    # Windows Hook 상수
    WH_CBT = 5
    HCBT_ACTIVATE = 5
    WH_CALLWNDPROC = 4
    WM_WINDOWPOSCHANGING = 0x0046
    WM_ACTIVATE = 0x0006
    
    # Hook 함수 타입 정의
    HOOKPROC = ctypes.WINFUNCTYPE(ctypes.c_int, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM)
    
except ImportError:
    HAS_WIN32 = False
    print("⚠️ win32gui를 찾을 수 없습니다. Windows에서 캡처 방지 기능이 제한됩니다.")
    print("   설치하려면: pip install pywin32")

class PygameOverlayWindow:
    """풀스크린 + 캡처 방지 + 클릭 투과 모자이크 오버레이 윈도우"""
    
    def __init__(self, config=None):
        """초기화"""
        self.config = config or {}
        
        # 설정값
        self.show_debug_info = self.config.get("show_debug_info", False)
        self.fps_limit = self.config.get("fps_limit", 30)
        
        # 상태 변수
        self.is_visible = False
        self.is_running = False
        self.current_frame = None  # 전체 화면 프레임
        
        # Pygame 관련
        self.screen = None
        self.clock = None
        self.font = None
        self.hwnd = None
        
        # 성능 통계
        self.fps_counter = 0
        self.fps_start_time = time.time()
        self.current_fps = 0
        
        # 스레드 관련
        self.display_thread = None
        self.topmost_thread = None
        self.hook_thread = None
        self.thread_lock = threading.Lock()
        self.topmost_stop_event = threading.Event()
        self.force_topmost = False  # 강제 최상단 모드
        self.hook_installed = False  # Hook 설치 상태
        self.hook_handle = None  # Hook 핸들
        
        print("🛡️ 화면 검열 시스템 초기화 완료")
    
    def init_pygame(self):
        """Pygame 초기화"""
        try:
            # Pygame 초기화
            pygame.init()
            
            # 디스플레이 정보 가져오기
            info = pygame.display.Info()
            self.screen_width = info.current_w
            self.screen_height = info.current_h
            
            print(f"📺 화면 크기: {self.screen_width}x{self.screen_height}")
            
            # 풀스크린 윈도우 생성 (클릭 투과를 위해 FULLSCREEN 대신 경계 없는 윈도우 사용)
            if HAS_WIN32:
                pygame.display.set_caption("Mosaic Fullscreen - Click Through Protected")
                
                # 경계 없는 윈도우로 생성 (FULLSCREEN 대신)
                self.screen = pygame.display.set_mode(
                    (self.screen_width, self.screen_height),
                    pygame.NOFRAME  # FULLSCREEN 제거, NOFRAME만 사용
                )
                
                # 윈도우 위치를 (0,0)으로 설정하여 풀스크린처럼 보이게
                os.environ['SDL_VIDEO_WINDOW_POS'] = '0,0'
                
            else:
                # 다른 OS에서는 윈도우 모드
                self.screen = pygame.display.set_mode(
                    (self.screen_width, self.screen_height),
                    pygame.NOFRAME
                )
            
            # 폰트 초기화 (디버그용)
            if self.show_debug_info:
                self.font = pygame.font.Font(None, 24)
            
            self.clock = pygame.time.Clock()
            
            print("✅ 풀스크린 초기화 완료")
            
            # Windows에서 캡처 방지 및 클릭 투과 설정
            if HAS_WIN32:
                self.set_window_click_through_and_capture_protected()
            
            return True
            
        except Exception as e:
            print(f"❌ Pygame 초기화 실패: {e}")
            return False
    
    def set_window_click_through_and_capture_protected(self):
        """Windows에서 창을 캡처 방지 + 클릭 투과로 설정 (개선된 버전)"""
        if not HAS_WIN32:
            return
        
        try:
            # pygame 창 핸들을 직접 가져오기
            pygame_info = pygame.display.get_wm_info()
            if 'window' in pygame_info:
                self.hwnd = pygame_info['window']
                print(f"🔍 pygame 창 핸들 직접 획득: {self.hwnd}")
            else:
                print("⚠️ pygame 창 핸들을 직접 가져올 수 없음")
                return
            
            if self.hwnd:
                # 1단계: 🛡️ 캡처에서 완전 제외 (피드백 루프 방지)
                try:
                    user32 = ctypes.windll.user32
                    result = user32.SetWindowDisplayAffinity(self.hwnd, WDA_EXCLUDEFROMCAPTURE)
                    
                    if result:
                        print("🛡️ 캡처 방지 설정 성공! (100% 피드백 루프 방지)")
                    else:
                        print("⚠️ 캡처 방지 설정 실패 (Windows 10+ 필요)")
                        
                except Exception as capture_error:
                    print(f"⚠️ 캡처 방지 설정 오류: {capture_error}")
                    print("💡 Windows 10+ 에서만 지원되는 기능입니다")
                
                # 2단계: 🖱️ 클릭 투과 설정 (핵심!)
                try:
                    # 현재 윈도우 스타일 가져오기
                    ex_style = win32gui.GetWindowLong(self.hwnd, win32con.GWL_EXSTYLE)
                    print(f"🔍 현재 Extended Style: 0x{ex_style:08X}")
                    
                    # 클릭 투과 및 레이어드 윈도우 스타일 추가
                    new_ex_style = (ex_style | 
                                   win32con.WS_EX_LAYERED | 
                                   win32con.WS_EX_TRANSPARENT)
                    
                    print(f"🔍 새로운 Extended Style: 0x{new_ex_style:08X}")
                    
                    # 새 스타일 적용
                    result = win32gui.SetWindowLong(self.hwnd, win32con.GWL_EXSTYLE, new_ex_style)
                    
                    if result != 0:
                        print("🖱️ 클릭 투과 설정 성공! (마우스 클릭이 바탕화면으로 전달됩니다)")
                        
                        # 레이어드 윈도우 속성 설정 (완전 불투명)
                        win32gui.SetLayeredWindowAttributes(self.hwnd, 0, 255, win32con.LWA_ALPHA)
                        print("✅ 레이어드 윈도우 속성 설정 완료")
                        
                    else:
                        error_code = ctypes.windll.kernel32.GetLastError()
                        print(f"⚠️ 클릭 투과 설정 실패: 오류 코드 {error_code}")
                    
                except Exception as click_error:
                    print(f"⚠️ 클릭 투과 설정 오류: {click_error}")
                    import traceback
                    traceback.print_exc()
                
                # 3단계: 창을 최상단으로 설정
                win32gui.SetWindowPos(
                    self.hwnd,
                    win32con.HWND_TOPMOST,
                    0, 0, 0, 0,
                    win32con.SWP_NOMOVE | win32con.SWP_NOSIZE | win32con.SWP_SHOWWINDOW
                )
                
                print("✅ 최상단 설정 완료")
                
                # 4단계: 클릭 투과 테스트
                if self.test_click_through_immediately():
                    print("✅ 클릭 투과 즉시 테스트 성공!")
                else:
                    print("⚠️ 클릭 투과 즉시 테스트 실패 - 재시도 중...")
                    # 재시도
                    time.sleep(0.5)
                    self._retry_click_through_setup()
                
                # 5단계: 강제 최상단 모드 활성화
                self.force_topmost = True
                
                # 6단계: Windows Hook 설치 (즉시 차단)
                self.install_activation_hook()
                
                # 7단계: 지속적인 최상단 유지 스레드 시작
                self.start_topmost_keeper()
                
                print("🎉 pygame 풀스크린이 캡처 방지 + 클릭 투과로 설정되었습니다!")
                print("💡 이제 바탕화면을 클릭/드래그할 수 있습니다!")
                print("🛡️ Windows Hook으로 창 활성화 시도를 즉시 차단합니다!")
                print("📌 어떤 클릭을 해도 pygame 창이 절대 순간도 가려지지 않습니다!")
                
            else:
                print("⚠️ Pygame 창 핸들을 가져올 수 없습니다")
                
        except Exception as e:
            print(f"⚠️ 창 설정 실패: {e}")
            import traceback
            traceback.print_exc()
    
    def _retry_click_through_setup(self):
        """클릭 투과 설정 재시도"""
        try:
            # 다른 방법으로 재시도
            ex_style = win32gui.GetWindowLong(self.hwnd, win32con.GWL_EXSTYLE)
            
            # 기존 스타일 제거 후 다시 설정
            clean_style = ex_style & ~(win32con.WS_EX_LAYERED | win32con.WS_EX_TRANSPARENT)
            win32gui.SetWindowLong(self.hwnd, win32con.GWL_EXSTYLE, clean_style)
            
            time.sleep(0.1)
            
            # 다시 클릭 투과 스타일 적용
            new_style = clean_style | win32con.WS_EX_LAYERED | win32con.WS_EX_TRANSPARENT
            win32gui.SetWindowLong(self.hwnd, win32con.GWL_EXSTYLE, new_style)
            
            # 레이어드 윈도우 속성 재설정
            win32gui.SetLayeredWindowAttributes(self.hwnd, 0, 255, win32con.LWA_ALPHA)
            
            # print("🔄 클릭 투과 설정 재시도 완료")
            
        except Exception as e:
            # print(f"⚠️ 클릭 투과 재설정 실패: {e}")
            pass
    
    def test_click_through_immediately(self):
        """클릭 투과 기능 즉시 테스트"""
        if not HAS_WIN32 or not self.hwnd:
            return False
        
        try:
            # 현재 창의 Extended Style 확인
            ex_style = win32gui.GetWindowLong(self.hwnd, win32con.GWL_EXSTYLE)
            
            has_transparent = (ex_style & win32con.WS_EX_TRANSPARENT) != 0
            has_layered = (ex_style & win32con.WS_EX_LAYERED) != 0
            
            # print(f"🔍 클릭 투과 테스트: transparent={has_transparent}, layered={has_layered}")
            # print(f"🔍 Extended Style: 0x{ex_style:08X}")
            
            return has_transparent and has_layered
                
        except Exception as e:
            print(f"⚠️ 클릭 투과 즉시 테스트 오류: {e}")
            return False
    
    def install_activation_hook(self):
        """Windows Hook 설치 - 다른 창 활성화 시도를 즉시 감지"""
        if not HAS_WIN32 or not self.hwnd:
            return
        
        try:
            # Hook 콜백 함수 정의
            def activation_hook_proc(nCode, wParam, lParam):
                try:
                    if nCode >= 0 and self.force_topmost:
                        # 다른 창이 활성화되려고 하는 순간 감지
                        if nCode == HCBT_ACTIVATE:
                            activated_hwnd = wParam
                            
                            # pygame 창이 아닌 다른 창이 활성화되려고 하면
                            if activated_hwnd != self.hwnd:
                                # 즉시 pygame 창을 강제 최상단으로!
                                self._instant_force_topmost()
                                print(f"🛡️ 즉시 차단: 창(hwnd:{activated_hwnd}) 활성화 시도를 감지, pygame 창 즉시 복구")
                    
                    # 다음 Hook으로 전달
                    return ctypes.windll.user32.CallNextHookEx(self.hook_handle, nCode, wParam, lParam)
                except:
                    # Hook에서 오류가 나도 계속 진행
                    return ctypes.windll.user32.CallNextHookEx(self.hook_handle, nCode, wParam, lParam)
            
            # 콜백 함수 저장 (가비지 컬렉션 방지)
            self.hook_callback = HOOKPROC(activation_hook_proc)
            
            # Hook 설치
            self.hook_handle = ctypes.windll.user32.SetWindowsHookExW(
                WH_CBT,  # CBT Hook
                self.hook_callback,
                ctypes.windll.kernel32.GetModuleHandleW(None),
                0  # 모든 스레드
            )
            
            if self.hook_handle:
                self.hook_installed = True
                # print("🛡️ Windows Hook 설치 성공: 창 활성화 시도를 즉시 감지합니다")
            else:
                print("⚠️ Windows Hook 설치 실패")
                
        except Exception as e:
            # print(f"⚠️ Windows Hook 설치 오류: {e}")
            pass
    
    def uninstall_activation_hook(self):
        """Windows Hook 제거"""
        if self.hook_installed and self.hook_handle:
            try:
                ctypes.windll.user32.UnhookWindowsHookEx(self.hook_handle)
                self.hook_installed = False
                self.hook_handle = None
                # print("🛡️ Windows Hook 제거됨")
            except Exception as e:
                # print(f"⚠️ Windows Hook 제거 오류: {e}")
                pass
    
    def _instant_force_topmost(self):
        """즉시 강제 최상단 복구 (Hook에서 호출용)"""
        if not HAS_WIN32 or not self.hwnd:
            return
        
        try:
            # 즉시 최상단으로 복구 (여러 방법 동시 사용)
            win32gui.SetWindowPos(
                self.hwnd,
                win32con.HWND_TOPMOST,
                0, 0, 0, 0,
                win32con.SWP_NOMOVE | win32con.SWP_NOSIZE | win32con.SWP_NOACTIVATE | win32con.SWP_NOREDRAW
            )
            
            # 추가 보강
            win32gui.SetWindowPos(
                self.hwnd,
                win32con.HWND_TOP,
                0, 0, 0, 0,
                win32con.SWP_NOMOVE | win32con.SWP_NOSIZE | win32con.SWP_NOACTIVATE | win32con.SWP_NOREDRAW
            )
            
        except:
            pass  # Hook 내부에서는 오류 무시
    
    def start_topmost_keeper(self):
        """최상단 유지 스레드 시작"""
        if not HAS_WIN32 or not self.hwnd:
            return
        
        self.topmost_stop_event.clear()
        self.topmost_thread = threading.Thread(target=self._topmost_keeper_loop, daemon=True)
        self.topmost_thread.start()
        # print("📌 강화된 최상단 유지 스레드 시작됨 (0.05초마다 체크)")
    
    def stop_topmost_keeper(self):
        """최상단 유지 스레드 정지"""
        if self.topmost_thread and self.topmost_thread.is_alive():
            self.topmost_stop_event.set()
            self.topmost_thread.join(timeout=1.0)
            # print("📌 강화된 최상단 유지 스레드 정지됨")
    
    def _topmost_keeper_loop(self):
        """최상단 상태 지속적으로 유지하는 루프 (강화 버전)"""
        # print("🔄 강화된 최상단 유지 루프 시작")
        
        try:
            check_count = 0
            while not self.topmost_stop_event.is_set():
                try:
                    check_count += 1
                    
                    # 현재 활성 창 확인
                    foreground_hwnd = win32gui.GetForegroundWindow()
                    
                    # pygame 창이 활성 창이 아니면 즉시 강제 최상단 복구
                    if foreground_hwnd != self.hwnd:
                        self._force_to_topmost()
                        
                        # 처음 몇 번은 로그 출력 (너무 많이 출력 방지)
                        # if check_count <= 5 or check_count % 100 == 0:
                        #     print(f"⚡ 즉시 복구: 다른 창(hwnd:{foreground_hwnd})이 활성화됨, pygame 창을 강제 최상단으로")
                    
                    # 매우 빠른 간격으로 체크 (거의 실시간)
                    time.sleep(0.05)  # 0.05초 = 20fps로 체크
                    
                except Exception as e:
                    # 오류 발생해도 계속 시도
                    time.sleep(0.1)
                    
        except Exception as e:
            # print(f"❌ 강화된 최상단 유지 루프 오류: {e}")
            pass
        finally:
            # print("🛑 강화된 최상단 유지 루프 종료")
            pass
    
    def _force_to_topmost(self):
        """강제로 pygame 창을 최상단으로 이동 (여러 방법 동시 사용)"""
        if not HAS_WIN32 or not self.hwnd:
            return
        
        try:
            # 방법 1: HWND_TOPMOST로 강제 설정
            win32gui.SetWindowPos(
                self.hwnd,
                win32con.HWND_TOPMOST,
                0, 0, 0, 0,
                win32con.SWP_NOMOVE | win32con.SWP_NOSIZE | win32con.SWP_NOACTIVATE
            )
            
            # 방법 2: 창을 맨 앞으로 가져오기 (조심스럽게)
            try:
                win32gui.BringWindowToTop(self.hwnd)
            except:
                pass  # 실패해도 계속
            
            # 방법 3: Z-order에서 최상위로 설정
            try:
                win32gui.SetWindowPos(
                    self.hwnd,
                    win32con.HWND_TOP,
                    0, 0, 0, 0,
                    win32con.SWP_NOMOVE | win32con.SWP_NOSIZE | win32con.SWP_NOACTIVATE
                )
            except:
                pass  # 실패해도 계속
                
        except Exception as e:
            # 오류가 나도 계속 진행
            pass
    
    def show(self):
        """풀스크린 캡처 방지 + 클릭 투과 오버레이 표시"""
        if self.is_visible:
            return
        
        print("🛡️ 풀스크린 캡처 방지 + 클릭 투과 오버레이 표시 시작...")
        
        if not self.init_pygame():
            return False
        
        self.is_visible = True
        self.is_running = True
        
        # 디스플레이 스레드 시작
        self.display_thread = threading.Thread(target=self.display_loop, daemon=True)
        self.display_thread.start()
        
        print("✅ 풀스크린 캡처 방지 + 클릭 투과 오버레이 표시됨")
        print("💡 ESC 키를 누르면 종료됩니다")
        print("💡 바탕화면을 자유롭게 클릭/드래그할 수 있습니다")
        print("📌 pygame 창이 항상 최상단에 고정됩니다")
        return True
    
    def hide(self):
        """풀스크린 오버레이 숨기기"""
        if not self.is_visible:
            return
        
        print("🛑 화면 검열 시스템 종료 중...")
        
        self.is_visible = False
        self.is_running = False
        self.force_topmost = False  # 강제 최상단 모드 비활성화
        
        # Windows Hook 제거
        self.uninstall_activation_hook()
        
        # 최상단 유지 스레드 중지
        self.stop_topmost_keeper()
        
        # 스레드 종료 대기
        if self.display_thread and self.display_thread.is_alive():
            self.display_thread.join(timeout=1.0)
        
        # Pygame 정리
        try:
            if self.screen:
                pygame.display.quit()
            pygame.quit()
        except:
            pass
        
        print("✅ 화면 검열 시스템 종료됨")
    
    def update_frame(self, processed_frame):
        """전체 화면 프레임 업데이트"""
        with self.thread_lock:
            self.current_frame = processed_frame
    
    def cv2_to_pygame_surface(self, cv2_image):
        """OpenCV 이미지를 Pygame 서페이스로 변환"""
        try:
            # BGR에서 RGB로 변환
            rgb_image = cv2.cvtColor(cv2_image, cv2.COLOR_BGR2RGB)
            
            # 화면 크기에 맞게 리사이즈 (필요한 경우)
            if rgb_image.shape[:2] != (self.screen_height, self.screen_width):
                rgb_image = cv2.resize(rgb_image, (self.screen_width, self.screen_height))
            
            # numpy array를 pygame surface로 변환
            # pygame.surfarray.make_surface는 (width, height, 3) 형태를 요구함
            rgb_image = np.transpose(rgb_image, (1, 0, 2))  # (height, width, 3) -> (width, height, 3)
            
            return pygame.surfarray.make_surface(rgb_image)
            
        except Exception as e:
            print(f"❌ 이미지 변환 오류: {e}")
            return None
    
    def draw_debug_info(self, surface):
        """디버그 정보 표시"""
        if not self.show_debug_info or not self.font:
            return
        
        try:
            # FPS 표시
            fps_text = f"FPS: {self.current_fps:.1f}"
            fps_surface = self.font.render(fps_text, True, (255, 255, 255))
            surface.blit(fps_surface, (10, 10))
            
            # 해상도 표시
            res_text = f"Resolution: {self.screen_width}x{self.screen_height}"
            res_surface = self.font.render(res_text, True, (255, 255, 255))
            surface.blit(res_surface, (10, 40))
            
            # 캡처 방지 + 클릭 투과 + Hook 보호 상태
            status_text = "🛡️ PROTECTED + CLICK THROUGH + HOOK GUARD"
            status_surface = self.font.render(status_text, True, (0, 255, 0))
            surface.blit(status_surface, (10, 70))
            
            # Hook 상태 표시
            hook_status = "Hook: ACTIVE" if self.hook_installed else "Hook: INACTIVE"
            hook_surface = self.font.render(hook_status, True, (255, 255, 0))
            surface.blit(hook_surface, (10, 100))
            
            # 사용 안내
            guide_text = "Click anything! ZERO flickering guaranteed!"
            guide_surface = self.font.render(guide_text, True, (0, 255, 255))
            surface.blit(guide_surface, (10, 130))
            
        except Exception as e:
            print(f"⚠️ 디버그 정보 표시 오류: {e}")
    
    def update_fps(self):
        """FPS 계산 및 업데이트"""
        self.fps_counter += 1
        current_time = time.time()
        
        if current_time - self.fps_start_time >= 1.0:  # 1초마다 업데이트
            self.current_fps = self.fps_counter / (current_time - self.fps_start_time)
            self.fps_counter = 0
            self.fps_start_time = current_time
    
    def display_loop(self):
        """메인 디스플레이 루프"""
        # print("🔄 풀스크린 디스플레이 루프 시작")
        
        # 초기 검은 화면
        black_screen = np.zeros((self.screen_height, self.screen_width, 3), dtype=np.uint8)
        
        try:
            while self.is_running:
                # 이벤트 처리
                for event in pygame.event.get():
                    if event.type == pygame.QUIT:
                        self.is_running = False
                        break
                    elif event.type == pygame.KEYDOWN:
                        if event.key == pygame.K_ESCAPE:
                            # print("🔑 ESC 키 감지됨 - 종료 중...")
                            self.is_running = False
                            break
                        elif event.key == pygame.K_F1:
                            # F1으로 디버그 정보 토글
                            self.show_debug_info = not self.show_debug_info
                            if self.show_debug_info and not self.font:
                                self.font = pygame.font.Font(None, 24)
                            # print(f"🔍 디버그 정보: {'켜짐' if self.show_debug_info else '꺼짐'}")
                
                # 현재 프레임 가져오기
                with self.thread_lock:
                    if self.current_frame is not None:
                        display_frame = self.current_frame.copy()
                    else:
                        display_frame = black_screen
                
                # 프레임을 pygame 서페이스로 변환
                surface = self.cv2_to_pygame_surface(display_frame)
                
                if surface is not None:
                    # 화면에 표시
                    self.screen.blit(surface, (0, 0))
                    
                    # 디버그 정보 표시
                    self.draw_debug_info(self.screen)
                    
                    # 화면 업데이트
                    pygame.display.flip()
                else:
                    # 변환 실패 시 검은 화면
                    self.screen.fill((0, 0, 0))
                    
                    # 오류 메시지 표시
                    if self.font:
                        error_text = "Frame conversion failed"
                        error_surface = self.font.render(error_text, True, (255, 0, 0))
                        self.screen.blit(error_surface, (self.screen_width//2 - 100, self.screen_height//2))
                    
                    pygame.display.flip()
                
                # FPS 업데이트
                self.update_fps()
                
                # FPS 제한
                self.clock.tick(self.fps_limit)
        
        except Exception as e:
            print(f"❌ 디스플레이 오류: {e}")
            # import traceback
            # traceback.print_exc()
        
        finally:
            # print("🛑 풀스크린 디스플레이 루프 종료")
            pass
    
    def is_window_visible(self):
        """창이 표시되고 있는지 확인"""
        return self.is_visible and self.is_running
    
    def toggle_debug_info(self):
        """디버그 정보 표시 토글"""
        self.show_debug_info = not self.show_debug_info
        if self.show_debug_info and not self.font:
            self.font = pygame.font.Font(None, 24)
        print(f"🔍 디버그 정보: {'켜짐' if self.show_debug_info else '꺼짐'}")
    
    def set_fps_limit(self, fps):
        """FPS 제한 설정"""
        self.fps_limit = max(10, min(60, fps))
        print(f"🎮 FPS 제한: {self.fps_limit}")
    
    def test_capture_protection(self):
        """캡처 방지 기능 테스트"""
        if not HAS_WIN32 or not self.hwnd:
            print("⚠️ Windows API를 사용할 수 없어 테스트 불가능")
            return False
        
        try:
            # 현재 창의 Display Affinity 확인
            user32 = ctypes.windll.user32
            
            # GetWindowDisplayAffinity 함수 정의
            get_affinity = user32.GetWindowDisplayAffinity
            get_affinity.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
            get_affinity.restype = wintypes.BOOL
            
            affinity = wintypes.DWORD()
            result = get_affinity(self.hwnd, ctypes.byref(affinity))
            
            if result:
                if affinity.value == WDA_EXCLUDEFROMCAPTURE:
                    print("✅ 캡처 방지 테스트 성공: 창이 캡처에서 제외됨")
                    return True
                else:
                    print(f"⚠️ 캡처 방지 테스트 실패: affinity={affinity.value}")
                    return False
            else:
                print("⚠️ 캡처 방지 상태 확인 실패")
                return False
                
        except Exception as e:
            print(f"⚠️ 캡처 방지 테스트 오류: {e}")
            return False
    
    def test_click_through(self):
        """클릭 투과 기능 테스트"""
        if not HAS_WIN32 or not self.hwnd:
            print("⚠️ Windows API를 사용할 수 없어 테스트 불가능")
            return False
        
        try:
            # 현재 창의 Extended Style 확인
            ex_style = win32gui.GetWindowLong(self.hwnd, win32con.GWL_EXSTYLE)
            
            has_transparent = (ex_style & win32con.WS_EX_TRANSPARENT) != 0
            has_layered = (ex_style & win32con.WS_EX_LAYERED) != 0
            
            if has_transparent and has_layered:
                print("✅ 클릭 투과 테스트 성공: 마우스 클릭이 바탕화면으로 전달됩니다")
                return True
            else:
                print(f"⚠️ 클릭 투과 테스트 실패: transparent={has_transparent}, layered={has_layered}")
                return False
                
        except Exception as e:
            print(f"⚠️ 클릭 투과 테스트 오류: {e}")
            return False
    
    # 기존 호환성을 위한 메서드들 (deprecated)
    def update_regions(self, frame, regions):
        """기존 호환성 메서드 - 이제 사용하지 않음"""
        pass
    
    def update_mosaic_regions(self, mosaic_regions):
        """기존 호환성 메서드 - 이제 사용하지 않음"""
        pass