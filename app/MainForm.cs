using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VirtualBattery
{
    sealed class MainForm : Form
    {
        readonly Label status = new Label();
        readonly Button install = new Button();
        readonly Button uninstall = new Button { Text = Strings.Uninstall };
        readonly Button apply = new Button { Text = Strings.Apply };
        readonly TrackBar level = new TrackBar { Minimum = 40, Maximum = 100, Value = 100, TickFrequency = 10 };
        readonly Label levelText = new Label();
        readonly TextBox output = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, TabStop = false };
        readonly Timer timer = new Timer { Interval = 2000 };
        readonly bool supported;
        bool busy;

        public MainForm(bool startUninstall)
        {
            Text = "Virtual Battery";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = SystemColors.Window;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            int line = Font.Height;
            int margin = line;
            int width = line * 26;
            int y = margin;
            void Put(Control control, int height, int gap)
            {
                control.SetBounds(margin, y, width, height);
                Controls.Add(control);
                y += height + gap;
            }

            Put(new Label { Text = "Virtual Battery", Font = new Font(Font.FontFamily, Font.Size * 1.8f, FontStyle.Bold) }, line * 5 / 2, 0);
            Put(new Label { Text = Strings.Subtitle, ForeColor = SystemColors.GrayText }, line * 3 / 2, line / 2);
            Put(status, line * 3 / 2, line / 2);
            Put(install, line * 3, line);
            Put(levelText, line * 5 / 4, 0);
            Put(level, line * 5 / 2, 0);
            status.Font = new Font(Font, FontStyle.Bold);
            install.Font = new Font(Font.FontFamily, Font.Size * 1.3f, FontStyle.Bold);

            int half = (width - margin) / 2;
            apply.SetBounds(margin, y, half, line * 2);
            uninstall.SetBounds(margin + half + margin, y, half, line * 2);
            Controls.Add(apply);
            Controls.Add(uninstall);
            y += line * 3;
            Put(output, line * 7, margin);
            ClientSize = new Size(width + margin * 2, y);
            AcceptButton = install;

            level.ValueChanged += (s, e) => levelText.Text = Strings.Level(level.Value);
            install.Click += async (s, e) =>
            {
                if (!Installer.IsInstalled && PowerStatus.Read().HasBattery && !Ask(Strings.RealBattery)) return;
                int percent = level.Value;
                await Run(i => i.Install(percent));
            };
            uninstall.Click += async (s, e) => await ConfirmUninstall();
            apply.Click += (s, e) => ApplyLevel();
            FormClosing += (s, e) => e.Cancel = busy;
            timer.Tick += (s, e) => RefreshState();

            level.Value = Device.GetLevel();
            levelText.Text = Strings.Level(level.Value);

            string blocker = Installer.GetBlocker();
            supported = blocker == null;
            if (!supported) output.Text = blocker + Environment.NewLine;
            RefreshState();
            timer.Start();
            if (startUninstall) Shown += async (s, e) => await ConfirmUninstall();
        }

        async Task ConfirmUninstall()
        {
            if (Installer.IsInstalled && Ask(Strings.ConfirmUninstall)) await Run(i => i.Uninstall());
        }

        void ApplyLevel()
        {
            try
            {
                if (Device.SetLevel(level.Value)) Log(Strings.Applied);
            }
            catch (Exception ex) { Log(Strings.Error + ex.Message); }
        }

        async Task Run(Action<Installer> action)
        {
            busy = true;
            output.Clear();
            RefreshState();
            try { await Task.Run(() => action(new Installer(m => Invoke((Action)(() => Log(m)))))); }
            catch (Exception ex) { Log(Strings.Error + ex.Message); }
            finally
            {
                busy = false;
                RefreshState();
            }
        }

        void RefreshState()
        {
            UseWaitCursor = busy;
            install.Enabled = !busy && supported;
            if (busy)
            {
                uninstall.Enabled = apply.Enabled = false;
                return;
            }

            bool installed = Installer.IsInstalled;
            install.Text = installed ? Strings.Reinstall : Strings.Install;
            uninstall.Enabled = apply.Enabled = installed;

            PowerStatus power = PowerStatus.Read();
            if (!installed) SetStatus(Strings.StatusNotInstalled, SystemColors.GrayText);
            else if (!power.HasBattery) SetStatus(Strings.StatusWaiting, Color.Firebrick);
            else SetStatus(Strings.StatusActive(power.Percent), Color.ForestGreen);
        }

        void SetStatus(string text, Color color)
        {
            status.Text = text;
            status.ForeColor = color;
        }

        void Log(string message) => output.AppendText(message + Environment.NewLine);

        bool Ask(string question) =>
            MessageBox.Show(this, question, Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }
}
