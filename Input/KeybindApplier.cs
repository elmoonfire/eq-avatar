using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EQAvatar.Spike.Ocr;

namespace EQAvatar.Spike.Input;

/// <summary>
/// Sets key binds inside the game, the same way a person would: find the row on the Controls →
/// Key binds screen, click the key cell, press the key. EQL accepts a bind on a single click of
/// the cell followed by the next key press, and it allows the same key on several actions, so
/// there is no conflict dialog to answer and no need to clear anything first.
///
/// Everything is verified: after setting the binds visible on a page, the page is read again
/// and each row must actually show the new key before it is counted as done. Anything that
/// can't be verified is reported, never silently assumed.
/// </summary>
public sealed class KeybindApplier
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>One bind to set: which action, which slot, and the key it should end up as.</summary>
    public sealed class Change
    {
        public string Action { get; init; } = "";
        public string Category { get; init; } = "";
        public bool Alternate { get; init; }           // false = primary slot
        public string DesiredKey { get; init; } = "";
        public string CurrentKey { get; init; } = "";
        public string Slot => Alternate ? "secondary" : "primary";
        public string Result { get; set; } = "";       // "" until attempted
        public bool Done { get; set; }
    }

    public sealed class Outcome
    {
        public int Applied, Failed, NotFound, Skipped, Sweeps;
        public List<Change> Changes { get; init; } = new();
    }

    /// <summary>Left/right mouse buttons can't be sent as a bind — the click that arms the cell
    /// and the click that would be the bind are the same gesture. Everything else is fair game.</summary>
    public static bool CanSend(string key)
    {
        var k = InputKey.Parse(key);
        if (k.IsNone) return false;
        return !(k.IsMouse && (k.Button == MouseBtn.Left || k.Button == MouseBtn.Right));
    }

    public async Task<Outcome> ApplyAsync(IntPtr hwnd, List<Change> changes, Action<string> log, CancellationToken ct)
    {
        var outcome = new Outcome { Changes = changes };
        var pending = new List<Change>();
        foreach (var c in changes)
        {
            if (c.DesiredKey.Trim().Length == 0)
            {
                c.Result = "left alone — clearing a bind isn't automated yet"; outcome.Skipped++;
            }
            else if (!CanSend(c.DesiredKey))
            {
                c.Result = $"set '{c.DesiredKey}' by hand — the bot can't send a left/right click as a bind"; outcome.Skipped++;
            }
            else pending.Add(c);
        }
        if (pending.Count == 0) return outcome;

        try { ShowWindow(hwnd, 9); SetForegroundWindow(hwnd); } catch { }
        await Task.Delay(700, ct);

        // start from the top of the list so one downward sweep can see every row
        var (cx, cy) = HumanizedMouse.CursorPos();
        var first = await KeybindReader.ReadPageAsync(hwnd);
        var (sx, sy) = first.HasRegion ? first.Center : (0, 0);
        if (sx > 0) { HumanizedMouse.MoveInstant(sx, sy); HumanizedMouse.Scroll(30); await Task.Delay(600, ct); }

        int dry = 0;
        while (pending.Count > 0 && outcome.Sweeps < 90 && dry < 3 && !ct.IsCancellationRequested)
        {
            outcome.Sweeps++;
            var page = await KeybindReader.ReadPageAsync(hwnd);
            if (page.Rows.Count == 0) { dry++; await ScrollDown(page, hwnd, ct); continue; }

            // everything we still owe that is visible right now
            var here = new List<(Change c, KeybindReader.KeyRow row)>();
            foreach (var c in pending)
            {
                var row = page.FindRow(c.Action);
                if (row is null) continue;
                if (c.Alternate ? !row.HasAlternateCell : !row.HasPrimaryCell) continue;
                here.Add((c, row));
            }

            if (here.Count == 0) { dry++; await ScrollDown(page, hwnd, ct); continue; }
            dry = 0;

            foreach (var (c, row) in here)
            {
                if (ct.IsCancellationRequested) break;
                int x = c.Alternate ? row.AlternateX : row.PrimaryX;
                log($"setting {c.Action} · {c.Slot} → {c.DesiredKey}");
                HumanizedMouse.MoveInstant(x, row.RowY);
                await Task.Delay(90, ct);
                InputProbe.MouseTap(MouseBtn.Left, 45);        // arm the cell
                await Task.Delay(260, ct);
                var k = InputKey.Parse(c.DesiredKey);
                if (k.IsMouse) InputProbe.MouseTap(k.Button, 55);
                else InputProbe.SendInputKey(k.Vk, 55);
                await Task.Delay(320, ct);
            }

            // read the page back and only believe what the game now shows
            await Task.Delay(250, ct);
            var after = await KeybindReader.ReadPageAsync(hwnd);
            foreach (var (c, _) in here)
            {
                var row = after.FindRow(c.Action);
                string now = row is null ? "" : (c.Alternate ? row.Bind.Alternate : row.Bind.Primary);
                if (row is not null && KeysLookEqual(now, c.DesiredKey))
                {
                    c.Done = true; c.Result = "set"; outcome.Applied++;
                    pending.Remove(c);
                }
                else if (row is not null)
                {
                    c.Result = $"tried, but the row still reads '{(now.Length == 0 ? "—" : now)}'";
                    outcome.Failed++;
                    pending.Remove(c);                      // don't loop forever on a stubborn row
                }
                // row vanished from view (scrolled): leave it pending for a later sweep
            }

            await ScrollDown(after.Rows.Count > 0 ? after : page, hwnd, ct);
        }

        foreach (var c in pending)
        {
            c.Result = "action never appeared on the key binds screen";
            outcome.NotFound++;
        }
        HumanizedMouse.MoveInstant(cx, cy);
        return outcome;
    }

    private static async Task ScrollDown(KeybindReader.KeybindPage page, IntPtr hwnd, CancellationToken ct)
    {
        if (page.HasRegion)
        {
            var (px, py) = page.Center;
            HumanizedMouse.MoveInstant(px, py);
        }
        HumanizedMouse.Scroll(-3);
        await Task.Delay(430, ct);
    }

    /// <summary>OCR spacing/case wobble shouldn't count as a failure ("Mouse 4" == "mouse4").</summary>
    public static bool KeysLookEqual(string a, string b)
    {
        static string N(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        string na = N(a), nb = N(b);
        if (na == nb) return true;
        var ka = InputKey.Parse(a); var kb = InputKey.Parse(b);
        return !ka.IsNone && !kb.IsNone && ka.Kind == kb.Kind && ka.Vk == kb.Vk && ka.Button == kb.Button;
    }
}
