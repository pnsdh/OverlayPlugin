using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: the TTS tab.  Changes apply at once (and are saved with the plugin config).  Laid out
    // by layout panels, so it follows the font and DPI rather than fixed positions.
    public class TtsConfigPanel : UserControl
    {
        private static readonly bool ko = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko";
        private static string T(string en, string kr) => ko ? kr : en;

        private readonly TtsService service;
        private readonly ActTtsBridge bridge;
        private readonly IPluginConfig pluginConfig;
        private readonly TtsConfig config;
        private readonly Label owner;
        private readonly Label previewResult;
        private readonly Label previewSpoken;
        private readonly TextBox previewText;
        private readonly DataGridView rules;
        private readonly Timer ownerTimer;
        private bool loading;

        public TtsConfigPanel(TinyIoCContainer container)
        {
            service = container.Resolve<TtsService>();
            container.TryResolve(ResolveOptions.FailUnregisteredOnly, out bridge);
            pluginConfig = container.Resolve<IPluginConfig>();
            config = service.Config;
            loading = true;

            Dock = DockStyle.Fill;
            AutoScroll = true;
            var root = new TableLayoutPanel
            {
                ColumnCount = 1,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8),
            };
            Controls.Add(root);

            // What reads.
            var general = Section(root, T("Text to speech", "TTS"));
            general.Controls.Add(Check(T("Read alerts with this plugin's TTS (off: ACT's own TTS, as before)", "알림을 이 플러그인의 TTS로 읽기 (끄면 지금까지처럼 ACT의 TTS)"),
                config.Enabled, v => { config.Enabled = v; ApplyBridge(); }));
            general.Controls.Add(Check(T("Also read what ACT itself speaks (its custom triggers, other plugins)", "ACT가 읽는 것(커스텀 트리거, 다른 플러그인)도 이 플러그인으로 읽기"),
                config.TakeOverActTts, v => { config.TakeOverActTts = v; ApplyBridge(); }));
            general.Controls.Add(Check(T("When another plugin speaks for ACT (e.g. TTSYukkuri), send overlays' alerts to it too", "다른 플러그인(TTSYukkuri 등)이 ACT의 TTS를 맡고 있으면 오버레이 알림도 그쪽으로 보내기"),
                config.DeferToOtherPlugin, v => config.DeferToOtherPlugin = v));
            owner = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 6) };
            general.Controls.Add(owner);

            // The voice.
            var voice = Section(root, T("Voice", "음성"));
            var voiceGrid = Grid(voice);
            var providerBox = Combo(service.Providers.Select(p => new Item(p.Name, p.Id)), config.Provider, v => { config.Provider = (string)v; Changed(); });
            Row(voiceGrid, T("Engine", "엔진"), providerBox);
            var voices = new List<Item> { new Item(T("Automatic (the alert's language)", "자동 (알림 언어에 맞춰)"), "") };
            voices.AddRange(service.Provider.GetVoices().Select(v => new Item(v.Name, v.Id)));
            Row(voiceGrid, T("Voice", "목소리"), Combo(voices, config.Voice, v => { config.Voice = (string)v; Changed(); }));
            Row(voiceGrid, T("Speed", "속도"), Slider(-10, 10, config.Rate, v => { config.Rate = v; Changed(); }, v => v == 0 ? T("normal", "보통") : (v > 0 ? "+" : "") + v));
            var devices = new List<Item> { new Item(T("Default device", "기본 장치"), "") };
            devices.AddRange(TtsPlayer.GetDevices().Select(d => new Item(d.Name, d.Id)));
            Row(voiceGrid, T("Output", "출력 장치"), Combo(devices, config.OutputDevice, v => config.OutputDevice = (string)v));

            // How loud.
            var volume = Section(root, T("Volume", "볼륨"));
            var volumeGrid = Grid(volume);
            string Percent(int v) => v + "%";
            Row(volumeGrid, T("All speech", "전체"), Slider(0, 100, config.Volume, v => config.Volume = v, Percent));
            Row(volumeGrid, T("Alarm (most important)", "경고 (가장 중요)"), Slider(0, 100, config.AlarmVolume, v => config.AlarmVolume = v, Percent));
            Row(volumeGrid, T("Alert", "주의"), Slider(0, 100, config.AlertVolume, v => config.AlertVolume = v, Percent));
            Row(volumeGrid, T("Info", "정보"), Slider(0, 100, config.InfoVolume, v => config.InfoVolume = v, Percent));
            Row(volumeGrid, T("Other (ACT, other plugins)", "기타 (ACT, 다른 플러그인)"), Slider(0, 100, config.OtherVolume, v => config.OtherVolume = v, Percent));

            // When alerts come together.
            var queue = Section(root, T("When alerts overlap", "알림이 겹칠 때"));
            var queueGrid = Grid(queue);
            Row(queueGrid, T("A new alert", "새 알림이 오면"), Combo(new[]
            {
                new Item(T("waits its turn", "순서대로 읽기"), TtsQueueMode.Queue),
                new Item(T("cuts in if more important", "더 중요한 알림이면 끊고 읽기"), TtsQueueMode.InterruptLower),
                new Item(T("always cuts in (the last one only)", "항상 끊고 읽기 (마지막 알림만)"), TtsQueueMode.InterruptAll),
            }, config.QueueMode, v => config.QueueMode = (TtsQueueMode)v));
            Row(queueGrid, T("Drop if not started within", "이 시간 안에 못 읽으면 버리기"), Seconds(config.MaxWaitMs, 0.5m, 10, v => config.MaxWaitMs = v));
            Row(queueGrid, T("Same text again within", "같은 문장이 다시 오면 무시"), Seconds(config.RepeatWindowMs, 0, 10, v => config.RepeatWindowMs = v));

            // Text made readable.
            var text = Section(root, T("Before reading", "읽기 전에 바꾸기"));
            text.Controls.Add(Check(T("Read text in brackets too", "괄호 안도 읽기"), config.ReadBrackets, v => { config.ReadBrackets = v; Changed(); }));
            text.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(760, 0),
                Text = T("Applied in order, top first. Language: only for voices of that language (empty: all). Regular expressions can use $1 in the replacement.",
                    "위에서부터 차례로 바꿉니다. 언어: 그 언어의 목소리일 때만 (비우면 전부). 정규식은 바꿀 말에 $1 등을 쓸 수 있습니다."),
            });
            rules = RulesGrid();
            text.Controls.Add(rules);
            var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
            buttons.Controls.Add(Button(T("Restore built-in rules", "기본 규칙 되돌리기"), RestoreBuiltIns));
            text.Controls.Add(buttons);

            // Try it.
            var preview = Section(root, T("Try it", "미리 듣기"));
            var previewRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            previewText = new TextBox { Width = 460, Text = T("Away from boss => stop (3 times) x2", "보스와 멀리 떨어지기 => 행동 멈추기 (3번) 탱버 x2") };
            previewText.TextChanged += (o, e) => ShowSpoken();
            previewRow.Controls.Add(previewText);
            previewRow.Controls.Add(Button(T("Read", "읽기"), () => service.Speak(previewText.Text, TtsSeverity.Alarm, "preview", Lang())));
            preview.Controls.Add(previewRow);
            previewSpoken = new Label { AutoSize = true, MaximumSize = new Size(760, 0) };
            preview.Controls.Add(previewSpoken);
            previewResult = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
            preview.Controls.Add(previewResult);
            service.Spoken += OnSpoken;

            ownerTimer = new Timer { Interval = 2000 };
            ownerTimer.Tick += (o, e) => ShowOwner();
            VisibleChanged += (o, e) =>
            {
                ownerTimer.Enabled = Visible;
                if (Visible)
                    ShowOwner();
            };
            ShowOwner();
            ShowSpoken();
            loading = false;
        }

        private static string Lang() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        private void Changed()
        {
            if (loading)
                return;
            service.ConfigChanged();
            ShowSpoken();
            pluginConfig.MarkDirty();
        }

        private void ApplyBridge()
        {
            bridge?.Apply();
            ShowOwner();
        }

        private void ShowOwner()
        {
            if (bridge == null)
                return;
            string who;
            switch (bridge.CurrentOwner)
            {
                case ActTtsBridge.Owner.Us: who = T("this plugin", "이 플러그인"); break;
                case ActTtsBridge.Owner.Act: who = T("ACT's own", "ACT 기본"); break;
                case ActTtsBridge.Owner.OtherPlugin: who = bridge.OwnerName; break;
                default: who = "?"; break;
            }
            owner.Text = T("ACT's TTS is now read by: ", "지금 ACT의 TTS를 읽는 곳: ") + who +
                (bridge.CurrentOwner == ActTtsBridge.Owner.OtherPlugin
                    ? T(" (left to it: taking it back would make the two fight over it)", " (그대로 둡니다: 다시 가져오면 두 플러그인이 서로 빼앗게 됩니다)")
                    : "");
        }

        private void ShowSpoken()
        {
            if (previewSpoken != null)
                previewSpoken.Text = T("Read as: ", "읽는 문장: ") + service.ProcessText(previewText.Text, Lang());
        }

        private void OnSpoken(TtsResult r)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed)
                    return;
                previewResult.Text = T($"Made in {r.MadeMs:0} ms, playing {r.StartedMs:0} ms after asked{(r.FromCache ? " (kept from before)" : "")}",
                    $"만드는 데 {r.MadeMs:0}ms, 요청부터 재생까지 {r.StartedMs:0}ms{(r.FromCache ? " (만들어 둔 음성)" : "")}");
            }));
        }

        private DataGridView RulesGrid()
        {
            var grid = new DataGridView
            {
                Width = 760,
                Height = 260,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                RowHeadersWidth = 24,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
            };
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = T("On", "사용"), FillWeight = 12 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Find", HeaderText = T("Find", "찾을 말"), FillWeight = 40 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Replace", HeaderText = T("Replace with", "바꿀 말"), FillWeight = 30 });
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Regex", HeaderText = T("Regex", "정규식"), FillWeight = 14 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Lang", HeaderText = T("Language", "언어"), FillWeight = 14 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Note", HeaderText = T("Note", "설명"), FillWeight = 40 });
            FillRules(grid);
            grid.CellValueChanged += (o, e) => SaveRules();
            grid.UserDeletedRow += (o, e) => SaveRules();
            grid.CurrentCellDirtyStateChanged += (o, e) =>
            {
                // Check boxes count as changed when clicked, not when the cell is left.
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            return grid;
        }

        private void FillRules(DataGridView grid)
        {
            bool wasLoading = loading;
            loading = true;
            grid.Rows.Clear();
            foreach (var r in config.Replacements)
            {
                int i = grid.Rows.Add(r.Enabled, r.Find, r.Replace, r.Regex, r.Lang, r.Note);
                grid.Rows[i].Tag = r.BuiltIn;
                if (r.BuiltIn)
                    grid.Rows[i].DefaultCellStyle.ForeColor = SystemColors.GrayText;
                if (r.Regex && !TtsTextProcessor.IsValidPattern(r.Find))
                    grid.Rows[i].Cells["Find"].Style.BackColor = Color.MistyRose;
            }
            loading = wasLoading;
        }

        private void SaveRules()
        {
            if (loading)
                return;
            var list = new List<TtsReplacement>();
            foreach (DataGridViewRow row in rules.Rows)
            {
                if (row.IsNewRow)
                    continue;
                string Cell(string name) => row.Cells[name].Value?.ToString() ?? "";
                bool Flag(string name) => row.Cells[name].Value is bool b && b;
                var find = Cell("Find");
                if (find == "")
                    continue;
                // A new row is on unless turned off.
                bool enabled = row.Cells["Enabled"].Value == null || Flag("Enabled");
                list.Add(new TtsReplacement
                {
                    Enabled = enabled,
                    Find = find,
                    Replace = Cell("Replace"),
                    Regex = Flag("Regex"),
                    Lang = Cell("Lang").Trim(),
                    Note = Cell("Note"),
                    BuiltIn = row.Tag is bool builtIn && builtIn,
                });
                row.Cells["Find"].Style.BackColor = Flag("Regex") && !TtsTextProcessor.IsValidPattern(find) ? Color.MistyRose : rules.DefaultCellStyle.BackColor;
            }
            config.Replacements = list;
            Changed();
        }

        // The built-in rules back as they came (turned on, edits undone), the user's own kept after.
        private void RestoreBuiltIns()
        {
            var own = config.Replacements.Where(r => !r.BuiltIn).ToList();
            config.Replacements = TtsTextProcessor.DefaultReplacements().Concat(own).ToList();
            FillRules(rules);
            Changed();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                service.Spoken -= OnSpoken;
                ownerTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        // Layout helpers.

        private static FlowLayoutPanel Section(TableLayoutPanel root, string title)
        {
            root.Controls.Add(new Label
            {
                Text = title,
                AutoSize = true,
                Font = new Font(SystemFonts.DefaultFont.FontFamily, SystemFonts.DefaultFont.Size + 1, FontStyle.Bold),
                Margin = new Padding(0, 10, 0, 4),
            });
            var body = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(12, 0, 0, 0),
            };
            root.Controls.Add(body);
            return body;
        }

        private static TableLayoutPanel Grid(Control parent)
        {
            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            parent.Controls.Add(grid);
            return grid;
        }

        private static void Row(TableLayoutPanel grid, string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) });
            grid.Controls.Add(control);
        }

        private CheckBox Check(string text, bool value, Action<bool> set)
        {
            var box = new CheckBox { Text = text, AutoSize = true, Checked = value };
            box.CheckedChanged += (o, e) =>
            {
                if (loading)
                    return;
                set(box.Checked);
                pluginConfig.MarkDirty();
            };
            return box;
        }

        private ComboBox Combo(IEnumerable<Item> items, object value, Action<object> set)
        {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
            foreach (var item in items)
                box.Items.Add(item);
            box.SelectedItem = box.Items.Cast<Item>().FirstOrDefault(i => Equals(i.Value, value)) ?? (box.Items.Count > 0 ? box.Items[0] : null);
            box.SelectedIndexChanged += (o, e) =>
            {
                if (loading || !(box.SelectedItem is Item item))
                    return;
                set(item.Value);
                pluginConfig.MarkDirty();
            };
            return box;
        }

        private Control Slider(int min, int max, int value, Action<int> set, Func<int, string> show)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var bar = new TrackBar { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), Width = 300, TickStyle = TickStyle.None, SmallChange = 1, LargeChange = Math.Max(1, (max - min) / 10), AutoSize = false, Height = Font.Height + 12 };
            var label = new Label { AutoSize = true, Text = show(bar.Value), Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
            bar.ValueChanged += (o, e) =>
            {
                label.Text = show(bar.Value);
                if (loading)
                    return;
                set(bar.Value);
                pluginConfig.MarkDirty();
            };
            row.Controls.Add(bar);
            row.Controls.Add(label);
            return row;
        }

        private Control Seconds(int ms, decimal min, decimal max, Action<int> set)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var box = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = 1, Increment = 0.5m, Width = 70, Value = Math.Max(min, Math.Min(max, ms / 1000m)) };
            box.ValueChanged += (o, e) =>
            {
                if (loading)
                    return;
                set((int)(box.Value * 1000));
                pluginConfig.MarkDirty();
            };
            row.Controls.Add(box);
            row.Controls.Add(new Label { Text = T("s", "초"), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) });
            return row;
        }

        private static Button Button(string text, Action click)
        {
            var button = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 2, 8, 2) };
            button.Click += (o, e) => click();
            return button;
        }

        private class Item
        {
            public string Name { get; }
            public object Value { get; }

            public Item(string name, object value)
            {
                Name = name;
                Value = value;
            }

            public override string ToString() => Name;
        }
    }
}
