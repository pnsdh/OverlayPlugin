using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Windows.Forms;
using RainbowMage.HtmlRenderer;

static class HitLoop
{
    static TimeSpan TreeCpu()
    {
        var parents = new Dictionary<int, List<int>>();
        using (var q = new ManagementObjectSearcher("select ProcessId, ParentProcessId from Win32_Process"))
            foreach (ManagementObject o in q.Get())
            {
                var id = Convert.ToInt32(o["ProcessId"]); var p = Convert.ToInt32(o["ParentProcessId"]);
                if (!parents.TryGetValue(p, out var l)) parents[p] = l = new List<int>();
                l.Add(id);
            }
        var sum = TimeSpan.Zero; var stack = new Stack<int>(); stack.Push(Process.GetCurrentProcess().Id); var seen = new HashSet<int>();
        while (stack.Count > 0)
        {
            var id = stack.Pop(); if (!seen.Add(id)) continue;
            try { sum += Process.GetProcessById(id).TotalProcessorTime; } catch { }
            if (parents.TryGetValue(id, out var c)) foreach (var x in c) stack.Push(x);
        }
        return sum;
    }

    public static void Run(OverlayForm form)
    {
        var r = form.Renderer;
        var t0 = DateTime.Now; var c0 = TreeCpu();
        var idle = new Timer { Interval = 10000 };
        idle.Tick += (s, e) =>
        {
            idle.Stop();
            var c1 = TreeCpu(); var t1 = DateTime.Now;
            Program.Log($"idle: cpu={(c1 - c0).TotalSeconds / (t1 - t0).TotalSeconds * 100:F1}%");
            bool pending = false; int n = 0, x = 0;
            var loop = new Timer { Interval = 30 };
            loop.Tick += (s2, e2) =>
            {
                if (pending) return;
                pending = true; n++; x = (x + 37) % 500;
                r.QueryOpaqueAt(x + 20, 60, _ => pending = false);
            };
            loop.Start();
            var end = new Timer { Interval = 10000 };
            end.Tick += (s3, e3) =>
            {
                end.Stop(); loop.Stop();
                var c2 = TreeCpu(); var t2 = DateTime.Now;
                Program.Log($"hit testing: {n} queries, cpu={(c2 - c1).TotalSeconds / (t2 - t1).TotalSeconds * 100:F1}%");
            };
            end.Start();
        };
        idle.Start();
    }
}
