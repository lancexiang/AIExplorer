namespace AIExplorer.Setup;

internal sealed class UninstallForm : Form
{
    private readonly InstallEngine _engine = new();
    private readonly Label _status = new();
    private readonly ProgressBar _bar = new();
    private readonly Button _uninstall;
    private readonly Button _close;
    private bool _busy;

    public UninstallForm()
    {
        Text = $"卸载 {InstallEngine.DisplayName}";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(460, 220);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.White;

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 56,
            BackColor = Color.FromArgb(0, 99, 177),
            Padding = new Padding(16, 14, 16, 14),
        };
        header.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
            Text = $"卸载 {InstallEngine.DisplayName}",
            TextAlign = ContentAlignment.MiddleLeft,
        });

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20) };
        var dir = InstallEngine.ReadInstallLocation() ?? InstallEngine.DefaultInstallDir;
        var info = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 48,
            Text = $"将从本机移除 {InstallEngine.DisplayName}。\r\n安装目录：{dir}\r\n（不会卸载系统共享的 Windows App Runtime）",
        };
        _status.Dock = DockStyle.Top;
        _status.Height = 24;
        _status.Text = "准备就绪。";
        _bar.Dock = DockStyle.Top;
        _bar.Height = 20;
        _bar.Minimum = 0;
        _bar.Maximum = 100;
        body.Controls.Add(_bar);
        body.Controls.Add(_status);
        body.Controls.Add(info);

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            BackColor = Color.FromArgb(240, 240, 240),
            Padding = new Padding(12),
        };
        _close = new Button { Text = "关闭", Width = 90, Height = 28, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        _uninstall = new Button { Text = "卸载", Width = 90, Height = 28, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        footer.Controls.Add(_close);
        footer.Controls.Add(_uninstall);
        footer.Resize += (_, _) =>
        {
            _close.Left = footer.ClientSize.Width - _close.Width - 12;
            _close.Top = 12;
            _uninstall.Left = _close.Left - _uninstall.Width - 8;
            _uninstall.Top = 12;
        };

        _close.Click += (_, _) => Close();
        _uninstall.Click += async (_, _) => await RunUninstallAsync();

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);
    }

    private async Task RunUninstallAsync()
    {
        if (_busy) return;
        if (MessageBox.Show(this,
                $"确定要卸载 {InstallEngine.DisplayName} 吗？",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _busy = true;
        _uninstall.Enabled = false;
        _close.Enabled = false;
        try
        {
            await Task.Run(() =>
            {
                _engine.Uninstall((percent, text) =>
                {
                    BeginInvoke(new Action(() =>
                    {
                        _bar.Value = Math.Max(0, Math.Min(100, percent));
                        _status.Text = text;
                    }));
                });
            });
            _status.Text = "卸载完成。";
            MessageBox.Show(this, "已卸载 AI Explorer。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "卸载失败：\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            _close.Enabled = true;
        }
        finally
        {
            _busy = false;
        }
    }
}
