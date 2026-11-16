using System;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace AuraToggle;

/// <summary>
/// The cooler screen: brightness, six readouts, two colours and a clip that is already stored on
/// the screen. Nothing here uploads a file. Driving stays on after the panel closes, because the
/// screen forgets the layout a few seconds after the program stops talking to it.
/// </summary>
internal sealed class RyuoPopup : PopupForm
{
    private readonly Label _status = new();
    private readonly ToggleSwitch _drive = new();
    private readonly Slider _brightness = new();
    private readonly Label _brightnessValue = new();
    private readonly Panel _titleSwatch = new();
    private readonly Panel _valueSwatch = new();
    private readonly Select _play = new();
    private readonly Select _media = new();
    private readonly Select[] _slots = new Select[6];
    private readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };

    private bool _filling;
    private bool _childOpen;
    private string[] _knownMedia = Array.Empty<string>();

    public RyuoPopup()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Theme.Ui;
        ForeColor = Theme.Text;
        Text = Strings.RyuoTitle;

        var layout = new Layout
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(this.Scaled(14)),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _status.AutoSize = true;
        _status.ForeColor = Theme.Text;
        _status.Margin = new Padding(0, 0, 0, this.Scaled(8));
        Add(layout, _status);

        _drive.CheckedChanged += (_, _) => Commit(RyuoApply.Full);
        Add(layout, Row(Strings.RyuoDrive, _drive));

        _brightness.Minimum = 0;
        _brightness.Maximum = 100;
        _brightness.Width = this.Scaled(180);
        _brightness.AccessibleName = Strings.SettingBrightness;
        _brightness.ValueCommitted += (_, _) => Commit(RyuoApply.Brightness);
        _brightness.ValueChanged += (_, _) => ShowBrightness();
        _brightnessValue.AutoSize = true;
        _brightnessValue.ForeColor = Theme.TextMuted;
        Add(layout, Row(Strings.SettingBrightness, Line(_brightness, _brightnessValue)));

        _titleSwatch.Size = new Size(this.Scaled(22), this.Scaled(22));
        _titleSwatch.Cursor = Cursors.Hand;
        _titleSwatch.Click += (_, _) => PickColour(_titleSwatch);
        _valueSwatch.Size = new Size(this.Scaled(22), this.Scaled(22));
        _valueSwatch.Cursor = Cursors.Hand;
        _valueSwatch.Click += (_, _) => PickColour(_valueSwatch);
        Add(layout, Row(Strings.RyuoTitleColor, _titleSwatch));
        Add(layout, Row(Strings.RyuoValueColor, _valueSwatch));

        Prepare(_play, Strings.RyuoPlayMode);
        _play.SetItems(new[]
        {
            new SelectItem("Single", Strings.RyuoPlaySingle, null),
            new SelectItem("Random", Strings.RyuoPlayRandom, null),
            new SelectItem("Cycle", Strings.RyuoPlayCycle, null),
        });
        _play.SelectionChanged += (_, _) => Commit(RyuoApply.Full);
        Add(layout, Row(Strings.RyuoPlayMode, _play));

        Prepare(_media, Strings.RyuoMedia);
        _media.PopupWidth = this.Scaled(280);
        _media.SelectionChanged += (_, _) => Commit(RyuoApply.Full);
        Add(layout, Row(Strings.RyuoMedia, _media));

        for (int i = 0; i < _slots.Length; i++)
        {
            var slot = new Select();
            _slots[i] = slot;
            Prepare(slot, string.Format(CultureInfo.CurrentCulture, Strings.RyuoSlot, i + 1));
            slot.SetItems(SlotItems());
            slot.SelectionChanged += (_, _) => Commit(RyuoApply.Live);
            Add(layout, Row(string.Format(CultureInfo.CurrentCulture, Strings.RyuoSlot, i + 1), slot));
        }

        var note = new Label
        {
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            MaximumSize = new Size(this.Scaled(280), 0),
            Text = Strings.RyuoNote,
            Margin = new Padding(0, this.Scaled(8), 0, 0),
        };
        Add(layout, note);

        Controls.Add(layout);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        ShowLayout(RyuoHost.Current);
        _statusTimer.Tick += (_, _) =>
        {
            ShowStatus();
            ShowListedMedia();
        };
        _statusTimer.Start();
        ShowStatus();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            string[] names = RyuoHost.Media();
            if (IsDisposed)
            {
                return;
            }

            try
            {
                BeginInvoke(() =>
                {
                    if (!IsDisposed && names.Length > 0)
                    {
                        _knownMedia = names;
                        ShowMedia(names);
                    }
                });
            }
            catch (InvalidOperationException)
            {
                // The panel closed between the check and the marshal.
            }
        });
    }

    public void Open(Point anchor, IWin32Window owner)
    {
        Show(owner);
        Rectangle screen = Screen.FromControl(this).WorkingArea;
        int x = Math.Max(screen.Left, Math.Min(anchor.X - Width, screen.Right - Width));
        int y = anchor.Y;
        if (y + Height > screen.Bottom)
        {
            y = Math.Max(screen.Top, anchor.Y - Height);
        }

        Location = new Point(x, y);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (!_childOpen)
        {
            Close();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            Close();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ShowLayout(RyuoLayout layout)
    {
        _filling = true;
        _drive.Checked = layout.Drive;
        _brightness.Value = layout.Brightness;
        ShowBrightness();
        _titleSwatch.BackColor = ToColour(layout.TitleColor);
        _valueSwatch.BackColor = ToColour(layout.ContentColor);
        _play.ShowSelection(layout.PlayMode);
        ShowMedia(new[] { layout.Media });
        _media.ShowSelection(layout.Media);
        string[] items = layout.NormalItems();
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i].ShowSelection(items[i].Length == 0 ? "off" : items[i]);
        }

        _filling = false;
    }

    private void ShowMedia(string[] names)
    {
        string selected = _media.Selected?.Key ?? "";
        var items = new System.Collections.Generic.List<SelectItem>();
        foreach (string name in names)
        {
            if (name.Length > 0)
            {
                items.Add(new SelectItem(name, name, null));
            }
        }

        if (items.Count == 0 && selected.Length > 0)
        {
            items.Add(new SelectItem(selected, selected, null));
        }

        if (items.Count == 0)
        {
            items.Add(new SelectItem(RyuoLayout.DefaultMedia, RyuoLayout.DefaultMedia, null));
        }

        bool filling = _filling;
        _filling = true;
        _media.SetItems(items);
        _media.ShowSelection(selected.Length > 0 ? selected : items[0].Key);
        _filling = filling;
    }

    private void ShowBrightness() =>
        _brightnessValue.Text = string.Format(CultureInfo.CurrentCulture, Strings.BrightnessValue, _brightness.Value);

    private void ShowStatus()
    {
        _status.Text = RyuoHost.Presence switch
        {
            RyuoPresence.Driving => Strings.RyuoStatusOn,
            RyuoPresence.Missing => Strings.RyuoStatusMissing,
            _ => Strings.RyuoStatusOff,
        };
    }

    private void ShowListedMedia()
    {
        if (_childOpen)
        {
            return;
        }

        string[] names = RyuoHost.CachedMedia;
        if (names.Length == 0 || SameMedia(names, _knownMedia))
        {
            return;
        }

        _knownMedia = names;
        ShowMedia(names);
    }

    private static bool SameMedia(string[] left, string[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void Commit(RyuoApply kind)
    {
        if (_filling)
        {
            return;
        }

        RyuoLayout layout = Readout();
        if (!_drive.Checked)
        {
            kind = RyuoApply.Full;
        }

        ThreadPool.QueueUserWorkItem(_ => RyuoHost.Apply(layout, kind));
    }

    private RyuoLayout Readout()
    {
        var items = new string[_slots.Length];
        for (int i = 0; i < items.Length; i++)
        {
            string key = _slots[i].Selected?.Key ?? "off";
            items[i] = key == "off" ? "" : key;
        }

        return new RyuoLayout
        {
            Drive = _drive.Checked,
            Brightness = _brightness.Value,
            TitleColor = Hex(_titleSwatch.BackColor),
            ContentColor = Hex(_valueSwatch.BackColor),
            PlayMode = _play.Selected?.Key ?? "Single",
            Media = _media.Selected?.Key ?? RyuoLayout.DefaultMedia,
            Items = items,
        };
    }

    private void PickColour(Panel swatch)
    {
        using var dialog = new ColorDialog { Color = swatch.BackColor, FullOpen = true };
        _childOpen = true;
        try
        {
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                swatch.BackColor = dialog.Color;
                Commit(RyuoApply.Live);
            }
        }
        finally
        {
            _childOpen = false;
            Activate();
        }
    }

    private void Prepare(Select select, string name)
    {
        select.AccessibleName = name;
        select.Width = this.Scaled(180);
        select.DesignHeight = 34;
        select.PopupOpening += (_, _) => _childOpen = true;
        select.PopupClosed += (_, _) =>
        {
            _childOpen = false;
            Activate();
        };
    }

    private static SelectItem[] SlotItems() => new[]
    {
        new SelectItem("off", Strings.RyuoItemOff, null),
        new SelectItem("CPU Load", Strings.RyuoItemCpuLoad, null),
        new SelectItem("CPU Speed Average", Strings.RyuoItemCpuSpeed, null),
        new SelectItem("Memory Utilization", Strings.RyuoItemMemory, null),
        new SelectItem("Date&Time", Strings.RyuoItemDate, null),
        new SelectItem("CPU Temperature", Strings.RyuoItemCpuTemp, null),
        new SelectItem("GPU Temperature", Strings.RyuoItemGpuTemp, null),
        new SelectItem("GPU Load", Strings.RyuoItemGpuLoad, null),
    };

    private static void Add(Layout layout, Control control)
    {
        int row = layout.RowCount;
        layout.RowCount = row + 1;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(control, 0, row);
    }

    private Layout Row(string caption, Control control)
    {
        var row = new Layout
        {
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, this.Scaled(6)),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, this.Scaled(148)));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var label = new Label
        {
            AutoSize = true,
            Text = caption,
            ForeColor = Theme.TextMuted,
            Margin = new Padding(0, this.Scaled(6), this.Scaled(8), 0),
        };
        row.Controls.Add(label, 0, 0);
        row.Controls.Add(control, 1, 0);
        return row;
    }

    private static Layout Line(Control left, Control right)
    {
        var line = new Layout { AutoSize = true, ColumnCount = 2 };
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        line.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        right.Margin = new Padding(left.Scaled(8), left.Scaled(4), 0, 0);
        line.Controls.Add(left, 0, 0);
        line.Controls.Add(right, 1, 0);
        return line;
    }

    private static Color ToColour(string hex)
    {
        if (hex.Length == 7 && int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            return Color.FromArgb(rgb | unchecked((int)0xFF000000));
        }

        return Color.White;
    }

    private static string Hex(Color color) =>
        string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
}
