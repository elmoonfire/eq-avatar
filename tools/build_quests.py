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


def section(text, *names):
    """Body of the first == Heading == whose name matches (case/plural insensitive)."""
    want = {n.lower().rstrip("s") for n in names}
    parts = re.split(r"\n=+\s*([^=\n]+?)\s*=+\s*\n", "\n" + text)
    for i in range(1, len(parts) - 1, 2):
        if parts[i].strip().lower().rstrip(":").rstrip("s") in want:
            return parts[i + 1]
    return ""


COORD = re.compile(r"(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)")
# "The Kerran Sha`rr is located at 902.55, 347.27, -6.81"  /  "may be located at -332, -836, -109"
LOCATED = re.compile(r"([A-Za-z`'’\- ]{3,60}?)\s+(?:is|may be|can be|are)?\s*(?:located|found)\s*(?:at|around|near)\s*" + COORD.pattern, re.I)
SPAWNS = re.compile(r"([A-Za-z`'’\- ]{3,60}?)\s+(?:has multiple spawns|spawns?)\s*(?:around|at|near)\s*" + COORD.pattern, re.I)
# "* You offered 1 Desecrated Kejaar Totem to The Kerran Sha`rr."
OFFERED = re.compile(r"You (?:offered|gave|hand(?:ed)?)\s+(\d+)?\s*([^\n]{2,80}?)\s+to\s+([^\n.]{2,60})", re.I)
GAVE_TO = re.compile(r"(?:give|hand|turn in|deliver|bring)\s+(?:the\s+|him\s+|her\s+|them\s+|some\s+)?([^\n.]{2,70}?)\s+to\s+([A-Z][^\n.,]{2,50})", re.I)
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
        if len(item) > 1 and len(npc) > 1:
            turnins.append({"item": item, "qty": int(qty), "npc": npc})
    if not turnins:
        for m in GAVE_TO.finditer(plain(walk)):
            item, npc = m.group(1).strip(" .,'\"*"), clean_npc(m.group(2))
            if 2 < len(item) < 60 and 2 < len(npc) < 50 and not item.lower().startswith(("him", "her", "them")):
                turnins.append({"item": item, "qty": 1, "npc": npc})
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

    items_needed = sorted({t["item"] for t in turnins})

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
    zones = sorted({z for q in quests for z in ([q["startZone"], q["endZone"]] + q["relatedZones"]) if z and z != "None"})

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
