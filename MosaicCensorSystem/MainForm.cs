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
        
        // 🚨 CRITICAL: 스레드 안전성을 위한 락 객체들
        private readonly object isRunningLock = new object();
        private readonly object statsLock = new object();
        private readonly object logLock = new object();
        private volatile bool isRunning = false;
        
        private Thread processThread;
        private bool debugMode = false;
        
        private const int FIXED_FPS = 60;
        private float currentConfidence = 0.3f;
        
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
            // 🚨 CRITICAL: UI 스레드에서만 폼 생성
            if (InvokeRequired)
            {
                throw new InvalidOperationException("MosaicApp must be created on UI thread");
            }
            
            Root = new Form
            {
                Text = "실시간 화면 검열 시스템 v4.0 (스레드 안전 버전)",
                Size = new System.Drawing.Size(500, 750),
                MinimumSize = new System.Drawing.Size(450, 550),
                StartPosition = FormStartPosition.CenterScreen
            };
            
            try
            {
                capturer = new ScreenCapturer(Config.GetSection("capture"));
                processor = new MosaicProcessor(null, Config.GetSection("mosaic"));
                overlay = new FullscreenOverlay(Config.GetSection("overlay"));
                
                CreateGui();
                
                if (debugMode)
                {
                    Directory.CreateDirectory("debug_detection");
                }
                
                Console.WriteLine("✅ MosaicApp 초기화 완료 (스레드 안전)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ MosaicApp 초기화 실패: {ex.Message}");
                throw;
            }
        }

        private void CreateGui()
        {
            var titleLabel = new Label
            {
                Text = "🛡️ 스레드 안전 화면 검열 시스템 v4.0",
                Font = new Font("Arial", 14, FontStyle.Bold),
                BackColor = Color.LightBlue,
                BorderStyle = BorderStyle.Fixed3D,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
                Height = 40,
                Dock = DockStyle.Top
            };
            
            SetupWindowDragging(titleLabel);
            
            var scrollInfo = new Label
            {
                Text = "📜 마우스 휠로 스크롤하여 모든 설정을 확인하세요",
                Font = new Font("Arial", 9),
                ForeColor = Color.Blue,
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
                Text = "💡 파란색 제목을 드래그해서 창을 이동하세요",
                Font = new Font("Arial", 9),
                ForeColor = Color.Gray,
                Location = new System.Drawing.Point(10, y),
                AutoSize = true
            };
            parent.Controls.Add(dragInfo);
            y += 30;
            
            statusLabel = new Label
            {
                Text = "⭕ 대기 중",
                Font = new Font("Arial", 12),
                ForeColor = Color.Red,
                Location = new System.Drawing.Point(10, y),
                AutoSize = true
            };
            parent.Controls.Add(statusLabel);
            y += 40;
            
            var infoGroup = new GroupBox
            {
                Text = "🚀 스레드 안전 버전! (Cross-thread 오류 해결)",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 130)
            };
            
            var infoText = @"🛡️ 화면 캡처에서 완전 제외로 피드백 루프 방지
🖥️ 전체 화면 매끄러운 검열 효과 표시 (모자이크/블러)
🖱️ 클릭 투과로 바탕화면 상호작용 가능
📌 스레드 안전성 보장으로 시스템 크래시 방지
⚡ CUDA 우선, CPU 자동 폴백으로 최고 성능
🎯 체크박스와 신뢰도 설정을 통한 정밀 제어";
            
            var infoLabel = new Label
            {
                Text = infoText,
                ForeColor = Color.Green,
                Location = new System.Drawing.Point(10, 20),
                Size = new System.Drawing.Size(440, 100)
            };
            infoGroup.Controls.Add(infoLabel);
            parent.Controls.Add(infoGroup);
            y += 140;
            
            var warningGroup = new GroupBox
            {
                Text = "⚠️ 중요 안내",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 80)
            };
            
            var warningText = @"풀스크린 모드에서는 모든 화면이 덮어집니다.
ESC 키를 눌러 종료하거나, Ctrl+Alt+Del로 강제 종료하세요.
F1 키로 디버그 정보를 켜고 끌 수 있습니다.";
            
            var warningLabel = new Label
            {
                Text = warningText,
                ForeColor = Color.Red,
                Location = new System.Drawing.Point(10, 20),
                Size = new System.Drawing.Size(440, 50)
            };
            warningGroup.Controls.Add(warningLabel);
            parent.Controls.Add(warningGroup);
            y += 90;

            // 검열 효과 타입 선택 그룹
            var censorTypeGroup = new GroupBox
            {
                Text = "🎨 검열 효과 타입 선택",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 80)
            };

            mosaicRadioButton = new RadioButton
            {
                Text = "🟦 모자이크 (픽셀화)",
                Checked = true,
                Location = new System.Drawing.Point(20, 25),
                AutoSize = true
            };
            mosaicRadioButton.CheckedChanged += OnCensorTypeChanged;

            blurRadioButton = new RadioButton
            {
                Text = "🌀 블러 (흐림 효과)",
                Location = new System.Drawing.Point(200, 25),
                AutoSize = true
            };
            blurRadioButton.CheckedChanged += OnCensorTypeChanged;

            censorTypeLabel = new Label
            {
                Text = "현재: 모자이크",
                Font = new Font("Arial", 10, FontStyle.Bold),
                ForeColor = Color.Blue,
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
                Text = "🎯 검열 대상 선택",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 240)
            };
            
            var availableTargets = new[]
            {
                "얼굴", "가슴", "겨드랑이", "보지", "발", "몸 전체",
                "자지", "팬티", "눈", "손", "교미", "신발",
                "가슴_옷", "여성"
            };
            
            var defaultTargets = new List<string> { "눈", "손" };
            
            for (int i = 0; i < availableTargets.Length; i++)
            {
                var target = availableTargets[i];
                var row = i / 3;
                var col = i % 3;
                
                var checkbox = new CheckBox
                {
                    Text = target,
                    Checked = defaultTargets.Contains(target),
                    Location = new System.Drawing.Point(15 + col * 145, 30 + row * 30),
                    Size = new System.Drawing.Size(140, 25),
                    AutoSize = false
                };
                
                targetCheckBoxes[target] = checkbox;
                targetsGroup.Controls.Add(checkbox);
            }
            parent.Controls.Add(targetsGroup);
            y += 250;
            
            var settingsGroup = new GroupBox
            {
                Text = "⚙️ 검열 설정",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 120)
            };
            
            var strengthTextLabel = new Label
            {
                Text = "검열 강도:",
                Location = new System.Drawing.Point(10, 25),
                AutoSize = true
            };
            settingsGroup.Controls.Add(strengthTextLabel);
            
            strengthSlider = new TrackBar
            {
                Minimum = 5,
                Maximum = 50,
                Value = 15,
                TickFrequency = 5,
                Location = new System.Drawing.Point(120, 20),
                Size = new System.Drawing.Size(280, 45)
            };
            strengthSlider.ValueChanged += UpdateStrengthLabel;
            settingsGroup.Controls.Add(strengthSlider);
            
            strengthLabel = new Label
            {
                Text = strengthSlider.Value.ToString(),
                Location = new System.Drawing.Point(410, 25),
                AutoSize = true
            };
            settingsGroup.Controls.Add(strengthLabel);
            
            var confidenceTextLabel = new Label
            {
                Text = "감지 신뢰도:",
                Location = new System.Drawing.Point(10, 65),
                AutoSize = true
            };
            settingsGroup.Controls.Add(confidenceTextLabel);
            
            confidenceSlider = new TrackBar
            {
                Minimum = 10,
                Maximum = 90,
                Value = 30,
                TickFrequency = 10,
                Location = new System.Drawing.Point(120, 60),
                Size = new System.Drawing.Size(280, 45)
            };
            confidenceSlider.ValueChanged += UpdateConfidenceLabel;
            settingsGroup.Controls.Add(confidenceSlider);
            
            confidenceLabel = new Label
            {
                Text = "0.3",
                Location = new System.Drawing.Point(410, 65),
                AutoSize = true
            };
            settingsGroup.Controls.Add(confidenceLabel);
            
            var fixedSettingsLabel = new Label
            {
                Text = $"🔧 고정 설정: FPS={FIXED_FPS} (스레드 안전 모드)",
                ForeColor = Color.Blue,
                Font = new Font("Arial", 9, FontStyle.Bold),
                Location = new System.Drawing.Point(10, 95),
                AutoSize = true
            };
            settingsGroup.Controls.Add(fixedSettingsLabel);
            
            parent.Controls.Add(settingsGroup);
            y += 130;
            
            var controlPanel = new Panel
            {
                BackColor = Color.LightGray,
                BorderStyle = BorderStyle.Fixed3D,
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 100)
            };
            
            var buttonLabel = new Label
            {
                Text = "🎮 메인 컨트롤",
                Font = new Font("Arial", 12, FontStyle.Bold),
                BackColor = Color.LightGray,
                Location = new System.Drawing.Point(10, 10),
                Size = new System.Drawing.Size(440, 25),
                TextAlign = ContentAlignment.MiddleCenter
            };
            controlPanel.Controls.Add(buttonLabel);
            
            startButton = new Button
            {
                Text = "🚀 풀스크린 시작",
                BackColor = Color.Green,
                ForeColor = Color.White,
                Font = new Font("Arial", 12, FontStyle.Bold),
                Size = new System.Drawing.Size(180, 50),
                Location = new System.Drawing.Point(50, 40)
            };
            startButton.Click += StartCensoring;
            controlPanel.Controls.Add(startButton);
            
            stopButton = new Button
            {
                Text = "🛑 검열 중지",
                BackColor = Color.Red,
                ForeColor = Color.White,
                Font = new Font("Arial", 12, FontStyle.Bold),
                Size = new System.Drawing.Size(180, 50),
                Location = new System.Drawing.Point(230, 40),
                Enabled = false
            };
            stopButton.Click += StopCensoring;
            controlPanel.Controls.Add(stopButton);
            
            parent.Controls.Add(controlPanel);
            y += 110;
            
            var statsGroup = new GroupBox
            {
                Text = "📊 실시간 통계",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 100)
            };
            
            var statsItems = new[]
            {
                ("처리된 프레임", "frames_processed"),
                ("감지된 객체", "objects_detected"),
                ("검열 적용", "censor_applied"),
                ("실행 시간", "runtime")
            };
            
            for (int i = 0; i < statsItems.Length; i++)
            {
                var (name, key) = statsItems[i];
                
                var nameLabel = new Label
                {
                    Text = $"{name}:",
                    Location = new System.Drawing.Point(10 + (i % 2) * 230, 25 + (i / 2) * 30),
                    AutoSize = true
                };
                statsGroup.Controls.Add(nameLabel);
                
                var valueLabel = new Label
                {
                    Text = "0",
                    Font = new Font("Arial", 10, FontStyle.Bold),
                    Location = new System.Drawing.Point(120 + (i % 2) * 230, 25 + (i / 2) * 30),
                    AutoSize = true
                };
                statsLabels[key] = valueLabel;
                statsGroup.Controls.Add(valueLabel);
            }
            parent.Controls.Add(statsGroup);
            y += 110;
            
            var logGroup = new GroupBox
            {
                Text = "📝 실시간 로그",
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
            y += 110;
            
            var debugGroup = new GroupBox
            {
                Text = "🐛 디버그 옵션",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 60)
            };
            
            debugCheckBox = new CheckBox
            {
                Text = "🐛 디버그 모드",
                Location = new System.Drawing.Point(10, 25),
                AutoSize = true
            };
            debugGroup.Controls.Add(debugCheckBox);
            
            showDebugInfoCheckBox = new CheckBox
            {
                Text = "🔍 풀스크린 디버그 정보",
                Location = new System.Drawing.Point(230, 25),
                AutoSize = true
            };
            debugGroup.Controls.Add(showDebugInfoCheckBox);
            parent.Controls.Add(debugGroup);
            y += 70;
            
            var testGroup = new GroupBox
            {
                Text = "✅ 스크롤 테스트",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 60)
            };
            
            var testLabel = new Label
            {
                Text = "여기까지 스크롤이 되었다면 성공! 위로 올라가서 버튼을 클릭하세요.",
                ForeColor = Color.Green,
                Font = new Font("Arial", 10, FontStyle.Bold),
                Location = new System.Drawing.Point(10, 25),
                Size = new System.Drawing.Size(440, 25),
                TextAlign = ContentAlignment.MiddleCenter
            };
            testGroup.Controls.Add(testLabel);
            parent.Controls.Add(testGroup);
        }

        private void OnCensorTypeChanged(object sender, EventArgs e)
        {
            if (sender is RadioButton radioButton && radioButton.Checked)
            {
                CensorType newType = mosaicRadioButton.Checked ? CensorType.Mosaic : CensorType.Blur;
                
                processor?.SetCensorType(newType);
                
                string typeText = newType == CensorType.Mosaic ? "모자이크" : "블러";
                censorTypeLabel.Text = $"현재: {typeText}";
                censorTypeLabel.ForeColor = newType == CensorType.Mosaic ? Color.Blue : Color.Purple;
                
                LogMessage($"🎨 검열 타입 변경: {typeText}");
            }
        }

        private void UpdateStrengthLabel(object sender, EventArgs e)
        {
            strengthLabel.Text = strengthSlider.Value.ToString();
            
            if (processor != null)
            {
                processor.SetStrength(strengthSlider.Value);
                
                string effectType = mosaicRadioButton.Checked ? "모자이크" : "블러";
                LogMessage($"💪 {effectType} 강도 변경: {strengthSlider.Value}");
            }
        }

        private void UpdateConfidenceLabel(object sender, EventArgs e)
        {
            currentConfidence = confidenceSlider.Value / 100.0f;
            confidenceLabel.Text = currentConfidence.ToString("F1");
            
            if (processor != null)
            {
                processor.ConfThreshold = currentConfidence;
                LogMessage($"🔍 신뢰도 변경: {currentConfidence:F1}");
            }
        }

        // 🚨 CRITICAL: 스레드 안전한 로그 메시지
        private void LogMessage(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var fullMessage = $"[{timestamp}] {message}";
            
            Console.WriteLine(fullMessage);
            
            // 🚨 CRITICAL: 비동기 UI 업데이트 (데드락 방지)
            Task.Run(() =>
            {
                try
                {
                    lock (logLock)
                    {
                        if (Root?.IsHandleCreated == true && !Root.IsDisposed)
                        {
                            Root.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (logTextBox != null && !logTextBox.IsDisposed)
                                    {
                                        logTextBox.AppendText(fullMessage + Environment.NewLine);
                                        
                                        if (logTextBox.Lines.Length > 100)
                                        {
                                            var lines = logTextBox.Lines.Skip(20).ToArray();
                                            logTextBox.Lines = lines;
                                        }
                                        
                                        logTextBox.SelectionStart = logTextBox.Text.Length;
                                        logTextBox.ScrollToCaret();
                                    }
                                }
                                catch { }
                            }));
                        }
                    }
                }
                catch { }
            });
        }

        // 🚨 CRITICAL: 스레드 안전한 통계 업데이트
        private void UpdateStats()
        {
            try
            {
                lock (statsLock)
                {
                    if (stats["start_time"] != null)
                    {
                        var runtime = (int)(DateTime.Now - (DateTime)stats["start_time"]).TotalSeconds;
                        var minutes = runtime / 60;
                        var seconds = runtime % 60;
                        
                        if (Root?.IsHandleCreated == true && !Root.IsDisposed)
                        {
                            Root.BeginInvoke(new Action(() =>
                            {
                                try
                                {
                                    if (statsLabels.ContainsKey("runtime"))
                                        statsLabels["runtime"].Text = $"{minutes:D2}:{seconds:D2}";
                                    if (statsLabels.ContainsKey("frames_processed"))
                                        statsLabels["frames_processed"].Text = stats["frames_processed"].ToString();
                                    if (statsLabels.ContainsKey("objects_detected"))
                                        statsLabels["objects_detected"].Text = stats["objects_detected"].ToString();
                                    if (statsLabels.ContainsKey("censor_applied"))
                                        statsLabels["censor_applied"].Text = stats["censor_applied"].ToString();
                                }
                                catch { }
                            }));
                        }
                    }
                }
            }
            catch { }
        }

        private void StartCensoring(object sender, EventArgs e)
        {
            try
            {
                Console.WriteLine("🚀 StartCensoring 시작 (스레드 안전 모드)");
                
                lock (isRunningLock)
                {
                    if (isRunning)
                    {
                        Console.WriteLine("⚠️ 이미 실행 중");
                        return;
                    }
                }
                
                var selectedTargets = new List<string>();
                foreach (var kvp in targetCheckBoxes)
                {
                    if (kvp.Value.Checked)
                        selectedTargets.Add(kvp.Key);
                }

                LogMessage($"🎯 선택된 타겟들: {string.Join(", ", selectedTargets)}");

                if (selectedTargets.Count == 0)
                {
                    MessageBox.Show("최소 하나의 검열 대상을 선택해주세요!", "경고", 
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string censorType = mosaicRadioButton.Checked ? "모자이크" : "블러";
                
                var result = MessageBox.Show(
                    $"화면 검열 시스템을 시작하시겠습니까?\n\n" +
                    $"• 전체 화면에 {censorType} 효과가 적용됩니다\n" +
                    "• 바탕화면을 자유롭게 사용할 수 있습니다\n" +
                    "• ESC 키로 언제든 종료할 수 있습니다\n\n" +
                    "계속하시겠습니까?",
                    "화면 검열 시작 확인",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                
                if (result != DialogResult.Yes)
                {
                    return;
                }
                
                // 프로세서 설정
                try
                {
                    processor.SetTargets(selectedTargets);
                    processor.SetStrength(strengthSlider.Value);
                    processor.ConfThreshold = currentConfidence;
                    processor.SetCensorType(mosaicRadioButton.Checked ? CensorType.Mosaic : CensorType.Blur);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"프로세서 설정 실패: {ex.Message}", "오류", 
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                
                if (!processor.IsModelLoaded())
                {
                    MessageBox.Show("ONNX 모델 로딩 실패!", "오류",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                
                debugMode = debugCheckBox.Checked;
                overlay.ShowDebugInfo = showDebugInfoCheckBox.Checked;
                overlay.SetFpsLimit(FIXED_FPS);
                
                lock (isRunningLock)
                {
                    isRunning = true;
                }
                
                lock (statsLock)
                {
                    stats["start_time"] = DateTime.Now;
                    stats["frames_processed"] = 0;
                    stats["objects_detected"] = 0;
                    stats["censor_applied"] = 0;
                }
                
                statusLabel.Text = $"✅ 풀스크린 검열 중 ({censorType})";
                statusLabel.ForeColor = Color.Green;
                startButton.Enabled = false;
                stopButton.Enabled = true;
                
                mosaicRadioButton.Enabled = false;
                blurRadioButton.Enabled = false;
                
                if (!overlay.Show())
                {
                    LogMessage("❌ 풀스크린 오버레이 시작 실패");
                    StopCensoring(null, null);
                    return;
                }
                
                // 🚨 CRITICAL: 스레드 생성 최적화
                processThread = new Thread(ProcessingLoop)
                {
                    Name = "ProcessingThread",
                    IsBackground = true,
                    Priority = ThreadPriority.Normal // High에서 Normal로 변경
                };
                processThread.SetApartmentState(ApartmentState.MTA); // 멀티스레드 아파트먼트
                processThread.Start();
                
                LogMessage($"🚀 화면 검열 시작! 대상: {string.Join(", ", selectedTargets)}");
                LogMessage($"⚙️ 설정: 타입={censorType}, 강도={strengthSlider.Value}, 신뢰도={currentConfidence}, FPS={FIXED_FPS}");
                
                Console.WriteLine("🎉 StartCensoring 완료!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"💥 StartCensoring 오류: {ex.Message}");
                MessageBox.Show($"검열 시작 중 오류 발생:\n\n{ex.Message}", "치명적 오류",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                
                try
                {
                    StopCensoring(null, null);
                }
                catch { }
            }
        }

        private void StopCensoring(object sender, EventArgs e)
        {
            lock (isRunningLock)
            {
                if (!isRunning)
                    return;
                
                isRunning = false;
            }
            
            LogMessage("🛑 화면 검열 중지 중...");
            
            try
            {
                overlay?.Hide();
            }
            catch { }
            
            if (processThread != null && processThread.IsAlive)
            {
                processThread.Join(2000); // 2초 대기
            }
            
            if (Root?.IsHandleCreated == true && !Root.IsDisposed)
            {
                Root.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        statusLabel.Text = "⭕ 대기 중";
                        statusLabel.ForeColor = Color.Red;
                        startButton.Enabled = true;
                        stopButton.Enabled = false;
                        
                        mosaicRadioButton.Enabled = true;
                        blurRadioButton.Enabled = true;
                    }
                    catch { }
                }));
            }
            
            LogMessage("✅ 화면 검열 중지됨");
        }

        // 🚨 CRITICAL: 완전히 재작성된 스레드 안전 ProcessingLoop
        private void ProcessingLoop()
        {
            LogMessage("🔄 스레드 안전 ProcessingLoop 시작");
            int frameCount = 0;
            var matPool = new Queue<Mat>();
            const int maxPoolSize = 3; // 풀 크기 줄임
            
            DateTime lastStatsUpdate = DateTime.Now;
            DateTime lastLogTime = DateTime.Now;
            int uiUpdateCounter = 0;
            const int uiUpdateInterval = 10; // UI 업데이트 빈도 줄임
            
            try
            {
                while (true)
                {
                    // 🚨 CRITICAL: 스레드 안전한 실행 상태 체크
                    bool shouldRun;
                    lock (isRunningLock)
                    {
                        shouldRun = isRunning;
                    }
                    
                    if (!shouldRun)
                    {
                        Console.WriteLine("🛑 ProcessingLoop 정상 종료 요청");
                        break;
                    }
                    
                    Mat originalFrame = null;
                    Mat processedFrame = null;
                    
                    try
                    {
                        // 🚨 CRITICAL: 안전한 프레임 획득
                        try
                        {
                            originalFrame = capturer?.GetFrame();
                        }
                        catch (Exception captureEx)
                        {
                            Console.WriteLine($"❌ 프레임 캡처 오류: {captureEx.Message}");
                            Thread.Sleep(100);
                            continue;
                        }
                        
                        if (originalFrame == null || originalFrame.Empty())
                        {
                            Thread.Sleep(33); // 30fps로 제한
                            continue;
                        }
                        
                        frameCount++;
                        
                        // 🚨 CRITICAL: 스레드 안전한 통계 업데이트
                        lock (statsLock)
                        {
                            stats["frames_processed"] = frameCount;
                        }
                        
                        // 프레임 복사 (풀 사용)
                        if (matPool.Count > 0)
                        {
                            processedFrame = matPool.Dequeue();
                            if (processedFrame.Size() != originalFrame.Size())
                            {
                                processedFrame.Dispose();
                                processedFrame = originalFrame.Clone();
                            }
                            else
                            {
                                originalFrame.CopyTo(processedFrame);
                            }
                        }
                        else
                        {
                            processedFrame = originalFrame.Clone();
                        }
                        
                        // 🚨 CRITICAL: 안전한 객체 감지
                        List<Detection> detections = null;
                        try
                        {
                            if (processor != null)
                            {
                                detections = processor.DetectObjects(originalFrame);
                            }
                        }
                        catch (OutOfMemoryException)
                        {
                            Console.WriteLine("💥 메모리 부족 - 강제 GC");
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            GC.Collect();
                            Thread.Sleep(1000);
                            continue;
                        }
                        catch (Exception detectEx)
                        {
                            Console.WriteLine($"❌ 객체 감지 오류: {detectEx.GetType().Name}");
                            Thread.Sleep(100);
                            continue;
                        }
                        
                        // 검열 효과 적용
                        if (detections != null && detections.Count > 0)
                        {
                            try
                            {
                                foreach (var detection in detections)
                                {
                                    if (detection != null && processor != null)
                                    {
                                        processor.ApplySingleCensorOptimized(processedFrame, detection);
                                    }
                                }
                                
                                lock (statsLock)
                                {
                                    stats["censor_applied"] = (int)stats["censor_applied"] + detections.Count;
                                    stats["objects_detected"] = (int)stats["objects_detected"] + detections.Count;
                                }
                            }
                            catch (Exception censorEx)
                            {
                                Console.WriteLine($"❌ 검열 적용 오류: {censorEx.Message}");
                            }
                        }
                        
                        // 🚨 CRITICAL: 안전한 오버레이 업데이트
                        try
                        {
                            if (overlay != null && processedFrame != null && !processedFrame.Empty())
                            {
                                overlay.UpdateFrame(processedFrame);
                            }
                        }
                        catch (Exception overlayEx)
                        {
                            Console.WriteLine($"❌ 오버레이 업데이트 오류: {overlayEx.Message}");
                        }
                        
                        // Mat 풀 관리
                        if (processedFrame != null)
                        {
                            if (matPool.Count < maxPoolSize)
                            {
                                matPool.Enqueue(processedFrame);
                                processedFrame = null;
                            }
                        }
                        
                        // 🚨 CRITICAL: UI 업데이트 (빈도 제한)
                        uiUpdateCounter++;
                        if (uiUpdateCounter >= uiUpdateInterval)
                        {
                            uiUpdateCounter = 0;
                            
                            // 비동기 UI 업데이트
                            Task.Run(() =>
                            {
                                try
                                {
                                    UpdateStats();
                                }
                                catch { }
                            });
                            
                            var now = DateTime.Now;
                            if ((now - lastLogTime).TotalSeconds >= 15) // 로그 빈도 줄임
                            {
                                lastLogTime = now;
                                var fps = frameCount / (now - (DateTime)stats["start_time"]).TotalSeconds;
                                Task.Run(() =>
                                {
                                    try
                                    {
                                        LogMessage($"🎯 처리: {frameCount}프레임, {fps:F1}fps");
                                    }
                                    catch { }
                                });
                            }
                        }
                        
                        // 오버레이 상태 체크
                        try
                        {
                            if (overlay != null && !overlay.IsWindowVisible())
                            {
                                Console.WriteLine("🛑 오버레이 창 닫힘");
                                lock (isRunningLock)
                                {
                                    isRunning = false;
                                }
                                break;
                            }
                        }
                        catch { }
                        
                        // 프레임 레이트 제한
                        Thread.Sleep(33); // 30fps로 제한 (CPU 부하 감소)
                    }
                    catch (Exception frameEx)
                    {
                        Console.WriteLine($"❌ 프레임 처리 오류: {frameEx.GetType().Name}");
                        Thread.Sleep(100);
                    }
                    finally
                    {
                        // 안전한 리소스 정리
                        try
                        {
                            originalFrame?.Dispose();
                            if (processedFrame != null && !matPool.Contains(processedFrame))
                            {
                                processedFrame?.Dispose();
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception fatalEx)
            {
                Console.WriteLine($"💥 ProcessingLoop 치명적 오류: {fatalEx.GetType().Name} - {fatalEx.Message}");
                
                try
                {
                    File.AppendAllText("fatal_processing_error.log", 
                        $"{DateTime.Now}: FATAL - {fatalEx}\n================\n");
                }
                catch { }
            }
            finally
            {
                Console.WriteLine("🧹 ProcessingLoop 정리 시작");
                
                // Mat 풀 정리
                try
                {
                    while (matPool.Count > 0)
                    {
                        matPool.Dequeue()?.Dispose();
                    }
                }
                catch { }
                
                // UI 업데이트
                try
                {
                    if (Root?.IsHandleCreated == true && !Root.IsDisposed)
                    {
                        Root.BeginInvoke(new Action(() => StopCensoring(null, null)));
                    }
                }
                catch { }
                
                Console.WriteLine("🏁 ProcessingLoop 완전 종료");
            }
        }

        public void Run()
        {
            Console.WriteLine("🛡️ 스레드 안전 화면 검열 시스템 v4.0 시작");
            Console.WriteLine("=" + new string('=', 60));
            
            try
            {
                Root.FormClosed += OnFormClosed;
                Application.Run(Root);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n🛑 오류 발생: {ex.Message}");
            }
            finally
            {
                Cleanup();
            }
        }

        private void OnFormClosed(object sender, FormClosedEventArgs e)
        {
            lock (isRunningLock)
            {
                if (isRunning)
                {
                    StopCensoring(null, null);
                }
            }
            
            Cleanup();
        }

        private void Cleanup()
        {
            Console.WriteLine("🧹 리소스 정리 중...");
            
            lock (isRunningLock)
            {
                isRunning = false;
            }
            
            if (processThread != null && processThread.IsAlive)
            {
                processThread.Join(3000); // 3초 대기
            }
            
            try
            {
                overlay?.Dispose();
                capturer?.Dispose();
                processor?.Dispose();
            }
            catch { }
            
            Console.WriteLine("✅ 리소스 정리 완료");
        }
    }
}