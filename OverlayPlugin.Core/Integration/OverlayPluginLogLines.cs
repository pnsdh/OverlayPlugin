using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RainbowMage.OverlayPlugin.MemoryProcessors.Combatant;
using RainbowMage.OverlayPlugin.MemoryProcessors.ContentFinderSettings;
using RainbowMage.OverlayPlugin.MemoryProcessors.InCombat;
using RainbowMage.OverlayPlugin.NetworkProcessors;
using RainbowMage.OverlayPlugin.Updater;
using MachinaRegion = System.String;
using OpcodeName = System.String;
using OpcodeVersion = System.String;

namespace RainbowMage.OverlayPlugin
{

    using Opcodes = Dictionary<MachinaRegion, Dictionary<OpcodeVersion, Dictionary<OpcodeName, OpcodeConfigEntry>>>;

    class OverlayPluginLogLines
    {
        public OverlayPluginLogLines(TinyIoCContainer container)
        {
            container.Register(new OverlayPluginLogLineConfig(container));
            container.Register(new LineMapEffect(container));
            container.Register(new LineFateControl(container));
            container.Register(new LineCEDirector(container));
            container.Register(new LineInCombat(container));
            container.Register(new LineCombatant(container));
            container.Register(new LineRSV(container));
            container.Register(new LineActorCastExtra(container));
            container.Register(new LineAbilityExtra(container));
            container.Register(new LineContentFinderSettings(container));
            container.Register(new LineNpcYell(container));
            container.Register(new LineBattleTalk2(container));
            container.Register(new LineCountdown(container));
            container.Register(new LineCountdownCancel(container));
            container.Register(new LineActorMove(container));
            container.Register(new LineActorSetPos(container));
            container.Register(new LineSpawnNpcExtra(container));
            container.Register(new LineActorControlExtra(container));
            container.Register(new LineActorControlSelfExtra(container));
            // Fork, for development: memory layout check against FFXIV_ACT_Plugin (op-selfcheck.enable).
            if (MemoryProcessors.MemorySelfCheck.Enabled)
                container.Register(new MemoryProcessors.MemorySelfCheck(container));
        }
    }

    class OverlayPluginLogLineConfig
    {
        private Opcodes opcodesFile;
        private Opcodes opcodesConfig;
        private ILogger logger;
        private FFXIVRepository repository;
        private PluginConfig config;
        private TinyIoCContainer container;

        private int exceptionCount = 0;
        private const int maxExceptionsLogged = 3;

        private bool haveAttemptedOpcodeDownload = false;

        private const string remoteOpcodeUrl = "https://raw.githubusercontent.com/OverlayPlugin/OverlayPlugin/main/OverlayPlugin.Core/resources/opcodes.jsonc";

        public OverlayPluginLogLineConfig(TinyIoCContainer container)
        {
            this.container = container;
            logger = container.Resolve<ILogger>();
            repository = container.Resolve<FFXIVRepository>();
            config = container.Resolve<PluginConfig>();

            LoadCachedOpcodesFromConfig();
            LoadOpcodesFromFile();
        }

        private bool LoadCachedOpcodesFromConfig()
        {
            var versionStr = config.CachedOpcodeOverlayPluginVersion;
            Version version;

            if (versionStr == "")
            {
                return false;
            }

            try
            {
                version = new Version(versionStr);
            }
            catch (Exception ex)
            {
                LogException($"Invalid CachedOpcodeOverlayPluginVersion {versionStr}: {ex}");
                ClearCachedOpcodes();
                return false;
            }

            // Only load cached opcodes if we have loaded them for this version.
            // If they're old, clear them.
            if (repository.GetOverlayPluginVersion() > version)
            {
                ClearCachedOpcodes();
                return false;
            }

            try
            {
                opcodesConfig = config.CachedOpcodeFile.ToObject<Opcodes>();
            }
            catch (Exception ex)
            {
                LogException($"Failed to parse cached opcodes: {ex}");
                ClearCachedOpcodes();
                return false;
            }

            logger.Log(LogLevel.Debug, "Loaded opcodes from config");
            return true;
        }

        private void SaveRemoteOpcodesToConfig()
        {
            if (!config.UpdateCheck)
            {
                logger.Log(LogLevel.Debug, "Skipping remote opcode fetch due to UpdateCheck=false");
                return;
            }

            try
            {
                var response = HttpClientWrapper.Get(remoteOpcodeUrl);
                var jsonData = JObject.Parse(response);
                // Validate that this can convert properly before storing it.
                JsonConvert.DeserializeObject<Opcodes>(response);

                config.CachedOpcodeFile = jsonData;
                config.CachedOpcodeOverlayPluginVersion = repository.GetOverlayPluginVersion().ToString();
                logger.Log(LogLevel.Debug, "Fetched remote opcodes");
                LoadCachedOpcodesFromConfig();
            }
            catch (Exception ex)
            {
                LogException($"Remote opcode error: {ex}");
                return;
            }
        }

        private void ClearCachedOpcodes()
        {
            logger.Log(LogLevel.Debug, "Clearing cached opcodes");
            config.CachedOpcodeOverlayPluginVersion = "";
            config.CachedOpcodeFile = new JObject();
        }

        private bool LoadOpcodesFromFile()
        {
            var main = container.Resolve<PluginMain>();
            var pluginDirectory = main.PluginDirectory;
            var opcodesPath = Path.Combine(pluginDirectory, "resources", "opcodes.jsonc");

            try
            {
                var jsonData = File.ReadAllText(opcodesPath);
                opcodesFile = JsonConvert.DeserializeObject<Opcodes>(jsonData);
                logger.Log(LogLevel.Debug, "Loaded opcodes from file");
                return true;
            }
            catch (Exception ex)
            {
                LogException(string.Format(Resources.ErrorCouldNotLoadReservedLogLines, ex));
                return false;
            }
        }

        private HashSet<string> loggedMessages = new HashSet<string>();

        private void LogOnce(LogLevel level, string message)
        {
            if (loggedMessages.Contains(message))
                return;
            loggedMessages.Add(message);
            logger.Log(level, message);
        }

        private void LogException(string message)
        {
            if (exceptionCount >= maxExceptionsLogged)
                return;
            exceptionCount++;
            logger.Log(LogLevel.Error, message);
        }

        private IOpcodeConfigEntry GetOpcode(string name, Opcodes opcodes, string version, string opcodeType, MachinaRegion machinaRegion)
        {
            if (opcodes == null)
                return null;

            if (opcodes.TryGetValue(machinaRegion, out var regionOpcodes))
            {
                if (regionOpcodes.TryGetValue(version, out var versionOpcodes))
                {
                    if (versionOpcodes.TryGetValue(name, out var opcode))
                    {
                        return opcode;
                    }
                    else
                    {
                        LogException($"No {opcodeType} opcode for game region {machinaRegion}, version {version}, opcode name {name}");
                    }
                }
                else
                {
                    LogException($"No {opcodeType} opcodes for game region {machinaRegion}, version {version}");
                }
            }
            else
            {
                LogException($"No {opcodeType} opcodes for game region {machinaRegion}");
            }

            return null;
        }
        // Fork: where this game version's opcodes come from, for the tech support info: the file
        // (how many names), the remote copy cached in the config, or nowhere.
        public string DescribeSource(MachinaRegion region, string version)
        {
            int Count(Opcodes opcodes) =>
                opcodes != null && opcodes.TryGetValue(region, out var byVersion) && byVersion.TryGetValue(version ?? "", out var names) ? names.Count : 0;
            int file = Count(opcodesFile);
            int cached = Count(opcodesConfig);
            var parts = new List<string> { file > 0 ? $"file ({file})" : "not in file" };
            if (opcodesConfig != null && opcodesConfig.Count > 0)
                parts.Add(cached > 0 ? $"cached remote ({cached})" : "cached remote (other versions)");
            return string.Join(", ", parts);
        }

        public IOpcodeConfigEntry this[string name]
        {
            get
            {
                var machinaRegion = repository.GetMachinaRegion().ToString();
                return this[name, machinaRegion];
            }
        }

        public IOpcodeConfigEntry this[string name, MachinaRegion machinaRegion]
        {
            get
            {
                var version = repository.GetGameVersion();
                if (version == null || version == "")
                {
                    LogOnce(LogLevel.Info, $"Could not detect game version from FFXIV_ACT_Plugin, defaulting to latest version for region {machinaRegion}");

                    var possibleVersions = new List<string>();
                    if (opcodesFile != null && opcodesFile.ContainsKey(machinaRegion))
                    {
                        foreach (var key in opcodesFile[machinaRegion].Keys)
                            possibleVersions.Add(key);
                    }

                    if (opcodesConfig != null && opcodesConfig.ContainsKey(machinaRegion))
                    {
                        foreach (var key in opcodesConfig[machinaRegion].Keys)
                            possibleVersions.Add(key);
                    }
                    possibleVersions.Sort();

                    if (possibleVersions.Count > 0)
                    {
                        version = possibleVersions[possibleVersions.Count - 1];
                        LogOnce(LogLevel.Info, $"Detected most recent version for {machinaRegion} = {version}");
                    }
                    else
                    {
                        LogOnce(LogLevel.Info, $"Could not determine latest version for region {machinaRegion}");
                        return null;
                    }
                }

                // Fork: the file first.  It is what a release ships or what a local patch tool put
                // there for a new game version; a remote copy is only fetched (and cached until the
                // next OverlayPlugin version) when something is missing, and before, being looked up
                // first, it hid any file put in place after it was cached.
                var opcode = GetOpcode(name, opcodesFile, version, "file", machinaRegion);
                if (opcode == null)
                {
                    opcode = GetOpcode(name, opcodesConfig, version, "config", machinaRegion);

                    // Try once to get this remotely, but only if this opcode or version is missing.
                    // TODO: we could consider getting this once always too, but for now
                    // if we ever have an incorrect (but present) opcode, another release is required.
                    if (opcode == null && !haveAttemptedOpcodeDownload)
                    {
                        haveAttemptedOpcodeDownload = true;
                        SaveRemoteOpcodesToConfig();
                        return GetOpcode(name, opcodesConfig, version, "config", machinaRegion);
                    }
                }

                return opcode;
            }
        }
    }
    interface IOpcodeConfigEntry
    {
        uint opcode { get; }
        uint size { get; }
    }

    [JsonObject(NamingStrategyType = typeof(Newtonsoft.Json.Serialization.DefaultNamingStrategy))]
    class OpcodeConfigEntry : IOpcodeConfigEntry
    {
        public uint opcode { get; set; }
        public uint size { get; set; }
    }
}
