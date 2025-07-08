#nullable disable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MosaicCensorSystem.UI
{
    /// <summary>
    /// 신호 클래스 (콜백 관리)
    /// </summary>
    public class Signal
    {
        private readonly List<Action> callbacks = new List<Action>();

        public void Connect(Action callback)
        {
            if (callback != null)
            {
                callbacks.Add(callback);
            }
        }

        public void Emit()
        {
            foreach (var callback in callbacks)
            {
                try
                {
                    callback?.Invoke();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ 신호 실행 오류: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// 메인 윈도우 클래스 (간단한 스크롤)
    /// </summary>
    public class MainWindow : Form
    {
        // 설정 및 상태 변수
        public int Strength { get; private set; }
        public List<string> Targets { get; private set; }
        public bool Running { get; private set; }
        public string RenderModeInfo { get; private set; }

        // UI 컨트롤
        private readonly Dictionary<string, CheckBox> checkboxes = new Dictionary<string, CheckBox>();
        private Label statusLabel;
        private Label renderModeLabel;
        private TrackBar strengthSlider;
        private Label strengthLabel;
        private TrackBar confidenceSlider;
        private Label confidenceLabel;

        // 콜백 함수
        public Action StartCallback { get; set; }
        public Action StopCallback { get; set; }

        // 드래그 관련
        private bool isDragging = false;
        private Point dragStartPoint;

        // 스크롤 가능한 컨테이너
        private ScrollablePanel scrollableContainer;

        public MainWindow(Dictionary<string, object> config = null)
        {
            config ??= Config.GetSection("mosaic");

            // 설정 초기화
            Strength = Convert.ToInt32(config.GetValueOrDefault("default_strength", 25));

            var defaultTargets = new List<string> { "얼굴", "가슴", "보지", "팬티" };
            if (config.GetValueOrDefault("default_targets", defaultTargets) is List<string> targets)
            {
                Targets = targets;
            }
            else
            {
                Targets = defaultTargets;
            }

            Running = false;
            RenderModeInfo = "기본 모드";

            // 윈도우 설정
            Text = "베타 칩";
            Size = new Size(400, 600);
            MinimumSize = new Size(350, 400);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;

            // UI 생성
            CreateWidgets();
        }

        private void CreateWidgets()
        {
            SuspendLayout();

            // 드래그 가능한 제목 바
            var titlePanel = new Panel
            {
                BackColor = Color.LightBlue,
                Height = 40,
                Dock = DockStyle.Top,
                BorderStyle = BorderStyle.Fixed3D,
                Cursor = Cursors.Hand
            };

            var titleLabel = new Label
            {
                Text = "🔐 베타 칩",
                Font = new Font("Arial", 10, FontStyle.Bold),
                BackColor = Color.LightBlue,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };

            titlePanel.Controls.Add(titleLabel);

            // 드래그 이벤트
            titlePanel.MouseDown += OnTitleMouseDown;
            titlePanel.MouseMove += OnTitleMouseMove;
            titlePanel.MouseUp += OnTitleMouseUp;
            titleLabel.MouseDown += OnTitleMouseDown;
            titleLabel.MouseMove += OnTitleMouseMove;
            titleLabel.MouseUp += OnTitleMouseUp;

            // 스크롤 안내
            var infoLabel = new Label
            {
                Text = "📜 마우스 휠로 스크롤 또는 우측 스크롤바 드래그",
                Font = new Font("Arial", 9),
                BackColor = Color.LightYellow,
                ForeColor = Color.Blue,
                Height = 25,
                Dock = DockStyle.Top,
                TextAlign = ContentAlignment.MiddleCenter
            };

            // 스크롤 가능한 메인 영역
            scrollableContainer = new ScrollablePanel
            {
                Dock = DockStyle.Fill
            };

            // 컨트롤 추가 순서 (아래부터)
            Controls.Add(scrollableContainer);
            Controls.Add(infoLabel);
            Controls.Add(titlePanel);

            // 실제 내용 생성
            CreateContent(scrollableContainer.ScrollableFrame);

            ResumeLayout(false);
            PerformLayout();
        }

        private void CreateContent(Panel parent)
        {
            if (parent == null) return;

            parent.SuspendLayout();

            // 메인 패널
            var mainPanel = new Panel
            {
                Padding = new Padding(20),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };

            int y = 0;

            // 모자이크 강도
            strengthLabel = new Label
            {
                Text = $"모자이크 강도: {Strength}",
                Location = new Point(0, y),
                AutoSize = true
            };
            y += 25;

            strengthSlider = new TrackBar
            {
                Minimum = 5,
                Maximum = 50,
                Value = Strength,
                TickFrequency = 5,
                Location = new Point(0, y),
                Width = 350
            };
            strengthSlider.ValueChanged += OnStrengthChanged;
            y += 50;

            // 렌더 모드 라벨
            renderModeLabel = new Label
            {
                Text = RenderModeInfo,
                Location = new Point(0, y),
                AutoSize = true
            };
            y += 30;

            // 검열 대상 프레임
            var targetsGroup = new GroupBox
            {
                Text = "검열 대상",
                Location = new Point(0, y),
                Size = new Size(350, 200)
            };

            // 대상 옵션 체크박스
            var options = new[]
            {
                "얼굴", "눈", "손", "가슴", "보지", "팬티",
                "겨드랑이", "자지", "몸 전체", "교미", "신발",
                "가슴_옷", "보지_옷", "여성"
            };

            const int checkY = 20;
            for (int i = 0; i < options.Length; i++)
            {
                var option = options[i];
                int row = i / 2;
                int col = i % 2;

                var checkbox = new CheckBox
                {
                    Text = option,
                    Checked = Targets.Contains(option),
                    Location = new Point(10 + col * 170, checkY + row * 25),
                    AutoSize = true
                };

                checkboxes[option] = checkbox;
                targetsGroup.Controls.Add(checkbox);
            }
            y += 220;

            // 추가 설정 프레임
            var settingsGroup = new GroupBox
            {
                Text = "추가 설정",
                Location = new Point(0, y),
                Size = new Size(350, 100)
            };

            // 신뢰도 설정
            confidenceLabel = new Label
            {
                Text = "감지 신뢰도: 0.1",
                Location = new Point(10, 20),
                AutoSize = true
            };

            confidenceSlider = new TrackBar
            {
                Minimum = 1,
                Maximum = 9,
                Value = 1,
                TickFrequency = 1,
                Location = new Point(10, 45),
                Width = 330
            };
            confidenceSlider.ValueChanged += OnConfidenceChanged;

            settingsGroup.Controls.Add(confidenceLabel);
            settingsGroup.Controls.Add(confidenceSlider);
            y += 120;

            // 버튼 프레임
            var buttonPanel = new Panel
            {
                BackColor = Color.LightGray,
                BorderStyle = BorderStyle.Fixed3D,
                Location = new Point(0, y),
                Size = new Size(350, 100)
            };

            var startButton = new Button
            {
                Text = "🚀 검열 시작",
                BackColor = Color.Green,
                ForeColor = Color.White,
                Font = new Font("Arial", 14, FontStyle.Bold),
                Size = new Size(140, 60),
                Location = new Point(20, 20),
                UseVisualStyleBackColor = false
            };
            startButton.Click += OnStartClicked;

            var stopButton = new Button
            {
                Text = "🛑 검열 중지",
                BackColor = Color.Red,
                ForeColor = Color.White,
                Font = new Font("Arial", 14, FontStyle.Bold),
                Size = new Size(140, 60),
                Location = new Point(190, 20),
                UseVisualStyleBackColor = false
            };
            stopButton.Click += OnStopClicked;

            buttonPanel.Controls.Add(startButton);
            buttonPanel.Controls.Add(stopButton);
            y += 120;

            // 상태 표시 프레임
            var statusGroup = new GroupBox
            {
                Text = "상태",
                Location = new Point(0, y),
                Size = new Size(350, 60)
            };

            statusLabel = new Label
            {
                Text = "⭕ 대기 중",
                Font = new Font("Arial", 12),
                ForeColor = Color.Red,
                Location = new Point(10, 25),
                AutoSize = true
            };

            statusGroup.Controls.Add(statusLabel);
            y += 80;

            // 스크롤 테스트
            var testGroup = new GroupBox
            {
                Text = "스크롤 테스트",
                Location = new Point(0, y),
                Size = new Size(350, 150)
            };

            var testTexts = new[]
            {
                "✅ 여기까지 스크롤이 되었다면 성공!",
                "✅ 위로 스크롤해서 버튼들을 사용하세요",
                "✅ 마우스 휠로 쉽게 스크롤 가능",
                "✅ 우측 스크롤바도 드래그 가능",
                "✅ 제목바 드래그로 창 이동 가능"
            };

            int testY = 20;
            foreach (var text in testTexts)
            {
                var label = new Label
                {
                    Text = text,
                    Location = new Point(10, testY),
                    AutoSize = true
                };
                testGroup.Controls.Add(label);
                testY += 20;
            }

            // 컨트롤 추가
            mainPanel.Controls.Add(strengthLabel);
            mainPanel.Controls.Add(strengthSlider);
            mainPanel.Controls.Add(renderModeLabel);
            mainPanel.Controls.Add(targetsGroup);
            mainPanel.Controls.Add(settingsGroup);
            mainPanel.Controls.Add(buttonPanel);
            mainPanel.Controls.Add(statusGroup);
            mainPanel.Controls.Add(testGroup);

            parent.Controls.Add(mainPanel);
            parent.ResumeLayout(false);
            parent.PerformLayout();
        }

        private void OnTitleMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                isDragging = true;
                dragStartPoint = e.Location;
            }
        }

        private void OnTitleMouseMove(object sender, MouseEventArgs e)
        {
            if (isDragging)
            {
                Point p = PointToScreen(e.Location);
                Location = new Point(p.X - dragStartPoint.X, p.Y - dragStartPoint.Y);
            }
        }

        private void OnTitleMouseUp(object sender, MouseEventArgs e)
        {
            isDragging = false;
        }

        private void OnStrengthChanged(object sender, EventArgs e)
        {
            if (sender is TrackBar slider)
            {
                Strength = slider.Value;
                if (strengthLabel != null)
                {
                    strengthLabel.Text = $"모자이크 강도: {Strength}";
                }
            }
        }

        private void OnConfidenceChanged(object sender, EventArgs e)
        {
            if (sender is TrackBar slider)
            {
                float confidence = slider.Value / 10.0f;
                if (confidenceLabel != null)
                {
                    confidenceLabel.Text = $"감지 신뢰도: {confidence:F1}";
                }
            }
        }

        private void OnStartClicked(object sender, EventArgs e)
        {
            Console.WriteLine("🖱️ 검열 시작 버튼 클릭됨");
            Running = true;
            Targets = GetSelectedTargets();
            Console.WriteLine($"🎯 선택된 타겟: {string.Join(", ", Targets)}");

            if (statusLabel != null)
            {
                statusLabel.Text = "✅ 검열 중";
                statusLabel.ForeColor = Color.Green;
            }

            try
            {
                if (StartCallback != null)
                {
                    Console.WriteLine("✅ 검열 시작 콜백 실행");
                    StartCallback();
                }
                else
                {
                    Console.WriteLine("⚠️ 검열 시작 콜백이 설정되지 않았습니다");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 시작 콜백 실행 오류: {ex.Message}");
            }
        }

        private void OnStopClicked(object sender, EventArgs e)
        {
            Console.WriteLine("🖱️ 검열 중지 버튼 클릭됨");
            Running = false;

            if (statusLabel != null)
            {
                statusLabel.Text = "⭕ 대기 중";
                statusLabel.ForeColor = Color.Red;
            }

            try
            {
                if (StopCallback != null)
                {
                    Console.WriteLine("✅ 검열 중지 콜백 실행");
                    StopCallback();
                }
                else
                {
                    Console.WriteLine("⚠️ 검열 중지 콜백이 설정되지 않았습니다");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 중지 콜백 실행 오류: {ex.Message}");
            }
        }

        public List<string> GetSelectedTargets()
        {
            var selected = new List<string>();

            foreach (var kvp in checkboxes)
            {
                if (kvp.Value?.Checked == true)
                {
                    selected.Add(kvp.Key);
                }
            }

            // 아무것도 선택되지 않았으면 첫 번째 항목 선택
            if (selected.Count == 0 && checkboxes.Count > 0)
            {
                var firstKey = checkboxes.Keys.First();
                if (checkboxes[firstKey] != null)
                {
                    checkboxes[firstKey].Checked = true;
                    selected.Add(firstKey);
                }
            }

            return selected;
        }

        public int GetStrength()
        {
            return Strength;
        }

        public void SetRenderModeInfo(string infoText)
        {
            RenderModeInfo = infoText ?? "기본 모드";

            if (renderModeLabel != null)
            {
                renderModeLabel.Text = RenderModeInfo;
            }
        }

        public new void Show()
        {
            try
            {
                base.Show();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ 창 표시 오류: {ex.Message}");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 컨트롤들 정리
                strengthSlider?.Dispose();
                confidenceSlider?.Dispose();
                scrollableContainer?.Dispose();

                foreach (var checkbox in checkboxes.Values)
                {
                    checkbox?.Dispose();
                }
                checkboxes.Clear();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// GUI 컨트롤러 클래스
    /// </summary>
    public class GUIController : MainWindow
    {
        public Signal StartCensoringSignal { get; }
        public Signal StopCensoringSignal { get; }
        public List<string> GetSelectedModelClasses()
        {
            var selectedKorean = GetSelectedTargets();

            return selectedKorean
                .Select(kor => classNameMap.TryGetValue(kor, out var eng) ? eng : null)
                .Where(eng => !string.IsNullOrEmpty(eng))
                .ToList();
        }

        // 한글 → 영어 클래스 이름 매핑
        private static readonly Dictionary<string, string> classNameMap = new()
        {
            { "얼굴", "face" },
            { "눈", "eye" },
            { "손", "hand" },
            { "가슴", "breast" },
            { "보지", "vagina" },
            { "팬티", "panty" },
            { "가슴_옷", "chest_clothes" },
            { "몸 전체", "body" },
            { "교미", "intercourse" },
            { "겨드랑이", "armpit" }
        };
        


        public GUIController(Dictionary<string, object> config = null) : base(config)
        {
            StartCensoringSignal = new Signal();
            StopCensoringSignal = new Signal();

            StartCallback = () => StartCensoringSignal.Emit();
            StopCallback = () => StopCensoringSignal.Emit();

            TopMost = true;

            Console.WriteLine("✅ Windows Forms GUI 컨트롤러 초기화 완료 (간단한 스크롤)");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StartCensoringSignal?.Emit();
                StopCensoringSignal?.Emit();
            }
            base.Dispose(disposing);
        }
    }
    
    
}