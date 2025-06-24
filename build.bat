@echo off
chcp 65001 >nul
cls
echo ==========================================
echo        완벽한 간단 빌드 스크립트
echo        화면 검열 시스템 → E:\
echo ==========================================
echo.

:: ===== 1단계: 기본 확인 =====
echo [1/5] 기본 파일 확인 중...

if not exist "main.py" (
    echo ❌ main.py가 없습니다!
    echo 💡 프로젝트 폴더에서 이 스크립트를 실행하세요.
    pause
    exit /b 1
)
echo ✅ main.py 확인

if not exist "resources\best.onnx" (
    echo ❌ resources\best.onnx가 없습니다!
    echo 💡 AI 모델 파일이 필요합니다.
    pause
    exit /b 1
)
echo ✅ resources\best.onnx 확인

if not exist "config.py" (
    echo ❌ config.py가 없습니다!
    pause
    exit /b 1
)
echo ✅ config.py 확인

:: ===== 2단계: E: 드라이브 확인 =====
echo.
echo [2/5] E: 드라이브 확인 중...

if not exist "E:\" (
    echo ❌ E: 드라이브가 없습니다!
    echo 💡 USB나 다른 드라이브를 E:로 연결하거나
    echo    스크립트에서 다른 경로로 변경하세요.
    pause
    exit /b 1
)
echo ✅ E: 드라이브 접근 가능

:: ===== 3단계: 이전 파일 정리 =====
echo.
echo [3/5] 이전 빌드 파일 정리 중...

if exist "E:\MosaicCensorSystem.exe" (
    del "E:\MosaicCensorSystem.exe" 2>nul
    echo ✅ 이전 파일 삭제됨
) else (
    echo ✅ 이전 파일 없음
)

:: ===== 4단계: Nuitka 빌드 실행 =====
echo.
echo [4/5] Nuitka 빌드 시작...
echo 💡 3-5분 정도 소요됩니다. 기다려주세요!
echo.

python -m nuitka ^
    --onefile ^
    --windows-disable-console ^
    --enable-plugin=tkinter ^
    --include-data-dir=resources=resources ^
    --output-dir=E:\ ^
    --output-filename=MosaicCensorSystem.exe ^
    --assume-yes-for-downloads ^
    --show-progress ^
    main.py

:: ===== 5단계: 결과 확인 =====
echo.
echo [5/5] 빌드 결과 확인 중...

if exist "E:\MosaicCensorSystem.exe" (
    echo.
    echo ==========================================
    echo              🎉 빌드 성공!
    echo ==========================================
    echo.
    echo 📁 파일 위치: E:\MosaicCensorSystem.exe
    echo 💾 파일 크기:
    dir "E:\MosaicCensorSystem.exe" | findstr "MosaicCensorSystem.exe"
    echo.
    echo ✅ 배포 준비 완료!
    echo 💡 이제 이 파일을 다른 컴퓨터에서도 실행할 수 있습니다.
    echo.
    
    :: E: 드라이브 열기
    echo 📂 E: 드라이브를 열겠습니다...
    explorer E:\
    
    echo.
    set /p test_run=✨ 지금 바로 테스트해보시겠습니까? (y/n): 
    if /i "%test_run%"=="y" (
        echo 🚀 실행 중...
        "E:\MosaicCensorSystem.exe"
    )
    
) else (
    echo.
    echo ==========================================
    echo              ❌ 빌드 실패
    echo ==========================================
    echo.
    echo 🔍 가능한 원인:
    echo   1. 필수 패키지 누락 → pip install pygame opencv-python numpy ultralytics onnxruntime mss
    echo   2. E: 드라이브 권한 문제 → 관리자 권한으로 실행
    echo   3. 메모리 부족 → 다른 프로그램 종료 후 재시도
    echo   4. 바이러스 백신 차단 → 일시적으로 비활성화
    echo.
    echo 💡 위의 Nuitka 로그를 확인하여 정확한 오류를 파악하세요.
)

echo.
echo ==========================================
echo [%TIME%] 빌드 프로세스 완료
pause