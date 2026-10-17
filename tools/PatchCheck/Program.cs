using System.Text.Json;

using PatchCheck;

// For a game patch: the memory signatures OverlayPlugin and cactbot's plugin rely on.
//
// dotnet run --project tools/PatchCheck -- list [--cactbot <dir>] [--json <out.json>]
//   Lists them (read from the sources); --json writes them as a pattern file for the sigscan /
//   sigregen commands of ActPluginRepack's CLI ({ name, pattern, syntax, offset, rip, ripOffset }).
// The cactbot folder defaults to ..\cactbot next to this repo.
string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
if (!File.Exists(Path.Combine(repo, "OverlayPlugin.sln")))
    repo = Directory.GetCurrentDirectory();
var opts = Options(args);
string? cactbot = opts.GetValueOrDefault("cactbot") ?? Path.Combine(repo, "..", "cactbot");
if (!Directory.Exists(cactbot))
    cactbot = null;

string command = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "list";
if (command != "list")
{
    Console.Error.WriteLine("usage: PatchCheck list [--cactbot <dir>] [--json <out.json>]");
    return 1;
}

var warnings = new List<string>();
var signatures = SignatureSources.Read(repo, cactbot, warnings);
foreach (var s in signatures)
    Console.WriteLine($"{s.Name,-58} offset {s.Offset,5}{(s.Rip ? $" rip{(s.RipOffset != 0 ? $"+{s.RipOffset}" : "")}" : "")}  {s.Pattern}");
foreach (string w in warnings)
    Console.WriteLine($"WARN {w}");
if (opts.TryGetValue("json", out var json))
{
    var items = signatures.Select(s => new { name = s.Name, pattern = s.Pattern, syntax = "hex", offset = s.Offset, rip = s.Rip, ripOffset = s.RipOffset });
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(json))!);
    File.WriteAllText(json, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"wrote {signatures.Count} patterns to {json}");
}
return warnings.Count > 0 ? 2 : 0;

static Dictionary<string, string> Options(string[] args)
{
    var o = new Dictionary<string, string>();
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i].StartsWith("--"))
            o[args[i][2..]] = args[++i];
    }
    return o;
}
