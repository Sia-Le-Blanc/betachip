from .base import BaseOverlay

# Pygame 오버레이 추가
try:
    from .pygame_overlay import PygameOverlayWindow
    __all__ = ['BaseOverlay', 'PygameOverlayWindow']
except ImportError:
    print("Pygame 모듈이 설치되어 있지 않습니다. 'pip install pygame' 명령으로 설치하세요.")
    __all__ = ['BaseOverlay']
