#!/usr/bin/env python3
"""Cruza las FSM volcadas (decomp/fsm, de tools/dump-fsms.sh) con el mod y escribe un informe en decomp/fsm-analysis/.

Lo que sale (un .txt por apartado, en español):
  jefes.txt         cada enemigo con 200 de vida o más: sus FSM, lo que crea, a quién avisa y sus transiciones raras
  acciones.txt      cada tipo de acción que usan los enemigos, cuántas veces y si el mod lo nombra en algún sitio
  crear.txt         acciones que crean o lanzan objetos en FSM de enemigos, con el prefab y si EnemySync las conoce
  componentes.txt   en qué salas sale cada componente de C# que importa al coop (los de docs/review-backlog.md)
  plataformas.txt   objetos sólidos que no son enemigos y que una FSM mueve (candidatos a desincronizarse)
  llamadas.txt      métodos del caballero y del juego que llaman las FSM (CallMethodProper, SendMessage...)
  zero-hp.txt       FSM de enemigos que atienden ZERO HP (muertes y cambios de fase por su lógica)

Uso: python tools/analyze-fsms.py
"""
import collections
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DUMPS = os.path.join(ROOT, "decomp", "fsm")
OUT = os.path.join(ROOT, "decomp", "fsm-analysis")
MOD = os.path.join(ROOT, "src", "HKMultiplayer")

HEADER = re.compile(r'^== (?:prefab (?P<prefab>.+?)|(?P<scene>[^:]+):(?P<path>.+?)) \[fsm "(?P<fsm>.*)"\](?P<rest>.*)$')
ACTION = re.compile(r'^    (?P<type>[A-Za-z0-9_]+)(?P<off> \(desactivada\))?:(?P<fields>.*)$')
STATE = re.compile(r'^  \[(?P<state>.*?)\](?: -> (?P<trans>.*))?$')
HP = re.compile(r'HealthManager\(hp=(-?\d+)\)')

SPAWN = re.compile(r'Spawn|Create|Fling|Instantiate|Summon|Clone|Shoot|Fire', re.I)
MOVE = re.compile(r'^(iTween|MoveTowards|MoveTo|Translate|SetPosition|SetVelocity|SetVelocity2d|AccelerateVelocity|'
                  r'ChaseObject|FollowObject|Curve|EaseFloat|AnimatePosition|SetRigidbody|AddForce)', re.I)
SOLID = re.compile(r'BoxCollider2D|PolygonCollider2D|EdgeCollider2D|CircleCollider2D|CompositeCollider2D')

# Componentes de C# de la lista de fallos: dónde salen en el juego
COMPONENTS = [
    "BridgeLever", "FlipPlatform", "DropPlatform", "JellyEgg", "SlopePlat", "ScuttlerControl", "EnemySpawner",
    "Walker", "Climber", "Crawler", "ConveyorBelt", "ConveyorZone", "VinePlatform", "VinePlatformCut", "BossStatueLever",
    "StalactiteControl", "DreamPlatform", "BossDoorCage", "KillOnContact", "BigCentipede", "DeactivateIfPlayerdataFalseDelayed",
    "DeactivateIfPlayerdataTrue", "DeactivateIfPlayerdataFalse", "ActivateIfPlayerdataTrue", "ActivateIfPlayerdataFalse",
    "RespawnTrigger", "PersistentIntItem", "BounceShroom", "DreamPlant", "DreamPlantOrb", "LiftPlatform", "Breakable",
    "BreakableObject", "GrassCut", "Grass", "HealthCocoon", "SpawnJarControl", "EnemyBullet", "ExtraDamageable",
    "Recoil", "ChaseObject", "ChaseObjectGround", "ChaseObjectVertical", "FaceHero", "TinkEffect", "HazardRespawnTrigger",
    "CameraLockArea", "TransitionPoint", "BossSceneController", "BossStatue", "BossSequenceDoor",
]


def read_mod_source():
    text = []
    for folder, _, files in os.walk(MOD):
        if os.sep + "bin" in folder or os.sep + "obj" in folder:
            continue
        for name in files:
            if name.endswith(".cs"):
                with open(os.path.join(folder, name), encoding="utf-8", errors="replace") as handle:
                    text.append(handle.read())
    return "\n".join(text)


def parse(path):
    """Devuelve (componentes de la sala {tipo: (n, ejemplo)}, lista de FSM)."""
    components = {}
    fsms = []
    current = None
    state = None
    section = None
    with open(path, encoding="utf-8", errors="replace") as handle:
        for raw in handle:
            line = raw.rstrip("\n")
            if line.startswith("## componentes de la sala"):
                section = "components"
                continue
            if section == "components":
                if not line.strip():
                    section = None
                    continue
                match = re.match(r"^  (\S+) x(\d+) \(p\. ej\. (.*)\)$", line)
                if match:
                    components[match.group(1)] = (int(match.group(2)), match.group(3))
                continue

            header = HEADER.match(line)
            if header:
                current = {
                    "file": os.path.basename(path)[:-4],
                    "scene": header.group("scene") or "prefab",
                    "path": header.group("path") or header.group("prefab"),
                    "fsm": header.group("fsm"),
                    "prefab": header.group("prefab") is not None,
                    "rest": header.group("rest"),
                    "components": "",
                    "copy_of": None,
                    "states": collections.OrderedDict(),
                    "globals": "",
                    "vars": [],
                }
                fsms.append(current)
                state = None
                continue
            if current is None:
                continue
            if line.startswith("  componentes: "):
                current["components"] = line[len("  componentes: "):]
            elif line.startswith("  (igual que "):
                current["copy_of"] = line[len("  (igual que "):-1]
            elif line.startswith("  globales: "):
                current["globals"] = line[len("  globales: "):]
            elif line.startswith("  var "):
                current["vars"].append(line[6:])
            else:
                match = STATE.match(line)
                if match:
                    state = {"trans": match.group("trans") or "", "actions": []}
                    current["states"][match.group("state")] = state
                    continue
                match = ACTION.match(line)
                if match and state is not None:
                    state["actions"].append((match.group("type"), bool(match.group("off")), match.group("fields").strip()))
    return components, fsms


def key_of(fsm):
    if fsm["prefab"]:
        return f'prefab {fsm["path"].split("/")[-1]} [{fsm["fsm"]}]'
    return f'{fsm["scene"]}:{fsm["path"]} [{fsm["fsm"]}]'


def field(fields, name):
    match = re.search(r"(?:^| )" + re.escape(name) + r"=(\S+(?: \S+)*?)(?= \w+=|$)", fields)
    return match.group(1) if match else ""


def main():
    if not os.path.isdir(DUMPS):
        sys.exit("No hay volcados en decomp/fsm: ejecuta antes bash tools/dump-fsms.sh")
    os.makedirs(OUT, exist_ok=True)
    mod = read_mod_source()
    enemy_sync = open(os.path.join(MOD, "Game", "Scene", "EnemySync.cs"), encoding="utf-8").read()

    files = sorted(f for f in os.listdir(DUMPS) if f.endswith(".txt") and not f.startswith("_worker"))
    by_key = {}
    all_fsms = []
    scene_components = collections.defaultdict(list)
    for name in files:
        components, fsms = parse(os.path.join(DUMPS, name))
        for type_name, (count, example) in components.items():
            scene_components[type_name].append((name[:-4], count, example))
        for fsm in fsms:
            by_key.setdefault((fsm["file"], key_of(fsm)), fsm)
            all_fsms.append(fsm)

    # Las copias toman los estados de su original (en el mismo archivo)
    for fsm in all_fsms:
        if fsm["copy_of"] and not fsm["states"]:
            original = by_key.get((fsm["file"], fsm["copy_of"]))
            if original:
                fsm["states"] = original["states"]
                fsm["globals"] = original["globals"]

    # Qué objetos son enemigos (tienen HealthManager) y qué FSM cuelgan de ellos
    enemy_paths = collections.defaultdict(dict)
    for fsm in all_fsms:
        hp = HP.search(fsm["components"])
        if hp and not fsm["prefab"]:
            enemy_paths[fsm["file"]][fsm["path"]] = int(hp.group(1))

    def enemy_of(fsm):
        if fsm["prefab"]:
            return HP.search(fsm["components"]) and fsm["path"] or None
        paths = enemy_paths.get(fsm["file"], {})
        best = None
        for path in paths:
            if fsm["path"] == path or fsm["path"].startswith(path + "/"):
                if best is None or len(path) > len(best):
                    best = path
        return best

    def mentioned(type_name):
        return re.search(r"\b" + re.escape(type_name) + r"\b", mod) is not None

    # ---- acciones.txt
    usage = collections.Counter()
    usage_enemy = collections.Counter()
    fsm_count = collections.defaultdict(set)
    example = {}
    for fsm in all_fsms:
        enemy = enemy_of(fsm)
        for state_name, state in fsm["states"].items():
            for type_name, off, _ in state["actions"]:
                if off:
                    continue
                usage[type_name] += 1
                if enemy:
                    usage_enemy[type_name] += 1
                    fsm_count[type_name].add((fsm["file"], fsm["path"], fsm["fsm"]))
                    example.setdefault(type_name, f'{fsm["file"]}: {fsm["path"]} [{fsm["fsm"]}] estado {state_name}')
    with open(os.path.join(OUT, "acciones.txt"), "w", encoding="utf-8") as out:
        out.write("# Acciones en FSM de enemigos (el objeto o un padre tiene HealthManager, o un prefab con vida).\n")
        out.write("# Columnas: veces en enemigos | FSM de enemigos distintas | veces en total | ¿el mod la nombra? | ejemplo\n\n")
        for type_name, count in sorted(usage_enemy.items(), key=lambda kv: (mentioned(kv[0]), -kv[1])):
            out.write(f'{count:6d} | {len(fsm_count[type_name]):4d} | {usage[type_name]:6d} | '
                      f'{"sí" if mentioned(type_name) else "NO"} | {type_name} | {example[type_name]}\n')

    # ---- crear.txt
    with open(os.path.join(OUT, "crear.txt"), "w", encoding="utf-8") as out:
        out.write("# Acciones que crean o lanzan objetos en FSM de enemigos.\n")
        out.write("# ¿EnemySync? = el tipo de acción se nombra en EnemySync.cs (MuteSpawns, seguimiento de lo creado...).\n\n")
        spawners = collections.defaultdict(list)
        for fsm in all_fsms:
            if not enemy_of(fsm):
                continue
            for state_name, state in fsm["states"].items():
                for type_name, off, fields in state["actions"]:
                    if off or not SPAWN.search(type_name):
                        continue
                    prefab = (field(fields, "gameObject") or field(fields, "objectToSpawn") or field(fields, "prefab") or
                              field(fields, "gameObjectToSpawn") or "?")
                    spawners[type_name].append(f'{fsm["file"]}: {fsm["path"]} [{fsm["fsm"]}] {state_name} -> {prefab[:80]}')
        for type_name in sorted(spawners, key=lambda t: (t in enemy_sync, -len(spawners[t]))):
            known = "sí" if re.search(r"\b" + re.escape(type_name) + r"\b", enemy_sync) else "NO"
            out.write(f"## {type_name} · {len(spawners[type_name])} usos · ¿EnemySync? {known}\n")
            for line in spawners[type_name][:40]:
                out.write(f"  {line}\n")
            if len(spawners[type_name]) > 40:
                out.write(f"  ... y {len(spawners[type_name]) - 40} más\n")
            out.write("\n")

    # ---- jefes.txt
    with open(os.path.join(OUT, "jefes.txt"), "w", encoding="utf-8") as out:
        out.write("# Jefes: enemigos con 200 de vida o más (al cargar la sala), o con una FSM que usa GGCheckIfBossScene\n")
        out.write("# (los que salen en el Salón de los Dioses; el Falso Caballero tiene 65 por fase), con sus FSM y las de\n")
        out.write("# sus hijos.\n\n")
        gg = {(f["file"], enemy_of(f)) for f in all_fsms if not f["prefab"] and enemy_of(f) and
              any(t == "GGCheckIfBossScene" for s in f["states"].values() for t, _, _ in s["actions"])}
        bosses = []
        for file_name, paths in enemy_paths.items():
            for path, hp in paths.items():
                if hp >= 200 or (file_name, path) in gg:
                    bosses.append((file_name, path, hp))
        seen_templates = set()
        for file_name, path, hp in sorted(bosses):
            owned = [f for f in all_fsms if f["file"] == file_name and not f["prefab"] and
                     (f["path"] == path or f["path"].startswith(path + "/"))]
            out.write(f"## {file_name}: {path} (hp {hp}) · {len(owned)} FSM\n")
            for fsm in owned:
                types = collections.Counter(t for s in fsm["states"].values() for t, off, _ in s["actions"] if not off)
                template = (fsm["fsm"], tuple(sorted(types)))
                repeated = template in seen_templates
                seen_templates.add(template)
                out.write(f'  [{fsm["fsm"]}] {fsm["path"]} · {len(fsm["states"])} estados{" (como otro jefe ya listado)" if repeated else ""}\n')
                if repeated:
                    continue
                if fsm["globals"]:
                    out.write(f'    globales: {fsm["globals"][:300]}\n')
                unknown = sorted(t for t in types if not mentioned(t))
                if unknown:
                    out.write(f'    acciones que el mod no nombra: {", ".join(unknown)}\n')
                for state_name, state in fsm["states"].items():
                    for type_name, off, fields in state["actions"]:
                        if off:
                            continue
                        if SPAWN.search(type_name):
                            out.write(f"    crea  {state_name}: {type_name} {fields[:160]}\n")
                        elif type_name in ("SendEventByName", "SendEvent", "SendEventToRegister", "BroadcastEvent") and \
                                re.search(r"Knight|Hero|BROADCAST|GameManager|Camera", fields):
                            out.write(f"    avisa {state_name}: {type_name} {fields[:160]}\n")
                        elif type_name.startswith("SetPlayerData") or type_name.startswith("IncrementPlayerData") or \
                                type_name in ("CallMethodProper", "CallMethod", "SendMessage", "SetHP", "SetInvincible"):
                            out.write(f"    llama {state_name}: {type_name} {fields[:160]}\n")
            out.write("\n")

    # ---- componentes.txt
    with open(os.path.join(OUT, "componentes.txt"), "w", encoding="utf-8") as out:
        out.write("# En qué salas sale cada componente de C# que importa al coop (de docs/review-backlog.md).\n\n")
        for type_name in COMPONENTS:
            entries = scene_components.get(type_name, [])
            out.write(f"## {type_name} · {len(entries)} salas\n")
            for scene, count, ex in entries[:60]:
                out.write(f"  {scene} x{count} ({ex})\n")
            if len(entries) > 60:
                out.write(f"  ... y {len(entries) - 60} salas más\n")
            out.write("\n")
        out.write("# Todos los componentes del juego que salen en alguna sala (tipo: salas)\n")
        for type_name in sorted(scene_components):
            out.write(f"  {type_name}: {len(scene_components[type_name])}\n")

    # ---- plataformas.txt
    with open(os.path.join(OUT, "plataformas.txt"), "w", encoding="utf-8") as out:
        out.write("# Objetos con colisión (no enemigos) que una FSM suya mueve. Sin saber si la colisión es trigger: revisar.\n\n")
        for fsm in all_fsms:
            if fsm["prefab"] or enemy_of(fsm) or not SOLID.search(fsm["components"]):
                continue
            moves = sorted({t for s in fsm["states"].values() for t, off, _ in s["actions"] if not off and MOVE.match(t)})
            if moves:
                out.write(f'{fsm["file"]}: {fsm["path"]} [{fsm["fsm"]}] · {", ".join(moves)} · {fsm["components"][:160]}\n')

    # ---- llamadas.txt
    calls = collections.defaultdict(set)
    for fsm in all_fsms:
        for state_name, state in fsm["states"].items():
            for type_name, off, fields in state["actions"]:
                if off or type_name not in ("CallMethodProper", "CallMethod", "SendMessage", "CallStaticMethod",
                                            "SendMessageV2", "InvokeMethod"):
                    continue
                method = field(fields, "methodName") or field(fields, "functionCall") or fields[:80]
                behaviour = field(fields, "behaviour") or field(fields, "className") or ""
                calls[f"{behaviour}.{method}"].add(f'{fsm["file"]}: {fsm["path"]} [{fsm["fsm"]}] {state_name}')
    with open(os.path.join(OUT, "llamadas.txt"), "w", encoding="utf-8") as out:
        out.write("# Métodos que llaman las FSM (componente.método): lo que hacen sin pasar por acciones normales.\n\n")
        for name in sorted(calls, key=lambda n: -len(calls[n])):
            places = sorted(calls[name])
            out.write(f"## {name} · {len(places)}\n")
            for place in places[:15]:
                out.write(f"  {place}\n")
            if len(places) > 15:
                out.write(f"  ... y {len(places) - 15} más\n")

    # ---- zero-hp.txt
    with open(os.path.join(OUT, "zero-hp.txt"), "w", encoding="utf-8") as out:
        out.write("# FSM de enemigos que cambian de estado con ZERO HP (o lo tienen como global): a qué estado van.\n\n")
        for fsm in all_fsms:
            if not enemy_of(fsm):
                continue
            targets = [f"{name}: {s['trans']}" for name, s in fsm["states"].items() if "ZERO HP" in s["trans"]]
            if "ZERO HP" in fsm["globals"]:
                targets.append("globales: " + fsm["globals"])
            if targets:
                out.write(f'{fsm["file"]}: {fsm["path"]} [{fsm["fsm"]}] · ' + " | ".join(t[:200] for t in targets) + "\n")

    print(f"{len(files)} volcados, {len(all_fsms)} FSM -> {OUT}")


if __name__ == "__main__":
    main()
