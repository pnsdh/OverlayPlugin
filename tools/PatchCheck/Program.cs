using System.Text.Json;
using System.Text.RegularExpressions;

using FFXIV.Signatures;

using PatchCheck;

// For a game patch: the memory signatures OverlayPlugin and cactbot's plugin rely on.
//
// PatchCheck list [--json <out.json>]
//   Lists them (read from the sources); --json writes them as a pattern file for the sigscan /
//   sigregen commands of ActPluginRepack's CLI ({ name, pattern, syntax, offset, rip, ripOffset }).
// PatchCheck scan [--exe <ffxiv_dx11.exe>] [--region Ko|Intl|Cn|Tc]
//   Scans a game exe: how many places each matches (code section, and all sections as the plugins
//   scan the running game), where the value it reads points, and whether a match straddles the
//   64KB blocks the plugins read in.  Judged are the ones in use: the newest version class of each
//   OverlayPlugin reader, and cactbot's file for the exe's region; older ones are shown for
//   reference.  Default exe: the Korean client's.
// PatchCheck regen --old <exe> --new <exe> [--region ...]
//   Makes the signatures in use again for a new exe from the old one (FFXIV.Signatures' SigRegen).
// Common: --cactbot <dir> (default ..\cactbot next to this repo).
string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
if (!File.Exists(Path.Combine(repo, "OverlayPlugin.sln")))
    repo = Directory.GetCurrentDirectory();
var opts = Options(args);
string? cactbot = opts.GetValueOrDefault("cactbot") ?? Path.Combine(repo, "..", "cactbot");
if (!Directory.Exists(cactbot))
    cactbot = null;

const string KoreanExe = @"C:\Program Files (x86)\FINAL FANTASY XIV - KOREA\game\ffxiv_dx11.exe";
string command = args.FirstOrDefault(a => !a.StartsWith("--") && !opts.ContainsValue(a)) ?? "list";
var warnings = new List<string>();
var signatures = SignatureSources.Read(repo, cactbot, warnings);
foreach (string w in warnings)
    Console.WriteLine($"WARN {w}");

try
{
    return command switch
    {
        "list" => List(),
        "scan" => Scan(),
        "regen" => Regen(),
        _ => Usage(),
    };
}
catch (SignatureException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

int Usage()
{
    Console.Error.WriteLine("usage: PatchCheck list [--json <file>] | scan [--exe <exe>] [--region <r>] | regen --old <exe> --new <exe>");
    return 1;
}

int List()
{
    foreach (var s in signatures)
        Console.WriteLine($"{s.Name,-58} offset {s.Offset,5}{(s.Rip ? $" rip{(s.RipOffset != 0 ? $"+{s.RipOffset}" : "")}" : "")}  {s.Pattern}");
    if (opts.TryGetValue("json", out var json))
    {
        var items = signatures.Select(s => new { name = s.Name, pattern = s.Pattern, syntax = "hex", offset = s.Offset, rip = s.Rip, ripOffset = s.RipOffset });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(json))!);
        File.WriteAllText(json, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"wrote {signatures.Count} patterns to {json}");
    }
    return warnings.Count > 0 ? 2 : 0;
}

int Scan()
{
    string exe = opts.GetValueOrDefault("exe") ?? KoreanExe;
    string region = opts.GetValueOrDefault("region") ?? (exe.Contains("KOREA", StringComparison.OrdinalIgnoreCase) ? "Ko" : "Intl");
    var text = new ScanTarget(exe, ScanScope.Text);
    var all = new ScanTarget(text.Image, exe, ScanScope.AllSections);
    Console.WriteLine($"{exe} ({GameVersion(exe)}), cactbot region {region}");
    int failed = 0;
    foreach (var s in signatures)
    {
        bool used = InUse(s, region);
        var pattern = SigPattern.Parse(s.Pattern, SigSyntax.Hex);
        var inText = pattern.Scan(text);
        var inAll = pattern.Scan(all);
        string where = string.Join(", ", inAll.Take(3).Select(m =>
        {
            var v = SigValue.Resolve(all.Image, m, s.Offset, s.Rip, s.RipOffset);
            return $"0x{m.Rva:X} -> {(v.ResolvedRva is int r ? $"0x{r:X} {v.Section ?? "outside any section"}" : v.Error)}{(m.MissedByChunkedScan(s.Offset, s.Rip) ? " MISSED BY 64KB READS" : "")}";
        }));
        // The plugins take the first match; cactbot refuses several.  A value outside every section,
        // or a match the runtime's 64KB reads miss (the match and the value it reads must fit in one
        // read, see FFXIVMemory.SigScan), is as bad as none.
        bool bad = inAll.Count != 1 || inAll.Any(m => m.MissedByChunkedScan(s.Offset, s.Rip) ||
            SigValue.Resolve(all.Image, m, s.Offset, s.Rip, s.RipOffset).Section == null);
        string level = !used ? "    " : bad ? "FAIL" : "OK  ";
        if (used && bad)
            failed++;
        string counts = inText.Count == inAll.Count ? $"{inAll.Count}" : $"{inText.Count} in code / {inAll.Count} in all";
        Console.WriteLine($"{level} {s.Name,-58} {counts,-18} {where}");
    }
    Console.WriteLine(failed == 0 ? "all signatures in use match once" : $"{failed} signatures in use are broken");
    return failed > 0 ? 2 : warnings.Count > 0 ? 3 : 0;
}

int Regen()
{
    if (!opts.TryGetValue("old", out var oldExe) || !opts.TryGetValue("new", out var newExe))
        return Usage();
    string region = opts.GetValueOrDefault("region") ?? (newExe.Contains("KOREA", StringComparison.OrdinalIgnoreCase) ? "Ko" : "Intl");
    Console.WriteLine($"old {oldExe} ({GameVersion(oldExe)})\nnew {newExe} ({GameVersion(newExe)})");
    var oldCode = new CodeImage(oldExe);
    var newCode = new CodeImage(newExe);
    int failed = 0;
    foreach (var s in signatures.Where(s => InUse(s, region)))
    {
        var r = SigRegen.Regenerate(oldCode, newCode, SigPattern.HexToPelite(s.Pattern), s.Offset);
        // The pattern can still match while the value is read from elsewhere: a moved field (offset),
        // or an instruction whose immediate grew (e.g. cmp [rip+x],imm8 to imm32: ripOffset 1 to 4).
        bool offsetMoved = r.Offset is int o && o != s.Offset;
        bool ripMoved = s.Rip && r.RipOffset is int ro && (ro != s.RipOffset || (r.OldRipOffset is int old && old != ro));
        string moved = (offsetMoved ? $" offset {s.Offset} -> {r.Offset}" : "") + (ripMoved ? $" ripOffset {s.RipOffset} -> {r.RipOffset}" : "");
        // Matches whose call targets could not be compared are said in the detail.
        string unchecked_ = r.CallTargetUnverified ? $"\n     ! call targets not compared: {r.Detail}" : "";
        if (r.AlreadyMatches && moved == "")
        {
            Console.WriteLine($"same {s.Name}  ({r.ReadField}){unchecked_}");
        }
        else if (r.AlreadyMatches)
        {
            Console.WriteLine($"MOVE {s.Name}  pattern still matches,{moved}  ({r.ReadField})\n     in {s.Source}{unchecked_}");
        }
        else if (r.Pattern != null)
        {
            Console.WriteLine($"NEW  {s.Name}\n     {SigPattern.PeliteToHex(r.Pattern)} offset {r.Offset} ripOffset {r.RipOffset}{(ripMoved ? $"  (ripOffset was {s.RipOffset}: change the reader too)" : "")}  ({r.ReadField})\n     in {s.Source}{unchecked_}");
        }
        else
        {
            failed++;
            Console.WriteLine($"FAIL {s.Name}: {r.Detail}");
        }
    }
    return failed > 0 ? 2 : 0;
}

// The newest version class of each OverlayPlugin reader is the one the plugin picks for a current
// game; cactbot reads its region's file.
bool InUse(Signature s, string region)
{
    if (s.Name.StartsWith("cactbot/", StringComparison.Ordinal))
        return s.Name.StartsWith($"cactbot/{region}.", StringComparison.Ordinal);
    string Group(Signature x) => Regex.Replace(x.Name, @"\d+(?=\.)", "");
    // Class numbers are versions without the dot: 655 is 6.55, 74 is 7.4.
    double Version(Signature x)
    {
        string d = Regex.Match(x.Name, @"(\d+)\.").Groups[1].Value;
        return d[0] - '0' + double.Parse("0." + (d.Length > 1 ? d[1..] : "0"), System.Globalization.CultureInfo.InvariantCulture);
    }
    return signatures.Where(x => !x.Name.StartsWith("cactbot/", StringComparison.Ordinal) && Group(x) == Group(s))
        .Max(Version) == Version(s);
}

static string GameVersion(string exe)
{
    string ver = Path.Combine(Path.GetDirectoryName(exe) ?? "", "ffxivgame.ver");
    return File.Exists(ver) ? File.ReadAllText(ver).Trim() : "version unknown";
}

static Dictionary<string, string> Options(string[] args)
{
    var o = new Dictionary<string, string>();
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i].StartsWith("--") && !args[i + 1].StartsWith("--"))
            o[args[i][2..]] = args[++i];
    }
    return o;
}
