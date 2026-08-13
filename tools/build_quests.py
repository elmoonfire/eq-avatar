#!/usr/bin/env python3
"""
Build quests.json for EQ Avatar's Questing page from eqlwiki.com (MediaWiki 1.45).

Every quest page carries a `questTopTable` infobox with a fixed set of rows, so the
infobox is parsed as structure rather than guessed at. Everything else (turn-ins,
coordinates, rewards, faction) is pulled out of the walkthrough prose with patterns
that the wiki uses consistently across the corpus.
"""
import json, re, sys, time, urllib.parse, urllib.request
from collections import Counter

API = "https://eqlwiki.com/api.php"
UA = "EQAvatar-questdata/1.0 (eqavatar.ldtlan.com)"


def api(params):
    params = dict(params, format="json")
    url = API + "?" + urllib.parse.urlencode(params)
    for attempt in range(4):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA})
            return json.loads(urllib.request.urlopen(req, timeout=60).read().decode())
        except Exception:
            if attempt == 3:
                raise
            time.sleep(2 + attempt * 2)


def category_members(cat):
    out, cont = [], None
    while True:
        p = {"action": "query", "list": "categorymembers", "cmtitle": cat, "cmlimit": "500"}
        if cont:
            p["cmcontinue"] = cont
        d = api(p)
        out += [m["title"] for m in d["query"]["categorymembers"] if m["ns"] == 0]
        if "continue" not in d:
            return out
        cont = d["continue"]["cmcontinue"]


# ---------------------------------------------------------------- wikitext helpers

LINK = re.compile(r"\[\[([^\]|]+)(?:\|([^\]]+))?\]\]")


def links(s):
    """Every [[target|label]] in order, as (target, display)."""
    return [(m.group(1).strip(), (m.group(2) or m.group(1)).strip()) for m in LINK.finditer(s)]


def plain(s):
    """Strip wiki markup down to readable text."""
    s = LINK.sub(lambda m: (m.group(2) or m.group(1)), s)
    s = re.sub(r"\{\{:?([^}|]*)(\|[^}]*)?\}\}", r"\1", s)
    s = re.sub(r"<[^>]+>", " ", s)
    s = s.replace("'''", "").replace("''", "")
    return re.sub(r"\s+", " ", s).strip()


def split_template_params(body):
    """Split a template body on top-level pipes (nested {{…}} and [[…]] survive)."""
    parts, depth, buf = [], 0, ""
    i = 0
    while i < len(body):
        c = body[i]
        two = body[i:i + 2]
        if two in ("{{", "[["):
            depth += 1
            buf += two
            i += 2
            continue
        if two in ("}}", "]]"):
            depth -= 1
            buf += two
            i += 2
            continue
        if c == "|" and depth == 0:
            parts.append(buf)
            buf = ""
        else:
            buf += c
        i += 1
    parts.append(buf)
    return parts


def simple_quest_box(text):
    """The newer {{Simple Quest |zone=… |quest giver=… |turn-in=…}} infobox."""
    m = re.search(r"\{\{\s*Simple Quest\s*(\|.*)", text, re.S | re.I)
    if not m:
        return {}
    # walk to the matching close brace
    depth, i, start = 2, m.end(1) - len(m.group(1)), None
    s = text[m.start():]
    depth, out_end = 0, None
    for i in range(len(s) - 1):
        if s[i:i + 2] == "{{":
            depth += 1
        elif s[i:i + 2] == "}}":
            depth -= 1
            if depth == 0:
                out_end = i
                break
    body = s[2:out_end] if out_end else s[2:]
    params = split_template_params(body)[1:]      # [0] is the template name
    out = {}
    for p in params:
        if "=" in p:
            k, v = p.split("=", 1)
            out[k.strip().lower()] = v.strip()
    return out


def infobox(text):
    """Parse the questTopTable rows into {label: raw cell}."""
    m = re.search(r"\{\|[^\n]*questTopTable(.*?)\n\|\}", text, re.S)
    if not m:
        return {}
    body, out, label = m.group(1), {}, None
    for raw in body.split("\n"):
        line = raw.strip()
        if line.startswith("!"):
            label = plain(line.lstrip("!").strip()).rstrip(":").strip()
        elif line.startswith("|") and not line.startswith("|-") and label:
            out.setdefault(label, raw.strip().lstrip("|").strip())
    return out


HEADING = re.compile(r"\n(=+)\s*([^=\n]+?)\s*=+[ \t]*(?=\n)")


def section(text, *names):
    """Body of the first == Heading == whose name matches (case/plural insensitive),
    INCLUDING everything under its deeper sub-headings.

    Stopping at the next heading of ANY depth was a silent data bug with real cost: on
    'This Means Warrr' the walkthrough's only turn-in — "You offered 1 Heretic
    Insurrection Orders to The Kerran Sha`rr" — lives under '=== Quest Stage 2: ===',
    so the section ended two lines in and the quest reached the app with an EMPTY
    turn-in list. The Questing card then had nothing to offer as the follow-on hand-in,
    and the cycle could not be completed at all. A section ends at the next heading of
    the SAME OR SHALLOWER level; its own subsections are part of it.
    """
    want = {n.lower().rstrip("s") for n in names}
    t = "\n" + text
    marks = [(m.start(), m.end(), len(m.group(1)), m.group(2)) for m in HEADING.finditer(t)]
    for i, (_s, e, lvl, name) in enumerate(marks):
        if name.strip().lower().rstrip(":").rstrip("s") in want:
            stop = len(t)
            for (s2, _e2, lvl2, _n2) in marks[i + 1:]:
                if lvl2 <= lvl:
                    stop = s2
                    break
            return t[e:stop]
    return ""


COORD = re.compile(r"(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)")
# "The Kerran Sha`rr is located at 902.55, 347.27, -6.81"  /  "may be located at -332, -836, -109"
LOCATED = re.compile(r"([A-Za-z`'’\- ]{3,60}?)\s+(?:is|may be|can be|are)?\s*(?:located|found)\s*(?:at|around|near)\s*" + COORD.pattern, re.I)
SPAWNS = re.compile(r"([A-Za-z`'’\- ]{3,60}?)\s+(?:has multiple spawns|spawns?)\s*(?:around|at|near)\s*" + COORD.pattern, re.I)
# The server's own log line, quoted verbatim on nearly every quest page:
# "* You offered 1 Desecrated Kejaar Totem to The Kerran Sha`rr."
# ONLY "offered" — that is the exact wording EQ prints. Letting "gave"/"hand" in here swept up
# prose ("...you hand in the Marching Orders to Gloradin") and stored "in the Marching Orders"
# as an item. Prose goes through the link-anchored fallback below instead, where it must name
# things the wiki actually has pages for.
OFFERED = re.compile(r"You offered\s+(\d+)?\s*([^\n]{2,80}?)\s+to\s+([^\n.]{2,60})", re.I)
# Prose fallback, used only when no log quote was found. Deliberately loose here and made
# strict at the call site, where both halves must name pages this wiki actually links to.
GAVE_TO = re.compile(r"(?:give|gave|hand(?:ed)?|turn in|deliver|bring)\s+(?:the\s+|him\s+|her\s+|them\s+|some\s+)?([^\n.]{2,70}?)\s+to\s+([^\n.,]{2,50})", re.I)
FACTION = re.compile(r"faction standing with\s+([^\n]{2,50}?)\s+has been adjusted by\s+(-?\d+)", re.I)
EXPPCT = re.compile(r"You gain experience!?\s*\(?([\d.]+%)?\)?\s*(?:@\s*level\s*(\d+))?", re.I)
ERA = re.compile(r"\{\{\s*([A-Za-z' ]+?)\s*Era\s*\}\}")


def clean_npc(s):
    s = plain(s).strip(" .,'\"*:;")
    s = re.sub(r"^(?:the\s+)?(?:npc\s+)?", "", s, flags=re.I) if s.lower().startswith("npc ") else s
    return s.strip()


def parse_level(raw):
    """'1+ (Rec. 10)' -> (1, '1+ (Rec. 10)').  '35 - 45' -> (35, ...)."""
    txt = plain(raw)
    m = re.search(r"\d+", txt)
    return (int(m.group(0)) if m else 0), txt


def reward_items(text, extra=""):
    """Reward section items come through as {{:Item Name}} transclusions."""
    sec = (section(text, "Reward", "Rewards") or "") + "\n" + (extra or "")
    out = []
    for m in re.finditer(r"\{\{:\s*([^}|]+?)\s*(?:\|[^}]*)?\}\}", sec):
        name = m.group(1).strip()
        if name and name.lower() not in ("youGainExperience".lower(), "checkboxlist", "end"):
            out.append(name)
    for tgt, disp in links(sec):
        if tgt.startswith(("File:", "Category:")):
            continue
        if tgt not in out and re.search(r"[A-Za-z]", tgt):
            out.append(tgt)
    if extra and not out:
        p = plain(extra)
        if p and p.lower() not in ("tbd", "none"):
            out.append(p[:70])
    seen, uniq = set(), []
    for n in out:
        k = n.lower()
        if k not in seen:
            seen.add(k)
            uniq.append(n)
    return uniq[:8]


def parse_page(title, text):
    if re.search(r"\{\{\s*Disambiguation\s*\}\}", text, re.I):
        return None
    ib = infobox(text)
    sq = simple_quest_box(text)
    if not ib and not sq:
        return None

    def cell(*names):
        for n in names:
            for k, v in ib.items():
                if k.lower().startswith(n.lower()):
                    return v
            for k, v in sq.items():
                if k.lower().startswith(n.lower().rstrip("s")) or k.lower() == n.lower():
                    return v
        return ""

    start_zone_raw = cell("Start Zone", "zone")
    giver_raw = cell("Quest Giver", "Giver", "NPC")
    lvl_raw = cell("Minimum Level", "Level")
    classes_raw = cell("Classes", "Class")
    rel_zones_raw = cell("Related Zones")
    rel_npcs_raw = cell("Related NPCs")

    start_zone = plain(start_zone_raw)
    start_npc = clean_npc(giver_raw)
    lvl_min, lvl_text = parse_level(lvl_raw)
    classes = [plain(c) for c in re.split(r",|/", classes_raw) if plain(c)]
    rel_zones = [d for t, d in links(rel_zones_raw)] or ([plain(rel_zones_raw)] if plain(rel_zones_raw) not in ("", "None") else [])
    rel_npcs = [d for t, d in links(rel_npcs_raw)] or ([plain(rel_npcs_raw)] if plain(rel_npcs_raw) not in ("", "None") else [])

    walk = section(text, "Walkthrough", "Walk Through") or text

    # ---- turn-ins: the single most useful thing for automation
    turnins = []
    # the Simple Quest box states them outright: |turn-in={{:Item}} |turn-in 2=2 {{:Item}}
    for k in sorted(k for k in sq if k.startswith("turn-in") or k.startswith("turn in")):
        raw = sq[k]
        it = re.search(r"\{\{:\s*([^}|]+?)\s*(?:\|[^}]*)?\}\}", raw)
        name = it.group(1).strip() if it else None
        if not name:
            lk = links(raw)
            name = lk[0][1] if lk else None
        if not name:
            name = plain(raw)[:60]
        qm = re.match(r"\s*(\d+)\s", raw)
        if name and name.lower() not in ("tbd", "none", ""):
            turnins.append({"item": name, "qty": int(qm.group(1)) if qm else 1,
                            "npc": start_npc})
    for m in OFFERED.finditer(plain(walk)):
        qty, item, npc = m.group(1) or "1", m.group(2).strip(" .,'\"*"), clean_npc(m.group(3))
        # The log quote is authoritative, but the sentence around it is not: keep hits whose
        # item reads like an item (a proper noun, as every EQ item is).
        if len(item) > 1 and len(npc) > 1 and re.search(r"[A-Z]", item):
            turnins.append({"item": item, "qty": int(qty), "npc": npc})
    if not turnins:
        # Prose fallback, anchored to the wiki's own links. "give X to Y" in running text is a
        # sentence, not data: matched loosely it produced turn-ins like "him the SMR he will give
        # you the Mecha" to "obtain the Ink of the Dark". So a match only counts when the item and
        # the NPC are things this page LINKS TO — every real item and NPC on eqlwiki is a page —
        # and the match is trimmed back to the linked name.
        linked = {}
        for tgt, disp in links(walk):
            if tgt.startswith(("File:", "Category:")):
                continue
            for nm in (tgt, disp):
                nm = nm.strip()
                if len(nm) > 2:
                    linked[nm.lower()] = nm
        for nm in [start_npc] + rel_npcs:
            if nm and len(nm) > 2:
                linked.setdefault(nm.lower(), nm)

        def anchored(fragment, at_end):
            """The longest linked name the fragment ends with (items) or starts with (NPCs)."""
            f = fragment.lower()
            best = ""
            for k, nm in linked.items():
                if (f.endswith(k) if at_end else f.startswith(k)) and len(k) > len(best):
                    best = k
            return linked.get(best, "")

        for m in GAVE_TO.finditer(plain(walk)):
            item = anchored(m.group(1).strip(" .,'\"*"), at_end=True)
            npc = anchored(clean_npc(m.group(2)), at_end=False)
            if item and npc and item.lower() != npc.lower():
                turnins.append({"item": item, "qty": 1, "npc": npc})
                if len(turnins) >= 4:            # a multi-item hand-in, not a whole walkthrough
                    break
    seen_ti, ti = set(), []
    for t in turnins:
        k = (t["item"].lower(), t["npc"].lower())
        if k not in seen_ti:
            seen_ti.add(k)
            ti.append(t)
    turnins = ti[:10]

    end_npc = turnins[-1]["npc"] if turnins else start_npc
    # the wiki writes NPC names inconsistently cased; prefer the infobox spelling when they match
    if end_npc and start_npc and end_npc.lower().replace("`", "'") == start_npc.lower().replace("`", "'"):
        end_npc = start_npc
    end_zone = start_zone
    if end_npc.lower() != start_npc.lower() and len(rel_zones) == 1 and rel_zones[0] != start_zone:
        end_zone = rel_zones[0]

    # ---- coordinates worth navigating to
    locs = []
    ptxt = plain(walk)
    for rx, kind in ((LOCATED, "npc"), (SPAWNS, "spawn")):
        for m in rx.finditer(ptxt):
            who = clean_npc(m.group(1))
            who = re.sub(r"^(?:and|the|a|at|to|in|on|is|of|for)\s+", "", who, flags=re.I).strip()
            if 2 < len(who) < 60:
                locs.append({"who": who, "kind": kind,
                             "x": float(m.group(2)), "y": float(m.group(3)), "z": float(m.group(4))})
    # fallback: any line carrying a coordinate triple, credited to the first name on that line
    if not locs:
        for line in walk.split("\n"):
            cm = COORD.search(line)
            if not cm:
                continue
            lk = [d for t, d in links(line) if not t.startswith(("File:", "Category:"))]
            who = lk[0] if lk else ""
            if not who:
                bm = re.search(r"'''\s*([^']{3,60}?)\s*'''", line)
                who = bm.group(1) if bm else ""
            who = clean_npc(who) or "quest location"
            locs.append({"who": who, "kind": "spot",
                         "x": float(cm.group(1)), "y": float(cm.group(2)), "z": float(cm.group(3))})

    seen, uniq_locs = set(), []
    for l in locs:
        k = (l["who"].lower(), l["x"], l["y"])
        if k not in seen:
            seen.add(k)
            uniq_locs.append(l)

    # Dialogue triggers: the bracketed words the NPC asks you to say back. The wiki records them
    # as transcript lines — "You say, 'explorrre the island'" — and saying the phrase does the
    # same thing as clicking the bracketed link in chat: it assigns/advances the task. Without
    # these a hand-in-only automation can't even get the quest INTO the journal.
    says = []
    for m in re.finditer(r"You say, '([^']+)'", text):
        phrase = m.group(1).strip()
        if phrase.lower().startswith("hail"):
            continue
        if phrase not in says:
            says.append(phrase)

    factions = [{"faction": plain(m.group(1)), "delta": int(m.group(2))} for m in FACTION.finditer(ptxt)]
    xm = EXPPCT.search(ptxt)

    cats = [m.group(1) for m in re.finditer(r"\[\[Category:\s*([^\]|]+?)\s*(?:\|[^\]]*)?\]\]", text)]
    era_m = ERA.search(text)

    # What the quest asks you to bring. The turn-ins are the strongest evidence, but most pages
    # never quote the log — they DO keep a "== Checklist ==" of linked item pages, which is the
    # wiki's own answer to "what do I need?" and covers hundreds of quests the prose patterns
    # can't reach. Feeding both lists means a quest the app can't fully script still tells you
    # which item to hand over.
    items_needed = [t["item"] for t in turnins]
    check = section(text, "Checklist", "Check List")
    for line in check.split("\n"):
        stripped = line.strip()
        if not stripped.startswith("*") or stripped.startswith("**"):
            continue                                  # sub-bullets are steps ("Locate and kill …")
        # ONLY the link the bullet leads with. The checklist convention is item-first
        # ("* [[Heretic Insurrection Orders]]", with the mob and the zone on indented sub-bullets
        # or later in the line), so taking every link on the line collects the creature that drops
        # the item and the zone it drops in — and those then appear in the app's hand-in dropdown
        # as things to give an NPC. Offering "Heretic" as a quest item is worse than offering
        # nothing: it builds a step that can never match and stops the run claiming you ran out.
        not_items = {x.strip().lower() for x in [start_zone, start_npc] + rel_zones + rel_npcs if x}
        for tgt, disp in links(stripped)[:1]:
            if tgt.startswith(("File:", "Category:")):
                continue
            name = disp.strip().lstrip(":").strip()
            if 2 < len(name) < 60 and name.lower() not in not_items:
                items_needed.append(name)
    seen_in, uniq_in = set(), []
    for n in items_needed:
        if n.lower() not in seen_in:
            seen_in.add(n.lower())
            uniq_in.append(n)
    items_needed = uniq_in[:12]

    return {
        "name": title,
        "url": "https://eqlwiki.com/" + urllib.parse.quote(title.replace(" ", "_")),
        "startZone": start_zone,
        "startNpc": start_npc,
        "endZone": end_zone,
        "endNpc": end_npc,
        "levelMin": lvl_min,
        "levelText": lvl_text,
        "classes": classes,
        "relatedZones": rel_zones,
        "relatedNpcs": rel_npcs,
        "rewards": reward_items(text, sq.get("reward", "")),
        "itemsNeeded": items_needed,
        "turnIns": turnins,
        "say": says[:6],
        "locs": uniq_locs[:12],
        "factions": factions[:12],
        "expText": (xm.group(1) or "") if xm else "",
        "era": (era_m.group(1) + " Era") if era_m else "",
        "categories": [c for c in cats if c != "Quests"][:12],
    }


def main():
    titles = category_members("Category:Quests")
    print(f"{len(titles)} pages in Category:Quests", file=sys.stderr)

    pages = {}
    for i in range(0, len(titles), 40):
        chunk = titles[i:i + 40]
        d = api({"action": "query", "prop": "revisions", "rvprop": "content",
                 "rvslots": "main", "titles": "|".join(chunk)})
        for _, p in d["query"]["pages"].items():
            try:
                pages[p["title"]] = p["revisions"][0]["slots"]["main"]["*"]
            except Exception:
                pass
        print(f"  fetched {min(i+40, len(titles))}/{len(titles)}", file=sys.stderr)
        time.sleep(0.25)

    quests, skipped = [], []
    for title, text in pages.items():
        try:
            q = parse_page(title, text)
        except Exception as e:
            q, = (None,)
            print(f"  !! {title}: {e}", file=sys.stderr)
        if q and q["startZone"]:
            quests.append(q)
        else:
            skipped.append(title)

    quests.sort(key=lambda q: q["name"].lower())
    # "Various"/"Any" are what the wiki writes when a quest spans the world — real words in the
    # Related Zones row, but not places, and a zone FILTER offering them filters to nothing.
    not_a_zone = {"none", "various", "any", "multiple", "all", "n/a", "tbd"}
    zones = sorted({z for q in quests for z in ([q["startZone"], q["endZone"]] + q["relatedZones"])
                    if z and z.strip().lower() not in not_a_zone})

    out = {
        "schema": 1,
        "source": "https://eqlwiki.com",
        "generated": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "questCount": len(quests),
        "zones": zones,
        "quests": quests,
    }
    json.dump(out, open("quests.json", "w"), indent=1, ensure_ascii=False)

    print(f"\nparsed {len(quests)} quests, skipped {len(skipped)} (no infobox)", file=sys.stderr)
    print(f"zones: {len(zones)}", file=sys.stderr)
    print(f"with turn-ins: {sum(1 for q in quests if q['turnIns'])}", file=sys.stderr)
    print(f"with coords:   {sum(1 for q in quests if q['locs'])}", file=sys.stderr)
    print(f"with rewards:  {sum(1 for q in quests if q['rewards'])}", file=sys.stderr)
    print("skipped sample:", skipped[:15], file=sys.stderr)
    print("top zones:", Counter(q["startZone"] for q in quests).most_common(12), file=sys.stderr)


if __name__ == "__main__":
    main()
