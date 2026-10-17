using System.Text.RegularExpressions;

namespace PatchCheck;

// A memory signature as one of the plugins scans for it: the value it reads sits `Offset` bytes
// after the end of the match (negative: inside or before it); with `Rip`, a 32-bit displacement
// relative to the end of those 4 bytes plus `RipOffset`, else a 64-bit pointer.
public sealed record Signature(
    string Name, string Pattern, int Offset, bool Rip, int RipOffset, string Source);

// The memory signatures of OverlayPlugin and cactbot's plugin, read from their sources.  Which
// constant each scan uses (and with what offsets) is in the table below, as the scans themselves
// sit in base classes; scans in the sources that the table doesn't know are reported, so a new one
// isn't silently left out.
public static class SignatureSources
{
    // (folder, file, signature constant, offset constant or number, rip, rip offset constant or number)
    private static readonly (string Dir, string File, string Sig, string Offset, bool Rip, string RipOffset)[] OverlayPlugin =
    [
        ("Combatant", "CombatantMemory70.cs", "charmapSignature", "0", true, "0"),
        ("Combatant", "CombatantMemory71.cs", "charmapSignature", "0", true, "0"),
        ("Combatant", "CombatantMemory72.cs", "charmapSignature", "0", true, "0"),
        ("Combatant", "CombatantMemory73.cs", "charmapSignature", "0", true, "0"),
        ("Combatant", "CombatantMemory74.cs", "charmapSignature", "0", true, "0"),
        ("Combatant", "CombatantMemory75.cs", "charmapSignature", "0", true, "0"),
        ("ClientFramework", "ClientFrameworkMemory70.cs", "clientFrameworkSignature", "clientFrameworkSignatureOffset", true, "0"),
        ("ContentFinderSettings", "ContentFinderSettings70.cs", "settingsSignature", "-29", true, "0"),
        ("ContentFinderSettings", "ContentFinderSettings70.cs", "inContentFinderSignature", "-34", true, "1"),
        ("ContentFinderSettings", "ContentFinderSettings71.cs", "settingsSignature", "-29", true, "0"),
        ("ContentFinderSettings", "ContentFinderSettings71.cs", "inContentFinderSignature", "-15", true, "1"),
        ("Enmity", "EnmityMemory60.cs", "enmitySignature", "0", true, "0"),
        ("EnmityHud", "EnmityHudMemory70.cs", "enmityHudSignature", "0", true, "0"),
        ("EnmityHud", "EnmityHudMemory73.cs", "enmityHudSignature", "0", true, "0"),
        ("InCombat", "InCombatMemory70.cs", "inCombatSignature", "inCombatSignatureOffset", true, "inCombatRIPOffset"),
        ("InCombat", "InCombatMemory73.cs", "inCombatSignature", "inCombatSignatureOffset", true, "inCombatRIPOffset"),
        ("JobGauge", "JobGauge655.cs", "jobDataSignature", "jobDataSignatureOffset", true, "0"),
        ("JobGauge", "JobGauge70.cs", "jobDataSignature", "jobDataSignatureOffset", true, "0"),
        ("JobGauge", "JobGauge74.cs", "jobDataSignature", "jobDataSignatureOffset", true, "0"),
        ("Target", "TargetMemory70.cs", "targetSignature", "0", true, "0"),
    ];

    // The scans OverlayPlugin's sources contain (file, first argument), all covered by the table
    // above: the base classes' with the signature passed in, and the version classes' own.
    private static readonly HashSet<(string File, string Arg)> OverlayPluginScans =
    [
        ("AggroMemory.cs", "aggroSignature"), // EnmityMemory60's signature (AggroMemory60)
        ("ClientFramework.cs", "clientFrameworkSignature"),
        ("CombatantMemory.cs", "charmapSignature"),
        ("ContentFinderSettings.cs", "settingsSignature"), // overridden by the 70/71 classes
        ("ContentFinderSettings.cs", "inContentFinderSignature"),
        ("ContentFinderSettings70.cs", "settingsSignature"),
        ("ContentFinderSettings70.cs", "inContentFinderSignature"),
        ("ContentFinderSettings71.cs", "settingsSignature"),
        ("ContentFinderSettings71.cs", "inContentFinderSignature"),
        ("EnmityMemory.cs", "enmitySignature"),
        ("EnmityHudMemory.cs", "enmityHudSignature"),
        ("InCombatMemory.cs", "inCombatSignature"),
        ("JobGauge.cs", "jobDataSignature"),
        ("TargetMemory.cs", "targetSignature"),
    ];

    // cactbot: one file per game region, each with the same three signatures.
    private static readonly string[] CactbotRegions = ["Intl", "Ko", "Cn", "Tc"];
    private static readonly string[] CactbotSignatures = ["kCharmap", "kJobData", "kInCombat"];

    private static readonly Regex Constant = new(
        @"(?:const|static)\s+(?:readonly\s+)?(?:string|String|int|bool)\s+(\w+)\s*=\s*(""[^""]*""|-?\d+|true|false)\s*;");
    private static readonly Regex Scan = new(@"SigScan\(\s*(\w+)\s*,");

    public static List<Signature> Read(string overlayPluginDir, string? cactbotDir, List<string> warnings)
    {
        var result = new List<Signature>();
        string mp = Path.Combine(overlayPluginDir, "OverlayPlugin.Core", "MemoryProcessors");
        foreach (var e in OverlayPlugin)
        {
            string file = Path.Combine(mp, e.Dir, e.File);
            if (!File.Exists(file))
            {
                warnings.Add($"OverlayPlugin: {e.Dir}/{e.File} is gone (update the table)");
                continue;
            }
            var c = Constants(file);
            if (!c.TryGetValue(e.Sig, out var pattern))
            {
                warnings.Add($"OverlayPlugin: no {e.Sig} in {e.Dir}/{e.File}");
                continue;
            }
            result.Add(new Signature($"OverlayPlugin/{Path.GetFileNameWithoutExtension(e.File)}.{e.Sig}",
                Unquote(pattern), Number(e.Offset, c, file), e.Rip, Number(e.RipOffset, c, file),
                $"OverlayPlugin.Core/MemoryProcessors/{e.Dir}/{e.File}"));
        }
        // Version classes the table doesn't list yet (e.g. a new CombatantMemory80.cs).
        var listed = OverlayPlugin.Select(e => e.File).ToHashSet();
        foreach (string file in Directory.EnumerateFiles(mp, "*.cs", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            var c = Constants(file);
            if (!listed.Contains(name) && c.Keys.Any(k => k.EndsWith("Signature", StringComparison.Ordinal) && c[k].StartsWith('"')))
                warnings.Add($"OverlayPlugin: {Path.GetRelativePath(mp, file)} has a signature the table doesn't list");
            foreach (Match m in Scan.Matches(File.ReadAllText(file)))
            {
                if (name != "FFXIVMemory.cs" && !OverlayPluginScans.Contains((name, m.Groups[1].Value)))
                    warnings.Add($"OverlayPlugin: SigScan({m.Groups[1].Value}, ...) in {name} isn't in the table");
            }
        }

        if (cactbotDir == null)
            return result;
        string cs = Path.Combine(cactbotDir, "plugin", "CactbotEventSource");
        foreach (string region in CactbotRegions)
        {
            string file = Path.Combine(cs, $"FFXIVProcess{region}.cs");
            if (!File.Exists(file))
            {
                warnings.Add($"cactbot: FFXIVProcess{region}.cs is gone");
                continue;
            }
            var c = Constants(file);
            foreach (string sig in CactbotSignatures)
            {
                if (!c.TryGetValue(sig + "Signature", out var pattern))
                {
                    warnings.Add($"cactbot: no {sig}Signature in FFXIVProcess{region}.cs");
                    continue;
                }
                result.Add(new Signature($"cactbot/{region}.{sig}", Unquote(pattern),
                    Number(sig + "SignatureOffset", c, file), c.GetValueOrDefault(sig + "SignatureRIP") == "true",
                    c.TryGetValue(sig + "RipOffset", out var ro) ? int.Parse(ro) : 0,
                    $"plugin/CactbotEventSource/FFXIVProcess{region}.cs"));
            }
            foreach (Match m in Scan.Matches(File.ReadAllText(file)))
            {
                string arg = m.Groups[1].Value;
                if (!CactbotSignatures.Any(s => arg == s + "Signature"))
                    warnings.Add($"cactbot: SigScan({arg}, ...) in FFXIVProcess{region}.cs isn't read by this tool");
            }
        }
        return result;
    }

    private static Dictionary<string, string> Constants(string file) =>
        Constant.Matches(File.ReadAllText(file)).GroupBy(m => m.Groups[1].Value)
            .ToDictionary(g => g.Key, g => g.First().Groups[2].Value);

    private static string Unquote(string s) => s.Trim('"');

    private static int Number(string nameOrNumber, Dictionary<string, string> constants, string file)
    {
        if (int.TryParse(nameOrNumber, out int n))
            return n;
        if (constants.TryGetValue(nameOrNumber, out var v) && int.TryParse(v, out n))
            return n;
        throw new InvalidDataException($"no number {nameOrNumber} in {file}");
    }
}
