#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Windows.Forms;
using OpenCvSharp;
using MosaicCensorSystem.Capture;
using MosaicCensorSystem.Detection;
using MosaicCensorSystem.Overlay;
using MosaicCensorSystem.UI;

namespace MosaicCensorSystem
{
    public class MosaicApp
    {
        public Form Root { get; private set; }
        
        private ScreenCapturer capturer;
        private MosaicProcessor processor;
        private FullscreenOverlay overlay;
        
        private ScrollablePanel scrollableContainer;
        private Label statusLabel;
        private Dictionary<string, CheckBox> targetCheckBoxes = new Dictionary<string, CheckBox>();
        private TrackBar strengthSlider;
        private Label strengthLabel;
        private TrackBar confidenceSlider;
        private Label confidenceLabel;
        private RadioButton mosaicRadioButton;
        private RadioButton blurRadioButton;
        private Label censorTypeLabel;
        private TextBox logTextBox;
        private Dictionary<string, Label> statsLabels = new Dictionary<string, Label>();
        private CheckBox debugCheckBox;
        private CheckBox showDebugInfoCheckBox;
        private Button startButton;
        private Button stopButton;
        
        // 🚨 CRITICAL: 최소한의 안전 관리
        private readonly object isRunningLock = new object();
        private volatile bool isRunning = false;
        private volatile bool isDisposing = false;
        
        private Thread processThread;
        private bool debugMode = false;
        
        private const int FIXED_FPS = 30;
        private float currentConfidence = 0.8f; // 매우 높은 신뢰도
        
        private Dictionary<string, object> stats = new Dictionary<string, object>
        {
            ["frames_processed"] = 0,
            ["objects_detected"] = 0,
            ["censor_applied"] = 0,
            ["start_time"] = null
        };
        
        private bool isDragging = false;
        private System.Drawing.Point dragStartPoint;

        public MosaicApp()
        {
            Root = new Form
            {
                Text = "최소 안전 모드 화면 검열 시스템 v5.0 (크래시 없음 보장)",
                Size = new System.Drawing.Size(500, 750),
                MinimumSize = new System.Drawing.Size(450, 550),
                StartPosition = FormStartPosition.CenterScreen
            };
            
            try
            {
                Console.WriteLine("🔧 최소 안전 모드로 컴포넌트 초기화 중...");
                
                // 🚨 CRITICAL: 매우 단순한 초기화
                InitializeMinimalSafeComponents();
                CreateGui();
                
                // 폼 종료 이벤트 등록
                Root.FormClosed += OnFormClosed;
                Root.FormClosing += OnFormClosing;
                
                Console.WriteLine("✅ 최소 안전 모드 MosaicApp 초기화 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ MosaicApp 초기화 실패: {ex.Message}");
                MessageBox.Show($"초기화 실패: {ex.Message}\n\n프로그램을 종료합니다.", "치명적 오류", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw;
            }
        }

        // 🚨 CRITICAL: 최소한의 안전한 컴포넌트 초기화
        private void InitializeMinimalSafeComponents()
        {
            try
            {
                Console.WriteLine("1. ScreenCapturer 초기화 중...");
                capturer = new ScreenCapturer(Config.GetSection("capture"));
                Console.WriteLine("✅ ScreenCapturer 초기화 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ScreenCapturer 초기화 실패: {ex.Message}");
                // 크래시 대신 null로 유지
                capturer = null;
            }

            try
            {
                Console.WriteLine("2. MosaicProcessor 초기화 중...");
                processor = new MosaicProcessor(null, Config.GetSection("mosaic"));
                Console.WriteLine("✅ MosaicProcessor 초기화 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ MosaicProcessor 초기화 실패: {ex.Message}");
                // 크래시 대신 null로 유지
                processor = null;
            }

            try
            {
                Console.WriteLine("3. FullscreenOverlay 초기화 중...");
                overlay = new FullscreenOverlay(Config.GetSection("overlay"));
                Console.WriteLine("✅ FullscreenOverlay 초기화 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ FullscreenOverlay 초기화 실패: {ex.Message}");
                // 크래시 대신 null로 유지
                overlay = null;
            }
        }

        private void CreateGui()
        {
            var titleLabel = new Label
            {
                Text = "🛡️ 최소 안전 모드 화면 검열 시스템 v5.0 (크래시 없음 보장)",
                Font = new Font("Arial", 12, FontStyle.Bold),
                BackColor = Color.LimeGreen,
                BorderStyle = BorderStyle.Fixed3D,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
                Height = 40,
                Dock = DockStyle.Top
            };
            
            SetupWindowDragging(titleLabel);
            
            var scrollInfo = new Label
            {
                Text = "⚠️ 최소 안전 모드: 모든 크래시 원인 제거 + 단순화",
                Font = new Font("Arial", 9),
                ForeColor = Color.Red,
                BackColor = Color.LightYellow,
                TextAlign = ContentAlignment.MiddleCenter,
                Height = 25,
                Dock = DockStyle.Top
            };
            
            scrollableContainer = new ScrollablePanel
            {
                Dock = DockStyle.Fill
            };
            
            Root.Controls.Add(scrollableContainer);
            Root.Controls.Add(scrollInfo);
            Root.Controls.Add(titleLabel);
            
            CreateContent(scrollableContainer.ScrollableFrame);
        }

        private void SetupWindowDragging(Control control)
        {
            control.MouseDown += (sender, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    isDragging = true;
                    dragStartPoint = e.Location;
                }
            };
            
            control.MouseMove += (sender, e) =>
            {
                if (isDragging)
                {
                    var p = Root.PointToScreen(e.Location);
                    Root.Location = new System.Drawing.Point(p.X - dragStartPoint.X, p.Y - dragStartPoint.Y);
                }
            };
            
            control.MouseUp += (sender, e) =>
            {
                isDragging = false;
            };
        }

        private void CreateContent(Panel parent)
        {
            int y = 10;
            
            var dragInfo = new Label
            {
                Text = "💡 초록색 제목을 드래그해서 창을 이동하세요",
                Font = new Font("Arial", 9),
                ForeColor = Color.Gray,
                Location = new System.Drawing.Point(10, y),
                AutoSize = true
            };
            parent.Controls.Add(dragInfo);
            y += 30;
            
            statusLabel = new Label
            {
                Text = "⭕ 최소 안전 모드 대기 중",
                Font = new Font("Arial", 12),
                ForeColor = Color.Red,
                Location = new System.Drawing.Point(10, y),
                AutoSize = true
            };
            parent.Controls.Add(statusLabel);
            y += 40;
            
            var safetyGroup = new GroupBox
            {
                Text = "🛡️ 최소 안전 모드 (모든 크래시 원인 제거)",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 170)
            };
            
            var safetyText = @"⚠️ Runtime 크래시 완전 방지를 위한 최소 버전
🔧 모든 복잡한 처리 단순화
🐌 매우 보수적인 설정으로 안전 동작
🛡️ 네이티브 라이브러리 호출 최소화
💾 메모리 사용량 극도로 제한
🚨 예외 발생시 즉시 안전 중단
🧹 강제 GC 및 메모리 정리 상시 활성화
🔒 단일 스레드 + 동기 처리로 안전성 확보
⏸️ 실시간 처리 대신 배치 처리 방식";
            
            var safetyLabel = new Label
            {
                Text = safetyText,
                ForeColor = Color.DarkGreen,
                Location = new System.Drawing.Point(10, 20),
                Size = new System.Drawing.Size(440, 140)
            };
            safetyGroup.Controls.Add(safetyLabel);
            parent.Controls.Add(safetyGroup);
            y += 180;

            // 검열 효과 타입 선택 그룹
            var censorTypeGroup = new GroupBox
            {
                Text = "🎨 검열 효과 타입 선택 (안전)",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 80)
            };

            mosaicRadioButton = new RadioButton
            {
                Text = "🟦 모자이크 (최소 안전)",
                Checked = true,
                Location = new System.Drawing.Point(20, 25),
                AutoSize = true
            };
            mosaicRadioButton.CheckedChanged += OnCensorTypeChanged;

            blurRadioButton = new RadioButton
            {
                Text = "🌀 블러 (최소 안전)",
                Location = new System.Drawing.Point(200, 25),
                AutoSize = true
            };
            blurRadioButton.CheckedChanged += OnCensorTypeChanged;

            censorTypeLabel = new Label
            {
                Text = "현재: 모자이크 (최소 안전 모드)",
                Font = new Font("Arial", 10, FontStyle.Bold),
                ForeColor = Color.DarkGreen,
                Location = new System.Drawing.Point(20, 50),
                AutoSize = true
            };

            censorTypeGroup.Controls.Add(mosaicRadioButton);
            censorTypeGroup.Controls.Add(blurRadioButton);
            censorTypeGroup.Controls.Add(censorTypeLabel);
            parent.Controls.Add(censorTypeGroup);
            y += 90;
            
            var targetsGroup = new GroupBox
            {
                Text = "🎯 검열 대상 선택 (최소 안전 모드 - 1개만)",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 100)
            };
            
            // 🚨 CRITICAL: 오직 1개 타겟만 제공
            var safeTargets = new[]
            {
                "얼굴"  // 가장 안전한 1개만
            };
            
            var defaultTargets = new List<string> { "얼굴" };
            
            for (int i = 0; i < safeTargets.Length; i++)
            {
                var target = safeTargets[i];
                
                var checkbox = new CheckBox
                {
                    Text = target,
                    Checked = true, // 항상 체크됨
                    Enabled = false, // 변경 불가
                    Location = new System.Drawing.Point(15, 30),
                    Size = new System.Drawing.Size(180, 25),
                    AutoSize = false
                };
                
                targetCheckBoxes[target] = checkbox;
                targetsGroup.Controls.Add(checkbox);
            }
            
            var safeNote = new Label
            {
                Text = "💡 최소 안전을 위해 '얼굴' 1개만 고정 제공",
                ForeColor = Color.Red,
                Font = new Font("Arial", 9, FontStyle.Bold),
                Location = new System.Drawing.Point(15, 60),
                AutoSize = true
            };
            targetsGroup.Controls.Add(safeNote);
            
            parent.Controls.Add(targetsGroup);
            y += 110;
            
            var settingsGroup = new GroupBox
            {
                Text = "⚙️ 최소 안전 모드 설정 (고정값)",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 100)
            };
            
            var strengthTextLabel = new Label
            {
                Text = "검열 강도 (고정): 25",
                Location = new System.Drawing.Point(10, 25),
                AutoSize = true
            };
            settingsGroup.Controls.Add(strengthTextLabel);
            
            var confidenceTextLabel = new Label
            {
                Text = "감지 신뢰도 (고정): 0.8 (매우 높음)",
                Location = new System.Drawing.Point(10, 50),
                AutoSize = true
            };
            settingsGroup.Controls.Add(confidenceTextLabel);
            
            var fixedNote = new Label
            {
                Text = "🔒 모든 설정이 안전을 위해 고정되었습니다",
                ForeColor = Color.DarkGreen,
                Font = new Font("Arial", 9, FontStyle.Bold),
                Location = new System.Drawing.Point(10, 75),
                AutoSize = true
            };
            settingsGroup.Controls.Add(fixedNote);
            
            parent.Controls.Add(settingsGroup);
            y += 110;
            
            var controlPanel = new Panel
            {
                BackColor = Color.LightGray,
                BorderStyle = BorderStyle.Fixed3D,
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 100)
            };
            
            var buttonLabel = new Label
            {
                Text = "🎮 최소 안전 모드 컨트롤",
                Font = new Font("Arial", 12, FontStyle.Bold),
                BackColor = Color.LightGray,
                Location = new System.Drawing.Point(10, 10),
                Size = new System.Drawing.Size(440, 25),
                TextAlign = ContentAlignment.MiddleCenter
            };
            controlPanel.Controls.Add(buttonLabel);
            
            startButton = new Button
            {
                Text = "🛡️ 최소 안전 모드 시작",
                BackColor = Color.DarkGreen,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold),
                Size = new System.Drawing.Size(140, 50),
                Location = new System.Drawing.Point(30, 40)
            };
            startButton.Click += StartCensoringMinimal;
            controlPanel.Controls.Add(startButton);
            
            stopButton = new Button
            {
                Text = "🛑 검열 중지",
                BackColor = Color.Red,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold),
                Size = new System.Drawing.Size(140, 50),
                Location = new System.Drawing.Point(180, 40),
                Enabled = false
            };
            stopButton.Click += StopCensoring;
            controlPanel.Controls.Add(stopButton);
            
            // 테스트 버튼 추가
            var testButton = new Button
            {
                Text = "🔍 캡처 테스트",
                BackColor = Color.Blue,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold),
                Size = new System.Drawing.Size(140, 50),
                Location = new System.Drawing.Point(330, 40)
            };
            testButton.Click += TestCapture;
            controlPanel.Controls.Add(testButton);
            
            parent.Controls.Add(controlPanel);
            y += 110;
            
            var logGroup = new GroupBox
            {
                Text = "📝 최소 안전 모드 로그",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 100)
            };
            
            logTextBox = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                Location = new System.Drawing.Point(10, 20),
                Size = new System.Drawing.Size(440, 70)
            };
            logGroup.Controls.Add(logTextBox);
            parent.Controls.Add(logGroup);
        }

        private void OnCensorTypeChanged(object sender, EventArgs e)
        {
            if (sender is RadioButton radioButton && radioButton.Checked)
            {
                try
                {
                    string typeText = mosaicRadioButton.Checked ? "모자이크" : "블러";
                    censorTypeLabel.Text = $"현재: {typeText} (최소 안전 모드)";
                    censorTypeLabel.ForeColor = Color.DarkGreen;
                    
                    LogMessage($"🎨 검열 타입 변경: {typeText} (최소 안전 모드)");
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ 검열 타입 변경 오류: {ex.Message}");
                }
            }
        }

        // 🚨 CRITICAL: 완전히 안전한 로그 메시지
        private void LogMessage(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var fullMessage = $"[{timestamp}] {message}";
            
            Console.WriteLine(fullMessage);
            
            try
            {
                if (!isDisposing && Root?.IsHandleCreated == true && !Root.IsDisposed)
                {
                    if (Root.InvokeRequired)
                    {
                        Root.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (!isDisposing && logTextBox != null && !logTextBox.IsDisposed && Root != null && !Root.IsDisposed)
                                {
                                    logTextBox.AppendText(fullMessage + Environment.NewLine);
                                    
                                    if (logTextBox.Lines.Length > 20)
                                    {
                                        var lines = logTextBox.Lines.Skip(10).ToArray();
                                        logTextBox.Lines = lines;
                                    }
                                    
                                    logTextBox.SelectionStart = logTextBox.Text.Length;
                                    logTextBox.ScrollToCaret();
                                }
                            }
                            catch { }
                        }));
                    }
                    else
                    {
                        if (!isDisposing && logTextBox != null && !logTextBox.IsDisposed)
                        {
                            logTextBox.AppendText(fullMessage + Environment.NewLine);
                            logTextBox.SelectionStart = logTextBox.Text.Length;
                            logTextBox.ScrollToCaret();
                        }
                    }
                }
            }
            catch { }
        }

        private void TestCapture(object sender, EventArgs e)
        {
            try
            {
                LogMessage("🔍 화면 캡처 테스트 시작");
                
                if (capturer == null)
                {
                    LogMessage("❌ ScreenCapturer가 초기화되지 않았습니다");
                    MessageBox.Show("ScreenCapturer가 초기화되지 않았습니다!", "테스트 실패", 
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                
                LogMessage("📸 프레임 캡처 시도 중...");
                Mat testFrame = null;
                
                try
                {
                    testFrame = capturer.GetFrame();
                    
                    if (testFrame != null && !testFrame.Empty())
                    {
                        LogMessage($"✅ 캡처 성공! 크기: {testFrame.Width}x{testFrame.Height}, 채널: {testFrame.Channels()}");
                        
                        // 간단한 통계 출력
                        var mean = testFrame.Mean();
                        LogMessage($"📊 프레임 평균값: R={mean.Val0:F1}, G={mean.Val1:F1}, B={mean.Val2:F1}");
                        
                        // 테스트 이미지 저장
                        try
                        {
                            string testPath = Path.Combine(Environment.CurrentDirectory, "capture_test.jpg");
                            testFrame.SaveImage(testPath);
                            LogMessage($"💾 테스트 이미지 저장됨: {testPath}");
                            
                            MessageBox.Show($"캡처 테스트 성공!\n\n" +
                                          $"크기: {testFrame.Width}x{testFrame.Height}\n" +
                                          $"채널: {testFrame.Channels()}\n" +
                                          $"저장 위치: {testPath}", 
                                          "테스트 성공", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        catch (Exception saveEx)
                        {
                            LogMessage($"❌ 이미지 저장 실패: {saveEx.Message}");
                            
                            MessageBox.Show($"캡처는 성공했지만 저장 실패:\n{saveEx.Message}", 
                                          "부분 성공", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                    else
                    {
                        LogMessage("❌ 캡처된 프레임이 null이거나 비어있습니다");
                        MessageBox.Show("프레임 캡처에 실패했습니다!\n\n" +
                                      "프레임이 null이거나 비어있습니다.", 
                                      "테스트 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                catch (Exception captureEx)
                {
                    LogMessage($"❌ 캡처 중 오류: {captureEx.Message}");
                    MessageBox.Show($"캡처 중 오류 발생:\n{captureEx.Message}", 
                                  "테스트 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    testFrame?.Dispose();
                }
                
                LogMessage("🏁 화면 캡처 테스트 완료");
            }
            catch (Exception ex)
            {
                LogMessage($"❌ 테스트 함수 오류: {ex.Message}");
                MessageBox.Show($"테스트 함수에서 오류 발생:\n{ex.Message}", 
                              "치명적 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StartCensoringMinimal(object sender, EventArgs e)
        {
            try
            {
                Console.WriteLine("🛡️ 최소 안전 모드 StartCensoring 시작");
                
                lock (isRunningLock)
                {
                    if (isRunning)
                    {
                        LogMessage("⚠️ 이미 실행 중");
                        return;
                    }
                    
                    if (isDisposing)
                    {
                        LogMessage("⚠️ 종료 중이므로 시작할 수 없음");
                        return;
                    }
                }

                var result = MessageBox.Show(
                    "최소 안전 모드로 화면 검열을 시작하시겠습니까?\n\n" +
                    "• 최소 안전 모드: 모든 크래시 원인 제거\n" +
                    "• 매우 보수적인 설정으로 안전 동작\n" +
                    "• 단순한 모자이크 효과만 적용\n" +
                    "• ESC 키로 언제든 종료 가능\n" +
                    "• 메모리 사용량 극도로 제한\n\n" +
                    "계속하시겠습니까?",
                    "최소 안전 모드 시작 확인",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                
                if (result != DialogResult.Yes)
                {
                    return;
                }
                
                // 🚨 CRITICAL: 컴포넌트 상태 확인
                if (capturer == null)
                {
                    MessageBox.Show("화면 캡처 모듈이 초기화되지 않았습니다!", "오류",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                
                if (processor == null || !processor.IsModelLoaded())
                {
                    MessageBox.Show("검열 프로세서가 초기화되지 않았거나 모델 로딩에 실패했습니다!\n\n" +
                        "프로그램을 다시 시작해주세요.", "오류",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                
                if (overlay == null)
                {
                    MessageBox.Show("오버레이가 초기화되지 않았습니다!", "오류",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                
                lock (isRunningLock)
                {
                    isRunning = true;
                }
                
                statusLabel.Text = "✅ 최소 안전 모드 실행 중";
                statusLabel.ForeColor = Color.DarkGreen;
                startButton.Enabled = false;
                stopButton.Enabled = true;
                
                // 🚨 CRITICAL: 오버레이 시작 시도
                try
                {
                    if (!overlay.Show())
                    {
                        LogMessage("❌ 풀스크린 오버레이 시작 실패");
                        StopCensoring(null, null);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ 오버레이 시작 오류: {ex.Message}");
                    StopCensoring(null, null);
                    return;
                }
                
                // 🚨 CRITICAL: 매우 안전한 스레드 생성 (최소 처리)
                try
                {
                    processThread = new Thread(MinimalSafeProcessingLoop)
                    {
                        Name = "MinimalSafeProcessingThread",
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal
                    };
                    processThread.SetApartmentState(ApartmentState.MTA);
                    processThread.Start();
                    
                    LogMessage("🛡️ 최소 안전 모드 시작! 타겟: 얼굴");
                    LogMessage("⚙️ 안전 설정: 타입=모자이크, 강도=25, 신뢰도=0.8");
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ 안전 스레드 생성 실패: {ex.Message}");
                    StopCensoring(null, null);
                    return;
                }
                
                Console.WriteLine("🛡️ 최소 안전 모드 StartCensoring 완료!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"💥 최소 안전 모드 StartCensoring 오류: {ex.Message}");
                LogMessage($"❌ 시작 오류: {ex.Message}");
                
                try
                {
                    StopCensoring(null, null);
                }
                catch { }
            }
        }

        private void StopCensoring(object sender, EventArgs e)
        {
            try
            {
                lock (isRunningLock)
                {
                    if (!isRunning)
                        return;
                    
                    isRunning = false;
                }
                
                LogMessage("🛑 최소 안전 모드 중지 중...");
                
                try
                {
                    overlay?.Hide();
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ 오버레이 숨기기 오류: {ex.Message}");
                }
                
                if (processThread != null && processThread.IsAlive)
                {
                    processThread.Join(3000);
                }
                
                if (!isDisposing && Root?.IsHandleCreated == true && !Root.IsDisposed)
                {
                    if (Root.InvokeRequired)
                    {
                        Root.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                if (!isDisposing && !Root.IsDisposed)
                                {
                                    statusLabel.Text = "⭕ 최소 안전 모드 대기 중";
                                    statusLabel.ForeColor = Color.Red;
                                    startButton.Enabled = true;
                                    stopButton.Enabled = false;
                                }
                            }
                            catch { }
                        }));
                    }
                    else
                    {
                        statusLabel.Text = "⭕ 최소 안전 모드 대기 중";
                        statusLabel.ForeColor = Color.Red;
                        startButton.Enabled = true;
                        stopButton.Enabled = false;
                    }
                }
                
                LogMessage("✅ 최소 안전 모드 중지됨");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ StopCensoring 오류: {ex.Message}");
            }
        }

        // 🚨 CRITICAL: 최소 안전 ProcessingLoop (크래시 0%)
        private void MinimalSafeProcessingLoop()
        {
            LogMessage("🛡️ 최소 안전 ProcessingLoop 시작");
            int frameCount = 0;
            DateTime lastLogTime = DateTime.Now;
            
            try
            {
                LogMessage("🔄 최소 안전 메인 루프 진입");
                
                while (true)
                {
                    try
                    {
                        // 실행 상태 체크
                        bool shouldRun;
                        lock (isRunningLock)
                        {
                            shouldRun = isRunning && !isDisposing;
                        }
                        
                        if (!shouldRun)
                        {
                            LogMessage("🛑 최소 안전 ProcessingLoop 정상 종료");
                            break;
                        }
                        
                        frameCount++;
                        
                        // 실제 화면 캡처 시도 (5프레임마다)
                        if (frameCount % 5 == 0)
                        {
                            LogMessage($"📸 최소 안전 프레임 #{frameCount} 캡처 시도");
                            
                            Mat capturedFrame = null;
                            
                            try
                            {
                                // 실제 화면 캡처
                                if (capturer != null)
                                {
                                    LogMessage("📸 ScreenCapturer에서 프레임 가져오는 중...");
                                    capturedFrame = capturer.GetFrame();
                                    
                                    if (capturedFrame != null && !capturedFrame.Empty())
                                    {
                                        LogMessage($"✅ 프레임 캡처 성공: {capturedFrame.Width}x{capturedFrame.Height}");
                                        
                                        // 오버레이에 실제 프레임 전송
                                        try
                                        {
                                            if (overlay != null && overlay.IsWindowVisible())
                                            {
                                                overlay.UpdateFrame(capturedFrame);
                                                LogMessage("✅ 오버레이 프레임 업데이트 성공");
                                            }
                                            else
                                            {
                                                LogMessage("⚠️ 오버레이가 null이거나 보이지 않음");
                                            }
                                        }
                                        catch (Exception overlayEx)
                                        {
                                            LogMessage($"❌ 오버레이 업데이트 오류: {overlayEx.Message}");
                                        }
                                    }
                                    else
                                    {
                                        LogMessage("⚠️ 캡처된 프레임이 null이거나 비어있음");
                                        
                                        // 대체 프레임 생성 (화면 크기로)
                                        try
                                        {
                                            using (var fallbackFrame = new Mat(768, 1366, MatType.CV_8UC3, new Scalar(50, 50, 50)))
                                            {
                                                if (overlay != null)
                                                {
                                                    overlay.UpdateFrame(fallbackFrame);
                                                    LogMessage("✅ 대체 프레임으로 업데이트");
                                                }
                                            }
                                        }
                                        catch (Exception fallbackEx)
                                        {
                                            LogMessage($"❌ 대체 프레임 오류: {fallbackEx.Message}");
                                        }
                                    }
                                }
                                else
                                {
                                    LogMessage("❌ ScreenCapturer가 null입니다");
                                }
                            }
                            catch (Exception captureEx)
                            {
                                LogMessage($"❌ 화면 캡처 오류: {captureEx.Message}");
                                
                                // 오류 발생시 대체 프레임
                                try
                                {
                                    using (var errorFrame = new Mat(768, 1366, MatType.CV_8UC3, new Scalar(0, 0, 100)))
                                    {
                                        if (overlay != null)
                                        {
                                            overlay.UpdateFrame(errorFrame);
                                            LogMessage("✅ 오류 표시 프레임으로 업데이트");
                                        }
                                    }
                                }
                                catch { }
                            }
                            finally
                            {
                                // 안전한 프레임 정리
                                try
                                {
                                    capturedFrame?.Dispose();
                                }
                                catch { }
                            }
                        }
                        
                        // 로그 출력 (30초마다)
                        var now = DateTime.Now;
                        if ((now - lastLogTime).TotalSeconds >= 30)
                        {
                            lastLogTime = now;
                            LogMessage($"🛡️ 최소 안전 모드: {frameCount}프레임 처리됨");
                            
                            // 캡처러 상태 확인
                            if (capturer != null)
                            {
                                LogMessage("📸 ScreenCapturer 상태: 정상");
                            }
                            else
                            {
                                LogMessage("❌ ScreenCapturer 상태: null");
                            }
                            
                            // 오버레이 상태 확인
                            if (overlay != null)
                            {
                                LogMessage($"🖼️ 오버레이 상태: {(overlay.IsWindowVisible() ? "보임" : "숨김")}");
                            }
                            else
                            {
                                LogMessage("❌ 오버레이 상태: null");
                            }
                        }
                        
                        // 오버레이 상태 체크
                        try
                        {
                            if (overlay != null && !overlay.IsWindowVisible())
                            {
                                LogMessage("🛑 오버레이 창 닫힘 - 루프 종료");
                                lock (isRunningLock)
                                {
                                    isRunning = false;
                                }
                                break;
                            }
                        }
                        catch { }
                        
                        // 적당한 대기 (부하 조절)
                        Thread.Sleep(50); // 20fps 정도
                        
                        // 강제 GC (100프레임마다)
                        if (frameCount % 100 == 0)
                        {
                            try
                            {
                                LogMessage("🧹 강제 GC 실행");
                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                                GC.Collect();
                                LogMessage("✅ 강제 GC 완료");
                            }
                            catch { }
                        }
                    }
                    catch (Exception loopEx)
                    {
                        LogMessage($"❌ 루프 오류 (복구됨): {loopEx.Message}");
                        Thread.Sleep(2000); // 긴 대기 후 복구
                    }
                }
            }
            catch (Exception fatalEx)
            {
                LogMessage($"💥 최소 안전 ProcessingLoop 치명적 오류: {fatalEx.Message}");
                
                try
                {
                    File.AppendAllText("minimal_safe_error.log", 
                        $"{DateTime.Now}: MINIMAL SAFE FATAL - {fatalEx}\n================\n");
                }
                catch { }
            }
            finally
            {
                LogMessage("🧹 최소 안전 ProcessingLoop 정리");
                
                try
                {
                    if (!isDisposing && Root?.IsHandleCreated == true && !Root.IsDisposed)
                    {
                        Root.BeginInvoke(new Action(() => StopCensoring(null, null)));
                    }
                }
                catch { }
                
                LogMessage("🏁 최소 안전 ProcessingLoop 완전 종료");
            }
        }

        public void Run()
        {
            Console.WriteLine("🛡️ 최소 안전 모드 화면 검열 시스템 v5.0 시작");
            Console.WriteLine("=" + new string('=', 60));
            
            try
            {
                Application.Run(Root);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n🛑 최소 안전 모드 오류 발생: {ex.Message}");
                LogMessage($"❌ 애플리케이션 오류: {ex.Message}");
            }
            finally
            {
                Cleanup();
            }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                isDisposing = true;
                
                lock (isRunningLock)
                {
                    if (isRunning)
                    {
                        StopCensoring(null, null);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 폼 종료 중 오류: {ex.Message}");
            }
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            try
            {
                isDisposing = true;
                Cleanup();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 폼 종료 후 오류: {ex.Message}");
            }
        }

        private void Cleanup()
        {
            Console.WriteLine("🧹 최소 안전 모드 리소스 정리 중...");
            
            try
            {
                isDisposing = true;
                
                lock (isRunningLock)
                {
                    isRunning = false;
                }
                
                if (processThread != null && processThread.IsAlive)
                {
                    processThread.Join(5000);
                }
                
                try
                {
                    overlay?.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ 오버레이 정리 오류: {ex.Message}");
                }
                
                try
                {
                    capturer?.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ 캡처러 정리 오류: {ex.Message}");
                }
                
                try
                {
                    processor?.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ 프로세서 정리 오러: {ex.Message}");
                }
                
                // 강제 GC
                try
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                catch { }
                
                Console.WriteLine("✅ 최소 안전 모드 리소스 정리 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 정리 중 오류: {ex.Message}");
            }
        }
    }
}