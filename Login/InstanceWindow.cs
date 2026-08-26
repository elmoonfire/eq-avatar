using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace EQAvatar.Spike.Login;

/// <summary>
/// Reading the instance UI, and — much more importantly — REFUSING to click the wrong thing in it.
///
/// This is the only place in the app that clicks a button which can END the thing the run depends
/// on. The window that offers "Enter" also offers "Quit Instance", they sit next to each other,
/// and <see cref="ScreenText.Find"/> is a SUBSTRING match: Find(items, "Enter") happily returns
/// the centre of "Enter Instance", of "Quit Instance" if OCR ran the two together, and of any
/// stray word in the chat behind the window that happens to contain the letters. A bot that
/// clicks the wrong one of those unattended does not lose a fight, it loses the night.
///
/// So the rules here are the opposite way round from the rest of the app's screen reading. Every
/// other read asks "can I find what I want?"; this one asks "can I prove this is not something
/// that would hurt?" and refuses when it cannot. It fails CLOSED — an unreadable window, a
/// smeared OCR line, two candidates that look equally good, and the answer is "no click", which
/// costs one grind session. A wrong click costs the instance.
/// </summary>
public static class InstanceWindow
{
    /// <summary>
    /// Words that end, abandon or dismiss something. If a candidate's text contains ANY of these
    /// it is refused outright, however well it matches what we wanted.
    ///
    /// "exit" and "end" earn their place from the same OCR failure: this window's buttons are
    /// small and close together, and two of them read as one item often enough that the merged
    /// text is the normal failure, not the exotic one. "Leave &amp; Quit Instance" is one real
    /// label; so is "Quit Instance"; so is "Cancel".
    /// </summary>
    private static readonly string[] Forbidden =
    {
        "quit", "leave", "cancel", "destroy", "delete", "remove", "abandon", "exit", "end ", "close",
    };

    /// <summary>Charges left, if the window says. "Charges: 1", "Charges 1", "charges:1".</summary>
    private static readonly Regex ChargesRe = new(
        @"\bcharges?\b\D{0,4}(\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Is this a label we must never click, whatever else it says?</summary>
    public static bool IsDangerous(string text)
    {
        string t = " " + text.ToLowerInvariant().Trim() + " ";
        foreach (string bad in Forbidden)
            if (t.Contains(bad, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// How close a READABLE dangerous label may sit to the point we are about to click before the
    /// whole thing is refused.
    ///
    /// OVERLAPPING, NOT MERELY ADJACENT. The first draft used 45 px, and a menu whose rows are 26
    /// px apart — which is every menu — put "Cancel" inside that radius of "Create" and refused
    /// the sequence on the normal case. Adjacency is what a list of buttons IS; what is abnormal
    /// is two readings sitting on top of each other, and a line of text is about 20 px tall, so
    /// that is the number. The real protection is upstream anyway: the point clicked is the centre
    /// of the OCR WORD, which is on its own button whatever OCR did with the neighbours. This is
    /// the backstop for a read that has gone wrong enough that the boxes themselves are unreliable.
    /// </summary>
    private const double DangerNearPx = 22;

    /// <summary>
    /// How close ANYTHING ELSE may sit — dangerous-looking or not.
    ///
    /// This is the guard that does not depend on reading the danger correctly. The blacklist above
    /// is a text test over exactly the thing OCR is worst at: one substituted character turns
    /// "Quit Instance" into "Ouit lnstance" and it stops being dangerous to any string comparison
    /// ever written. What does NOT change is that something is rendering right on top of the
    /// button we mean to press — and a button with a stranger crowding it is not a button worth
    /// pressing unattended, whatever that stranger turned out to say.
    /// </summary>
    private const double CrowdedPx = 16;

    /// <summary>Two readings this close together are the same thing seen twice, not two things.
    /// <see cref="ScreenText.ReadBitmapAsync"/> emits every WORD and then the whole LINE, so a
    /// one-word button arrives as two entries at the same centre — and an "is it unambiguous?"
    /// test that does not know this reports every clean read as ambiguous.</summary>
    private const double SamePointPx = 14;

    /// <summary>
    /// The one safe way to turn "I want the button that says X" into a click point.
    ///
    /// THE THING BEING CLICKED IS A WORD, NOT A LINE, and that is the whole design. ScreenText
    /// hands back both: the individual words at their own centres, and the line they belong to at
    /// the AVERAGE of those centres. The line entry is the dangerous one — when OCR runs two
    /// adjacent buttons together, "Enter    Quit Instance" is one line whose centre is the empty
    /// space BETWEEN the two buttons, and clicking a point chosen from it is how a bot presses
    /// something nobody meant it to. The word "Enter" is on the Enter button no matter what OCR
    /// did with its neighbours.
    ///
    /// So the test is a whitelist, not a blacklist over text OCR is already known to corrupt:
    ///
    ///  1. The entry's text is EXACTLY the word wanted. Not "contains" — the app's ordinary
    ///     ScreenText.Find is a substring match and would return "Quit Instance" for "Enter" if
    ///     OCR merged them. Exactness is what makes this a word and not a line.
    ///  2. Readings at the same point are collapsed, because the word and its one-word line are
    ///     the same button counted twice.
    ///  3. Exactly one cluster survives. Two means two things on screen say this and there is no
    ///     version of guessing between them worth having.
    ///  4. NOTHING DANGEROUS IS NEAR IT. Geometric, in pixels, against every other reading on
    ///     screen — so a "Quit Instance" that OCR rendered as "Ouit lnstance" still protects the
    ///     Enter beside it, because what is checked is where it IS, not how it was spelled.
    /// </summary>
    public static bool FindSafeButton(List<FoundText> items, string word, out Point center, out string why)
    {
        center = default;

        // (1) + (2): exact-text readings, collapsed by position.
        var clusters = new List<(double X, double Y, string Text)>();
        foreach (FoundText f in items)
        {
            if (!string.Equals(f.Text.Trim(), word, StringComparison.OrdinalIgnoreCase)) continue;
            bool merged = false;
            foreach ((double cx, double cy, string _) in clusters)
                if (Near(cx, cy, f.X, f.Y, SamePointPx)) { merged = true; break; }
            if (!merged) clusters.Add((f.X, f.Y, f.Text.Trim()));
        }

        if (clusters.Count == 0)
        {
            // Say whether the word was on screen at all but only inside something longer — that is
            // a merged OCR line, and it is a different problem from "the window isn't up".
            bool insideSomething = items.Any(f => f.Text.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
            why = insideSomething
                ? $"“{word}” only ever appeared inside a longer run of text, which means OCR merged it with "
                  + "whatever is beside it — and beside Enter is Quit Instance"
                : $"nothing on screen said “{word}” on its own";
            return false;
        }
        if (clusters.Count > 1)
        {
            why = $"“{word}” appears in {clusters.Count} different places on screen, so I can't tell which one "
                + "is the button";
            return false;
        }

        // (4): is anything sitting on top of it?
        (double bx, double by, string btext) = clusters[0];
        foreach (FoundText f in items)
        {
            string t = f.Text.Trim();
            if (string.Equals(t, word, StringComparison.OrdinalIgnoreCase)) continue;   // our own cluster
            bool dangerous = IsDangerous(t);
            if (!Near(bx, by, f.X, f.Y, dangerous ? DangerNearPx : CrowdedPx)) continue;
            why = dangerous
                ? $"“{t}” is reading within {DangerNearPx:0} pixels of the “{btext}” I would click, and I will "
                  + "not press a point that close to something that ends the instance"
                : $"“{t}” is reading within {CrowdedPx:0} pixels of the “{btext}” I would click. I can't tell "
                  + "what is really under that point, and “Quit Instance” is what sits beside “Enter”";
            return false;
        }

        center = new Point(bx, by);
        why = $"“{btext}”";
        return true;
    }

    private static bool Near(double ax, double ay, double bx, double by, double px)
    {
        double dx = ax - bx, dy = ay - by;
        return dx * dx + dy * dy <= px * px;
    }


    /// <summary>Charges left, or null when the window doesn't say. Null is NOT zero: an unreadable
    /// count must not be reported to the user as "you're out of charges", which sends them looking
    /// at a timer instead of at the real fault.</summary>
    public static int? ChargesLeft(List<FoundText> items)
    {
        foreach (FoundText f in items)
        {
            Match m = ChargesRe.Match(f.Text);
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                return n;
        }
        return null;
    }

    /// <summary>The "Next Charge 0d:0h:19m:41s" line, verbatim, for the message to the user.</summary>
    public static string? NextChargeIn(List<FoundText> items)
    {
        foreach (FoundText f in items)
            if (f.Text.Contains("next charge", StringComparison.OrdinalIgnoreCase))
                return f.Text.Trim();
        return null;
    }

    /// <summary>Does this look like the instance options window at all, rather than whatever else
    /// happened to be on screen when the click missed?</summary>
    public static bool LooksLikeOptions(List<FoundText> items)
    {
        bool instance = false, option = false;
        foreach (FoundText f in items)
        {
            if (f.Text.Contains("instance", StringComparison.OrdinalIgnoreCase)) instance = true;
            if (f.Text.Contains("option", StringComparison.OrdinalIgnoreCase)
                || f.Text.Contains("difficult", StringComparison.OrdinalIgnoreCase)
                || f.Text.Contains("charge", StringComparison.OrdinalIgnoreCase)) option = true;
        }
        return instance && option;
    }

    private static string Quote(IEnumerable<string> xs) => string.Join(", ", xs.Select(x => "“" + x + "”"));
}
