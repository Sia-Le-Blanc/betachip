# opengl_overlay_window.py
import cv2
import numpy as np
import time
import os
import threading
import sys

try:
    import pygame
    from pygame.locals import *
    from OpenGL.GL import *
    from OpenGL.GLU import *
    HAS_OPENGL = True
except ImportError:
    HAS_OPENGL = False
    print("⚠️ PyOpenGL 또는 pygame 모듈이 설치되지 않았습니다.")
    print("pip install pygame PyOpenGL PyOpenGL_accelerate 명령으로 설치하세요.")

class OpenGLOverlayWindow:
    """OpenGL을 사용한 오버레이 윈도우"""
    def __init__(self):
        self.shown = False
        self.mosaic_regions = []
        self.original_image = None
        self.frame_count = 0
        self.debug_dir = "debug_overlay"
        os.makedirs(self.debug_dir, exist_ok=True)
        
        # 렌더링 스레드 관련
        self.render_thread = None
        self.stop_event = threading.Event()
        self.render_interval = 1/30  # 30fps
        
        # 화면 크기
        try:
            import win32api
            self.width = win32api.GetSystemMetrics(0)  # SM_CXSCREEN
            self.height = win32api.GetSystemMetrics(1)  # SM_CYSCREEN
        except:
            self.width = 1366  # 기본값
            self.height = 768  # 기본값
            
        # OpenGL 초기화 확인
        self.initialized = False
        if not HAS_OPENGL:
            print("❌ OpenGL 또는 Pygame 모듈이 없어 초기화가 불가능합니다.")
        else:
            print(f"✅ OpenGL 기반 오버레이 창 초기화 완료 (해상도: {self.width}x{self.height})")
            self.initialized = True
    
    def _render_thread_func(self):
        """렌더링 스레드 함수"""
        if not HAS_OPENGL:
            print("❌ OpenGL 모듈이 없어 렌더링이 불가능합니다.")
            return
            
        try:
            # Pygame 및 OpenGL 초기화
            pygame.init()
            pygame.display.set_caption("Mosaic Overlay")
            
            flags = DOUBLEBUF | OPENGL | NOFRAME  # 테두리 없는 창

            screen = pygame.display.set_mode((self.width, self.height), flags)
            
            try:
                import win32gui
                import win32con
                import win32api
                
                # 윈도우 핸들 가져오기
                HWND = pygame.display.get_wm_info()['window']
                
                # 윈도우 스타일 설정
                ex_style = win32gui.GetWindowLong(HWND, win32con.GWL_EXSTYLE)
                win32gui.SetWindowLong(HWND, win32con.GWL_EXSTYLE, 
                                    ex_style | win32con.WS_EX_LAYERED | win32con.WS_EX_TRANSPARENT)
                
                # 윈도우 투명도 설정
                win32gui.SetLayeredWindowAttributes(HWND, 0, 255, win32con.LWA_COLORKEY)
                
                # 클릭 통과를 위한 설정
                win32gui.SetWindowPos(HWND, win32con.HWND_TOPMOST, 0, 0, 0, 0,
                                    win32con.SWP_NOMOVE | win32con.SWP_NOSIZE)
            except Exception as e:
                print(f"⚠️ 윈도우 투명 설정 실패: {e}")

            # OpenGL 설정
            glViewport(0, 0, self.width, self.height)
            glMatrixMode(GL_PROJECTION)
            glLoadIdentity()
            glOrtho(0, self.width, self.height, 0, -1, 1)  # 좌상단이 (0,0)
            glMatrixMode(GL_MODELVIEW)
            
            # 알파 블렌딩 활성화
            glEnable(GL_BLEND)
            glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA)
            
            # 텍스처 설정
            glEnable(GL_TEXTURE_2D)
            
            # Always-on-top 설정 (플랫폼별 설정)
            if sys.platform == "win32":
                try:
                    import win32gui
                    import win32con
                    HWND = pygame.display.get_wm_info()['window']
                    win32gui.SetWindowPos(
                        HWND, win32con.HWND_TOPMOST, 
                        0, 0, 0, 0, 
                        win32con.SWP_NOMOVE | win32con.SWP_NOSIZE
                    )
                except Exception as e:
                    print(f"⚠️ Always-on-top 설정 실패: {e}")
            
            # Main render loop
            last_render_time = time.time()
            frame_count = 0
            font = pygame.font.SysFont('Arial', 18)  # 기본 폰트
            
            while not self.stop_event.is_set():
                # 이벤트 처리
                for event in pygame.event.get():
                    if event.type == pygame.QUIT:
                        self.stop_event.set()
                
                # 화면 지우기 (투명 배경)
                glClearColor(0.0, 0.0, 0.0, 0.0)
                glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT)
                
                # 모자이크 영역 그리기
                if self.mosaic_regions:
                    for x, y, w, h, label, _ in self.mosaic_regions:
                        # 화면 경계 확인
                        if x < 0 or y < 0 or x > self.width or y > self.height:
                            continue
                            
                        # 빨간색 반투명 사각형 그리기
                        glColor4f(1.0, 0.0, 0.0, 0.7)  # R, G, B, Alpha
                        glBegin(GL_QUADS)
                        glVertex2f(x, y)           # 좌상단
                        glVertex2f(x + w, y)       # 우상단
                        glVertex2f(x + w, y + h)   # 우하단
                        glVertex2f(x, y + h)       # 좌하단
                        glEnd()
                        
                        # 테두리 그리기 (흰색)
                        glColor4f(1.0, 1.0, 1.0, 1.0)  # 흰색
                        glLineWidth(2.0)
                        glBegin(GL_LINE_LOOP)
                        glVertex2f(x, y)
                        glVertex2f(x + w, y)
                        glVertex2f(x + w, y + h)
                        glVertex2f(x, y + h)
                        glEnd()
                        
                        # 텍스트 렌더링 (PyGame 텍스트)
                        try:
                            text_surface = font.render(label, True, (255, 255, 255))
                            text_data = pygame.image.tostring(text_surface, "RGBA", True)
                            text_width, text_height = text_surface.get_size()
                            
                            # 텍스처 생성 및 바인딩
                            texture_id = glGenTextures(1)
                            glBindTexture(GL_TEXTURE_2D, texture_id)
                            
                            # 텍스처 설정
                            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR)
                            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR)
                            glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, text_width, text_height, 
                                        0, GL_RGBA, GL_UNSIGNED_BYTE, text_data)
                            
                            # 텍스트 위치 (라벨 내부)
                            tx, ty = x + 5, y + 5
                            
                            # 텍스처 렌더링
                            glEnable(GL_TEXTURE_2D)
                            glColor4f(1.0, 1.0, 1.0, 1.0)  # 흰색
                            
                            glBegin(GL_QUADS)
                            glTexCoord2f(0, 0); glVertex2f(tx, ty)
                            glTexCoord2f(1, 0); glVertex2f(tx + text_width, ty)
                            glTexCoord2f(1, 1); glVertex2f(tx + text_width, ty + text_height)
                            glTexCoord2f(0, 1); glVertex2f(tx, ty + text_height)
                            glEnd()
                            
                            glDisable(GL_TEXTURE_2D)
                            
                            # 텍스처 삭제
                            glDeleteTextures(1, [texture_id])
                        except Exception as e:
                            print(f"⚠️ 텍스트 렌더링 오류: {e}")
                
                # 화면 갱신
                pygame.display.flip()
                
                # FPS 계산 및 출력
                frame_count += 1
                elapsed = time.time() - last_render_time
                if frame_count >= 30:  # 30프레임마다 FPS 출력
                    fps = frame_count / elapsed
                    print(f"⚡️ OpenGL 오버레이 FPS: {fps:.1f}")
                    last_render_time = time.time()
                    frame_count = 0
                
                # 프레임 레이트 제한
                time.sleep(self.render_interval)
            
            # 종료 처리
            pygame.quit()
            
        except Exception as e:
            print(f"❌ OpenGL 렌더링 스레드 오류: {e}")
            import traceback
            traceback.print_exc()
    
    def show(self):
        """오버레이 창 표시"""
        if not self.initialized:
            print("❌ OpenGL이 초기화되지 않아 표시할 수 없습니다.")
            return
            
        print("✅ OpenGL 오버레이 창 표시")
        self.shown = True
        
        # 렌더링 스레드 시작
        if self.render_thread is None or not self.render_thread.is_alive():
            self.stop_event.clear()
            self.render_thread = threading.Thread(target=self._render_thread_func, daemon=True)
            self.render_thread.start()
            print("✅ OpenGL 렌더링 스레드 시작됨")
    
    def hide(self):
        """오버레이 창 숨기기"""
        print("🛑 OpenGL 오버레이 창 숨기기")
        self.shown = False
        
        # 렌더링 스레드 중지
        if self.render_thread and self.render_thread.is_alive():
            self.stop_event.set()
            self.render_thread.join(timeout=1.0)
            self.render_thread = None
            print("🛑 OpenGL 렌더링 스레드 중지됨")
    
    def update_regions(self, original_image, mosaic_regions):
        """모자이크 영역 업데이트"""
        try:
            if original_image is None:
                return
            
            self.frame_count += 1
            
            # 모자이크 정보 저장
            self.original_image = original_image
            self.mosaic_regions = mosaic_regions
            
            # 모자이크 영역 조회 및 로그 출력
            if len(mosaic_regions) > 0:
                if self.frame_count % 30 == 0:  # 30프레임마다 로그 출력
                    print(f"✅ 모자이크 영역 {len(mosaic_regions)}개 처리 중 (프레임 #{self.frame_count})")
                    self._save_debug_image()
            else:
                if self.frame_count % 100 == 0:  # 100프레임마다 로그 출력
                    print(f"📢 모자이크 영역 없음 (프레임 #{self.frame_count})")
        
        except Exception as e:
            print(f"❌ 오버레이 업데이트 실패: {e}")
    
    def get_window_handle(self):
        """윈도우 핸들 반환"""
        try:
            if self.shown and pygame.display.get_init():
                return pygame.display.get_wm_info()['window']
        except:
            pass
        return 0
    
    def _save_debug_image(self):
        """디버깅용 이미지 저장"""
        try:
            if self.original_image is None or not self.mosaic_regions:
                return
                
            # 표시된 모자이크 영역 시각화
            debug_image = self.original_image.copy()
            for x, y, w, h, label, _ in self.mosaic_regions:
                # 원본 이미지에 박스 표시
                cv2.rectangle(debug_image, (x, y), (x+w, y+h), (0, 0, 255), 2)  # 빨간색
                cv2.putText(debug_image, label, (x, y-5), cv2.FONT_HERSHEY_SIMPLEX, 0.5, (0, 0, 255), 1)
            
            # 이미지 저장
            debug_path = f"{self.debug_dir}/overlay_opengl_{time.strftime('%Y%m%d_%H%M%S')}.jpg"
            cv2.imwrite(debug_path, debug_image)
            print(f"📸 디버깅용 오버레이 이미지 저장: {debug_path}")
        except Exception as e:
            print(f"⚠️ 디버깅 이미지 저장 실패: {e}")
    
    def __del__(self):
        """소멸자"""
        self.hide()