using YunYun.Core;

namespace YunYun.App;

/// <summary>Settings window. To add an option: a property in Core/Settings.cs + a control here (see TODOs).</summary>
sealed class SettingsForm : Form
{
    public event Action<Settings>? Applied;

    Settings _base;
    int[]? _position;
    readonly TrackBar _idleScale = Slider(20, 100), _scale = Slider(30, 100), _volume = Slider(0, 100);
    readonly Label _idleScaleLbl = new() { AutoSize = true }, _scaleLbl = new() { AutoSize = true }, _volumeLbl = new() { AutoSize = true };
    readonly NumericUpDown _fps = new() { Minimum = 15, Maximum = 120, Width = 70, TextAlign = HorizontalAlignment.Right };
    readonly ComboBox _layer = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    readonly CheckBox _remember = new() { Text = "Запоминать, где она сидит", AutoSize = true };
    readonly CheckBox _muted = new() { Text = "Без звука", AutoSize = true };
    readonly CheckBox _startHidden = new() { Text = "Запускать свёрнутой в трей", AutoSize = true };
    readonly CheckBox _autostart = new() { Text = "Запускать вместе с Windows", AutoSize = true };

    static readonly (string Key, string Name)[] LayerNames =
    {
        (Layers.Desktop, "Только на рабочем столе (под окнами)"),
        (Layers.Normal, "Как обычное окно"),
        (Layers.Top, "Поверх всех окон"),
    };

    public SettingsForm(Settings s)
    {
        _base = s;
        SuspendLayout();
        // Fixed sizes below are in 96-dpi pixels and get scaled to the monitor's DPI;
        // everything with text is auto-sized, so it fits at any scaling / font size.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "YunYun — настройки";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;

        foreach (var (_, name) in LayerNames) _layer.Items.Add(name);
        Bind(_idleScale, _idleScaleLbl);
        Bind(_scale, _scaleLbl);
        Bind(_volume, _volumeLbl);
        _muted.CheckedChanged += (_, _) => _volume.Enabled = !_muted.Checked;

        _tabs.TabPages.Add(Page("Внешний вид",
            Row("Размер в покое", _idleScale, _idleScaleLbl),
            Row("Размер при перетаскивании", _scale, _scaleLbl),
            Row("Частота анимации, кадр/с", _fps)));
            // TODO: skins, outline, transparency
        var reset = AutoButton("Вернуть в угол экрана");
        reset.Click += (_, _) => { _position = null; Apply(); };
        _tabs.TabPages.Add(Page("Поведение", Row("Где показывать", _layer), Single(_remember), Single(reset)));
            // TODO: walking around, reactions to the cursor
        _tabs.TabPages.Add(Page("Звук", Row("Громкость", _volume, _volumeLbl), Single(_muted)));
        _tabs.TabPages.Add(Page("Система", Single(_startHidden), Single(_autostart)));

        var ok = AutoButton("ОК");
        var apply = AutoButton("Применить");
        var cancel = AutoButton("Отмена");
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        ok.Click += (_, _) => { Apply(); Hide(); };
        apply.Click += (_, _) => Apply();
        cancel.Click += (_, _) => Hide();
        AcceptButton = ok;
        CancelButton = cancel;
        _buttons.Controls.AddRange(new Control[] { cancel, apply, ok });   // right to left

        Controls.Add(_tabs);
        Controls.Add(_buttons);
        LoadFrom(s);
        ResumeLayout(false);
        PerformLayout();
    }

    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly FlowLayoutPanel _buttons = new()
    {
        Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 6, 8, 8),
    };

    /// <summary>Window size from what the pages and buttons actually need (after DPI scaling).</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        int w = _buttons.PreferredSize.Width, h = 0;
        foreach (TabPage p in _tabs.TabPages)
        {
            var need = p.Controls[0].PreferredSize;
            w = Math.Max(w, need.Width + 16);
            h = Math.Max(h, need.Height);
        }
        int tabHeader = _tabs.GetTabRect(0).Height + 12;
        ClientSize = new Size(Math.Max(w, LogicalToDeviceUnits(440)),
                              h + tabHeader + _buttons.PreferredSize.Height + LogicalToDeviceUnits(12));
    }

    static Button AutoButton(string text) => new()
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly,
        MinimumSize = new Size(88, 28), Padding = new Padding(8, 2, 8, 2), Margin = new Padding(4, 0, 0, 0),
        UseVisualStyleBackColor = true,
    };

    static TrackBar Slider(int min, int max) => new()
    {
        Minimum = min, Maximum = max, TickFrequency = 10, Width = 220, AutoSize = true,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
    };
    static void Bind(TrackBar t, Label l) => t.ValueChanged += (_, _) => l.Text = $"{t.Value}%";

    /// <summary>A page is a 3-column table: caption | control | value ("60%").</summary>
    static TabPage Page(string title, params Control[][] rows)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 8, 8, 4),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var row in rows)
        {
            int r = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (row.Length == 1) { t.Controls.Add(row[0], 0, r); t.SetColumnSpan(row[0], 3); continue; }
            for (int c = 0; c < row.Length; c++) t.Controls.Add(row[c], c, r);
        }
        var page = new TabPage(title) { UseVisualStyleBackColor = true };
        page.Controls.Add(t);
        return page;
    }

    static Control[] Row(string caption, params Control[] controls)
    {
        var label = new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 12, 8) };
        foreach (var c in controls)
        {
            if (c is Label v) { v.Anchor = AnchorStyles.Left; v.Margin = new Padding(6, 8, 3, 8); v.MinimumSize = new Size(40, 0); }
            else if (c is not TrackBar) { c.Anchor = AnchorStyles.Left; c.Margin = new Padding(3, 6, 3, 6); }
        }
        return new Control[] { label }.Concat(controls).ToArray();
    }

    static Control[] Single(Control c)
    {
        if (c is CheckBox cb) cb.AutoSize = true;
        c.Anchor = AnchorStyles.Left;
        c.Margin = new Padding(3, 8, 3, 4);
        return new[] { c };
    }

    public void LoadFrom(Settings s)
    {
        _base = s;
        _idleScale.Value = Math.Clamp((int)Math.Round(s.IdleScale * 100), _idleScale.Minimum, _idleScale.Maximum);
        _scale.Value = Math.Clamp((int)Math.Round(s.Scale * 100), _scale.Minimum, _scale.Maximum);
        _volume.Value = Math.Clamp(s.Volume, 0, 100);
        _idleScaleLbl.Text = $"{_idleScale.Value}%";
        _scaleLbl.Text = $"{_scale.Value}%";
        _volumeLbl.Text = $"{_volume.Value}%";
        _fps.Value = Math.Clamp(s.Fps, 15, 120);
        _layer.SelectedIndex = Math.Max(0, Array.FindIndex(LayerNames, l => l.Key == s.Layer));
        _remember.Checked = s.RememberPosition;
        _muted.Checked = s.Muted;
        _volume.Enabled = !s.Muted;
        _startHidden.Checked = s.StartHidden;
        _autostart.Checked = s.Autostart;
        _position = s.Position;
    }

    Settings Collect()
    {
        var s = _base.Clone();
        s.IdleScale = _idleScale.Value / 100.0;
        s.Scale = _scale.Value / 100.0;
        s.Volume = _volume.Value;
        s.Muted = _muted.Checked;
        s.Fps = (int)_fps.Value;
        s.Layer = LayerNames[Math.Max(0, _layer.SelectedIndex)].Key;
        s.RememberPosition = _remember.Checked;
        s.StartHidden = _startHidden.Checked;
        s.Autostart = _autostart.Checked;
        s.Position = _position;
        return s.Clamp();
    }

    void Apply()
    {
        var s = Collect();
        _base = s;
        Applied?.Invoke(s);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }   // reuse the window
        base.OnFormClosing(e);
    }
}
