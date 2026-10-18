using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
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
        private TrackBar masterBar;
        private Label cactbotNote;
        private Label statsLabel;
        private Label paidNote;
        private readonly TtsConfig config;
        private Label owner;
        private Label previewResult;
        private Label previewSpoken;
        private TextBox previewText;
        private DataGridView rules;
        private readonly Timer ownerTimer;
        private FlowLayoutPanel providerFields;
        private Label cacheSize;
        private Label prepared;
        private FlowLayoutPanel usagePanel;
        private readonly WheelGuard wheelGuard;
        // Tabs shown only for some engines: slow ones (fallback, making ahead), paid ones (usage).
        private TabPage slowPage;
        private TabPage usagePage;
        private readonly List<TabPage> allPages = new List<TabPage>();
        private CheckBox localPrepare;
        private Label localPrepareHint;
        private Label speedHint;
        private TabControl tabs;
        private TabPage madePage;
        private DataGridView madeGrid;
        private List<TtsCacheGroup> madeGroups = new List<TtsCacheGroup>();
        private int madeGeneration;
        private Action refreshUsage;
        private bool loading;

        public TtsConfigPanel(TinyIoCContainer container)
        {
            service = container.Resolve<TtsService>();
            container.TryResolve(ResolveOptions.FailUnregisteredOnly, out bridge);
            pluginConfig = container.Resolve<IPluginConfig>();
            config = service.Config;

            Dock = DockStyle.Fill;
            DoubleBuffered = true;
            // The wheel scrolls the page, never a value under the pointer (see WheelGuard).
            wheelGuard = new WheelGuard(this);
            Application.AddMessageFilter(wheelGuard);
            service.Usage.Changed += OnUsageChanged;
            service.Prepared += OnPrepared;
            service.Spoken += OnSpoken;
            service.VolumeChanged += OnVolumeChanged;
            service.PausedChanged += OnUsageChanged;
            ownerTimer = new Timer { Interval = 2000 };
            ownerTimer.Tick += (o, e) =>
            {
                ShowOwner();
                if (tabs?.SelectedTab == slowPage)
                    ShowStats();
            };
            VisibleChanged += (o, e) =>
            {
                ownerTimer.Enabled = Visible;
                if (Visible)
                {
                    ShowOwner();
                    OnPrepared(service.PrepareProgress.Done, service.PrepareProgress.Total);
                    refreshDownload?.Invoke();
                    refreshUsage?.Invoke();
                }
            };
            Build();
        }

        // The tab's contents, made again after a reset to defaults.  Laid out once at the end (not
        // control by control), so it does not show half-built.  Pages: the voice, alerts
        // overlapping, reading rules, made speech and usage; the preview below them all.
        private void Build()
        {
            loading = true;
            SuspendLayout();
            foreach (Control c in Controls.Cast<Control>().ToList())
                c.Dispose();
            Controls.Clear();
            madeGroups = new List<TtsCacheGroup>();
            tabs = new TabControl { Dock = DockStyle.Fill };
            Controls.Add(tabs);
            Controls.Add(PreviewStrip());

            // The voice.
            var basic = Page(T("Voice", "기본"));
            var general = Section(basic, T("Text to speech", "TTS"));
            general.Controls.Add(Check(T("Read alerts with this plugin's TTS (off: ACT's own TTS, as before)", "알림을 이 플러그인의 TTS로 읽기 (끄면 지금까지처럼 ACT의 TTS)"),
                config.Enabled, v => { config.Enabled = v; ApplyBridge(); Changed(); ShowCactbotNote(); }));
            cactbotNote = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(700, 0),
                ForeColor = Color.FromArgb(190, 70, 0),
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(20, 0, 3, 6),
                Visible = false,
            };
            general.Controls.Add(cactbotNote);
            general.Controls.Add(Check(T("Also read what ACT itself speaks (its custom triggers, other plugins)", "ACT가 읽는 것(커스텀 트리거, 다른 플러그인)도 이 플러그인으로 읽기"),
                config.TakeOverActTts, v => { config.TakeOverActTts = v; ApplyBridge(); }));
            general.Controls.Add(Check(T("When another plugin speaks for ACT (e.g. TTSYukkuri), send overlays' alerts to it too", "다른 플러그인(TTSYukkuri 등)이 ACT의 TTS를 맡고 있으면 오버레이 알림도 그쪽으로 보내기"),
                config.DeferToOtherPlugin, v => config.DeferToOtherPlugin = v));
            owner = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 2, 3, 6) };
            general.Controls.Add(owner);

            var voice = Section(basic, T("Voice", "음성"));
            var voiceGrid = Grid(voice);
            ComboBox engines = null;
            engines = Combo(service.Providers.Select(p => new Item($"{p.Name} ({p.Note})", p.Id)), config.Provider, v =>
            {
                if (!ConfirmPaid((string)v))
                {
                    loading = true;
                    engines.SelectedItem = engines.Items.Cast<Item>().FirstOrDefault(i => Equals(i.Value, config.Provider));
                    loading = false;
                    return;
                }
                config.Provider = (string)v;
                Changed();
                ShowProviderFields();
            });
            Grouped(engines, item =>
            {
                var p = service.Providers.FirstOrDefault(x => Equals(x.Id, item.Value));
                return p == null ? 0 : p.Id == "http" ? 2 : TtsUsageCounter.Billed(p) ? 1 : 0;
            });
            Row(voiceGrid, T("Engine", "엔진"), engines);
            // The chosen engine's own settings (voice, or address, key...), made again when it changes.
            providerFields = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            voice.Controls.Add(providerFields);
            var voiceGrid2 = Grid(voice);
            Row(voiceGrid2, T("Speed", "속도"), SpeedSlider());
            speedHint = Hint("");
            voice.Controls.Add(speedHint);
            var voiceGrid3 = Grid(voice);
            var devices = new List<Item> { new Item(T("Default device", "기본 장치"), "") };
            devices.AddRange(TtsPlayer.GetDevices().Select(d => new Item(d.Name, d.Id)));
            Row(voiceGrid3, T("Output", "출력 장치"), Combo(devices, config.OutputDevice, v => config.OutputDevice = (string)v));

            var volume = Section(basic, T("Volume", "볼륨"));
            var volumeGrid = Grid(volume);
            string Percent(int v) => v + "%";
            var master = Slider(0, 100, config.Volume, v => config.Volume = v, Percent);
            masterBar = master.Controls.OfType<TrackBar>().First();
            Row(volumeGrid, T("All speech", "전체"), master);
            Row(volumeGrid, T("ACT's own (custom triggers, other plugins)", "ACT가 읽는 것 (커스텀 트리거, 다른 플러그인)"), Slider(0, 100, config.OtherVolume, v => config.OtherVolume = v, Percent));
            volume.Controls.Add(Hint(T("cactbot's alerts: by how important they are in cactbot's config (Raidboss > TTS voice). The volume of all speech is the same one there.",
                "캑봇 알림은 캑봇 설정(레이드보스 > TTS 음성)에서 경고·주의·안내별로 정합니다. 전체 볼륨은 그곳과 같은 값입니다.")));

            var reset = Section(basic, T("Settings", "설정"));
            reset.Controls.Add(Button(T("Reset settings to defaults...", "설정을 기본값으로..."), ResetToDefaults));
            reset.Controls.Add(Hint(T(KeptOnReset, KeptOnResetKo)));

            // When alerts come together.
            var alerts = Page(T("Alerts", "알림 처리"));
            var queue = Section(alerts, T("When alerts overlap", "알림이 겹칠 때"));
            var queueGrid = Grid(queue);
            Row(queueGrid, T("A new alert", "새 알림이 오면"), Combo(new[]
            {
                new Item(T("waits its turn", "순서대로 읽기"), TtsQueueMode.Queue),
                new Item(T("cuts in if more important", "더 중요한 알림이면 끊고 읽기"), TtsQueueMode.InterruptLower),
                new Item(T("always cuts in (the last one only)", "항상 끊고 읽기 (마지막 알림만)"), TtsQueueMode.InterruptAll),
            }, config.QueueMode, v => config.QueueMode = (TtsQueueMode)v));
            Row(queueGrid, T("Drop if not started within", "이 시간 안에 못 읽으면 버리기"), Seconds(config.MaxWaitMs, 0.5m, 10, v => config.MaxWaitMs = v));
            Row(queueGrid, T("Same text again within", "같은 문장이 다시 오면 무시"), Seconds(config.RepeatWindowMs, 0, 10, v => config.RepeatWindowMs = v));
            queue.Controls.Add(Hint(T("How important an alert is comes from the overlay (cactbot: alarm, alert, info). ACT's own speech (custom triggers, other plugins) says nothing of it and counts as least important.",
                "알림이 얼마나 중요한지는 오버레이가 알려 줍니다 (캑봇: 경고, 주의, 정보). ACT가 읽는 것(커스텀 트리거, 다른 플러그인)은 중요도가 없어서 가장 낮은 것으로 칩니다.")));

            // Text made readable.
            var reading = Page(T("Reading", "읽기 규칙"));
            var text = Section(reading, T("Before reading", "읽기 전에 바꾸기"));
            text.Controls.Add(Check(T("Read text in brackets too", "괄호 안도 읽기"), config.ReadBrackets, v => { config.ReadBrackets = v; Changed(); }));
            text.Controls.Add(Hint(T("Applied in order, top first. Language: only for voices of that language (empty: all). Regular expressions can use $1 in the replacement. A row is deleted by selecting it on its left edge and pressing Delete.",
                "위에서부터 차례로 바꿉니다. 언어: 그 언어의 목소리일 때만 (비우면 전부). 정규식은 바꿀 말에 $1 등을 쓸 수 있습니다. 행은 왼쪽 끝을 눌러 고른 뒤 Delete 키로 지웁니다.")));
            rules = RulesGrid();
            text.Controls.Add(rules);
            text.Controls.Add(Button(T("Restore built-in rules", "기본 규칙 되돌리기"), RestoreBuiltIns));

            // Engines that take time: falling back, making ahead, how it went (shown for them only,
            // see ShowProviderFields).
            var slow = Page(T("Fallback, ahead", "대신 읽기·미리 만들기"));
            slowPage = (TabPage)slow.Parent;
            var network = Section(slow, T("Fallback and making ahead", "대신 읽기와 미리 만들기"));
            network.Controls.Add(Hint(T("For engines that take time to make speech (services on the internet, Supertonic).",
                "음성을 만드는 데 시간이 걸리는 엔진(인터넷 서비스, Supertonic)에 적용됩니다.")));
            var networkGrid = Grid(network);
            Row(networkGrid, T("Windows' voice instead if not ready within", "이 시간 안에 준비 안 되면 Windows 음성으로 대신 읽기"), Seconds(config.FallbackMs, 0.1m, 5, v => config.FallbackMs = v, 0.1m));
            network.Controls.Add(Hint(T("Not for the preview below, which waits for the voice itself.", "아래 미리 듣기에는 쓰지 않습니다 (그 목소리 자체를 기다림).")));
            network.Controls.Add(Check(T("Make ahead the alerts cactbot sends on entering a zone (so the first time is quick too; a paid service charges each once)",
                "캑봇이 지역에 들어갈 때 보내 주는 알림 문장을 미리 만들어 두기 (처음 나오는 알림도 바로 읽음, 유료 서비스는 문장마다 한 번 요금)"), config.PrepareAhead, v => { config.PrepareAhead = v; if (!v) service.CancelPrepare(); }));
            network.Controls.Add(Hint(T("A paid service makes all of a zone's alerts the first time (often hundreds of texts, thousands of characters). Made once, kept for good; but a small free allowance (ElevenLabs' free plan: 10,000 characters a month; Gemini's free tier: requests a day) can be gone in a few zones. So a paid service makes ahead only once a monthly limit is set (Usage tab). When a service says it is over its limit, it is left alone for a while and Windows' voice reads.",
                "유료 서비스는 지역에 처음 들어갈 때 그 지역 알림 전부(보통 수백 문장, 수천 자)를 만듭니다. 한 번 만든 음성은 계속 쓰지만, 무료 사용량이 적은 서비스(ElevenLabs 무료 플랜 월 1만 자, Gemini 무료 등급의 하루 요청 수)는 몇 지역 만에 다 쓸 수 있습니다. 그래서 유료 서비스는 월 한도를 정해야 미리 만듭니다 (사용량 탭). 서비스가 한도를 넘었다고 답하면 한동안 보내지 않고 Windows 음성으로 읽습니다.")));
            localPrepare = Check(T("Also with Supertonic (uses the CPU for a few minutes after entering a zone)",
                "Supertonic도 미리 만들기 (지역에 들어간 뒤 몇 분 동안 CPU를 씀)"), config.PrepareAheadLocal, v => { config.PrepareAheadLocal = v; if (!v) service.CancelPrepare(); });
            network.Controls.Add(localPrepare);
            localPrepareHint = Hint(T("Usually not needed: made at the time, an alert is ready well within the fallback time. If This session below shows alerts read late, turn it on.",
                "보통은 필요 없습니다. 그때그때 만들어도 대신 읽기 기준 시간 안에 넉넉히 준비됩니다. 아래 이번 접속 통계에 늦음이 자주 보이면 켜세요."));
            network.Controls.Add(localPrepareHint);
            prepared = Hint("");
            network.Controls.Add(prepared);
            // How it did this session: whether making ahead is needed.
            var stats = Section(slow, T("This session", "이번 접속 통계"));
            statsLabel = new Label { AutoSize = true, MaximumSize = new Size(700, 0), Margin = new Padding(3, 0, 3, 3) };
            stats.Controls.Add(statsLabel);
            stats.Controls.Add(Hint(T("Made speech: made before (ahead, or heard before). Made in time: within the fallback time. To see whether making ahead is needed, play with it off and look here.",
                "만들어 둔 음성: 미리 만들었거나 전에 한 번 읽어서 저장된 것. 제시간: 대신 읽기 기준 시간 안에 만든 것. 미리 만들기가 필요한지 보려면 끄고 한 판 돌아 본 뒤 여기를 보세요.")));
            stats.Controls.Add(Button(T("Reset the stats", "통계 초기화"), () =>
            {
                service.Stats.Reset();
                ShowStats();
            }));

            // A paid voice's usage (made again with the provider's fields; shown for it only).
            var usage = Page(T("Usage", "사용량"));
            usagePage = (TabPage)usage.Parent;
            usagePanel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            usage.Controls.Add(usagePanel);

            // Made speech kept on disk.
            var made = Page(T("Made speech", "만든 음성"));
            madePage = (TabPage)made.Parent;
            MadeSection(made);
            tabs.SelectedIndexChanged += (o, e) =>
            {
                if (tabs.SelectedTab == madePage)
                    RefreshMade();
                if (tabs.SelectedTab == slowPage)
                    ShowStats();
            };

            ShowOwner();
            ShowProviderFields();
            ShowSpoken();
            OnPrepared(service.PrepareProgress.Done, service.PrepareProgress.Total);
            ResumeLayout(true);
            loading = false;
        }

        private TrackBar speedBar;
        private Label speedLabel;

        // Speed as times the voice's own (0.50 to 2.00), the same for every engine; what the
        // chosen one says it at is shown beside it when it cannot reach it.
        private Control SpeedSlider()
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            speedBar = new TrackBar
            {
                Minimum = 50,
                Maximum = 200,
                TickFrequency = 25,
                TickStyle = TickStyle.None,
                SmallChange = 5,
                LargeChange = 10,
                Value = (int)Math.Round(Math.Max(0.5, Math.Min(2.0, config.Speed)) * 100),
                Width = 300,
                AutoSize = false,
                Height = Font.Height + 12,
            };
            speedLabel = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
            speedBar.ValueChanged += (o, e) =>
            {
                // Steps of 0.05.
                int snapped = (int)Math.Round(speedBar.Value / 5.0) * 5;
                if (snapped != speedBar.Value)
                {
                    speedBar.Value = snapped;
                    return;
                }
                UpdateSpeed();
                if (loading || Math.Abs(config.Speed - speedBar.Value / 100.0) < 1e-9)
                    return;
                config.Speed = speedBar.Value / 100.0;
                Changed();
            };
            row.Controls.Add(speedBar);
            row.Controls.Add(speedLabel);
            return row;
        }

        private void UpdateSpeed()
        {
            if (speedLabel == null || speedBar == null)
                return;
            var provider = service.Provider;
            double v = speedBar.Value / 100.0;
            if (provider.MinSpeed >= provider.MaxSpeed)
            {
                speedLabel.Text = T($"{v:0.00}x", $"{v:0.00}배");
                speedHint.Text = T($"{provider.Name} has no speed setting (its way of speaking can be told in words).",
                    $"{provider.Name}에는 속도 설정이 없습니다 (말투 지시로 바꿀 수 있음).");
            }
            else
            {
                double effective = Math.Max(provider.MinSpeed, Math.Min(provider.MaxSpeed, v));
                speedLabel.Text = T($"{v:0.00}x", $"{v:0.00}배") +
                    (Math.Abs(effective - v) > 0.001 ? T($" (said at {effective:0.00}x)", $" (이 엔진은 {effective:0.00}배로 읽음)") : "");
                speedHint.Text = T($"1.00x: the voice's own speed. {provider.Name}: {provider.MinSpeed:0.00}x to {provider.MaxSpeed:0.00}x (beyond: the nearest end).",
                    $"1.00배가 그 목소리 본래 속도입니다. {provider.Name}: {provider.MinSpeed:0.00}~{provider.MaxSpeed:0.00}배 (그 밖은 가까운 끝값으로).");
            }
            speedHint.Visible = true;
        }

        private TableLayoutPanel Page(string title)
        {
            var page = new TabPage { Text = title, AutoScroll = true, UseVisualStyleBackColor = true };
            var root = new TableLayoutPanel
            {
                ColumnCount = 1,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8),
            };
            page.Controls.Add(root);
            tabs.TabPages.Add(page);
            allPages.Add(page);
            return root;
        }

        // A tab put back in its place among the others, or taken out.
        private void ShowTab(TabPage page, bool shown)
        {
            if (page == null || tabs.TabPages.Contains(page) == shown)
                return;
            if (!shown)
            {
                tabs.TabPages.Remove(page);
                return;
            }
            int index = 0;
            foreach (var other in allPages)
            {
                if (other == page)
                    break;
                if (tabs.TabPages.Contains(other))
                    index++;
            }
            tabs.TabPages.Insert(index, page);
        }

        // Try it: below every page, so a change can be heard from where it is made.
        private Control PreviewStrip()
        {
            var strip = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                Padding = new Padding(8, 6, 8, 8),
            };
            strip.Controls.Add(new Label
            {
                Text = T("Try it (the chosen voice, waited for)", "미리 듣기 (고른 목소리를 기다려서 읽음)"),
                AutoSize = true,
                Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
                Margin = new Padding(3, 0, 3, 3),
            });
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            previewText = new TextBox { Width = 460, Text = T("Away from boss => stop (3 times) x2", "보스와 멀리 떨어지기 => 행동 멈추기 (3번) 탱버 x2") };
            previewText.TextChanged += (o, e) => ShowSpoken();
            row.Controls.Add(previewText);
            row.Controls.Add(Button(T("Read", "읽기"), () =>
            {
                if (string.IsNullOrWhiteSpace(previewText.Text))
                {
                    previewResult.Text = T("Nothing to read.", "읽을 글자가 없습니다.");
                    return;
                }
                previewResult.Text = T("making...", "만드는 중...");
                service.Speak(previewText.Text, TtsSeverity.Alarm, "preview", Lang());
            }));
            // For asking about an error without a screenshot.
            row.Controls.Add(Button(T("Copy the result", "결과 복사"), () =>
            {
                var text = string.Join(Environment.NewLine, new[] { ProviderLine(), previewSpoken.Text, previewResult.Text }.Where(t => !string.IsNullOrWhiteSpace(t)));
                try
                {
                    Clipboard.SetText(text);
                }
                catch (Exception)
                {
                    // The clipboard held by another program: nothing copied.
                }
            }));
            strip.Controls.Add(row);
            previewSpoken = new Label { AutoSize = true, MaximumSize = new Size(760, 0) };
            strip.Controls.Add(previewSpoken);
            previewResult = new Label { AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = SystemColors.GrayText };
            strip.Controls.Add(previewResult);
            return strip;
        }

        // What the reset leaves (the same in its button's hint and its question).
        private const string KeptOnReset = "Engines' voices and quality go back too. Kept: whether TTS is on, API keys and IDs, addresses and regions, a hand-written HTTP request, downloaded models, usage counts, made speech and your own rules.";
        private const string KeptOnResetKo = "엔진별 목소리·품질도 돌아갑니다. 그대로 두는 것: TTS 켜짐 여부, API 키·ID, 주소·지역, 직접 작성한 HTTP 요청, 다운로드한 모델, 사용량, 만든 음성, 직접 추가한 규칙.";

        // Every setting back to its default, but what the user brought or made: keys, downloaded
        // models, usage counts, made speech, their own rules (and whether TTS is on).
        private void ResetToDefaults()
        {
            if (MessageBox.Show(this,
                T("Reset the TTS settings to their defaults? " + KeptOnReset, "TTS 설정을 기본값으로 되돌릴까요? " + KeptOnResetKo),
                "TTS", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;
            var d = new TtsConfig();
            config.TakeOverActTts = d.TakeOverActTts;
            config.DeferToOtherPlugin = d.DeferToOtherPlugin;
            config.Provider = d.Provider;
            config.Voice = d.Voice;
            config.Speed = d.Speed;
            config.Volume = d.Volume;
            config.OtherVolume = d.OtherVolume;
            config.OutputDevice = d.OutputDevice;
            config.QueueMode = d.QueueMode;
            config.MaxWaitMs = d.MaxWaitMs;
            config.RepeatWindowMs = d.RepeatWindowMs;
            config.ReadBrackets = d.ReadBrackets;
            config.FallbackMs = d.FallbackMs;
            config.DiskCacheMB = d.DiskCacheMB;
            config.PrepareAhead = d.PrepareAhead;
            config.PrepareAheadLocal = d.PrepareAheadLocal;
            config.Replacements = TtsTextProcessor.DefaultReplacements().Concat(config.Replacements.Where(r => !r.BuiltIn)).ToList();
            foreach (var provider in service.Providers)
            {
                if (provider.Id == "http" || !config.ProviderSettings.TryGetValue(provider.Id, out var saved) || saved == null)
                    continue;
                foreach (var field in provider.Fields)
                {
                    if (field.Kind != TtsFieldKind.Secret && field.Key != "url" && field.Key != "region" && field.Key != "id")
                        saved.Remove(field.Key);
                }
            }
            bridge?.Apply();
            service.ConfigChanged();
            pluginConfig.MarkDirty();
            Build();
        }

        // Made speech on disk, by voice and zone: what it takes, whether it was paid for, removed by
        // choice (asked first; a paid one is made, and paid for, again when needed).
        private void MadeSection(TableLayoutPanel page)
        {
            var section = Section(page, T("Made speech on disk", "디스크에 보관한 음성"));
            section.Controls.Add(Hint(T("Speech from services on the internet and Supertonic is kept and played from here; it is not made (or paid for) again, also after restarting ACT. Over the size limit the oldest go first, free voices' before paid ones'.",
                "인터넷 서비스와 Supertonic으로 만든 음성을 보관해 다시 만들지 않고(유료는 요금도 다시 안 듦) 바로 재생합니다. ACT를 다시 시작해도 남습니다. 크기 한도를 넘으면 오래된 것부터, 무료 음성 것을 먼저 지웁니다.")));
            var sizeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var sizeBox = new NumericUpDown { Minimum = 0, Maximum = 10000, Increment = 50, Width = 80, Value = Math.Max(0, Math.Min(10000, config.DiskCacheMB)) };
            sizeBox.ValueChanged += (o, e) =>
            {
                if (loading)
                    return;
                config.DiskCacheMB = (int)sizeBox.Value;
                pluginConfig.MarkDirty();
            };
            sizeRow.Controls.Add(new Label { Text = T("Size limit", "크기 한도"), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 6, 3) });
            sizeRow.Controls.Add(sizeBox);
            sizeRow.Controls.Add(new Label { Text = "MB", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) });
            cacheSize = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) };
            sizeRow.Controls.Add(cacheSize);
            section.Controls.Add(sizeRow);

            madeGrid = new DataGridView
            {
                Width = 760,
                Height = 220,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            };
            madeGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Pick", HeaderText = T("Pick", "선택"), FillWeight = 10 });
            madeGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Voice", HeaderText = T("Voice", "엔진 · 목소리"), FillWeight = 45, ReadOnly = true });
            madeGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Zone", HeaderText = T("Zone", "지역"), FillWeight = 30, ReadOnly = true });
            madeGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Count", HeaderText = T("Texts", "문장"), FillWeight = 10, ReadOnly = true });
            madeGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Size", HeaderText = T("Size", "크기"), FillWeight = 12, ReadOnly = true });
            madeGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Paid", HeaderText = T("Paid", "유료"), FillWeight = 8, ReadOnly = true });
            foreach (DataGridViewColumn column in madeGrid.Columns)
                column.SortMode = DataGridViewColumnSortMode.NotSortable;
            madeGrid.CurrentCellDirtyStateChanged += (o, e) =>
            {
                if (madeGrid.IsCurrentCellDirty && madeGrid.CurrentCell is DataGridViewCheckBoxCell)
                    madeGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            section.Controls.Add(madeGrid);

            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            buttons.Controls.Add(Button(T("Refresh", "새로 고침"), RefreshMade));
            buttons.Controls.Add(Button(T("Delete picked...", "선택한 것 지우기..."), () => DeleteMade(Picked())));
            buttons.Controls.Add(Button(T("Delete all...", "모두 지우기..."), () => DeleteMade(madeGroups)));
            section.Controls.Add(buttons);
        }

        private List<TtsCacheGroup> Picked()
        {
            var picked = new List<TtsCacheGroup>();
            foreach (DataGridViewRow row in madeGrid.Rows)
            {
                if (row.Cells["Pick"].Value is bool b && b && row.Tag is TtsCacheGroup g)
                    picked.Add(g);
            }
            return picked;
        }

        // Read off the UI thread (the folder can hold thousands of files), shown when done.
        private void RefreshMade()
        {
            if (madeGrid == null)
                return;
            var grid = madeGrid;
            int generation = ++madeGeneration;
            System.Threading.Tasks.Task.Run(() => (Groups: service.DiskCache.Groups(), Size: service.DiskCache.Size())).ContinueWith(t =>
            {
                // An older scan finishing after a newer one (e.g. after a delete) is not shown.
                if (grid.IsDisposed || t.IsFaulted || generation != madeGeneration)
                    return;
                madeGroups = t.Result.Groups;
                grid.Rows.Clear();
                foreach (var g in madeGroups)
                {
                    int i = grid.Rows.Add(false,
                        g.Label ?? T("(made before this was recorded)", "(기록 전에 만든 것)"),
                        g.Zone ?? T("(unknown)", "(모름)"),
                        g.Shared > 0 ? T($"{g.Count:N0} ({g.Shared:N0} shared)", $"{g.Count:N0} (공유 {g.Shared:N0})") : g.Count.ToString("N0"),
                        (g.Bytes / 1024.0 / 1024.0).ToString("0.0") + " MB",
                        g.Paid ? "✓" : "");
                    grid.Rows[i].Tag = g;
                }
                // Each file once (a file used in several zones is in each zone's group).
                long total = t.Result.Size;
                if (!cacheSize.IsDisposed)
                    cacheSize.Text = T($"now {total / 1024.0 / 1024.0:0.0} MB", $"지금 {total / 1024.0 / 1024.0:0.0}MB");
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void DeleteMade(List<TtsCacheGroup> groups)
        {
            if (groups.Count == 0)
                return;
            // Speech another zone also uses stays (only these zones come off it).
            var plan = service.DiskCache.Remove(groups, false);
            bool paid = groups.Any(g => g.Paid);
            var message = plan.Deleted > 0
                ? T($"Delete {plan.Deleted:N0} made texts?", $"만든 음성 {plan.Deleted:N0}개를 지울까요?")
                : T("Nothing here is used by these zones alone, so nothing is deleted.", "이 지역에서만 쓰는 음성이 없어서 지울 것은 없습니다.");
            if (plan.Kept > 0)
                message += "\n\n" + T($"{plan.Kept:N0} more are also used in other zones: they are kept (only these zones come off them).",
                    $"다른 지역에서도 쓰는 {plan.Kept:N0}개는 지우지 않고 남깁니다 (이 지역 표시만 뺍니다).");
            if (paid && plan.Deleted > 0)
                message += "\n\n" + T("Some were made by a paid service: they are made, and paid for, again when needed.",
                    "유료 서비스로 만든 것이 있습니다. 지우면 필요할 때 다시 만들며 요금도 다시 듭니다.");
            if (MessageBox.Show(this, message, "TTS", MessageBoxButtons.OKCancel, paid && plan.Deleted > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK)
                return;
            // Off the UI thread (thousands of files take a while), shown again when done.
            System.Threading.Tasks.Task.Run(() => service.DiskCache.Remove(groups, true)).ContinueWith(_ =>
            {
                service.ClearMemory();
                if (!IsDisposed)
                    RefreshMade();
            }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
        }

        // The engine's own settings: Windows' voices to pick from, or a service's fields.
        // Said in a paid engine's fields, with its monthly limit (kept up when the limit changes).
        private string PaidNoteText(ITtsProvider provider)
        {
            long limit = service.Usage.Get(provider).MonthlyLimit;
            return T($"Paid: billed to your account by the character. Monthly limit: {(limit > 0 ? $"{limit:N0} characters" : "none (nothing is made ahead until one is set)")}",
                $"유료: 읽은 글자 수만큼 본인 계정에 요금이 나옵니다. 월 한도: {(limit > 0 ? $"{limit:N0}자" : "없음 (정하기 전에는 미리 만들기 안 함)")}");
        }

        // A paid service chosen: what it costs and how making ahead uses it, said before it is used.
        private bool ConfirmPaid(string id)
        {
            var provider = service.Providers.FirstOrDefault(p => p.Id == id);
            if (provider == null || !TtsUsageCounter.Paid(provider, new TtsSettings(provider, config)))
                return true;
            return MessageBox.Show(this,
                T($"{provider.Name} is a paid service.\n\n" +
                  "- What it reads is billed to your own account, by the character.\n" +
                  "- Making ahead makes all of a zone's alerts the first time you enter it: often hundreds of texts, thousands of characters. With cactbot's \"make ahead alerts naming a player too\", more.\n" +
                  "- Free allowances differ and can be small (ElevenLabs' free plan: 10,000 characters a month).\n" +
                  "- Nothing is made ahead until you set a monthly limit (Usage tab). At the limit, Windows' voice reads for the rest of the month.\n\n" +
                  "Use this engine?",
                  $"{provider.Name}은(는) 유료 서비스입니다.\n\n" +
                  "· 읽은 글자 수만큼 본인 계정에 요금이 청구됩니다.\n" +
                  "· 미리 만들기는 지역에 처음 들어갈 때 그 지역 알림 전부(보통 수백 문장, 수천 자)를 만듭니다. 캑봇의 \"플레이어가 들어간 알림도 미리 만들기\"를 켜면 더 늘어납니다.\n" +
                  "· 무료 사용량은 서비스마다 다르고 작을 수 있습니다 (ElevenLabs 무료 플랜은 월 1만 자).\n" +
                  "· 월 한도를 정하기 전에는 미리 만들기를 하지 않습니다 (사용량 탭). 한도에 닿으면 그 달 남은 기간은 Windows 음성으로 읽습니다.\n\n" +
                  "이 엔진을 쓸까요?"),
                "TTS", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
        }

        private void ShowProviderFields()
        {
            bool wasLoading = loading;
            loading = true;
            providerFields.SuspendLayout();
            foreach (Control c in providerFields.Controls.Cast<Control>().ToList())
                c.Dispose();
            providerFields.Controls.Clear();
            var provider = service.Provider;
            var grid = Grid(providerFields);
            if (provider is ITtsDownloadable downloadable)
            {
                // A local model: downloaded only when asked for, then its own settings.
                providerFields.Controls.Add(DownloadPanel(downloadable));
                foreach (var field in provider.Fields)
                    Row(grid, ko ? field.LabelKo : field.Label, FieldControl(provider, field));
                providerFields.Controls.SetChildIndex(grid, providerFields.Controls.Count - 1);
            }
            else if (!provider.IsSlow)
            {
                var voices = new List<Item> { new Item(T("Automatic (the alert's language)", "자동 (알림 언어에 맞춰)"), "") };
                voices.AddRange(provider.GetVoices(service.Settings).Select(v => new Item(v.Name, v.Id)));
                Row(grid, T("Voice", "목소리"), Combo(voices, config.Voice, v => { config.Voice = (string)v; Changed(); }));
            }
            else
            {
                foreach (var field in provider.Fields)
                    Row(grid, ko ? field.LabelKo : field.Label, FieldControl(provider, field));
                paidNote = null;
                if (TtsUsageCounter.Paid(provider, service.Settings))
                {
                    providerFields.Controls.Add(paidNote = new Label
                    {
                        Text = PaidNoteText(provider),
                        AutoSize = true,
                        MaximumSize = new Size(760, 0),
                        ForeColor = Color.FromArgb(190, 70, 0),
                        Font = new Font(Font, FontStyle.Bold),
                        Margin = new Padding(3, 0, 3, 6),
                    });
                    // First, right under the engine.
                    providerFields.Controls.SetChildIndex(paidNote, 0);
                }
                providerFields.Controls.Add(Hint(T("Your own key; it is kept encrypted for this Windows user.",
                    "본인 키를 씁니다. 키는 이 Windows 사용자만 풀 수 있게 암호화해 저장합니다.")));
            }
            providerFields.ResumeLayout();
            // Falling back and making ahead are only for a slow voice (network, Supertonic), usage
            // only for a paid one; the Supertonic option only for it.
            ShowTab(slowPage, provider.IsSlow);
            ShowTab(usagePage, TtsUsageCounter.Billed(provider));
            if (localPrepare != null)
                localPrepare.Visible = localPrepareHint.Visible = provider is ITtsDownloadable;
            UpdateSpeed();
            ShowUsage();
            loading = wasLoading;
        }

        private void OnUsageChanged()
        {
            if (IsDisposed || !IsHandleCreated || !Visible)
                return;
            BeginInvoke((Action)(() =>
            {
                if (!IsDisposed)
                    refreshUsage?.Invoke();
            }));
        }

        // How much the chosen paid voice was used, what it may cost, and its monthly limit.
        private void ShowUsage()
        {
            if (usagePanel == null)
                return;
            bool wasLoading = loading;
            loading = true;
            foreach (Control c in usagePanel.Controls.Cast<Control>().ToList())
                c.Dispose();
            usagePanel.Controls.Clear();
            refreshUsage = null;
            var provider = service.Provider;
            if (!TtsUsageCounter.Billed(provider))
            {
                loading = wasLoading;
                return;
            }
            var usage = service.Usage.Get(provider);
            usagePanel.Controls.Add(new Label
            {
                Text = T("Usage of this service (counted here)", "이 서비스 사용량 (여기서 센 것)"),
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(3, 3, 3, 3),
            });
            var lines = new Label { AutoSize = true, MaximumSize = new Size(620, 0), Margin = new Padding(3, 0, 3, 6) };
            usagePanel.Controls.Add(lines);
            var resume = Button(T("Try the service again now", "지금 다시 보내 보기"), () => service.Resume(provider));
            resume.Visible = false;
            usagePanel.Controls.Add(resume);
            refreshUsage = () =>
            {
                if (lines.IsDisposed)
                    return;
                var u = service.Usage.Get(provider);
                string Money(double? v) => v.HasValue ? v.Value.ToString("0.00", CultureInfo.CurrentCulture) : "";
                var text = T($"This month ({u.Month}): {u.Chars:N0} characters, {u.Requests:N0} requests ({u.PreparedChars:N0} characters fetched ahead); {u.Saved:N0} times not sent (kept from before)",
                    $"이번 달 ({u.Month}): {u.Chars:N0}자, 요청 {u.Requests:N0}회 (그중 미리 만들기 {u.PreparedChars:N0}자) · 저장된 음성으로 안 보낸 횟수 {u.Saved:N0}회");
                text += "\n" + T($"Last month ({(u.LastMonth == "" ? "-" : u.LastMonth)}): {u.LastMonthChars:N0} characters · In all: {u.TotalChars:N0}",
                    $"지난달 ({(u.LastMonth == "" ? "-" : u.LastMonth)}): {u.LastMonthChars:N0}자 · 누적 {u.TotalChars:N0}자");
                if (u.PricePerMillion > 0)
                    text += "\n" + T($"Estimate: this month {Money(u.Cost(u.Chars))}, last month {Money(u.Cost(u.LastMonthChars))}",
                        $"예상 요금: 이번 달 {Money(u.Cost(u.Chars))}, 지난달 {Money(u.Cost(u.LastMonthChars))}");
                if (u.MonthlyLimit > 0)
                    text += "\n" + T($"Limit: {u.Chars:N0} of {u.MonthlyLimit:N0} characters this month{(u.Chars >= u.MonthlyLimit ? " - reached, Windows' voice reads" : "")}",
                        $"한도: 이번 달 {u.Chars:N0} / {u.MonthlyLimit:N0}자{(u.Chars >= u.MonthlyLimit ? " · 닿음, Windows 음성으로 읽는 중" : "")}");
                var pause = service.PausedFor(provider);
                if (pause != null)
                    text += "\n" + TtsService.PausedText(provider, pause.Value);
                resume.Visible = pause != null;
                lines.Text = text;
                if (paidNote != null && !paidNote.IsDisposed)
                    paidNote.Text = PaidNoteText(provider);
            };
            refreshUsage();

            var grid = Grid(usagePanel);
            var price = new NumericUpDown { Minimum = 0, Maximum = 100000, DecimalPlaces = 2, Increment = 1, Width = 100, Value = (decimal)Math.Max(0, Math.Min(100000, usage.PricePerMillion)) };
            price.ValueChanged += (o, e) =>
            {
                if (loading)
                    return;
                service.Usage.Get(provider).PricePerMillion = (double)price.Value;
                pluginConfig.MarkDirty();
                refreshUsage?.Invoke();
            };
            var priceRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            priceRow.Controls.Add(price);
            priceRow.Controls.Add(Hint(T("in your currency, from the service's price page; 0: no estimate", "서비스 요금표의 값, 본인 통화로. 0이면 계산 안 함")));
            Row(grid, T("Price per million characters", "1백만 자당 요금"), priceRow);
            var limit = new NumericUpDown { Minimum = 0, Maximum = 1000000000, Increment = 10000, ThousandsSeparator = true, Width = 120, Value = Math.Max(0, Math.Min(1000000000, usage.MonthlyLimit)) };
            limit.ValueChanged += (o, e) =>
            {
                if (loading)
                    return;
                service.Usage.Get(provider).MonthlyLimit = (long)limit.Value;
                pluginConfig.MarkDirty();
                refreshUsage?.Invoke();
            };
            var limitRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            limitRow.Controls.Add(limit);
            var limitHint = Hint(T("characters; past it Windows' voice reads until next month. 0: no limit (a paid service then makes nothing ahead)", "자. 넘으면 다음 달까지 Windows 음성으로 읽음. 0이면 제한 없음 (이때 유료 서비스는 미리 만들기를 하지 않음)"));
            // Beside the box: kept within the page.
            limitHint.MaximumSize = new Size(400, 0);
            limitRow.Controls.Add(limitHint);
            Row(grid, T("Monthly limit", "월 한도"), limitRow);

            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            buttons.Controls.Add(Button(T("Reset the count", "사용량 초기화"), () =>
            {
                if (MessageBox.Show(this,
                    T("Clear this service's counts (this month, last month, in all)? The monthly limit then counts from 0.",
                      "이 서비스의 사용량(이번 달, 지난달, 누적)을 지울까요? 월 한도도 0부터 다시 셉니다."),
                    "TTS", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                    return;
                service.Usage.Reset(provider);
                pluginConfig.MarkDirty();
                refreshUsage?.Invoke();
            }));
            if (provider is ITtsUsageSource source)
            {
                var result = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
                buttons.Controls.Add(Button(T("Ask the service", "서비스에서 확인"), async () =>
                {
                    result.Text = T("asking...", "확인 중...");
                    try
                    {
                        var settings = service.Settings;
                        var text = await System.Threading.Tasks.Task.Run(() => source.FetchUsage(settings, System.Threading.CancellationToken.None));
                        if (!result.IsDisposed)
                            result.Text = text;
                    }
                    catch (Exception ex)
                    {
                        if (!result.IsDisposed)
                            result.Text = T("failed: ", "실패: ") + ex.GetBaseException().Message;
                    }
                }));
                buttons.Controls.Add(result);
            }
            usagePanel.Controls.Add(buttons);
            usagePanel.Controls.Add(Hint(T("Counted by this plugin from what it sent; the service's own bill is what counts (other apps using the same key are not counted here).",
                "이 플러그인이 보낸 만큼 센 값입니다. 실제 요금은 서비스 청구 기준이며, 같은 키를 다른 프로그램이 쓴 양은 여기 안 잡힙니다.")));
            loading = wasLoading;
        }

        private System.Threading.CancellationTokenSource downloading;
        // The download panel shown now (made again with the provider fields): what a running
        // download reports to.
        private Action refreshDownload;
        private Action<string> downloadStatus;

        // Download, progress, delete, and what the user agrees to (the model's license).
        private Control DownloadPanel(ITtsDownloadable model)
        {
            var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 0, 0, 6) };
            var status = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(3, 3, 3, 3) };
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            Button download = null, cancel = null, delete = null;
            string Mb(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0");
            refreshDownload = Show;
            downloadStatus = text =>
            {
                if (!status.IsDisposed)
                    status.Text = text;
            };
            void Show()
            {
                if (status.IsDisposed)
                    return;
                bool busy = downloading != null;
                status.Text = model.Ready
                    ? T($"Downloaded ({Mb(model.SizeOnDisk())} MB on disk). Loaded while chosen (a few hundred MB of memory).",
                        $"다운로드함 (디스크 {Mb(model.SizeOnDisk())}MB). 이 엔진을 고른 동안 메모리를 수백 MB 씁니다.")
                    : busy ? status.Text
                    : T($"Not downloaded. About {Mb(model.DownloadSize)} MB, only when you ask for it.",
                        $"아직 다운로드하지 않았습니다. 약 {Mb(model.DownloadSize)}MB이며 누를 때만 다운로드합니다.");
                if (model.Ready && model.LoadError != null && !busy)
                    status.Text = model.LoadError;
                download.Visible = !model.Ready && !busy;
                cancel.Visible = busy;
                delete.Visible = !busy && (model.Ready || model.SizeOnDisk() > 0);
            }
            download = Button(T("Download", "다운로드"), async () =>
            {
                downloading = new System.Threading.CancellationTokenSource();
                Show();
                var progress = new Progress<(long Done, long Total)>(p =>
                    downloadStatus?.Invoke(T($"Downloading... {Mb(p.Done)} / {Mb(p.Total)} MB", $"다운로드 중... {Mb(p.Done)} / {Mb(p.Total)}MB")));
                try
                {
                    await model.Download(progress, downloading.Token);
                    downloading = null;
                    service.ConfigChanged();
                    refreshDownload?.Invoke();
                }
                catch (Exception ex)
                {
                    downloading = null;
                    refreshDownload?.Invoke();
                    downloadStatus?.Invoke(ex is OperationCanceledException
                            ? T("Download stopped (what came is kept; downloading again goes on from there).", "다운로드를 멈췄습니다 (받은 파일은 남아 있어 다시 누르면 이어서 받습니다).")
                            : T("Download failed: ", "다운로드하지 못했습니다: ") + ex.GetBaseException().Message);
                }
            });
            cancel = Button(T("Stop", "멈추기"), () => downloading?.Cancel());
            delete = Button(T("Delete", "지우기"), () =>
            {
                try
                {
                    model.Delete();
                    Show();
                    if (Directory.Exists(model.Folder))
                        status.Text = T("Deleted. One file in use is removed when ACT restarts.", "지웠습니다. 사용 중인 파일 하나는 ACT를 다시 시작하면 지워집니다.");
                }
                catch (Exception ex)
                {
                    Show();
                    status.Text = T("Could not delete everything: ", "모두 지우지 못했습니다: ") + ex.Message;
                }
            });
            buttons.Controls.Add(download);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(delete);
            var license = new LinkLabel
            {
                AutoSize = true,
                Text = T("Model license (OpenRAIL-M): downloading means you accept it", "모델 라이선스 (OpenRAIL-M): 다운로드하면 이 조건에 동의하는 것입니다"),
                Margin = new Padding(3, 0, 3, 3),
            };
            license.LinkClicked += (o, e) => System.Diagnostics.Process.Start(model.LicenseUrl);
            panel.Controls.Add(status);
            panel.Controls.Add(buttons);
            panel.Controls.Add(license);
            panel.Controls.Add(Hint(T("Kept in ", "저장 위치: ") + model.Folder));
            Show();
            return panel;
        }

        private Control FieldControl(ITtsProvider provider, TtsField field)
        {
            config.ProviderSettings.TryGetValue(provider.Id, out var saved);
            string value = null;
            saved?.TryGetValue(field.Key, out value);
            if (field.Kind == TtsFieldKind.Secret)
                value = TtsSecrets.Unprotect(value);
            if (string.IsNullOrEmpty(value))
                value = field.Default;

            void Save(string v)
            {
                if (loading)
                    return;
                if (!config.ProviderSettings.TryGetValue(provider.Id, out var dict) || dict == null)
                    config.ProviderSettings[provider.Id] = dict = new Dictionary<string, string>();
                dict[field.Key] = field.Kind == TtsFieldKind.Secret ? TtsSecrets.Protect(v) : v;
                Changed();
            }

            Control input;
            if (field.Kind == TtsFieldKind.Choice)
            {
                var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
                var names = (ko ? field.ChoiceNamesKo : field.ChoiceNames) ?? field.ChoiceNames ?? field.Choices;
                for (int i = 0; i < field.Choices.Length; i++)
                    box.Items.Add(new Item(i < names.Length ? names[i] : field.Choices[i], field.Choices[i]));
                // A saved value no longer offered (e.g. more threads than now): its default shown.
                int selected = Array.IndexOf(field.Choices, value);
                if (selected < 0)
                    selected = Array.IndexOf(field.Choices, field.Default);
                box.SelectedIndex = field.Choices.Length == 0 ? -1 : Math.Max(0, selected);
                box.SelectedIndexChanged += (o, e) => Save((box.SelectedItem as Item)?.Value as string ?? "");
                input = box;
            }
            else if ((field.Key == "voice" && provider is ITtsVoiceSource) ||
                (provider is ITtsChoiceSource choices && choices.ListedFields.Contains(field.Key)))
            {
                bool voiceField = field.Key == "voice" && provider is ITtsVoiceSource;
                // Typed, or picked from the list the service gives.
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
                var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 380, Text = value };
                box.TextChanged += (o, e) => Save(VoiceId(box));
                var status = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };
                row.Controls.Add(box);
                row.Controls.Add(Button(T("Fetch the list", "목록 불러오기"), async () =>
                {
                    status.Text = T("fetching...", "불러오는 중...");
                    try
                    {
                        var voices = voiceField ? await service.FetchVoicesAsync() : await service.FetchChoicesAsync(field.Key);
                        if (box.IsDisposed)
                            return;
                        var current = box.Text;
                        box.Items.Clear();
                        // The alert language's voices first.
                        var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                        foreach (var v in voices.OrderBy(v => v.Lang == lang ? 0 : 1).ThenBy(v => v.Rank).ThenBy(v => v.Name))
                            box.Items.Add(new Item(v.Name, v.Id));
                        box.Text = current;
                        status.Text = voiceField ? T($"{voices.Count} voices", $"목소리 {voices.Count}개") : T($"{voices.Count} found", $"{voices.Count}개");
                        box.DroppedDown = true;
                    }
                    catch (Exception ex)
                    {
                        if (!status.IsDisposed)
                            status.Text = T("failed: ", "실패: ") + ex.GetBaseException().Message;
                    }
                }));
                row.Controls.Add(status);
                input = row;
            }
            else
            {
                var box = new TextBox { Width = 380, Text = value };
                if (field.Kind == TtsFieldKind.Secret)
                    box.UseSystemPasswordChar = true;
                if (field.Kind == TtsFieldKind.Multiline)
                {
                    box.Multiline = true;
                    box.Height = Font.Height * 4;
                    box.ScrollBars = ScrollBars.Vertical;
                    box.Text = value.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                }
                box.TextChanged += (o, e) => Save(box.Text.Replace(Environment.NewLine, "\n"));
                input = box;
            }
            if (string.IsNullOrEmpty(ko ? field.HintKo : field.Hint))
                return input;
            var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0) };
            stack.Controls.Add(input);
            stack.Controls.Add(Hint(ko ? field.HintKo : field.Hint));
            return stack;
        }

        // A voice picked from the list is shown by name; its ID is what is saved.
        private static string VoiceId(ComboBox box) =>
            box.SelectedItem is Item item && item.Name == box.Text ? (string)item.Value : box.Text.Trim();

        private static Label Hint(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 0, 3, 6),
        };

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

        // TTS on, but cactbot outputs its alerts without it: none of them is read (cactbot says how
        // when it starts; see TtsService.CactbotOutput).
        private void ShowCactbotNote()
        {
            if (cactbotNote == null || cactbotNote.IsDisposed)
                return;
            string output = service.CactbotOutput;
            string name;
            switch (output)
            {
                case "textAndSound": name = T("Text and sound", "텍스트와 소리"); break;
                case "textOnly": name = T("Text only", "텍스트만"); break;
                case "disabled": name = T("Disabled", "비활성화"); break;
                default: name = null; break;
            }
            cactbotNote.Visible = config.Enabled && name != null;
            if (name != null)
                cactbotNote.Text = T($"cactbot's alerts are not read now: its default alert output is \"{name}\". Set it to \"Text and TTS\" in cactbot's config (Raidboss > Language and output), or with the button in its TTS voice card.",
                    $"캑봇 알림은 지금 읽지 않습니다. 캑봇의 '기본 알람 출력 방식'이 '{name}'입니다. 캑봇 설정 > 레이드보스 > 언어·출력에서 '텍스트와 TTS'로 바꾸세요 (TTS 음성 칸의 버튼으로도 바꿀 수 있음).");
        }

        private void ShowOwner()
        {
            ShowCactbotNote();
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

        // Which engine and settings (no keys), for a copied result.
        private string ProviderLine()
        {
            var provider = service.Providers.FirstOrDefault(p => p.Id == config.Provider);
            if (provider == null)
                return null;
            var s = new TtsSettings(provider, config);
            // An address without its query: a key is sometimes pasted into it.
            string Shown(TtsField f) =>
                f.Key == "url" && Uri.TryCreate(s[f.Key], UriKind.Absolute, out var url) ? url.GetLeftPart(UriPartial.Path) : s[f.Key];
            var shown = provider.Fields.Where(f => f.Kind != TtsFieldKind.Secret && f.Kind != TtsFieldKind.Multiline && !string.IsNullOrEmpty(s[f.Key]))
                .Select(f => $"{f.Key}={Shown(f)}");
            return $"TTS: {provider.Name} [{string.Join(", ", shown)}] speed={config.Speed:0.##}";
        }

        // What a service's error means, before its own words.
        private string Explain(string error)
        {
            if (error.Contains("paid_plan_required"))
                return T("This voice needs a paid plan (pick a default voice). ", "이 목소리는 유료 플랜이 필요합니다 (기본 목소리를 고르세요). ");
            // The status, first or after the service's name ("ElevenLabs: 401 Unauthorized: ...").
            var status = System.Text.RegularExpressions.Regex.Match(error, "(?:^|: )(40[1-4]|429) ").Groups[1].Value;
            switch (status)
            {
                case "401":
                case "403":
                    return T("The key is wrong or lacks permission. ", "키가 맞지 않거나 권한이 없습니다. ");
                case "402":
                    return T("The plan or credits do not allow it. ", "플랜이나 크레딧이 부족합니다. ");
                case "429":
                    return T("Too many requests or over the limit (try again later). ", "요청이 너무 많거나 한도를 넘었습니다 (잠시 후 다시). ");
                case "404":
                    return T("Not found (check the model, voice or address). ", "찾을 수 없습니다 (모델, 목소리, 주소 확인). ");
                default:
                    return "";
            }
        }

        private void OnSpoken(TtsResult r)
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            // Alerts read meanwhile are not the preview's result.
            if (!r.Preview)
                return;
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed)
                    return;
                if (r.Error != null && r.MadeMs == 0)
                {
                    previewResult.Text = T("Could not say it: ", "읽지 못했습니다: ") + Explain(r.Error) + r.Error;
                    return;
                }
                previewResult.Text = T($"Made in {r.MadeMs:0} ms, playing {r.StartedMs:0} ms after asked{(r.FromCache ? " (kept from before)" : "")}",
                    $"만드는 데 {r.MadeMs:0}ms, 요청부터 재생까지 {r.StartedMs:0}ms{(r.FromCache ? " (만들어 둔 음성)" : "")}") +
                    (r.FellBack ? T(" - Windows' voice instead: ", " · Windows 음성으로 대신 읽음: ") + (r.Error ?? T("slower than the fallback time (kept for next time)", "대신 읽기 시간보다 늦음 (다음부터는 받은 음성으로)")) : "");
            }));
        }

        private void ShowStats()
        {
            if (statsLabel != null && !statsLabel.IsDisposed)
                statsLabel.Text = service.Stats.Describe();
        }

        // Set in cactbot's config: shown here too.
        private void OnVolumeChanged()
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            BeginInvoke((Action)(() =>
            {
                if (masterBar == null || masterBar.IsDisposed)
                    return;
                bool wasLoading = loading;
                loading = true;
                masterBar.Value = Math.Max(masterBar.Minimum, Math.Min(masterBar.Maximum, config.Volume));
                loading = wasLoading;
            }));
        }

        private void OnPrepared(int done, int total)
        {
            if (IsDisposed)
                return;
            void Show()
            {
                if (IsDisposed)
                    return;
                var last = service.LastPrepare;
                string size = last.Billed
                    ? T($" (about {last.Chars:N0} characters, counted as usage)", $" (약 {last.Chars:N0}자, 사용량에 잡힘)")
                    : "";
                prepared.Text = total == 0
                    ? service.PrepareNote ?? ""
                    : T($"Made ahead for this zone: {done} of {total}", $"이 지역 알림 미리 만들기: {done}/{total}") + size;
            }
            if (!IsHandleCreated)
                return;
            if (InvokeRequired)
                BeginInvoke((Action)Show);
            else
                Show();
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
                // Rows selected by their header can be deleted (Delete key).
                SelectionMode = DataGridViewSelectionMode.RowHeaderSelect,
            };
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = T("On", "사용"), FillWeight = 12 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Find", HeaderText = T("Find", "찾을 말"), FillWeight = 40 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Replace", HeaderText = T("Replace with", "바꿀 말"), FillWeight = 30 });
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Regex", HeaderText = T("Regex", "정규식"), FillWeight = 14 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Lang", HeaderText = T("Language", "언어"), FillWeight = 14 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Note", HeaderText = T("Note", "설명"), FillWeight = 40 });
            // The order is the order rules apply: not sorted by a header click.
            foreach (DataGridViewColumn column in grid.Columns)
                column.SortMode = DataGridViewColumnSortMode.NotSortable;
            FillRules(grid);
            grid.DefaultValuesNeeded += (o, e) => e.Row.Cells["Enabled"].Value = true;
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
                Application.RemoveMessageFilter(wheelGuard);
                service.Spoken -= OnSpoken;
                service.VolumeChanged -= OnVolumeChanged;
                service.PausedChanged -= OnUsageChanged;
                service.Prepared -= OnPrepared;
                service.Usage.Changed -= OnUsageChanged;
                downloading?.Cancel();
                ownerTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        // Scrolling the tab with the wheel passes over choice boxes, number boxes and sliders, which
        // would take the wheel and change their value: over them it scrolls the tab instead (an
        // opened list still scrolls itself).  Seen before any control gets it, as Windows sends the
        // wheel to the control under the pointer (or the focused one) and some handle it natively.
        private class WheelGuard : IMessageFilter
        {
            private const int WM_MOUSEWHEEL = 0x020A;
            private readonly TtsConfigPanel panel;

            public WheelGuard(TtsConfigPanel panel)
            {
                this.panel = panel;
            }

            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WM_MOUSEWHEEL || panel.IsDisposed || !panel.Visible)
                    return false;
                for (var c = Control.FromChildHandle(m.HWnd); c != null; c = c.Parent)
                {
                    if (c == panel)
                        return false;
                    if (c is ComboBox combo && combo.DroppedDown)
                        return false;
                    if (c is ComboBox || c is NumericUpDown || c is TrackBar)
                    {
                        if (!IsInside(c))
                            return false;
                        int delta = (short)((long)m.WParam >> 16);
                        // The scrolling page it is on.
                        for (var p = c.Parent; p != null && p != panel; p = p.Parent)
                        {
                            if (p is ScrollableControl page && page.AutoScroll)
                            {
                                var at = page.AutoScrollPosition;
                                page.AutoScrollPosition = new Point(-at.X, -at.Y - delta);
                                break;
                            }
                        }
                        return true;
                    }
                }
                return false;
            }

            private bool IsInside(Control c)
            {
                for (; c != null; c = c.Parent)
                {
                    if (c == panel)
                        return true;
                }
                return false;
            }
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
            // One width for all labels, so the rows of a section line up across its tables.
            grid.Controls.Add(new Label { Text = label, AutoSize = true, MinimumSize = new Size(grid.Font.Height * 12, 0), Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) });
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

        // A line in the open list between groups (free, paid, a request written by hand).
        private static void Grouped(ComboBox box, Func<Item, int> group)
        {
            box.DrawMode = DrawMode.OwnerDrawFixed;
            box.ItemHeight = box.Font.Height + 6;
            box.DrawItem += (o, e) =>
            {
                e.DrawBackground();
                if (e.Index < 0 || !(box.Items[e.Index] is Item item))
                    return;
                var bounds = e.Bounds;
                TextRenderer.DrawText(e.Graphics, item.Name, e.Font, new Rectangle(bounds.X + 2, bounds.Y, bounds.Width - 2, bounds.Height), e.ForeColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                // Not in the closed box, only between groups in the list.
                bool inList = (e.State & DrawItemState.ComboBoxEdit) == 0;
                if (inList && e.Index > 0 && box.Items[e.Index - 1] is Item before && group(before) != group(item))
                {
                    using (var pen = new Pen(SystemColors.ControlDark))
                        e.Graphics.DrawLine(pen, bounds.Left + 2, bounds.Top, bounds.Right - 2, bounds.Top);
                }
                e.DrawFocusRectangle();
            };
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

        private Control Seconds(int ms, decimal min, decimal max, Action<int> set, decimal step = 0.5m)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var box = new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = 1, Increment = step, Width = 70, Value = Math.Max(min, Math.Min(max, ms / 1000m)) };
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

        private static Button Button(string text, Func<System.Threading.Tasks.Task> click)
        {
            var button = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 2, 8, 2) };
            button.Click += async (o, e) => await click();
            return button;
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
