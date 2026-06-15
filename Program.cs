using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TCPTunnel
{
    static class Program
    {
        private const string MutexName = "TCP Tunnel.SingleInstance";
        private static Mutex singleInstanceMutex;

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.SetCompatibleTextRenderingDefault(false);

            bool createdNew;
            singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                Application.Run(new AlreadyRunningForm());
                return;
            }

            Application.ApplicationExit += (_, __) =>
            {
                try { singleInstanceMutex?.ReleaseMutex(); } catch { }
                try { singleInstanceMutex?.Dispose(); } catch { }
                singleInstanceMutex = null;
            };

            Application.Run(new MainForm());
        }
    }

    public sealed class AlreadyRunningForm : Form
    {
        public AlreadyRunningForm()
        {
            Text = "TCP Tunnel - уже запущен";
            ShowIcon = true;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            ClientSize = new Size(520, 210);
            BackColor = Color.FromArgb(26, 26, 26);
            ForeColor = Color.FromArgb(241, 243, 245);
            Font = new Font("Comic Neue", 11f, FontStyle.Regular, GraphicsUnit.Point);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

            var title = new Label
            {
                AutoSize = false,
                Left = 22,
                Top = 24,
                Width = 476,
                Height = 32,
                Font = new Font("Comic Neue", 16f, FontStyle.Bold, GraphicsUnit.Point),
                Text = "TCP Tunnel уже запущен",
                ForeColor = Color.FromArgb(241, 243, 245),
            };

            var message = new Label
            {
                AutoSize = false,
                Left = 22,
                Top = 68,
                Width = 476,
                Height = 62,
                Font = new Font("Comic Neue", 10.5f, FontStyle.Regular, GraphicsUnit.Point),
                Text = "На этом компьютере уже открыто другое окно TCP Tunnel. Закройте его и попробуйте снова.",
                ForeColor = Color.FromArgb(173, 181, 189),
            };

            var exitBtn = new Button
            {
                Text = "Выход",
                Width = 92,
                Height = 34,
                Left = ClientSize.Width - 114,
                Top = ClientSize.Height - 56,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(26, 26, 26),
                ForeColor = Color.FromArgb(241, 243, 245),
                Cursor = Cursors.Hand,
            };
            exitBtn.FlatAppearance.BorderSize = 0;
            exitBtn.FlatAppearance.MouseDownBackColor = Color.FromArgb(26, 26, 26);
            exitBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(36, 36, 36);
            exitBtn.Click += (_, __) => Close();

            Controls.Add(title);
            Controls.Add(message);
            Controls.Add(exitBtn);
        }
    }
}
