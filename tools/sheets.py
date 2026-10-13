"""RiskOfCraft design sheets: preflight and code generation.

    python -I tools/sheets.py preflight      # every empty cell and unresolved reference; exit 1 if any
    python -I tools/sheets.py generate       # preflight, then write the generated code

The sheets in design/ are the source of truth. Each row generates one piece of code built from its
columns: protocol structs become Rust #[repr(C)] and C# StructLayout structs, gameplay rows become
C# table entries, the command ring becomes Java constants for the guest.
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DESIGN = os.path.join(ROOT, "design")

TYPES = {  # sheet type -> (bytes, rust, c#)
    "u8": (1, "u8", "byte"), "u16": (2, "u16", "ushort"), "u32": (4, "u32", "uint"), "i32": (4, "i32", "int"),
    "u64": (8, "u64", "ulong"), "i64": (8, "i64", "long"), "f32": (4, "f32", "float"), "f64": (8, "f64", "double"),
}


def load(name):
    with open(os.path.join(DESIGN, name), encoding="utf-8") as f:
        return json.load(f)


def lines(name):
    with open(os.path.join(DESIGN, "mc_index", name), encoding="utf-8") as f:
        return {line.strip() for line in f if line.strip()}


def parse_type(t):
    m = re.fullmatch(r"([a-z0-9]+)(?:\[(\d+)\])?", t)
    if not m or m.group(1) not in TYPES:
        return None
    return m.group(1), int(m.group(2) or 0)


def layout(fields):
    """Natural C layout: (offsets, size, align)."""
    offsets, off, align = [], 0, 1
    for f in fields:
        base, n = parse_type(f["type"])
        size = TYPES[base][0]
        off = (off + size - 1) // size * size
        offsets.append(off)
        off += size * max(n, 1)
        align = max(align, size)
    return offsets, (off + align - 1) // align * align, align


# ---------------------------------------------------------------------------------------------
def preflight():
    problems, unverified = [], []

    def empty_cells(sheet_name, table_name, table, skip=()):
        for i, row in enumerate(table["rows"]):
            for col in table["columns"]:
                if col in skip:
                    continue
                v = row.get(col, None)
                if v is None or (isinstance(v, str) and v.strip() == ""):
                    problems.append(f"{sheet_name}:{table_name} row {row.get('id', i)}: '{col}' is empty")
            extra = set(row) - set(table["columns"])
            if extra:
                problems.append(f"{sheet_name}:{table_name} row {row.get('id', i)}: unknown columns {sorted(extra)}")

    structs = load("protocol_structs.json")
    fields = load("protocol_fields.json")
    regions = load("protocol_regions.json")
    hooks = load("hooks.json")
    systems = load("systems.json")
    game = load("gameplay.json")

    empty_cells("protocol_structs", "rows", structs)
    empty_cells("protocol_fields", "rows", fields, skip=("note",))
    empty_cells("protocol_regions", "rows", regions, skip=("note",))
    empty_cells("protocol_regions", "constants", regions["constants"], skip=("note",))
    enums = load("protocol_enums.json")
    empty_cells("protocol_enums", "rows", enums, skip=("note",))
    seen_enum = set()
    for e in enums["rows"]:
        key = (e["enum"], e["name"])
        if key in seen_enum:
            problems.append(f"protocol_enums {e['enum']}.{e['name']}: defined twice")
        seen_enum.add(key)
        if not str(e["value"]).isdigit():
            problems.append(f"protocol_enums {e['enum']}.{e['name']}: value {e['value']} is not a number")
    empty_cells("hooks", "rows", hooks)
    empty_cells("systems", "rows", systems, skip=("verified",))
    for name, table in game.items():
        if isinstance(table, dict) and "rows" in table:
            empty_cells("gameplay", name, table, skip=("note",))

    # protocol: every field typed, offsets as the header fixes them, sizes as static_asserted
    struct_ids = {s["id"] for s in structs["rows"]}
    by_struct = {}
    for f in fields["rows"]:
        if f["struct"] not in struct_ids:
            problems.append(f"protocol_fields {f['struct']}.{f['field']}: struct not in protocol_structs")
            continue
        if parse_type(f["type"]) is None:
            problems.append(f"protocol_fields {f['struct']}.{f['field']}: unknown type {f['type']}")
            continue
        by_struct.setdefault(f["struct"], []).append(f)
    for s in structs["rows"]:
        fs = by_struct.get(s["id"])
        if not fs:
            problems.append(f"protocol_structs {s['id']}: no fields")
            continue
        offs, size, _ = layout(fs)
        for f, o in zip(fs, offs):
            if o != f["offset"]:
                problems.append(f"protocol_fields {s['id']}.{f['field']}: offset {f['offset']} but the layout puts it at {o}")
        if size != s["size"]:
            problems.append(f"protocol_structs {s['id']}: size {s['size']} but the fields add up to {size}")

    # regions: entries fit, no overlaps, mapping size is the end of the last region
    sizes = {s["id"]: s["size"] for s in structs["rows"]}
    spans = []
    for r in regions["rows"]:
        off, n = int(r["offset"], 16), int(r["bytes"], 16)
        if r["entry_struct"] != "-" and r["entry_struct"] not in sizes:
            problems.append(f"protocol_regions {r['id']}: entry_struct {r['entry_struct']} not in protocol_structs")
        if r["shape"] == "entry":
            need = 0x80 + r["entries"] * sizes.get(r["entry_struct"], 0)
            if need > n:
                problems.append(f"protocol_regions {r['id']}: {r['entries']} entries need 0x{need:X} bytes, region has 0x{n:X}")
            if r["entries"] & (r["entries"] - 1):
                problems.append(f"protocol_regions {r['id']}: entries {r['entries']} is not a power of two")
        spans.append((off, off + n, r["id"]))
    spans.sort()
    for (a0, a1, a), (b0, b1, b) in zip(spans, spans[1:]):
        if a1 > b0:
            problems.append(f"protocol_regions {a} (ends 0x{a1:X}) overlaps {b} (starts 0x{b0:X})")
    consts = {c["id"]: c["value"] for c in regions["constants"]["rows"]}
    if int(consts.get("MappingBytes", "0"), 16) != spans[-1][1]:
        problems.append(f"protocol_regions: MappingBytes {consts.get('MappingBytes')} != end of last region 0x{spans[-1][1]:X}")

    # hooks and systems: modules exist, references resolve
    hook_ids = {h["id"] for h in hooks["rows"]}
    tables = {k for k, v in game.items() if isinstance(v, dict) and "rows" in v}
    for h in hooks["rows"]:
        if h["module"] != "n/a" and not os.path.exists(os.path.join(ROOT, h["module"])):
            problems.append(f"hooks {h['id']}: module {h['module']} does not exist (unimplemented)")
    for s in systems["rows"]:
        for ref in s["hooks"].split(","):
            if ref.strip() not in ("-", "") and ref.strip() not in hook_ids:
                problems.append(f"systems {s['id']}: hook '{ref.strip()}' not in hooks.json")
        for ref in s["data"].split(","):
            if ref.strip() not in ("-", "") and ref.strip() not in tables:
                problems.append(f"systems {s['id']}: data table '{ref.strip()}' not in gameplay.json")
        if not os.path.exists(os.path.join(ROOT, s["module"])):
            problems.append(f"systems {s['id']}: module {s['module']} does not exist (unimplemented)")
        if not s.get("verified"):
            unverified.append(s["id"])

    # gameplay: Minecraft and RoR2 names resolve against the indexes
    loot = lines("loot_tables.txt")
    items = lines("items.txt")
    attrs = lines("attributes.txt")
    jar_files = lines("textures.txt")
    bodies = set()
    with open(os.path.join(DESIGN, "mc_index", "ror2_bodies_code.txt"), encoding="utf-8") as f:
        for line in f:
            bodies.update(line.split())
    for r in game["loot_chests"]["rows"]:
        if r["mc_loot_table"] not in loot:
            problems.append(f"gameplay:loot_chests {r['id']}: loot table {r['mc_loot_table']} not in Minecraft 26.3")
    for r in game["monsters"]["rows"]:
        if r["mc_loot_table"] not in loot:
            problems.append(f"gameplay:monsters {r['id']}: loot table {r['mc_loot_table']} not in Minecraft 26.3")
        if r["ror2_body"] != "*" and r["ror2_body"] not in bodies:
            problems.append(f"gameplay:monsters {r['id']}: body {r['ror2_body']} not in RoR2's content")
    for r in game["starter_kit"]["rows"]:
        if r["mc_item"] not in items:
            problems.append(f"gameplay:starter_kit {r['id']}: item {r['mc_item']} not in Minecraft 26.3")
    for r in game["nether_mobs"]["rows"]:
        if r["mc_entity"] + "_spawn_egg" not in items:
            problems.append(f"gameplay:nether_mobs {r['id']}: entity {r['mc_entity']} not in Minecraft 26.3 (no spawn egg)")
    for r in game["scaling"]["rows"]:
        if r["mc_target"].split(":", 1)[-1] not in attrs:
            problems.append(f"gameplay:scaling {r['id']}: attribute {r['mc_target']} not in Minecraft 26.3")
    dig = {e["name"] for e in enums["rows"] if e["enum"] == "DigMaterial"}
    for r in game["surfaces"]["rows"]:
        if r["dig_material"] not in dig:
            problems.append(f"gameplay:surfaces {r['id']}: dig material {r['dig_material']} not in protocol_enums DigMaterial")
    for r in game["loot_pools"]["rows"]:
        if r["mc_loot_table"] not in loot:
            problems.append(f"gameplay:loot_pools {r['id']}: loot table {r['mc_loot_table']} not in Minecraft 26.3")
        if not any(t["tier"] == r["tier"] for t in game["chest_tiers"]["rows"]):
            problems.append(f"gameplay:loot_pools {r['id']}: tier {r['tier']} not in chest_tiers")
    for t in game["chest_tiers"]["rows"]:
        if not any(p["tier"] == t["tier"] for p in game["loot_pools"]["rows"]):
            problems.append(f"gameplay:chest_tiers {t['id']}: no loot_pools rows")
    for r in game["rare_items"]["rows"]:
        if r["mc_item"] not in items:
            problems.append(f"gameplay:rare_items {r['id']}: item {r['mc_item']} not in Minecraft 26.3")
    stages = {r["id"] for r in game["custom_stages"]["rows"]}
    for r in game["portals"]["rows"]:
        if r["custom_stage"] not in stages:
            problems.append(f"gameplay:portals {r['id']}: custom stage {r['custom_stage']} not in custom_stages")
    for r in game["custom_stages"]["rows"]:
        for col in ("top_texture", "side_texture", "accent_texture"):
            name = r[col].split(":", 1)[-1]
            if f"assets/minecraft/textures/{name}.png" not in jar_files:
                problems.append(f"gameplay:custom_stages {r['id']}: {col} {r[col]} not in Minecraft 26.3")
    cmd_ids = {r["id"] for r in game["commands"]["rows"]}
    for r in game["scaling"]["rows"]:
        if r["command"] not in cmd_ids:
            problems.append(f"gameplay:scaling {r['id']}: command {r['command']} not in commands")
    seen = set()
    for r in game["messages"]["rows"]:
        if r["msg_type"] in seen:
            problems.append(f"gameplay:messages {r['id']}: msg_type {r['msg_type']} used twice")
        seen.add(r["msg_type"])
    for name in ("loot_chests", "monsters", "starter_kit", "scaling", "commands", "keybinds", "messages", "damage", "surfaces", "portals", "custom_stages", "chest_tiers", "loot_pools", "rare_items", "villagers", "nether_mobs"):
        ids = [r["id"] for r in game[name]["rows"]]
        if len(ids) != len(set(ids)):
            problems.append(f"gameplay:{name}: duplicate ids")

    return problems, unverified


# ---------------------------------------------------------------------------------------------
HEADER = "// GENERATED by tools/sheets.py from design/*.json. Do not edit; change the sheet and regenerate.\n"


def gen_rust(structs, by_struct, regions):
    out = [HEADER, "#![allow(non_snake_case, non_camel_case_types, dead_code)]\n"]
    for c in regions["constants"]["rows"]:
        v = c["value"]
        if c["id"] == "MappingName":
            out.append(f'pub const MAPPING_NAME: &str = "{v.replace(chr(92), chr(92) * 2)}";\n')
        else:
            out.append(f"pub const {re.sub(r'(?<!^)(?=[A-Z])', '_', c['id']).upper()}: u64 = {v};\n")
    out.append("\n")
    for r in regions["rows"]:
        name = re.sub(r"(?<!^)(?=[A-Z])", "_", r["id"]).upper()
        out.append(f"pub const OFF_{name}: usize = {r['offset']};\n")
        out.append(f"pub const BYTES_{name}: usize = {r['bytes']};\n")
        if r["entries"]:
            out.append(f"pub const ENTRIES_{name}: usize = {r['entries']};\n")
    for s in structs["rows"]:
        out.append(f"\n/// {s['c_name']} ({s['direction']}, {s['shape']}): {s['used_by']}\n")
        out.append("#[repr(C)]\n#[derive(Clone, Copy)]\n")
        out.append(f"pub struct {s['id']} {{\n")
        for f in by_struct[s["id"]]:
            base, n = parse_type(f["type"])
            rt = TYPES[base][1]
            fname = "r#" + f["field"] if f["field"] in ("type", "ref", "move", "loop") else f["field"]
            out.append(f"    pub {fname}: {f'[{rt}; {n}]' if n else rt},\n")
        out.append("}\n")
        out.append(f"const _: () = assert!(core::mem::size_of::<{s['id']}>() == {s['size']});\n")
        out.append(f"impl Default for {s['id']} {{ fn default() -> Self {{ unsafe {{ core::mem::zeroed() }} }} }}\n")
    out.append("\n/// (struct, field, offset) for the layout self-test.\npub const FIELD_OFFSETS: &[(&str, &str, usize)] = &[\n")
    for s in structs["rows"]:
        for f in by_struct[s["id"]]:
            out.append(f'    ("{s["id"]}", "{f["field"]}", {f["offset"]}),\n')
    out.append("];\n\n/// (struct, size)\npub const STRUCT_SIZES: &[(&str, usize)] = &[\n")
    for s in structs["rows"]:
        out.append(f'    ("{s["id"]}", core::mem::size_of::<{s["id"]}>()),\n')
    out.append("];\n")
    return "".join(out)


ENUM_TYPES = {"InputType": "ushort", "HurtKind": "ushort", "DigMaterial": "byte"}
FLAG_ENUMS = {"HostFlags", "GuestFlags", "HitFlags", "ActorFlags", "TriFlags"}


def gen_cs_enums(enums):
    out, groups = [], {}
    for e in enums["rows"]:
        groups.setdefault(e["enum"], []).append(e)
    for name, rows in groups.items():
        if name == "Overlay":
            for r in rows:
                out.append(f"    public const uint Overlay{r['name']} = {r['value']};\n")
            continue
        flags = "    [System.Flags]\n" if name in FLAG_ENUMS else ""
        out.append(f"\n{flags}    public enum {name} : {ENUM_TYPES.get(name, 'uint')}\n    {{\n")
        for r in rows:
            note = f" // {r['note']}" if r.get("note") else ""
            out.append(f"        {r['name']} = {r['value']},{note}\n")
        out.append("    }\n")
    return "".join(out)


def gen_cs_proto(structs, by_struct, regions):
    out = [HEADER, "using System.Runtime.InteropServices;\n\nnamespace RiskOfCraft.Link;\n\n",
           "/// <summary>The shared-memory layout, generated from design/protocol_*.json (the Rust link uses the same sheet).</summary>\n",
           "internal static unsafe partial class Proto\n{\n"]
    for c in regions["constants"]["rows"]:
        v = c["value"]
        if c["id"] == "MappingName":
            out.append(f'    public const string MappingName = "{v.replace(chr(92), chr(92) * 2)}";\n')
        elif c["id"] in ("Magic", "Version"):
            out.append(f"    public const uint {c['id']} = {v};\n")
        else:
            out.append(f"    public const long {c['id']} = {v};\n")
    for r in regions["rows"]:
        out.append(f"    public const long Off{r['id']} = {r['offset']};\n")
        if r["entries"]:
            out.append(f"    public const int {r['id']}Entries = {r['entries']};\n")
    for s in structs["rows"]:
        out.append(f"\n    /// <summary>{s['c_name']} ({s['direction']}): {s['used_by']}</summary>\n")
        out.append(f"    [StructLayout(LayoutKind.Sequential)]\n    public struct {s['id']}\n    {{\n")
        for f in by_struct[s["id"]]:
            base, n = parse_type(f["type"])
            ct = TYPES[base][2]
            note = f" // {f['note']}" if f.get("note") else ""
            if n:
                out.append(f"        public fixed {ct} {f['field']}[{n}];{note}\n")
            else:
                out.append(f"        public {ct} {f['field']};{note}\n")
        out.append("    }\n")
    out.append(gen_cs_enums(load("protocol_enums.json")))
    out.append("\n    /// <summary>(struct, size) as the sheet fixes them.</summary>\n    public static readonly (string name, int expected, int actual)[] Sizes =\n    {\n")
    for s in structs["rows"]:
        out.append(f'        ("{s["id"]}", {s["size"]}, sizeof({s["id"]})),\n')
    out.append("    };\n\n    /// <summary>(struct, field, offset) as the sheet fixes them.</summary>\n    public static readonly (System.Type type, string field, int expected)[] Offsets =\n    {\n")
    for s in structs["rows"]:
        for f in by_struct[s["id"]]:
            out.append(f'        (typeof({s["id"]}), "{f["field"]}", {f["offset"]}),\n')
    out.append("    };\n}\n")
    return "".join(out)


def cs_str(s):
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def gen_cs_tables(game):
    out = [HEADER, "using System.Collections.Generic;\n\nnamespace RiskOfCraft.Game;\n\n",
           "/// <summary>Gameplay tables generated from design/gameplay.json: one entry per sheet row.</summary>\n",
           "internal static class Tables\n{\n"]
    out.append("    public readonly struct ScalingRow\n    {\n        public readonly string Id, Ror2Stat, Ror2Base, McTarget, Command, Mode;\n        public readonly float McBase, MinChange;\n")
    out.append("        public ScalingRow(string id, string ror2Stat, string ror2Base, string mcTarget, float mcBase, string command, float minChange, string mode)\n")
    out.append("        { Id = id; Ror2Stat = ror2Stat; Ror2Base = ror2Base; McTarget = mcTarget; McBase = mcBase; Command = command; MinChange = minChange; Mode = mode; }\n    }\n\n")
    out.append("    public static readonly ScalingRow[] Scaling =\n    {\n")
    for r in game["scaling"]["rows"]:
        out.append(f"        new({cs_str(r['id'])}, {cs_str(r['ror2_stat'])}, {cs_str(r['ror2_base'])}, {cs_str(r['mc_target'])}, {r['mc_base']}f, {cs_str(r['command'])}, {r['min_change']}f, {cs_str(r.get('mode', 'ratio'))}),\n")
    out.append("    };\n\n")
    for r in game["damage"]["rows"]:
        name = "".join(p.capitalize() for p in r["id"].split("_"))
        out.append(f"    /// <summary>{r['note']}</summary>\n    public const float {name} = {r['value']}f;\n")
    out.append("\n    /// <summary>RoR2 interactable name -> Minecraft loot table.</summary>\n    public static readonly Dictionary<string, string> ChestLoot = new()\n    {\n")
    for r in game["loot_chests"]["rows"]:
        out.append(f"        [{cs_str(r['ror2_object'])}] = {cs_str(r['mc_loot_table'])}, // {r['id']}\n")
    out.append("    };\n\n    /// <summary>RoR2 body name -> (Minecraft loot table, XP points). \"*\" is the fallback.</summary>\n")
    out.append("    public static readonly Dictionary<string, (string table, int xp)> MonsterLoot = new()\n    {\n")
    for r in game["monsters"]["rows"]:
        out.append(f"        [{cs_str(r['ror2_body'])}] = ({cs_str(r['mc_loot_table'])}, {r['xp']}), // {r['id']}\n")
    out.append("    };\n\n    /// <summary>RoR2 SurfaceDef name -> what digging into it gives. \"*\" is the fallback.</summary>\n")
    out.append("    public static readonly Dictionary<string, (RiskOfCraft.Link.Proto.DigMaterial material, bool diggable, bool terrain)> Surfaces = new()\n    {\n")
    for r in game["surfaces"]["rows"]:
        out.append(f"        [{cs_str(r['ror2_surface'])}] = (RiskOfCraft.Link.Proto.DigMaterial.{r['dig_material']}, {str(r['diggable']).lower()}, {str(r['terrain']).lower()}), // {r['id']}\n")
    out.append("    };\n\n    public static readonly (string item, int count)[] StarterKit =\n    {\n")
    for r in game["starter_kit"]["rows"]:
        out.append(f"        ({cs_str(r['mc_item'])}, {r['count']}), // {r['id']}\n")
    out.append("    };\n\n    /// <summary>Minecraft command templates by id; {name} placeholders.</summary>\n    public static readonly Dictionary<string, string> Commands = new()\n    {\n")
    for r in game["commands"]["rows"]:
        out.append(f"        [{cs_str(r['id'])}] = {cs_str(r['template'])},\n")
    out.append("    };\n\n    /// <summary>Host-reserved keys (UnityEngine.KeyCode names).</summary>\n")
    for r in game["keybinds"]["rows"]:
        name = "".join(p.capitalize() for p in r["id"].split("_"))
        out.append(f"    public const string Key{name} = {cs_str(r['key'])}; // {r['goes_to']}: {r['does']}\n")
    out.append("\n")
    for r in game["messages"]["rows"]:
        name = "".join(p.capitalize() for p in r["id"].split("_"))
        out.append(f"    public const short Msg{name} = {r['msg_type']}; // {r['direction']}: {r['fields']}\n")
    out.append("}\n")
    return "".join(out)


def gen_java(regions, structs):
    consts = {c["id"]: c["value"] for c in regions["constants"]["rows"]}
    cr = next(r for r in regions["rows"] if r["id"] == "CommandRing")
    size = next(s["size"] for s in structs["rows"] if s["id"] == "HostCommand")
    return (HEADER + "package dev.skycraft.link;\n\n"
            "/** RiskOfCraft's additions to the protocol (design/protocol_regions.json). */\n"
            "public final class RocProto {\n"
            f"\tpublic static final int VERSION = {consts['Version']};\n"
            f"\tpublic static final String MAPPING_NAME = \"{consts['MappingName'].replace(chr(92), chr(92) * 2)}\";\n"
            f"\tpublic static final long OFF_COMMAND_RING = {cr['offset']};\n"
            f"\tpublic static final int COMMAND_RING_ENTRIES = {cr['entries']};\n"
            f"\tpublic static final long COMMAND_BYTES = {size};\n"
            "\tpublic static final long CR_HEAD = 0x00, CR_TAIL = 0x40, CR_DATA = 0x80;\n"
            "\tpublic static final long HC_ID = 0, HC_LENGTH = 4, HC_TEXT = 8;\n"
            f"\tpublic static final int HC_TEXT_BYTES = {size - 8};\n\n"
            "\tprivate RocProto() {\n\t}\n}\n")


def gen_loot_tables(game):
    """Guest data pack: one chest loot table per chest tier, and the rare pool."""
    files = {}
    rare_entries = []
    for r in game["rare_items"]["rows"]:
        entry = {"type": "minecraft:item", "name": "minecraft:" + r["mc_item"], "weight": r["rare_weight"]}
        if r["count"] > 1:
            entry["functions"] = [{"function": "minecraft:set_count", "count": r["count"]}]
        rare_entries.append(entry)
    files["rare"] = {"type": "minecraft:chest", "pools": [{"rolls": 1, "entries": rare_entries}]}
    for t in game["chest_tiers"]["rows"]:
        entries = [{"type": "minecraft:loot_table", "value": "minecraft:" + p["mc_loot_table"], "weight": p["weight"]}
                   for p in game["loot_pools"]["rows"] if p["tier"] == t["tier"]]
        files[t["id"]] = {"type": "minecraft:chest", "pools": [
            {"rolls": {"type": "minecraft:uniform", "min": t["rolls_min"], "max": t["rolls_max"]}, "entries": entries},
            {"rolls": 1, "entries": [
                {"type": "minecraft:loot_table", "value": "riskofcraft:chests/rare", "weight": t["rare_weight"]},
                {"type": "minecraft:empty", "weight": max(1, 100 - t["rare_weight"])}]},
        ]}
        if t.get("eye_weight", 0) > 0:
            files[t["id"]]["pools"].append({"rolls": 1, "entries": [
                {"type": "minecraft:item", "name": "minecraft:ender_eye", "weight": t["eye_weight"],
                 "functions": [{"function": "minecraft:set_count", "count": {"type": "minecraft:uniform", "min": 1, "max": 2}}]},
                {"type": "minecraft:empty", "weight": max(1, 100 - t["eye_weight"])}]})
    return {f"fabric/src/main/resources/data/riskofcraft/loot_table/chests/{k}.json": json.dumps(v, indent=2) + "\n" for k, v in files.items()}


def gen_cs_tables2(game):
    out = [HEADER, "using System.Collections.Generic;\n\nnamespace RiskOfCraft.Game;\n\n",
           "/// <summary>Portals, custom stages, chests, rare items, the lunar shop and the dragon (design/gameplay.json).</summary>\n",
           "internal static class Tables2\n{\n"]
    out.append("    public sealed record Portal(string Id, string McBlock, float HoldSeconds, string CustomStage);\n")
    out.append("    public static readonly Portal[] Portals =\n    {\n")
    for r in game["portals"]["rows"]:
        out.append(f"        new({cs_str(r['id'])}, {cs_str(r['mc_block'])}, {r['hold_seconds']}f, {cs_str(r['custom_stage'])}),\n")
    out.append("    };\n\n")
    out.append("    public sealed record CustomStage(string Id, string BaseScene, string Title, string Subtitle, string TopTexture, string SideTexture, string AccentTexture, UnityEngine.Color Fog, float InteractableCreditMultiplier, int McChests, int ChestTierBonus, string Decor, bool EndsRun, float SelfLight, UnityEngine.Color Light, float FogStrength);\n")
    out.append("    public static readonly Dictionary<string, CustomStage> CustomStages = new()\n    {\n")
    for r in game["custom_stages"]["rows"]:
        fr, fg, fb = (float(x) for x in r["fog_rgb"].split(","))
        out.append(f"        [{cs_str(r['id'])}] = new({cs_str(r['id'])}, {cs_str(r['base_scene'])}, {cs_str(r['title'])}, {cs_str(r['subtitle'])}, {cs_str(r['top_texture'])}, {cs_str(r['side_texture'])}, {cs_str(r['accent_texture'])}, new UnityEngine.Color({fr}f, {fg}f, {fb}f), {r['interactable_credit_multiplier']}f, {r['mc_chests']}, {r['chest_tier_bonus']}, {cs_str(r['decor'])}, {str(r['ends_run']).lower()}, {r.get('self_light', 0)}f, new UnityEngine.Color({', '.join(str(float(x)) + 'f' for x in r.get('light_rgb', '1,1,1').split(','))}), {r.get('fog_strength', 1.0)}f),\n")
    out.append("    };\n\n")
    out.append("    public sealed record ChestTier(string Id, int Tier, int Chests);\n    public static readonly ChestTier[] ChestTiers =\n    {\n")
    for r in game["chest_tiers"]["rows"]:
        out.append(f"        new({cs_str(r['id'])}, {r['tier']}, {r['chests']}),\n")
    out.append("    };\n\n")
    out.append("    public sealed record RareItem(string Id, string McItem, int Count, int LunarCost, string DisplayName);\n    public static readonly RareItem[] RareItems =\n    {\n")
    for r in game["rare_items"]["rows"]:
        out.append(f"        new({cs_str(r['id'])}, {cs_str(r['mc_item'])}, {r['count']}, {r['lunar_cost']}, {cs_str(r['display_name'])}),\n")
    out.append("    };\n\n")
    out.append("    public sealed record Villager(string Id, string Profession, int Level, string BiomeType);\n    public static readonly Villager[] Villagers =\n    {\n")
    for r in game["villagers"]["rows"]:
        out.append(f"        new({cs_str(r['id'])}, {cs_str(r['profession'])}, {r['level']}, {cs_str(r['biome_type'])}),\n")
    out.append("    };\n\n")
    out.append("    public sealed record NetherMob(string Id, string McEntity, int Weight, bool InAir);\n    public static readonly NetherMob[] NetherMobs =\n    {\n")
    for r in game["nether_mobs"]["rows"]:
        out.append(f"        new({cs_str(r['id'])}, {cs_str(r['mc_entity'])}, {r['weight']}, {str(r['in_air']).lower()}),\n")
    out.append("    };\n\n")
    for table, name in (("lunar_shop", "LunarShop"), ("end_portal_frame", "EndPortalFrame"), ("dragon", "Dragon"), ("nether_spawning", "NetherSpawning")):
        out.append(f"    public static readonly Dictionary<string, float> {name} = new()\n    {{\n")
        for r in game[table]["rows"]:
            out.append(f"        [{cs_str(r['id'])}] = {r['value']}f, // {r['note']}\n")
        out.append("    };\n\n")
    out.append("}\n")
    return "".join(out)


def write(path, text):
    full = os.path.join(ROOT, path)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    old = open(full, encoding="utf-8").read() if os.path.exists(full) else None
    if old != text:
        with open(full, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        print(f"  wrote {path}")
    else:
        print(f"  same  {path}")


def generate():
    structs = load("protocol_structs.json")
    fields = load("protocol_fields.json")
    regions = load("protocol_regions.json")
    game = load("gameplay.json")
    by_struct = {}
    for f in fields["rows"]:
        by_struct.setdefault(f["struct"], []).append(f)
    write("link/src/proto.rs", gen_rust(structs, by_struct, regions))
    write("ror2/src/Generated/Proto.g.cs", gen_cs_proto(structs, by_struct, regions))
    write("ror2/src/Generated/Tables.g.cs", gen_cs_tables(game))
    write("fabric/src/main/java/dev/skycraft/link/RocProto.java", gen_java(regions, structs))
    write("ror2/src/Generated/Tables2.g.cs", gen_cs_tables2(game))
    for path, text in gen_loot_tables(game).items():
        write(path, text)


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "preflight"
    if cmd == "generate":
        # Code generation only needs the protocol and gameplay sheets to be consistent; module
        # existence is what the generated code and the hand-written modules fill in.
        generate()
        return 0
    problems, unverified = preflight()
    for p in problems:
        print("  FAIL", p)
    print(f"preflight: {len(problems)} problem(s); {len(unverified)} system(s) not verified in game yet: {', '.join(unverified) or 'none'}")
    if cmd == "release" and unverified:
        return 1
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
