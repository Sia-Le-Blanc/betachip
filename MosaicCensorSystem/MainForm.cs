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
        // 1. 필드/프로퍼티 선언
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
        private Button startButton;
        private Button stopButton;
        private Button testButton;
        
        // 기능 레벨 컨트롤
        private ComboBox featureLevelCombo;
        private Label featureLevelLabel;
        private CheckBox enableDetectionCheckBox;
        private CheckBox enableCensoringCheckBox;
        private TrackBar fpsSlider;
        private Label fpsLabel;
        
        // 스레드 관리
        private readonly object isRunningLock = new object();
        private readonly object statsLock = new object();
        private volatile bool isRunning = false;
        private volatile bool isDisposing = false;
        private Thread processThread;
        
        // 설정값들
        private int targetFPS = 15;
        private float currentConfidence = 0.7f;
        private int currentStrength = 20;
        private bool enableDetection = false;
        private bool enableCensoring = false;
        
        private Dictionary<string, object> stats = new Dictionary<string, object>
        {
            ["frames_processed"] = 0,
            ["objects_detected"] = 0,
            ["censor_applied"] = 0,
            ["start_time"] = null,
            ["detection_time"] = 0.0,
            ["fps"] = 0.0
        };
        
        private bool isDragging = false;
        private System.Drawing.Point dragStartPoint;

        // 2. 생성자
        public MosaicApp()
        {
            Root = new Form
            {
                Text = "점진적 기능 복구 화면 검열 시스템 v6.0 (안전한 단계별 복구)",
                Size = new System.Drawing.Size(500, 850),
                MinimumSize = new System.Drawing.Size(450, 650),
                StartPosition = FormStartPosition.CenterScreen
            };
            
            try
            {
                Console.WriteLine("🔧 점진적 기능 복구 모드로 컴포넌트 초기화 중...");
                
                InitializeSafeComponents();
                CreateGui();
                
                Root.FormClosed += OnFormClosed;
                Root.FormClosing += OnFormClosing;
                
                Console.WriteLine("✅ 점진적 기능 복구 모드 MosaicApp 초기화 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ MosaicApp 초기화 실패: {ex.Message}");
                MessageBox.Show($"초기화 실패: {ex.Message}\n\n프로그램을 종료합니다.", "치명적 오류", 
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                throw;
            }
        }

        // 3. Public 메서드
        public void Run()
        {
            Console.WriteLine("🔄 점진적 기능 복구 화면 검열 시스템 v6.0 시작");
            Console.WriteLine("=" + new string('=', 60));
            
            try
            {
                Application.Run(Root);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n🛑 점진적 복구 모드 오류 발생: {ex.Message}");
                LogMessage($"❌ 애플리케이션 오류: {ex.Message}");
            }
            finally
            {
                Cleanup();
            }
        }

        // 4. Private 초기화 메서드들
        private void InitializeSafeComponents()
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
                overlay = null;
            }
        }

        private void CreateGui()
        {
            var titleLabel = new Label
            {
                Text = "🔄 점진적 기능 복구 화면 검열 시스템 v6.0 (단계별 안전 복구)",
                Font = new Font("Arial", 12, FontStyle.Bold),
                BackColor = Color.SkyBlue,
                BorderStyle = BorderStyle.Fixed3D,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleCenter,
                Height = 40,
                Dock = DockStyle.Top
            };
            
            SetupWindowDragging(titleLabel);
            
            var scrollInfo = new Label
            {
                Text = "⚙️ 점진적 복구: 캡처 성공 → 성능 향상 → 검열 기능 단계별 추가",
                Font = new Font("Arial", 9),
                ForeColor = Color.Blue,
                BackColor = Color.LightCyan,
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
                Text = "💡 하늘색 제목을 드래그해서 창을 이동하세요",
                Font = new Font("Arial", 9),
                ForeColor = Color.Gray,
                Location = new System.Drawing.Point(10, y),
                AutoSize = true
            };
            parent.Controls.Add(dragInfo);
            y += 30;
            
            statusLabel = new Label
            {
                Text = "⭕ 점진적 복구 모드 대기 중",
                Font = new Font("Arial", 12),
                ForeColor = Color.Red,
                Location = new System.Drawing.Point(10, y),
                AutoSize = true
            };
            parent.Controls.Add(statusLabel);
            y += 40;
            
            // 기능 레벨 선택
            var featureLevelGroup = new GroupBox
            {
                Text = "🔄 기능 레벨 선택 (단계별 복구)",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 100)
            };
            
            featureLevelLabel = new Label
            {
                Text = "기능 레벨:",
                Location = new System.Drawing.Point(10, 25),
                AutoSize = true
            };
            featureLevelGroup.Controls.Add(featureLevelLabel);
            
            featureLevelCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new System.Drawing.Point(100, 22),
                Size = new System.Drawing.Size(340, 25)
            };
            featureLevelCombo.Items.AddRange(new string[]
            {
                "레벨 1: 화면 캡처만 (현재 상태)",
                "레벨 2: 캡처 + 성능 향상 (고fps)",
                "레벨 3: 캡처 + 객체 감지 (검열 없음)",
                "레벨 4: 캡처 + 감지 + 모자이크 검열",
                "레벨 5: 전체 기능 (감지 + 검열 + 트래킹)"
            });
            featureLevelCombo.SelectedIndex = 0;
            featureLevelCombo.SelectedIndexChanged += OnFeatureLevelChanged;
            featureLevelGroup.Controls.Add(featureLevelCombo);
            
            var levelInfo = new Label
            {
                Text = "💡 레벨을 점진적으로 올려가며 안정성을 확인하세요",
                ForeColor = Color.Blue,
                Font = new Font("Arial", 9),
                Location = new System.Drawing.Point(10, 55),
                Size = new System.Drawing.Size(440, 35)
            };
            featureLevelGroup.Controls.Add(levelInfo);
            
            parent.Controls.Add(featureLevelGroup);
            y += 110;
            
            // 성능 설정
            var performanceGroup = new GroupBox
            {
                Text = "⚡ 성능 설정",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 120)
            };
            
            var fpsTextLabel = new Label
            {
                Text = "목표 FPS:",
                Location = new System.Drawing.Point(10, 25),
                AutoSize = true
            };
            performanceGroup.Controls.Add(fpsTextLabel);
            
            fpsSlider = new TrackBar
            {
                Minimum = 5,
                Maximum = 60,
                Value = targetFPS,
                TickFrequency = 5,
                Location = new System.Drawing.Point(100, 20),
                Size = new System.Drawing.Size(280, 45)
            };
            fpsSlider.ValueChanged += OnFpsChanged;
            performanceGroup.Controls.Add(fpsSlider);
            
            fpsLabel = new Label
            {
                Text = $"{targetFPS} fps",
                Location = new System.Drawing.Point(390, 25),
                AutoSize = true
            };
            performanceGroup.Controls.Add(fpsLabel);
            
            enableDetectionCheckBox = new CheckBox
            {
                Text = "🔍 객체 감지 활성화",
                Checked = enableDetection,
                Enabled = false,
                Location = new System.Drawing.Point(10, 70),
                AutoSize = true
            };
            enableDetectionCheckBox.CheckedChanged += OnDetectionToggle;
            performanceGroup.Controls.Add(enableDetectionCheckBox);
            
            enableCensoringCheckBox = new CheckBox
            {
                Text = "🎨 검열 효과 활성화",
                Checked = enableCensoring,
                Enabled = false,
                Location = new System.Drawing.Point(200, 70),
                AutoSize = true
            };
            enableCensoringCheckBox.CheckedChanged += OnCensoringToggle;
            performanceGroup.Controls.Add(enableCensoringCheckBox);
            
            parent.Controls.Add(performanceGroup);
            y += 130;

            // 검열 효과 타입 선택 그룹
            var censorTypeGroup = new GroupBox
            {
                Text = "🎨 검열 효과 타입 선택",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 80)
            };

            mosaicRadioButton = new RadioButton
            {
                Text = "🟦 모자이크",
                Checked = true,
                Location = new System.Drawing.Point(20, 25),
                AutoSize = true
            };
            mosaicRadioButton.CheckedChanged += OnCensorTypeChanged;

            blurRadioButton = new RadioButton
            {
                Text = "🌀 블러",
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
                Size = new System.Drawing.Size(460, 120)
            };
            
            var safeTargets = new[]
            {
                "얼굴", "눈", "손", "신발"
            };
            
            var defaultTargets = new List<string> { "얼굴" };
            
            for (int i = 0; i < safeTargets.Length; i++)
            {
                var target = safeTargets[i];
                var row = i / 2;
                var col = i % 2;
                
                var checkbox = new CheckBox
                {
                    Text = target,
                    Checked = defaultTargets.Contains(target),
                    Location = new System.Drawing.Point(15 + col * 200, 30 + row * 30),
                    Size = new System.Drawing.Size(180, 25),
                    AutoSize = false
                };
                
                targetCheckBoxes[target] = checkbox;
                targetsGroup.Controls.Add(checkbox);
            }
            
            var targetNote = new Label
            {
                Text = "💡 안전한 타겟들로 시작합니다",
                ForeColor = Color.Blue,
                Font = new Font("Arial", 9),
                Location = new System.Drawing.Point(15, 90),
                AutoSize = true
            };
            targetsGroup.Controls.Add(targetNote);
            
            parent.Controls.Add(targetsGroup);
            y += 130;
            
            var settingsGroup = new GroupBox
            {
                Text = "⚙️ 고급 설정",
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
                Minimum = 10,
                Maximum = 40,
                Value = currentStrength,
                TickFrequency = 5,
                Location = new System.Drawing.Point(120, 20),
                Size = new System.Drawing.Size(280, 45)
            };
            strengthSlider.ValueChanged += OnStrengthChanged;
            settingsGroup.Controls.Add(strengthSlider);
            
            strengthLabel = new Label
            {
                Text = currentStrength.ToString(),
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
                Minimum = 30,
                Maximum = 90,
                Value = (int)(currentConfidence * 100),
                TickFrequency = 10,
                Location = new System.Drawing.Point(120, 60),
                Size = new System.Drawing.Size(280, 45)
            };
            confidenceSlider.ValueChanged += OnConfidenceChanged;
            settingsGroup.Controls.Add(confidenceSlider);
            
            confidenceLabel = new Label
            {
                Text = currentConfidence.ToString("F1"),
                Location = new System.Drawing.Point(410, 65),
                AutoSize = true
            };
            settingsGroup.Controls.Add(confidenceLabel);
            
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
                Text = "🎮 점진적 복구 모드 컨트롤",
                Font = new Font("Arial", 12, FontStyle.Bold),
                BackColor = Color.LightGray,
                Location = new System.Drawing.Point(10, 10),
                Size = new System.Drawing.Size(440, 25),
                TextAlign = ContentAlignment.MiddleCenter
            };
            controlPanel.Controls.Add(buttonLabel);
            
            startButton = new Button
            {
                Text = "🔄 점진적 복구 시작",
                BackColor = Color.RoyalBlue,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold),
                Size = new System.Drawing.Size(120, 50),
                Location = new System.Drawing.Point(20, 40)
            };
            startButton.Click += StartProgressive;
            controlPanel.Controls.Add(startButton);
            
            stopButton = new Button
            {
                Text = "🛑 중지",
                BackColor = Color.Red,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold),
                Size = new System.Drawing.Size(120, 50),
                Location = new System.Drawing.Point(170, 40),
                Enabled = false
            };
            stopButton.Click += StopProgressive;
            controlPanel.Controls.Add(stopButton);
            
            testButton = new Button
            {
                Text = "🔍 캡처 테스트",
                BackColor = Color.Green,
                ForeColor = Color.White,
                Font = new Font("Arial", 10, FontStyle.Bold),
                Size = new System.Drawing.Size(120, 50),
                Location = new System.Drawing.Point(320, 40)
            };
            testButton.Click += TestCapture;
            controlPanel.Controls.Add(testButton);
            
            parent.Controls.Add(controlPanel);
            y += 110;
            
            var logGroup = new GroupBox
            {
                Text = "📝 점진적 복구 로그",
                Location = new System.Drawing.Point(10, y),
                Size = new System.Drawing.Size(460, 120)
            };
            
            logTextBox = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                Location = new System.Drawing.Point(10, 20),
                Size = new System.Drawing.Size(440, 90)
            };
            logGroup.Controls.Add(logTextBox);
            parent.Controls.Add(logGroup);
        }

        // 5. 이벤트 핸들러들
        private void OnFeatureLevelChanged(object sender, EventArgs e)
        {
            int level = featureLevelCombo.SelectedIndex + 1;
            LogMessage($"🔄 기능 레벨 변경: 레벨 {level}");
            
            switch (level)
            {
                case 1: // 캡처만
                    enableDetectionCheckBox.Enabled = false;
                    enableDetectionCheckBox.Checked = false;
                    enableCensoringCheckBox.Enabled = false;
                    enableCensoringCheckBox.Checked = false;
                    fpsSlider.Maximum = 30;
                    LogMessage("📋 레벨 1: 화면 캡처만 활성화");
                    break;
                    
                case 2: // 캡처 + 성능 향상
                    enableDetectionCheckBox.Enabled = false;
                    enableDetectionCheckBox.Checked = false;
                    enableCensoringCheckBox.Enabled = false;
                    enableCensoringCheckBox.Checked = false;
                    fpsSlider.Maximum = 60;
                    LogMessage("📋 레벨 2: 고성능 캡처 모드");
                    break;
                    
                case 3: // 캡처 + 감지
                    enableDetectionCheckBox.Enabled = true;
                    enableDetectionCheckBox.Checked = true;
                    enableCensoringCheckBox.Enabled = false;
                    enableCensoringCheckBox.Checked = false;
                    fpsSlider.Maximum = 40;
                    LogMessage("📋 레벨 3: 객체 감지 추가 (검열 없음)");
                    break;
                    
                case 4: // 캡처 + 감지 + 검열
                    enableDetectionCheckBox.Enabled = true;
                    enableDetectionCheckBox.Checked = true;
                    enableCensoringCheckBox.Enabled = true;
                    enableCensoringCheckBox.Checked = true;
                    fpsSlider.Maximum = 30;
                    LogMessage("📋 레벨 4: 기본 검열 기능 추가");
                    break;
                    
                case 5: // 전체 기능
                    enableDetectionCheckBox.Enabled = true;
                    enableDetectionCheckBox.Checked = true;
                    enableCensoringCheckBox.Enabled = true;
                    enableCensoringCheckBox.Checked = true;
                    fpsSlider.Maximum = 25;
                    LogMessage("📋 레벨 5: 전체 기능 활성화");
                    break;
            }
            
            enableDetection = enableDetectionCheckBox.Checked;
            enableCensoring = enableCensoringCheckBox.Checked;
        }

        private void OnFpsChanged(object sender, EventArgs e)
        {
            targetFPS = fpsSlider.Value;
            fpsLabel.Text = $"{targetFPS} fps";
            LogMessage($"⚡ 목표 FPS 변경: {targetFPS}");
        }

        private void OnDetectionToggle(object sender, EventArgs e)
        {
            enableDetection = enableDetectionCheckBox.Checked;
            LogMessage($"🔍 객체 감지: {(enableDetection ? "활성화" : "비활성화")}");
        }

        private void OnCensoringToggle(object sender, EventArgs e)
        {
            enableCensoring = enableCensoringCheckBox.Checked;
            LogMessage($"🎨 검열 효과: {(enableCensoring ? "활성화" : "비활성화")}");
        }

        private void OnCensorTypeChanged(object sender, EventArgs e)
        {
            if (sender is RadioButton radioButton && radioButton.Checked)
            {
                try
                {
                    CensorType newType = mosaicRadioButton.Checked ? CensorType.Mosaic : CensorType.Blur;
                    processor?.SetCensorType(newType);
                    
                    string typeText = newType == CensorType.Mosaic ? "모자이크" : "블러";
                    censorTypeLabel.Text = $"현재: {typeText}";
                    
                    LogMessage($"🎨 검열 타입 변경: {typeText}");
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ 검열 타입 변경 오류: {ex.Message}");
                }
            }
        }

        private void OnStrengthChanged(object sender, EventArgs e)
        {
            currentStrength = strengthSlider.Value;
            strengthLabel.Text = currentStrength.ToString();
            processor?.SetStrength(currentStrength);
            LogMessage($"💪 검열 강도 변경: {currentStrength}");
        }

        private void OnConfidenceChanged(object sender, EventArgs e)
        {
            currentConfidence = confidenceSlider.Value / 100.0f;
            confidenceLabel.Text = currentConfidence.ToString("F1");
            if (processor != null)
                processor.ConfThreshold = currentConfidence;
            LogMessage($"🔍 신뢰도 변경: {currentConfidence:F1}");
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
                        StopProgressive(null, null);
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

        // 6. 기능 메서드들
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
                
                Mat testFrame = null;
                
                try
                {
                    testFrame = capturer.GetFrame();
                    
                    if (testFrame != null && !testFrame.Empty())
                    {
                        LogMessage($"✅ 캡처 성공! 크기: {testFrame.Width}x{testFrame.Height}");
                        
                        string testPath = Path.Combine(Environment.CurrentDirectory, "capture_test.jpg");
                        testFrame.SaveImage(testPath);
                        LogMessage($"💾 테스트 이미지 저장됨: {testPath}");
                        
                        MessageBox.Show($"캡처 테스트 성공!\n\n크기: {testFrame.Width}x{testFrame.Height}\n저장: {testPath}", 
                                      "테스트 성공", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        LogMessage("❌ 캡처 실패");
                        MessageBox.Show("캡처 실패!", "테스트 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                finally
                {
                    testFrame?.Dispose();
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ 테스트 오류: {ex.Message}");
                MessageBox.Show($"테스트 오류: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StartProgressive(object sender, EventArgs e)
        {
            try
            {
                Console.WriteLine("🔄 점진적 복구 모드 StartProgressive 시작");
                
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

                // 선택된 기능 레벨 확인
                int level = featureLevelCombo.SelectedIndex + 1;
                string levelDescription = featureLevelCombo.SelectedItem.ToString();
                
                var selectedTargets = new List<string>();
                foreach (var kvp in targetCheckBoxes)
                {
                    if (kvp.Value.Checked)
                        selectedTargets.Add(kvp.Key);
                }

                if (selectedTargets.Count == 0)
                    selectedTargets.Add("얼굴"); // 기본값

                LogMessage($"🎯 선택된 타겟들: {string.Join(", ", selectedTargets)}");

                var result = MessageBox.Show(
                    $"점진적 복구 모드로 시작하시겠습니까?\n\n" +
                    $"• {levelDescription}\n" +
                    $"• 목표 FPS: {targetFPS}\n" +
                    $"• 객체 감지: {(enableDetection ? "활성화" : "비활성화")}\n" +
                    $"• 검열 효과: {(enableCensoring ? "활성화" : "비활성화")}\n" +
                    $"• 타겟: {string.Join(", ", selectedTargets)}\n\n" +
                    "계속하시겠습니까?",
                    "점진적 복구 모드 시작 확인",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                
                if (result != DialogResult.Yes)
                    return;
                
                // 컴포넌트 상태 확인
                if (capturer == null)
                {
                    MessageBox.Show("화면 캡처 모듈이 초기화되지 않았습니다!", "오류",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                
                if (overlay == null)
                {
                    MessageBox.Show("오버레이가 초기화되지 않았습니다!", "오류",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                
                if (enableDetection && (processor == null || !processor.IsModelLoaded()))
                {
                    MessageBox.Show("객체 감지가 활성화되었지만 프로세서가 준비되지 않았습니다!\n\n" +
                        "레벨을 낮추거나 프로그램을 다시 시작해주세요.", "오류",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                
                // 프로세서 설정
                if (processor != null && enableDetection)
                {
                    processor.SetTargets(selectedTargets);
                    processor.SetStrength(currentStrength);
                    processor.ConfThreshold = currentConfidence;
                    processor.SetCensorType(mosaicRadioButton.Checked ? CensorType.Mosaic : CensorType.Blur);
                }
                
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
                    stats["detection_time"] = 0.0;
                    stats["fps"] = 0.0;
                }
                
                statusLabel.Text = $"✅ 레벨 {level} 실행 중 ({targetFPS}fps)";
                statusLabel.ForeColor = Color.Blue;
                startButton.Enabled = false;
                stopButton.Enabled = true;
                featureLevelCombo.Enabled = false;
                
                // 오버레이 시작
                try
                {
                    if (!overlay.Show())
                    {
                        LogMessage("❌ 풀스크린 오버레이 시작 실패");
                        StopProgressive(null, null);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ 오버레이 시작 오류: {ex.Message}");
                    StopProgressive(null, null);
                    return;
                }
                
                // 처리 스레드 시작
                try
                {
                    processThread = new Thread(ProgressiveProcessingLoop)
                    {
                        Name = "ProgressiveProcessingThread",
                        IsBackground = true,
                        Priority = ThreadPriority.Normal
                    };
                    processThread.SetApartmentState(ApartmentState.MTA);
                    processThread.Start();
                    
                    LogMessage($"🔄 점진적 복구 모드 시작! 레벨={level}, FPS={targetFPS}");
                    LogMessage($"⚙️ 설정: 감지={enableDetection}, 검열={enableCensoring}, 타겟={string.Join(",", selectedTargets)}");
                }
                catch (Exception ex)
                {
                    LogMessage($"❌ 처리 스레드 생성 실패: {ex.Message}");
                    StopProgressive(null, null);
                    return;
                }
                
                Console.WriteLine("🔄 점진적 복구 모드 StartProgressive 완료!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"💥 점진적 복구 모드 StartProgressive 오류: {ex.Message}");
                LogMessage($"❌ 시작 오류: {ex.Message}");
                
                try
                {
                    StopProgressive(null, null);
                }
                catch { }
            }
        }

        private void StopProgressive(object sender, EventArgs e)
        {
            try
            {
                lock (isRunningLock)
                {
                    if (!isRunning)
                        return;
                    
                    isRunning = false;
                }
                
                LogMessage("🛑 점진적 복구 모드 중지 중...");
                
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
                                    statusLabel.Text = "⭕ 점진적 복구 모드 대기 중";
                                    statusLabel.ForeColor = Color.Red;
                                    startButton.Enabled = true;
                                    stopButton.Enabled = false;
                                    featureLevelCombo.Enabled = true;
                                }
                            }
                            catch { }
                        }));
                    }
                }
                
                LogMessage("✅ 점진적 복구 모드 중지됨");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ StopProgressive 오류: {ex.Message}");
            }
        }

        // 7. 메인 처리 루프
        private void ProgressiveProcessingLoop()
        {
            LogMessage("🔄 점진적 복구 ProcessingLoop 시작");
            int frameCount = 0;
            DateTime lastLogTime = DateTime.Now;
            var frameTimes = new List<double>();
            var detectionTimes = new List<double>();
            
            int frameskip = Math.Max(1, 60 / targetFPS); // 목표 FPS에 따른 프레임 스킵
            
            try
            {
                LogMessage($"🔄 처리 루프 진입 - 목표 FPS: {targetFPS}, 프레임 스킵: {frameskip}");
                
                while (true)
                {
                    var frameStartTime = DateTime.Now;
                    
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
                            LogMessage("🛑 점진적 복구 ProcessingLoop 정상 종료");
                            break;
                        }
                        
                        frameCount++;
                        Mat capturedFrame = null;
                        Mat processedFrame = null;
                        
                        try
                        {
                            // STEP 1: 화면 캡처 (모든 레벨에서 수행)
                            if (frameCount % frameskip == 0) // 프레임 스킵 적용
                            {
                                try
                                {
                                    if (capturer != null)
                                    {
                                        capturedFrame = capturer.GetFrame();
                                        
                                        if (capturedFrame != null && !capturedFrame.Empty())
                                        {
                                            processedFrame = capturedFrame.Clone();
                                        }
                                    }
                                }
                                catch (Exception captureEx)
                                {
                                    LogMessage($"❌ 캡처 오류: {captureEx.Message}");
                                    Thread.Sleep(100);
                                    continue;
                                }
                                
                                if (processedFrame == null || processedFrame.Empty())
                                {
                                    Thread.Sleep(50);
                                    continue;
                                }
                                
                                // STEP 2: 객체 감지 (레벨 3+ 에서만)
                                List<MosaicCensorSystem.Detection.Detection> detections = null;
                                if (enableDetection && processor != null)
                                {
                                    var detectionStart = DateTime.Now;
                                    try
                                    {
                                        detections = processor.DetectObjects(capturedFrame);
                                        
                                        var detectionTime = (DateTime.Now - detectionStart).TotalMilliseconds;
                                        detectionTimes.Add(detectionTime);
                                        if (detectionTimes.Count > 50)
                                            detectionTimes.RemoveRange(0, 25);
                                    }
                                    catch (Exception detectEx)
                                    {
                                        LogMessage($"❌ 감지 오류: {detectEx.Message}");
                                    }
                                }
                                
                                // STEP 3: 검열 효과 적용 (레벨 4+ 에서만)
                                if (enableCensoring && detections != null && detections.Count > 0)
                                {
                                    try
                                    {
                                        int appliedCount = 0;
                                        
                                        // 최대 3개만 처리 (성능 고려)
                                        foreach (var detection in detections.Take(3))
                                        {
                                            if (processor != null)
                                            {
                                                processor.ApplySingleCensorOptimized(processedFrame, detection);
                                                appliedCount++;
                                            }
                                        }
                                        
                                        if (appliedCount > 0)
                                        {
                                            lock (statsLock)
                                            {
                                                stats["censor_applied"] = (int)stats["censor_applied"] + appliedCount;
                                                stats["objects_detected"] = (int)stats["objects_detected"] + detections.Count;
                                            }
                                        }
                                    }
                                    catch (Exception censorEx)
                                    {
                                        LogMessage($"❌ 검열 오류: {censorEx.Message}");
                                    }
                                }
                                
                                // STEP 4: 오버레이 업데이트 (모든 레벨에서 수행)
                                try
                                {
                                    if (overlay != null && overlay.IsWindowVisible())
                                    {
                                        overlay.UpdateFrame(processedFrame);
                                    }
                                }
                                catch (Exception overlayEx)
                                {
                                    LogMessage($"❌ 오버레이 오류: {overlayEx.Message}");
                                }
                                
                                // 통계 업데이트
                                lock (statsLock)
                                {
                                    stats["frames_processed"] = frameCount;
                                }
                            }
                            
                            // 프레임 시간 기록
                            var frameTime = (DateTime.Now - frameStartTime).TotalMilliseconds;
                            frameTimes.Add(frameTime);
                            if (frameTimes.Count > 100)
                                frameTimes.RemoveRange(0, 50);
                            
                            // 로그 출력 (30초마다)
                            var now = DateTime.Now;
                            if ((now - lastLogTime).TotalSeconds >= 30)
                            {
                                lastLogTime = now;
                                
                                lock (statsLock)
                                {
                                    if (stats["start_time"] != null)
                                    {
                                        var totalSeconds = (now - (DateTime)stats["start_time"]).TotalSeconds;
                                        var actualFps = frameCount / totalSeconds;
                                        var avgFrameTime = frameTimes.Count > 0 ? frameTimes.Average() : 0;
                                        var avgDetectionTime = detectionTimes.Count > 0 ? detectionTimes.Average() : 0;
                                        
                                        stats["fps"] = actualFps;
                                        stats["detection_time"] = avgDetectionTime;
                                        
                                        LogMessage($"🔄 성능: {actualFps:F1}fps (목표:{targetFPS}), 프레임:{avgFrameTime:F1}ms, 감지:{avgDetectionTime:F1}ms");
                                        LogMessage($"📊 통계: 프레임:{frameCount}, 감지:{stats["objects_detected"]}, 검열:{stats["censor_applied"]}");
                                    }
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
                            
                            // 목표 FPS에 맞춘 대기
                            int targetDelay = 1000 / targetFPS;
                            int actualDelay = Math.Max(1, targetDelay - (int)frameTime);
                            Thread.Sleep(actualDelay);
                        }
                        catch (Exception frameEx)
                        {
                            LogMessage($"❌ 프레임 처리 오류: {frameEx.Message}");
                            Thread.Sleep(100);
                        }
                        finally
                        {
                            // 안전한 리소스 정리
                            try
                            {
                                capturedFrame?.Dispose();
                                processedFrame?.Dispose();
                            }
                            catch { }
                        }
                        
                        // 강제 GC (200프레임마다)
                        if (frameCount % 200 == 0)
                        {
                            try
                            {
                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                                GC.Collect();
                            }
                            catch { }
                        }
                    }
                    catch (Exception loopEx)
                    {
                        LogMessage($"❌ 루프 오류 (복구됨): {loopEx.Message}");
                        Thread.Sleep(500);
                    }
                }
            }
            catch (Exception fatalEx)
            {
                LogMessage($"💥 점진적 복구 ProcessingLoop 치명적 오류: {fatalEx.Message}");
                
                try
                {
                    File.AppendAllText("progressive_error.log", 
                        $"{DateTime.Now}: PROGRESSIVE FATAL - {fatalEx}\n================\n");
                }
                catch { }
            }
            finally
            {
                LogMessage("🧹 점진적 복구 ProcessingLoop 정리");
                
                try
                {
                    if (!isDisposing && Root?.IsHandleCreated == true && !Root.IsDisposed)
                    {
                        Root.BeginInvoke(new Action(() => StopProgressive(null, null)));
                    }
                }
                catch { }
                
                LogMessage("🏁 점진적 복구 ProcessingLoop 완전 종료");
            }
        }

        // 8. 유틸리티 메서드들
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
                                if (!isDisposing && logTextBox != null && !logTextBox.IsDisposed)
                                {
                                    logTextBox.AppendText(fullMessage + Environment.NewLine);
                                    
                                    if (logTextBox.Lines.Length > 30)
                                    {
                                        var lines = logTextBox.Lines.Skip(15).ToArray();
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
        }

        private void Cleanup()
        {
            Console.WriteLine("🧹 점진적 복구 모드 리소스 정리 중...");
            
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
                    Console.WriteLine($"❌ 프로세서 정리 오류: {ex.Message}");
                }
                
                try
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                catch { }
                
                Console.WriteLine("✅ 점진적 복구 모드 리소스 정리 완료");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 정리 중 오류: {ex.Message}");
            }
        }
    }
}