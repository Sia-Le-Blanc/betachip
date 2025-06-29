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
        private TextBox logTextBox;
        private Dictionary<string, Label> statsLabels = new Dictionary<string, Label>();
        private CheckBox debugCheckBox;
        private CheckBox showDebugInfoCheckBox;
        private Button startButton;
        private Button stopButton;
        
        private bool isRunning = false;
        private Thread processThread;
        private bool debugMode = false;
        
        // 고정값들
        private const float FIXED_CONFIDENCE = 0.35f;
        private const int FIXED_FPS = 60;
        
        private Dictionary<string, object> stats = new Dictionary<string, object>
        {
            ["frames_processed"] = 0,
            ["objects_detected"] = 0,
            ["mosaic_applied"] = 0,
            ["start_time"] = null
        };
        
        private bool isDragging = false;
        private System.Drawing.Point dragStartPoint;

        public MosaicApp()
        {
            Root = new Form
            {
                Text = "실시간 화면 검열 시스템 v3.1 (CUDA 자동감지 + 최적화)",
                Size = new System.Drawing.Size(500, 600),
                MinimumSize = new System.Drawing.Size(450, 400),
                StartPosition = FormStartPosition.CenterScreen
            };
            
            capturer = new ScreenCapturer(Config.GetSection("capture"));
            processor = new MosaicProcessor(null, Config.GetSection("mosaic"));
            overlay = new FullscreenOverlay(Config.GetSection("overlay"));
            
            CreateGui();
            
            if (debugMode)
            {
                Directory.CreateDirectory("debug_detection");
            }
        }

        private void CreateGui()
        {
            var titleLabel = new Label
            {
                Text = "🛡️ 풀스크린 화면 검열 시스템 (CUDA 자동)",
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
                Text = "🚀 CUDA 자동감지 최적화 버전!",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 130)
            };
            
            var infoText = @"🛡️ 화면 캡처에서 완전 제외로 피드백 루프 방지
🖥️ 전체 화면 매끄러운 모자이크 표시
🖱️ 클릭 투과로 바탕화면 상호작용 가능
📌 Windows Hook으로 창 활성화 즉시 차단
⚡ CUDA 우선, CPU 자동 폴백으로 최고 성능
✅ 고정 설정 (FPS: 60, 신뢰도: 0.35)";
            
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
            
            var targetsGroup = new GroupBox
            {
                Text = "🎯 모자이크 대상 선택",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 240)
            };
            
            var availableTargets = new[]
            {
                "얼굴", "가슴", "겨드랑이", "보지", "발", "몸 전체",
                "자지", "팬티", "눈", "손", "교미", "신발",
                "가슴_옷", "보지_옷", "여성"
            };
            
            var defaultTargets = Config.Get<List<string>>("mosaic", "default_targets", new List<string>());
            
            // 개선된 체크박스 레이아웃 (3열)
            for (int i = 0; i < availableTargets.Length; i++)
            {
                var target = availableTargets[i];
                var row = i / 3;  // 3열로 배치
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
            y += 250; // 확장된 높이
            
            var settingsGroup = new GroupBox
            {
                Text = "⚙️ 모자이크 설정",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 80)
            };
            
            var strengthTextLabel = new Label
            {
                Text = "모자이크 강도:",
                Location = new System.Drawing.Point(10, 25),
                AutoSize = true
            };
            settingsGroup.Controls.Add(strengthTextLabel);
            
            strengthSlider = new TrackBar
            {
                Minimum = 5,
                Maximum = 50,
                Value = Config.Get<int>("mosaic", "default_strength", 15),
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
            
            // 고정 설정 안내 라벨
            var fixedSettingsLabel = new Label
            {
                Text = $"🔧 고정 설정: FPS={FIXED_FPS}, 신뢰도={FIXED_CONFIDENCE}",
                ForeColor = Color.Blue,
                Font = new Font("Arial", 9, FontStyle.Bold),
                Location = new System.Drawing.Point(10, 55),
                AutoSize = true
            };
            settingsGroup.Controls.Add(fixedSettingsLabel);
            
            parent.Controls.Add(settingsGroup);
            y += 90;
            
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
                ("모자이크 적용", "mosaic_applied"),
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

        private void UpdateStrengthLabel(object sender, EventArgs e)
        {
            strengthLabel.Text = strengthSlider.Value.ToString();
        }

        private void LogMessage(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            var fullMessage = $"[{timestamp}] {message}";
            
            // 콘솔 출력은 즉시
            Console.WriteLine(fullMessage);
            
            // UI 업데이트는 비동기로 (메인 루프 차단 방지)
            Task.Run(() =>
            {
                try
                {
                    if (Root.InvokeRequired)
                    {
                        Root.Invoke(new Action(() =>
                        {
                            try
                            {
                                if (logTextBox != null && !logTextBox.IsDisposed)
                                {
                                    logTextBox.AppendText(fullMessage + Environment.NewLine);
                                    
                                    // 로그가 너무 길어지면 앞부분 제거 (메모리 절약)
                                    if (logTextBox.Lines.Length > 100)
                                    {
                                        var lines = logTextBox.Lines.Skip(20).ToArray();
                                        logTextBox.Lines = lines;
                                    }
                                    
                                    logTextBox.SelectionStart = logTextBox.Text.Length;
                                    logTextBox.ScrollToCaret();
                                }
                            }
                            catch { } // UI 업데이트 실패해도 무시
                        }));
                    }
                }
                catch { } // 전체 실패해도 무시
            });
        }

        private void UpdateStats()
        {
            try
            {
                if (stats["start_time"] != null)
                {
                    var runtime = (int)(DateTime.Now - (DateTime)stats["start_time"]).TotalSeconds;
                    var minutes = runtime / 60;
                    var seconds = runtime % 60;
                    
                    if (Root.InvokeRequired)
                    {
                        Root.Invoke(new Action(() =>
                        {
                            try
                            {
                                if (statsLabels.ContainsKey("runtime"))
                                    statsLabels["runtime"].Text = $"{minutes:D2}:{seconds:D2}";
                                if (statsLabels.ContainsKey("frames_processed"))
                                    statsLabels["frames_processed"].Text = stats["frames_processed"].ToString();
                                if (statsLabels.ContainsKey("objects_detected"))
                                    statsLabels["objects_detected"].Text = stats["objects_detected"].ToString();
                                if (statsLabels.ContainsKey("mosaic_applied"))
                                    statsLabels["mosaic_applied"].Text = stats["mosaic_applied"].ToString();
                            }
                            catch { } // UI 업데이트 실패해도 메인 루프에 영향 없도록
                        }));
                    }
                }
            }
            catch { } // 통계 업데이트 실패해도 무시
        }

        private void StartCensoring(object sender, EventArgs e)
        {
            if (isRunning)
                return;
            
            var selectedTargets = new List<string>();
            foreach (var kvp in targetCheckBoxes)
            {
                if (kvp.Value.Checked)
                    selectedTargets.Add(kvp.Key);
            }

            LogMessage($"🎯 선택된 타겟들: {string.Join(", ", selectedTargets)}");

            if (selectedTargets.Count == 0)
            {
                MessageBox.Show("최소 하나의 모자이크 대상을 선택해주세요!", "경고", 
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            
            var result = MessageBox.Show(
                "화면 검열 시스템을 시작하시겠습니까?\n\n" +
                "• 전체 화면에 모자이크가 적용됩니다\n" +
                "• 바탕화면을 자유롭게 사용할 수 있습니다\n" +
                "• ESC 키로 언제든 종료할 수 있습니다\n\n" +
                "계속하시겠습니까?",
                "화면 검열 시작 확인",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            
            if (result != DialogResult.Yes)
                return;
            
            processor.SetTargets(selectedTargets);
            processor.SetStrength(strengthSlider.Value);
            processor.ConfThreshold = FIXED_CONFIDENCE;
            
            LogMessage($"🔍 현재 작업 디렉토리: {Environment.CurrentDirectory}");
            LogMessage($"🔍 실행 파일 디렉토리: {AppDomain.CurrentDomain.BaseDirectory}");
            LogMessage($"🔍 예상 모델 경로: {Program.ONNX_MODEL_PATH}");
            LogMessage($"🔍 파일 존재 여부: {System.IO.File.Exists(Program.ONNX_MODEL_PATH)}");

            if (!processor.IsModelLoaded())
            {
                LogMessage("❌ ONNX 모델이 로드되지 않았습니다!");
                LogMessage("🔍 모델 로딩 중 에러가 발생했을 가능성이 높습니다");
                LogMessage("🔍 가능한 원인: 1) ONNX Runtime 문제 2) 모델 파일 손상 3) 권한 문제");
                MessageBox.Show("ONNX 모델 로딩 실패!");
                return;
            }
            LogMessage("ONNX 모델 로드 확인됨!");
            debugMode = debugCheckBox.Checked;
            
            overlay.ShowDebugInfo = showDebugInfoCheckBox.Checked;
            overlay.SetFpsLimit(FIXED_FPS);
            
            isRunning = true;
            stats["start_time"] = DateTime.Now;
            stats["frames_processed"] = 0;
            stats["objects_detected"] = 0;
            stats["mosaic_applied"] = 0;
            
            statusLabel.Text = "✅ 풀스크린 검열 중";
            statusLabel.ForeColor = Color.Green;
            startButton.Enabled = false;
            stopButton.Enabled = true;
            
            if (!overlay.Show())
            {
                LogMessage("❌ 풀스크린 오버레이 시작 실패");
                StopCensoring(null, null);
                return;
            }
            
            processThread = new Thread(ProcessingLoop)
            {
                Name = "ProcessingThread",
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            processThread.Start();
            
            LogMessage($"🚀 화면 검열 시작! 대상: {string.Join(", ", selectedTargets)}");
            LogMessage($"⚙️ 설정: 강도={strengthSlider.Value}, 신뢰도={FIXED_CONFIDENCE}, FPS={FIXED_FPS}");
            
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(3000);
                
                if (overlay.TestCaptureProtection())
                {
                    LogMessage("🛡️ 캡처 방지 기능 활성화됨");
                }
                
                if (overlay.TestClickThrough())
                {
                    LogMessage("🖱️ 클릭 투과 기능 활성화됨");
                    LogMessage("💡 바탕화면을 자유롭게 사용할 수 있습니다");
                }
            });
        }

        private void StopCensoring(object sender, EventArgs e)
        {
            if (!isRunning)
                return;
            
            LogMessage("🛑 화면 검열 중지 중...");
            
            isRunning = false;
            
            overlay.Hide();
            
            if (processThread != null && processThread.IsAlive)
            {
                processThread.Join(1000);
            }
            
            if (Root.InvokeRequired)
            {
                Root.Invoke(new Action(() =>
                {
                    statusLabel.Text = "⭕ 대기 중";
                    statusLabel.ForeColor = Color.Red;
                    startButton.Enabled = true;
                    stopButton.Enabled = false;
                }));
            }
            else
            {
                statusLabel.Text = "⭕ 대기 중";
                statusLabel.ForeColor = Color.Red;
                startButton.Enabled = true;
                stopButton.Enabled = false;
            }
            
            LogMessage("✅ 화면 검열 중지됨");
        }

        private void ProcessingLoop()
        {
            LogMessage("🔄 CUDA 자동감지 고성능 처리 루프 시작");
            int frameCount = 0;
            
            // 성능 최적화 변수들
            DateTime lastStatsUpdate = DateTime.Now;
            DateTime lastLogTime = DateTime.Now;
            
            // Mat 객체 재사용을 위한 풀 (GC 압박 감소)
            var matPool = new Queue<Mat>();
            const int maxPoolSize = 5;
            
            // UI 업데이트 주기 제어 (UI 스레드 부하 감소)
            int uiUpdateCounter = 0;
            const int uiUpdateInterval = 5;
            
            // 디버그 저장 주기 제어
            int debugSaveCounter = 0;
            const int debugSaveInterval = 180;
            
            try
            {
                while (isRunning)
                {
                    var originalFrame = capturer.GetFrame();
                    if (originalFrame == null || originalFrame.Empty())
                    {
                        Thread.Sleep(1);
                        continue;
                    }
                    
                    frameCount++;
                    stats["frames_processed"] = frameCount;
                    
                    // Mat 풀에서 재사용 가능한 객체 가져오기
                    Mat processedFrame;
                    if (matPool.Count > 0)
                    {
                        processedFrame = matPool.Dequeue();
                        originalFrame.CopyTo(processedFrame);
                    }
                    else
                    {
                        processedFrame = originalFrame.Clone();
                    }
                    
                    var detections = processor.DetectObjects(originalFrame);
                    
                    if (detections != null && detections.Count > 0)
                    {
                        // 개선된 타겟 매칭 (부분 문자열 포함)
                        var targetDetections = detections.Where(d => 
                            processor.Targets.Contains(d.ClassName) || 
                            processor.Targets.Any(target => d.ClassName.Contains(target) || target.Contains(d.ClassName))
                        ).ToList();
                        
                        LogMessage($"🔍 전체 감지: {string.Join(", ", detections.Select(d => d.ClassName))}");
                        LogMessage($"🎯 현재 타겟: {string.Join(", ", processor.Targets)}");
                        LogMessage($"✅ 모자이크 대상: {string.Join(", ", targetDetections.Select(d => d.ClassName))}");
                        
                        if (targetDetections.Count > 0)
                        {
                            // 순차 또는 병렬 처리 선택
                            if (targetDetections.Count <= 2)
                            {
                                foreach (var detection in targetDetections)
                                {
                                    ApplySingleMosaic(processedFrame, detection);
                                }
                            }
                            else
                            {
                                Parallel.ForEach(targetDetections, detection =>
                                {
                                    lock (processedFrame)
                                    {
                                        ApplySingleMosaic(processedFrame, detection);
                                    }
                                });
                            }
                            
                            stats["mosaic_applied"] = (int)stats["mosaic_applied"] + targetDetections.Count;
                        }
                        
                        stats["objects_detected"] = (int)stats["objects_detected"] + detections.Count;
                    }
                    
                    // 오버레이 업데이트
                    overlay.UpdateFrame(processedFrame);
                    
                    // Mat 객체 풀에 반환
                    if (matPool.Count < maxPoolSize)
                    {
                        matPool.Enqueue(processedFrame);
                    }
                    else
                    {
                        processedFrame.Dispose();
                    }
                    
                    // UI 업데이트 주기 제어
                    uiUpdateCounter++;
                    if (uiUpdateCounter >= uiUpdateInterval)
                    {
                        uiUpdateCounter = 0;
                        
                        Task.Run(() => UpdateStats());
                        
                        var now = DateTime.Now;
                        if ((now - lastLogTime).TotalSeconds >= 10)
                        {
                            lastLogTime = now;
                            var fps = frameCount / (now - (DateTime)stats["start_time"]).TotalSeconds;
                            Task.Run(() => LogMessage($"🎯 처리 중: {frameCount}프레임, {fps:F1}fps, 감지:{stats["objects_detected"]}, 모자이크:{stats["mosaic_applied"]}"));
                        }
                    }
                    
                    // 디버그 저장 주기 제어
                    if (debugMode)
                    {
                        debugSaveCounter++;
                        if (debugSaveCounter >= debugSaveInterval && (int)stats["mosaic_applied"] > 0)
                        {
                            debugSaveCounter = 0;
                            
                            var debugFrame = originalFrame.Clone();
                            Task.Run(() =>
                            {
                                try
                                {
                                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                                    var processedPath = $"debug_detection/processed_{timestamp}.jpg";
                                    Cv2.ImWrite(processedPath, debugFrame, new ImageEncodingParam(ImwriteFlags.JpegQuality, 80));
                                }
                                catch { }
                                finally
                                {
                                    debugFrame.Dispose();
                                }
                            });
                        }
                    }
                    
                    // 오버레이 창 상태 확인
                    if (!overlay.IsWindowVisible())
                    {
                        isRunning = false;
                        break;
                    }
                    
                    originalFrame.Dispose();
                    
                    // 즉시 반응을 위한 최소 대기
                    Thread.Sleep(0);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ 처리 오류: {ex.Message}");
            }
            finally
            {
                // Mat 풀 정리
                while (matPool.Count > 0)
                {
                    matPool.Dequeue().Dispose();
                }
                
                if (Root.InvokeRequired)
                {
                    Root.Invoke(new Action(() => StopCensoring(null, null)));
                }
                else
                {
                    StopCensoring(null, null);
                }
            }
        }

        // 단일 모자이크 적용 메서드
        private void ApplySingleMosaic(Mat processedFrame, MosaicCensorSystem.Detection.Detection detection)
        {
            try
            {
                var bbox = detection.BBox;
                int x1 = bbox[0], y1 = bbox[1], x2 = bbox[2], y2 = bbox[3];
                
                if (x2 > x1 && y2 > y1 && x1 >= 0 && y1 >= 0 && x2 <= processedFrame.Width && y2 <= processedFrame.Height)
                {
                    using (var region = new Mat(processedFrame, new Rect(x1, y1, x2 - x1, y2 - y1)))
                    {
                        if (!region.Empty())
                        {
                            using (var mosaicRegion = processor.ApplyMosaic(region, strengthSlider.Value))
                            {
                                mosaicRegion.CopyTo(region);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ 단일 모자이크 적용 오류: {ex.Message}");
            }
        }

        public void Run()
        {
            Console.WriteLine("🛡️ CUDA 자동감지 화면 검열 시스템 시작");
            Console.WriteLine("=" + new string('=', 45));
            
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
            if (isRunning)
            {
                StopCensoring(null, null);
            }
            
            Cleanup();
        }

        private void Cleanup()
        {
            Console.WriteLine("🧹 리소스 정리 중...");
            
            if (isRunning)
            {
                isRunning = false;
            }
            
            if (processThread != null && processThread.IsAlive)
            {
                processThread.Join(1000);
            }
            
            overlay?.Dispose();
            capturer?.Dispose();
            processor?.Dispose();
            
            Console.WriteLine("✅ 리소스 정리 완료");
        }
    }
}