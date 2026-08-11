namespace AIExplorer.Setup;

internal sealed class WizardForm : Form
{
    private enum Page
    {
        Welcome,
        Options,
        Progress,
        Finish,
    }

    private readonly string _packageRoot;
    private readonly InstallEngine _engine = new();
    private Page _page = Page.Welcome;
    private string _installDir = InstallEngine.DefaultInstallDir;
    private bool _installSucceeded;
    private string _errorMessage = "";

    private readonly Panel _header;
    private readonly Label _title;
    private readonly Label _subtitle;
    private readonly Panel _body;
    private readonly Panel _footer;
    private readonly Button _back;
    private readonly Button _next;
    private readonly Button _cancel;

    // Welcome
    private readonly Panel _pageWelcome = new() { Dock = DockStyle.Fill };

    // Options
    private readonly Panel _pageOptions = new() { Dock = DockStyle.Fill };
    private readonly TextBox _pathBox = new();
    private readonly CheckBox _chkDesktop = new() { Text = "创建桌面快捷方式", Checked = true, AutoSize = true };
    private readonly CheckBox _chkStartMenu = new() { Text = "创建开始菜单快捷方式", Checked = true, AutoSize = true };

    // Progress
    private readonly Panel _pageProgress = new() { Dock = DockStyle.Fill };
    private readonly Label _progressLabel = new();
    private readonly ProgressBar _progressBar = new();
    private readonly ListBox _componentList = new();

    // Finish
    private readonly Panel _pageFinish = new() { Dock = DockStyle.Fill };
    private readonly Label _finishLabel = new();
    private readonly CheckBox _chkLaunch = new() { Text = "安装完成后运行 AI Explorer", Checked = true, AutoSize = true };

    public WizardForm(string packageRoot)
    {
        _packageRoot = packageRoot;

        Text = $"{InstallEngine.DisplayName} 安装向导";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 400);
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = Color.White;

        _header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 72,
            BackColor = Color.FromArgb(0, 99, 177),
            Padding = new Padding(20, 14, 20, 14),
        };
        _title = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 28,
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
            Text = "欢迎使用 AI Explorer",
        };
        _subtitle = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = Color.FromArgb(220, 235, 250),
            Text = "这将在您的计算机上安装 AI Explorer。",
        };
        _header.Controls.Add(_subtitle);
        _header.Controls.Add(_title);

        _body = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 16, 24, 8),
            BackColor = Color.White,
        };

        _footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            BackColor = Color.FromArgb(240, 240, 240),
            Padding = new Padding(12, 12, 12, 12),
        };
        _cancel = MakeButton("取消", DialogResult.None);
        _next = MakeButton("下一步(N)", DialogResult.None);
        _back = MakeButton("< 上一步(B)", DialogResult.None);
        _cancel.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _next.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _back.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _cancel.Location = new Point(_footer.Width - 100, 12);
        _next.Location = new Point(_footer.Width - 210, 12);
        _back.Location = new Point(_footer.Width - 320, 12);
        _footer.Resize += (_, _) =>
        {
            _cancel.Left = _footer.ClientSize.Width - _cancel.Width - 12;
            _next.Left = _cancel.Left - _next.Width - 8;
            _back.Left = _next.Left - _back.Width - 8;
        };
        _footer.Controls.Add(_cancel);
        _footer.Controls.Add(_next);
        _footer.Controls.Add(_back);

        BuildWelcome();
        BuildOptions();
        BuildProgress();
        BuildFinish();

        Controls.Add(_body);
        Controls.Add(_footer);
        Controls.Add(_header);

        _back.Click += (_, _) => GoBack();
        _next.Click += (_, _) => GoNext();
        _cancel.Click += (_, _) =>
        {
            if (_page == Page.Progress)
            {
                return;
            }

            if (_page == Page.Finish)
            {
                Close();
                return;
            }

            if (MessageBox.Show(this, "确定要退出安装吗？", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) ==
                DialogResult.Yes)
            {
                Close();
            }
        };

        ShowPage(Page.Welcome);
    }

    private static Button MakeButton(string text, DialogResult dr)
    {
        return new Button
        {
            Text = text,
            Width = 100,
            Height = 28,
            DialogResult = dr,
            UseVisualStyleBackColor = true,
        };
    }

    private void BuildWelcome()
    {
        var t = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            Text =
                "安装程序将安装以下组件：\r\n\r\n" +
                "  • AI Explorer 应用程序\r\n" +
                "  • 私有 .NET 8 运行时（无需系统预装）\r\n" +
                "  • Windows App Runtime 1.6（若本机缺失）\r\n" +
                "  • 开始菜单 / 桌面快捷方式\r\n" +
                "  • 控制面板卸载入口\r\n\r\n" +
                "建议在继续前关闭其他正在运行的 AI Explorer 窗口。\r\n\r\n" +
                "单击“下一步”继续。",
        };
        _pageWelcome.Controls.Add(t);
    }

    private void BuildOptions()
    {
        var lbl = new Label
        {
            Text = "安装位置：",
            AutoSize = true,
            Location = new Point(0, 8),
        };
        _pathBox.Location = new Point(0, 32);
        _pathBox.Width = 400;
        _pathBox.Text = _installDir;

        var browse = new Button
        {
            Text = "浏览(B)…",
            Width = 90,
            Height = 26,
            Location = new Point(410, 30),
            UseVisualStyleBackColor = true,
        };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "选择 AI Explorer 安装目录",
                SelectedPath = Directory.Exists(_pathBox.Text) ? _pathBox.Text : InstallEngine.DefaultInstallDir,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _pathBox.Text = Path.Combine(dlg.SelectedPath, InstallEngine.AppName);
            }
        };

        _chkDesktop.Location = new Point(0, 80);
        _chkStartMenu.Location = new Point(0, 110);

        var note = new Label
        {
            AutoSize = false,
            Location = new Point(0, 160),
            Size = new Size(500, 60),
            ForeColor = Color.FromArgb(80, 80, 80),
            Text = "默认安装到当前用户目录，一般无需管理员权限。\r\n若 Windows App Runtime 安装失败，可尝试以管理员身份重新运行本安装程序。",
        };

        _pageOptions.Controls.Add(lbl);
        _pageOptions.Controls.Add(_pathBox);
        _pageOptions.Controls.Add(browse);
        _pageOptions.Controls.Add(_chkDesktop);
        _pageOptions.Controls.Add(_chkStartMenu);
        _pageOptions.Controls.Add(note);
    }

    private void BuildProgress()
    {
        _progressLabel.AutoSize = false;
        _progressLabel.Dock = DockStyle.Top;
        _progressLabel.Height = 28;
        _progressLabel.Text = "准备安装…";

        _progressBar.Dock = DockStyle.Top;
        _progressBar.Height = 22;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;

        var spacer = new Panel { Dock = DockStyle.Top, Height = 12 };

        var listTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Text = "组件进度：",
        };

        _componentList.Dock = DockStyle.Fill;
        _componentList.IntegralHeight = false;
        _componentList.Items.AddRange(new object[]
        {
            "○ 解除文件封锁",
            "○ Windows App Runtime 1.6",
            "○ 应用程序文件",
            "○ 私有 .NET 运行时",
            "○ 快捷方式与卸载注册",
        });

        _pageProgress.Controls.Add(_componentList);
        _pageProgress.Controls.Add(listTitle);
        _pageProgress.Controls.Add(spacer);
        _pageProgress.Controls.Add(_progressBar);
        _pageProgress.Controls.Add(_progressLabel);
    }

    private void BuildFinish()
    {
        _finishLabel.AutoSize = false;
        _finishLabel.Dock = DockStyle.Top;
        _finishLabel.Height = 120;
        _finishLabel.Text = "安装已完成。";

        _chkLaunch.Dock = DockStyle.Top;
        _chkLaunch.Padding = new Padding(0, 12, 0, 0);

        var tip = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 80,
            ForeColor = Color.FromArgb(80, 80, 80),
            Text = "可在“设置 → 应用 → 已安装的应用”中卸载 AI Explorer。\r\n也可运行安装目录下的 Uninstall.exe。",
        };

        _pageFinish.Controls.Add(tip);
        _pageFinish.Controls.Add(_chkLaunch);
        _pageFinish.Controls.Add(_finishLabel);
    }

    private void ShowPage(Page page)
    {
        _page = page;
        _body.Controls.Clear();

        switch (page)
        {
            case Page.Welcome:
                _title.Text = "欢迎使用 AI Explorer";
                _subtitle.Text = $"安装程序将引导您完成 {InstallEngine.DisplayName} {InstallEngine.ProductVersion} 的安装。";
                _body.Controls.Add(_pageWelcome);
                _back.Enabled = false;
                _next.Enabled = true;
                _next.Text = "下一步(N) >";
                _cancel.Enabled = true;
                _cancel.Text = "取消";
                break;

            case Page.Options:
                _title.Text = "安装选项";
                _subtitle.Text = "选择安装位置与快捷方式。";
                _body.Controls.Add(_pageOptions);
                _back.Enabled = true;
                _next.Enabled = true;
                _next.Text = "安装(I)";
                _cancel.Enabled = true;
                _cancel.Text = "取消";
                break;

            case Page.Progress:
                _title.Text = "正在安装";
                _subtitle.Text = "请稍候，正在复制文件并配置运行时…";
                _body.Controls.Add(_pageProgress);
                _back.Enabled = false;
                _next.Enabled = false;
                _cancel.Enabled = false;
                _cancel.Text = "取消";
                break;

            case Page.Finish:
                _title.Text = _installSucceeded ? "完成安装向导" : "安装未完成";
                _subtitle.Text = _installSucceeded
                    ? "AI Explorer 已成功安装到本机。"
                    : "安装过程中出现错误。";
                _finishLabel.Text = _installSucceeded
                    ? $"安装目录：\r\n{_installDir}\r\n\r\n单击“完成”退出安装向导。"
                    : $"安装失败：\r\n{_errorMessage}\r\n\r\n详细日志见安装目录或 %TEMP%\\AIExplorer-Setup-error.log";
                _chkLaunch.Visible = _installSucceeded;
                _chkLaunch.Checked = _installSucceeded;
                _body.Controls.Add(_pageFinish);
                _back.Enabled = false;
                _next.Enabled = true;
                _next.Text = "完成(F)";
                _cancel.Enabled = true;
                _cancel.Text = "关闭";
                break;
        }
    }

    private void GoBack()
    {
        if (_page == Page.Options)
        {
            ShowPage(Page.Welcome);
        }
    }

    private async void GoNext()
    {
        if (_page == Page.Welcome)
        {
            ShowPage(Page.Options);
            return;
        }

        if (_page == Page.Options)
        {
            _installDir = _pathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(_installDir))
            {
                MessageBox.Show(this, "请输入有效的安装路径。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                Directory.CreateDirectory(_installDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法创建安装目录：\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            ShowPage(Page.Progress);
            await RunInstallAsync();
            return;
        }

        if (_page == Page.Finish)
        {
            if (_installSucceeded && _chkLaunch.Checked)
            {
                try { InstallEngine.LaunchApp(_installDir); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "启动失败：\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            Close();
        }
    }

    private async Task RunInstallAsync()
    {
        _installSucceeded = false;
        _errorMessage = "";
        MarkComponent(0, running: true);

        try
        {
            await Task.Run(() =>
            {
                _engine.Install(
                    _packageRoot,
                    _installDir,
                    _chkDesktop.Checked,
                    _chkStartMenu.Checked,
                    (percent, text) =>
                    {
                        BeginInvoke(new Action(() =>
                        {
                            _progressBar.Value = Math.Max(0, Math.Min(100, percent));
                            _progressLabel.Text = text;
                            UpdateComponents(percent);
                        }));
                    });
            });

            _installSucceeded = true;
            for (var i = 0; i < _componentList.Items.Count; i++)
            {
                MarkComponent(i, done: true);
            }
        }
        catch (Exception ex)
        {
            _installSucceeded = false;
            _errorMessage = ex.Message;
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "AIExplorer-Setup-error.log"),
                    _engine.LogText + "\r\n" + ex,
                    System.Text.Encoding.UTF8);
            }
            catch
            {
                // ignore
            }
        }

        ShowPage(Page.Finish);
    }

    private void UpdateComponents(int percent)
    {
        if (percent >= 5) MarkComponent(0, done: true);
        if (percent >= 15 && percent < 45) MarkComponent(1, running: true);
        if (percent >= 45) MarkComponent(1, done: true);
        if (percent >= 45 && percent < 65) MarkComponent(2, running: true);
        if (percent >= 65) MarkComponent(2, done: true);
        if (percent >= 65 && percent < 85) MarkComponent(3, running: true);
        if (percent >= 85) MarkComponent(3, done: true);
        if (percent >= 85 && percent < 100) MarkComponent(4, running: true);
        if (percent >= 100) MarkComponent(4, done: true);
    }

    private void MarkComponent(int index, bool done = false, bool running = false)
    {
        if (index < 0 || index >= _componentList.Items.Count) return;
        var raw = _componentList.Items[index]?.ToString() ?? "";
        var name = raw.TrimStart('○', '●', '◎', ' ', '\t');
        // strip previous prefixes
        if (name.StartsWith("完成 ")) name = name.Substring(3);
        if (name.StartsWith("进行中 ")) name = name.Substring(4);

        if (done) _componentList.Items[index] = "● 完成 " + name;
        else if (running) _componentList.Items[index] = "◎ 进行中 " + name;
        else _componentList.Items[index] = "○ " + name;
    }
}
