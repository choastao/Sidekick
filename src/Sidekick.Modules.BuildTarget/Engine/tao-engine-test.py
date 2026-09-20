"""C1b 联调：直接驱动 tao-engine-server.lua，验证试穿机制真的生效。

关键自校验：
  1) 试穿「BD 里原来那件头盔」→ DPS/EHP 必须与基线**完全相同**（同一件物品，机器应该给出同一个数）
  2) 试穿「把生命词缀改大的同一件头盔」→ EHP 必须**变化**（否则说明 rep 机制没生效，只是把原值返回了）
用法：python tao-engine-test.py
"""

import html
import json
import os
import re
import subprocess
import sys
import time

BASE = r"C:\Users\Administrator\poe2-work\pob2-engine"
ENGINE_SRC = os.path.join(BASE, "repo", "src")
LUAJIT = os.path.join(BASE, "luajit", "luajit.exe")
SERVER = os.path.join(BASE, "tao-engine-server.lua")

xml = open(os.path.join(BASE, "build.xml"), encoding="utf-8").read()

# --- 从 BD 里抽出 Helmet 槽与它的物品原文 ---
# ⚠ 属性顺序是 itemId 在前、name 在后（别写成 name 在前的正则，匹配不到 —— 已踩）
slots = {}
for tag in re.findall(r"<Slot[^>]*>", xml):
    nm = re.search(r'name="([^"]+)"', tag)
    iid = re.search(r'itemId="(\d+)"', tag)
    if nm and iid:
        slots[nm.group(1)] = iid.group(1)
items = dict(re.findall(r"<Item id=\"(\d+)\">(.*?)</Item>", xml, re.S))
print("BD 槽位:", {k: v for k, v in list(slots.items())[:14]}, "| 物品数:", len(items))

helmet_id = slots.get("Helmet")
helmet_raw = html.unescape(items.get(helmet_id, "")) if helmet_id else ""
print(f"\nHelmet itemId={helmet_id} 原文（前 300 字）:\n{helmet_raw[:300]}")

if not helmet_raw:
    print("✗ 没拿到头盔物品原文，无法测试穿")
    sys.exit(1)

# 造一件"改过的"头盔：给词缀区**追加**一条显式词缀（PoB raw 格式里 Implicits 之后就是显式词缀区）
def boost(text: str, line: str = "+500 to maximum Life") -> str:
    return text.rstrip("\n") + "\n" + line + "\n"

helmet_boosted = boost(helmet_raw)

# 另一件完全不同的物品（Body Armour）—— 拿去试穿 Helmet 槽，数字必须大变，
# 这是"rep 机制真的替换了槽位"最强的一条证明。
body_id = slots.get("Body Armour")
body_raw = html.unescape(items.get(body_id, "")) if body_id else ""
print(f"\n改过的头盔（追加一条 +500 to maximum Life）:\n{helmet_boosted[-260:]}")
print(f"\n对照组：Body Armour 那件（itemId={body_id}，前 120 字）:\n{body_raw[:120]}")

# --- 起 helper ---
proc = subprocess.Popen(
    [LUAJIT, SERVER],
    cwd=ENGINE_SRC,                      # ⚠ 必须是 pob/src（HeadlessWrapper 相对 dofile）
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.DEVNULL,
    text=True,
    encoding="utf-8",
    bufsize=1,
)

next_id = 0

def send(method, params=None, timeout=180):
    global next_id
    next_id += 1
    request = {"id": next_id, "method": method}
    if params is not None:
        request["params"] = params
    proc.stdin.write(json.dumps(request) + "\n")
    proc.stdin.flush()

    deadline = time.time() + timeout
    while time.time() < deadline:
        line = proc.stdout.readline()
        if not line:
            raise RuntimeError("helper 关掉了 stdout（可能崩了）")
        line = line.strip()
        if line.startswith("{") and '"id"' in line:
            return json.loads(line)
    raise TimeoutError(f"{method} 超时")

def call(method, params=None, label=None):
    t0 = time.time()
    resp = send(method, params)
    ms = (time.time() - t0) * 1000
    tag = label or method
    if not resp.get("ok"):
        print(f"  {tag:<22} ✗ {resp.get('error')}  ({ms:.0f} ms)")
        return None
    print(f"  {tag:<22} OK  ({ms:.0f} ms)")
    return resp.get("result")

print("\n--- 1) ping / load_build ---")
print("  ping:", call("ping"))
loaded = call("load_build", {"xml": xml, "name": "tao-test"}, "load_build")
if not loaded:
    print("✗ load_build 失败")
    sys.exit(1)
base = loaded["stats"]
print(f"  基线: DPS={base['dps']:.0f}  EHP={base['ehp']:.0f}  Life={base['life']}")

print("\n--- 2) stats（应与基线一致）---")
print("  stats:", call("stats")["stats"]["dps"])

print("\n--- 3) 试穿「原来那件头盔」（应与基线完全相同）---")
same = call("equip", {"slot": "Helmet", "item": helmet_raw}, "equip(原物)")
print(f"  DPS={same['stats']['dps']:.2f}  EHP={same['stats']['ehp']:.2f}")
print(f"  与基线一致: DPS {abs(same['stats']['dps'] - base['dps']) < 1e-6} | EHP {abs(same['stats']['ehp'] - base['ehp']) < 1e-6}")

print("\n--- 4) 试穿「改过数值的头盔」（应变化）---")
boosted = call("equip", {"slot": "Helmet", "item": helmet_boosted}, "equip(改过)")
print(f"  DPS={boosted['stats']['dps']:.2f}  EHP={boosted['stats']['ehp']:.2f}")
d_dps = boosted["stats"]["dps"] - base["dps"]
d_ehp = boosted["stats"]["ehp"] - base["ehp"]
print(f"  delta: DPS={d_dps:+.2f}  EHP={d_ehp:+.2f}")
print(f"  ⟹ 追加词缀生效: {abs(d_dps) > 1e-6 or abs(d_ehp) > 1e-6}")

print("\n--- 4b) 对照组：把 Body Armour 那件试穿到 Helmet 槽（数字必须大变）---")
if body_raw:
    cross = call("equip", {"slot": "Helmet", "item": body_raw}, "equip(错槽对照)")
    if cross:
        print(f"  DPS={cross['stats']['dps']:.2f}  EHP={cross['stats']['ehp']:.2f}")
        print(f"  ⟹ 槽位替换真的生效: "
              f"{abs(cross['stats']['dps'] - base['dps']) > 1 or abs(cross['stats']['ehp'] - base['ehp']) > 1}")

print("\n--- 5) slots 清单 ---")
sl = call("slots")
print("  ", sl.get("slots") if sl else None)

print("\n--- 6) shutdown ---")
call("shutdown")
proc.wait(timeout=20)
print("  退出码:", proc.returncode)
