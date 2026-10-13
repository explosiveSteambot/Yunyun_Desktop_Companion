using YunYun.Core;

namespace YunYun.App;

/// <summary>Ties everything together: companion window, tray icon, settings, sounds, Recycle Bin game.</summary>
sealed class AppController : ApplicationContext
{
    // Grab-sound timing and the reaction duration live in CompanionWindow (its frame loop).
    const int BinVisitsToQuit = 3;                         // after this many visits the program closes

    readonly string _cfgPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YunYunDesktop", "settings.json");

    Settings _s;
    readonly CompanionWindow _comp;
    readonly SoundBank _sound = new();
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _toggleItem;
    SettingsForm? _settings;
    int _binVisits;

    public AppController()
    {
        _s = SettingsStore.Load(_cfgPath);
        try
        {
            Autostart.RepairPath();
            _s.Autostart = Autostart.IsEnabled();    // the registry decides (it can be changed in Task Manager)
        }
        catch (Exception) { /* registry unavailable: keep the saved value */ }
        _sound.SetVolume(_s.Volume, _s.Muted);

        var rig = RigDefinition.Parse(Assets.ReadText("rig/rig.json"));
        var ragdoll = new Ragdoll(rig, file => Assets.LoadRgba("rig/" + file));

        _comp = new CompanionWindow(_s, ragdoll);
        _comp.HideRequested += HideCompanion;
        _comp.SettingsRequested += OpenSettings;
        _comp.QuitRequested += Quit;
        _comp.GrabSoundDue += () => _sound.PlayRandom();
        _comp.BinEntered += OnBinEntered;
        _comp.ReactionFinished += OnReactionDone;

        var menu = new ContextMenuStrip();
        _toggleItem = new ToolStripMenuItem("Спрятать", null, (_, _) => Toggle());
        menu.Items.Add(_toggleItem);
        menu.Items.Add("Настройки…", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Quit());
        _tray = new NotifyIcon
        {
            Icon = Assets.AppIcon(),
            Text = "YunYun",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Toggle(); };

        if (_s.StartHidden) _toggleItem.Text = "Показать";
        else ShowCompanion();
    }

    // ------------------------------------------------------------------ visibility
    void ShowCompanion()
    {
        _comp.Start();
        _toggleItem.Text = "Спрятать";
    }

    void HideCompanion()
    {
        _comp.Stop();
        _toggleItem.Text = "Показать";
    }

    void Toggle()
    {
        if (_comp.Visible) HideCompanion();
        else ShowCompanion();
    }

    // ------------------------------------------------------------------ recycle bin
    void OnBinEntered()
    {
        _binVisits++;
        _comp.ShowReaction(_binVisits - 1);          // pictures in order: bin_1, bin_2, bin_3
        _sound.PlayRandom();
    }

    void OnReactionDone()
    {
        _comp.FinishReaction();                      // top-left corner (this is what gets saved)
        if (_binVisits >= BinVisitsToQuit) Quit();
        else Save();
    }

    // ------------------------------------------------------------------ settings
    void OpenSettings()
    {
        if (_settings == null)
        {
            _settings = new SettingsForm(_s);
            _settings.Applied += ApplySettings;
        }
        try { _s.Autostart = Autostart.IsEnabled(); } catch (Exception) { }   // may have been changed in Task Manager
        _settings.LoadFrom(_s);
        _settings.Show();
        _settings.Activate();
    }

    void ApplySettings(Settings s)
    {
        bool resetPos = s.Position == null && _s.Position != null;
        try
        {
            if (s.Autostart != Autostart.IsEnabled()) Autostart.Set(s.Autostart);
        }
        catch (Exception e)
        {
            s.Autostart = !s.Autostart;
            MessageBox.Show("Не удалось изменить автозапуск:\n" + e.Message, "YunYun", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _s = s;
        _comp.ApplySettings(s);
        _sound.SetVolume(s.Volume, s.Muted);
        if (resetPos) _comp.PlaceInitial();
        Save();
    }

    void Save()
    {
        if (_s.RememberPosition && _comp.HasPosition && !_comp.Dragging && !_comp.Reacting) _s.Position = _comp.RestingPosition;
        try { SettingsStore.Save(_cfgPath, _s); }
        catch (IOException) { /* settings are not critical */ }
        catch (UnauthorizedAccessException) { }
    }

    void Quit()
    {
        Save();
        _tray.Visible = false;
        _tray.Dispose();
        _sound.Dispose();
        _settings?.Dispose();
        _comp.Dispose();
        ExitThread();
    }
}
