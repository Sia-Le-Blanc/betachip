using System;
using System.Drawing;
using System.Windows.Forms;

namespace MosaicCensorSystem.UI
{
    /// <summary>
    /// 스크롤 가능한 패널 클래스
    /// </summary>
    public class ScrollablePanel : UserControl
    {
        private Panel canvasPanel;
        private VScrollBar scrollBar;
        private Panel scrollableFrame;

        public Panel ScrollableFrame => scrollableFrame;

        public ScrollablePanel()
        {
            InitializeComponents();
        }

        private void InitializeComponents()
        {
            // Canvas 패널
            canvasPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = false
            };

            // 스크롤바
            scrollBar = new VScrollBar
            {
                Dock = DockStyle.Right,
                Minimum = 0,
                Maximum = 100,
                LargeChange = 10,
                SmallChange = 1
            };

            // 스크롤 가능한 프레임
            scrollableFrame = new Panel
            {
                Location = new Point(0, 0),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink
            };

            // 이벤트 연결
            scrollBar.Scroll += OnScrollBarScroll;
            scrollableFrame.SizeChanged += OnScrollableFrameSizeChanged;
            canvasPanel.MouseWheel += OnCanvasPanelMouseWheel;

            // 컨트롤 추가
            canvasPanel.Controls.Add(scrollableFrame);
            Controls.Add(canvasPanel);
            Controls.Add(scrollBar);

            // 마우스 휠 이벤트 전파
            BindMouseWheelRecursive(this);
        }

        private void OnScrollBarScroll(object sender, ScrollEventArgs e)
        {
            UpdateScrollPosition();
        }

        private void OnScrollableFrameSizeChanged(object sender, EventArgs e)
        {
            UpdateScrollBarRange();
        }

        private void OnCanvasPanelMouseWheel(object sender, MouseEventArgs e)
        {
            HandleMouseWheel(e.Delta);
        }

        private void HandleMouseWheel(int delta)
        {
            int scrollAmount = SystemInformation.MouseWheelScrollLines * 3;
            int newValue = scrollBar.Value - (delta / 120) * scrollAmount;
            
            newValue = Math.Max(scrollBar.Minimum, Math.Min(scrollBar.Maximum - scrollBar.LargeChange + 1, newValue));
            scrollBar.Value = newValue;
            
            UpdateScrollPosition();
        }

        private void UpdateScrollBarRange()
        {
            if (scrollableFrame.Height > canvasPanel.Height)
            {
                scrollBar.Visible = true;
                scrollBar.Maximum = scrollableFrame.Height;
                scrollBar.LargeChange = canvasPanel.Height;
                
                if (scrollBar.Value > scrollBar.Maximum - scrollBar.LargeChange + 1)
                {
                    scrollBar.Value = scrollBar.Maximum - scrollBar.LargeChange + 1;
                }
            }
            else
            {
                scrollBar.Visible = false;
                scrollBar.Value = 0;
            }
            
            UpdateScrollPosition();
        }

        private void UpdateScrollPosition()
        {
            scrollableFrame.Top = -scrollBar.Value;
        }

        private void BindMouseWheelRecursive(Control control)
        {
            control.MouseWheel += (sender, e) =>
            {
                HandleMouseWheel(e.Delta);
                ((HandledMouseEventArgs)e).Handled = true;
            };

            foreach (Control child in control.Controls)
            {
                BindMouseWheelRecursive(child);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateScrollBarRange();
        }

        public void AddControl(Control control)
        {
            scrollableFrame.Controls.Add(control);
            BindMouseWheelRecursive(control);
        }
    }
}