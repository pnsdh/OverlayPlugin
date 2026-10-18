using System;
using Advanced_Combat_Tracker;

namespace RainbowMage.OverlayPlugin.Tts
{
    // Fork: ACT's own TTS (its custom triggers, other plugins calling ActGlobals.oFormActMain.TTS)
    // read by TtsService.  ACT lets one plugin replace how it speaks (PlayTtsMethod); others do
    // too (e.g. Hojoring's TTSYukkuri, which takes it again every 3 seconds whenever it is not
    // its own).  So this only takes it once, and only while it is still ACT's own; it never takes
    // it back from another plugin (which would make the two fight over it), and gives it back on
    // shutdown only if it is still this one's.
    public class ActTtsBridge : IDisposable
    {
        private readonly ILogger logger;
        private readonly TtsService service;
        private readonly FormActMain.PlayTtsDelegate ours;
        private FormActMain.PlayTtsDelegate original;
        // ACT's own way of speaking, as first found (kept across a disable and enable of the plugin
        // in the same ACT: an older bridge's delegate handed back by another plugin is replaced
        // with this one's, not mistaken for another plugin).
        private static FormActMain.PlayTtsDelegate actOwn;

        public ActTtsBridge(TinyIoCContainer container, TtsService service)
        {
            logger = container.Resolve<ILogger>();
            this.service = service;
            ours = Speak;
        }

        public enum Owner
        {
            Act,
            Us,
            OtherPlugin,
            Unknown,
        }

        public Owner CurrentOwner
        {
            get
            {
                var current = ActGlobals.oFormActMain?.PlayTtsMethod;
                if (current == null)
                    return Owner.Unknown;
                if (IsOurs(current))
                    return Owner.Us;
                // A disabled bridge's (this plugin before it was enabled again): free to take.
                if (current.Method.DeclaringType == typeof(ActTtsBridge))
                    return Owner.Act;
                return current.Method.DeclaringType == typeof(FormActMain) ? Owner.Act : Owner.OtherPlugin;
            }
        }

        // The plugin that speaks for ACT now, by its assembly's name (for the settings).
        public string OwnerName
        {
            get
            {
                var current = ActGlobals.oFormActMain?.PlayTtsMethod;
                return current?.Method.DeclaringType?.Assembly.GetName().Name ?? "";
            }
        }

        // Whether overlays' speech should go to ACT's TTS (another plugin speaking for it, and the
        // user wants that one) rather than to this engine.
        public bool DeferToOther => service.Config.DeferToOtherPlugin && CurrentOwner == Owner.OtherPlugin;

        public void Apply()
        {
            var act = ActGlobals.oFormActMain;
            if (act == null)
                return;
            var want = service.Config.Enabled && service.Config.TakeOverActTts;
            var owner = CurrentOwner;
            if (want && owner == Owner.Act)
            {
                if (act.PlayTtsMethod.Method.DeclaringType == typeof(FormActMain))
                    actOwn = act.PlayTtsMethod;
                original = actOwn ?? act.PlayTtsMethod;
                act.PlayTtsMethod = ours;
                logger.Log(LogLevel.Info, "TTS: reading ACT's TTS too.");
            }
            else if (want && owner == Owner.OtherPlugin)
            {
                logger.Log(LogLevel.Info, $"TTS: {OwnerName} speaks for ACT; left to it.");
            }
            else if (!want)
            {
                Release();
            }
        }

        private void Release()
        {
            var act = ActGlobals.oFormActMain;
            if (act == null || act.IsDisposed)
                return;
            if (IsOurs(act.PlayTtsMethod) && original != null)
            {
                act.PlayTtsMethod = original;
                logger.Log(LogLevel.Info, "TTS: ACT's TTS given back.");
            }
        }

        private bool IsOurs(Delegate d) => d != null && d.Target == this && d.Method == ours.Method;

        private void Speak(string text)
        {
            // Here ACT asked this one.  After shutdown, a plugin that took it from this one may
            // still hand it back here: ACT's own speaks then.
            if (disposed)
                original?.Invoke(text);
            else
                service.Speak(text, TtsSeverity.Other, "ACT");
        }

        private volatile bool disposed;

        public void Dispose()
        {
            Release();
            disposed = true;
        }
    }
}
