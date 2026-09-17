#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
占位皮肤生成器 + 契约校验

把"字符网格"画成 PNG，输出到 Unity 工程的 StreamingAssets/skins/ 下。

为什么要用工具生成，而不是直接画 PNG？
  · 素材源码是可读、可 diff、可 review 的**文本** —— 改一个字符就是改一个像素
  · 顺手按《皮肤契约设计.md》§4 的规范做校验，把美术错误变成明确的报告
    （这就是 §4.3「引擎的自动检查」在构建期的版本）

只依赖 Python 标准库（手写了一个最小 PNG 编码器）。

用法：
    python _tools/make_skin.py
"""

import json
import os
import struct
import sys
import zlib

# 控制台编码：中文 Windows 默认是 GBK，装不下 ✅ 之类的符号会直接崩。
# 这里强制 UTF-8 输出，并且"出错就替换"而不是抛异常。
# （这也正是我们自己项目的那条规则：屏幕上的字只用 ASCII —— 状态标记用 [OK]/[XX] 而不是 emoji。）
try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

# ══════════════════════════════════════════════════════════════════
#  调色板
# ══════════════════════════════════════════════════════════════════

PALETTE = {
    '.': None,                    # 透明
    'H': (0x3A, 0x1A, 0x5E),      # 深紫：头发描边
    'h': (0x7A, 0x3F, 0xD1),      # 主紫：头发
    'L': (0xC9, 0xA8, 0xFF),      # 淡紫：头发高光
    'S': (0xF6, 0xD7, 0xB8),      # 肤色
    'E': (0x2E, 0x1A, 0x4A),      # 眼睛
    'D': (0x6B, 0x46, 0xC1),      # 衣裙主色
    'B': (0x2E, 0x1A, 0x4A),      # 靴子
    'W': (0x5A, 0x3C, 0x22),      # 牌子：深木色（边框）
    'w': (0x8B, 0x6A, 0x42),      # 牌子：浅木色（板面 = 数字的区域）
}

# ══════════════════════════════════════════════════════════════════
#  占位角色的美术源文件
#
#  只画**左半边**（16 列），右半边由镜像生成 —— 两个好处：
#    · 少打一半字
#    · **对称由构造保证**，不会手抖画歪
#
#  约定（见《皮肤契约设计.md》§4.1）：
#    ② 脚踩在画布底边      → 最后一行就是靴子
#    ③ 空白只留在头顶      → 前两行是空的
#    ④ 重心对水平中点      → 镜像天然满足
#    ⑨ 起手姿势要接近      → 占位阶段先不管
# ══════════════════════════════════════════════════════════════════

GRID_LEFT = [
    # ── 牌子（第 0..11 行）──────────────────────────────────────────
    # ⚠️ 牌子**绝对不能在帧之间移动**：皮肤里的 textArea 是固定坐标，
    #    牌子一挪，数字就跟不上它（《皮肤契约设计.md》§6.2）。
    #    所以下面 BOB_ROWS 故意把牌子排除在"呼吸"之外。
    "................",   #  0
    "................",   #  1
    "..WWWWWWWWWWWWWW",   #  2  板子上沿
    "..Wwwwwwwwwwwwww",   #  3  ↓ 板面：数字画在这一片里
    "..Wwwwwwwwwwwwww",   #  4
    "..Wwwwwwwwwwwwww",   #  5
    "..Wwwwwwwwwwwwww",   #  6
    "..Wwwwwwwwwwwwww",   #  7
    "..Wwwwwwwwwwwwww",   #  8
    "..Wwwwwwwwwwwwww",   #  9
    "..Wwwwwwwwwwwwww",   # 10
    "..WWWWWWWWWWWWWW",   # 11  板子下沿

    # ── 她（第 12..43 行）───────────────────────────────────────────
    "................",   # 12  板子和头顶之间的空隙
    "................",   # 13
    "............HH..",   # 14  猫耳发髻
    "...........Hhh..",   # 15
    "...........Hhhh.",   # 16
    "..........HhhLLL",   # 17  头顶
    ".........HhhLLLL",   # 18
    "........HhhLLLLL",   # 19
    "........HhSSSSSS",   # 20  额头
    ".......HhhSSSSSS",   # 21
    ".......HhSSSSSSS",   # 22
    ".......HhSSSSEES",   # 23  眼睛（最内侧必须是 S，镜像后中间才有空隙）
    ".......HhSSSSEES",   # 24
    ".......HhSSSSSSS",   # 25
    ".......HhhSSSSSS",   # 26
    "........HhhSSSSS",   # 27  下巴
    "..........HhSSSS",   # 28  脖子
    ".......HhhhhDDDD",   # 29  领口
    "......HhhhhhDDDD",   # 30  肩膀
    ".....HhhhhhhDDDD",   # 31
    ".....HhhhhhhDDDD",   # 32
    ".....HhhhhhhDDDD",   # 33
    ".....HhhhhhDDDDD",   # 34
    "......HhhhhDDDDD",   # 35
    ".......HhhDDDDDD",   # 36  双马尾垂到腰
    "........hhDDDDDD",   # 37
    ".........hDDDDDD",   # 38
    "..........DDDDDD",   # 39  裙摆底
    "...........SSSS.",   # 40  腿
    "...........SSSS.",   # 41
    "..........BBBBB.",   # 42  靴子
    "..........BBBBB.",   # 43  脚底 = 画布底边
]

# "呼吸"作用的网格行范围（闭区间）—— 只动她的头身，**牌子不动**。
BOB_ROWS = (14, 29)

# 牌子上"数字可以画在哪里"（网格坐标，含端点）
SIGN_INNER_ROWS = (3, 10)     # 板面内部的行
SIGN_INNER_LEFT = 3           # 板面内部最左的列（镜像后自动对称）

MIRROR = True
UPSCALE = 2          # 16→32（镜像后）再 ×2 → 64

# ══════════════════════════════════════════════════════════════════
#  动作表
#
#  每个动作 = 一组帧 + 播放参数 + 一份**调色板覆盖**。
#
#  为什么用"改颜色"来区分动作？因为占位阶段没有真正的美术资源，
#  但状态机必须能用**眼睛**验证 —— 换个颜色是最不可能看错的信号。
#  （真角色当然会画成不同的姿势。）
# ══════════════════════════════════════════════════════════════════

# 调色板覆盖：char -> (r, g, b)
STYLE_NORMAL = {}
STYLE_LIGHT = {                 # 亮一档：她"注意到你了"
    'h': (0x9A, 0x6F, 0xE8),
    'L': (0xE4, 0xD4, 0xFF),
    'D': (0x8B, 0x6B, 0xE1),
}
STYLE_GOLD = {                  # 变金：被点到的反应
    'h': (0xFF, 0xD1, 0x66),
    'L': (0xFF, 0xF0, 0xC0),
    'D': (0xF0, 0xC0, 0x40),
    'H': (0x8A, 0x5A, 0x10),
}
STYLE_DARK = {                  # 暗一档：被拎起来了
    'h': (0x5A, 0x2E, 0xA1),
    'L': (0x9A, 0x7F, 0xD0),
    'D': (0x4A, 0x2E, 0x8F),
}

# ── 待机池用的两个风格（§5.7）──
# 占位阶段"换个姿势"就是"换个颜色 + 换个呼吸节奏"，因为这里没有真正的美术资源。
# 冷灰蓝 = 没精神/睡着；嫩绿 = 精神一下/伸个懒腰。两个都和上面的 hover/click/drag 明显不同。
STYLE_SLEEP = {
    'h': (0x60, 0x6E, 0x96),
    'L': (0xA8, 0xB6, 0xD6),
    'D': (0x46, 0x52, 0x74),
    'H': (0x28, 0x30, 0x4A),
}
STYLE_STRETCH = {
    'h': (0x5E, 0xB8, 0x7E),
    'L': (0xC0, 0xEE, 0xD2),
    'D': (0x3E, 0x8A, 0x5C),
    'H': (0x1E, 0x50, 0x32),
}

# 顺序就是生成顺序。bob = 头部下沉的像素序列（脚不动）。
ACTIONS = [
    ('idle',  {'fps': 6,  'bob': [0, 1, 2, 1], 'style': STYLE_NORMAL}),
    ('hover', {'fps': 4,  'bob': [0, 1],       'style': STYLE_LIGHT}),
    ('click', {'fps': 10, 'bob': [0, 1],       'style': STYLE_GOLD, 'noLoop': True}),
    ('drag',  {'fps': 1,  'bob': [1],          'style': STYLE_DARK}),

    # ── 待机池（《皮肤契约设计.md》§5.7）──
    # 以 idle_ 开头 = 自动进随机待机池。引擎不需要在任何配置里登记它们。
    # ⚠️ 它们**必须和 idle 一样保持牌子不动**（§4.2 ⑩）——
    #    这里天然满足：bob 的范围（BOB_ROWS）不含牌子那几行，style 只换颜色。
    #    反过来说，**占位素材验证不了这条纪律**（因为构造上不可能违反它）。
    ('idle_sleep',   {'fps': 2, 'bob': [2, 2, 1, 1], 'style': STYLE_SLEEP}),
    ('idle_stretch', {'fps': 4, 'bob': [0, 2, 0],    'style': STYLE_STRETCH}),
]

# ══════════════════════════════════════════════════════════════════
#  最小 PNG 编码器（纯标准库）
# ══════════════════════════════════════════════════════════════════

def write_png(path, width, height, rows):
    """rows: 每行是 [(r,g,b,a), ...]，长度 = width"""
    raw = bytearray()
    for y in range(height):
        raw.append(0)                                  # 过滤器类型 0 = None
        for (r, g, b, a) in rows[y]:
            raw += bytes((r, g, b, a))

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data
                + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))

    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(bytes(raw), 9))
    png += chunk(b'IEND', b'')

    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'wb') as f:
        f.write(png)


# ══════════════════════════════════════════════════════════════════
#  网格 → 像素
# ══════════════════════════════════════════════════════════════════

def build_rows(bob=0, style=None):
    """把字符网格变成像素行。

    bob   = 头部下沉的像素数（0/1/2…），**脚不动**。
            这是占位阶段用来做"呼吸"的：只有上半身（第 2..17 行）整体下移，
            下半身（第 18..31 行，含靴子）原地不动 —— 所以落脚点始终不变，
            符合《皮肤契约设计.md》§1.2「姿态画出来、位置算出来」。
    style = 调色板覆盖（哪些字符换成别的颜色）。占位阶段用它区分动作。
    """
    palette = dict(PALETTE)
    if style:
        palette.update(style)

    width = len(GRID_LEFT[0])
    problems = []
    for i, row in enumerate(GRID_LEFT):
        if len(row) != width:
            problems.append(f"  第 {i} 行长度是 {len(row)}，应该是 {width}")
    if problems:
        print("字符网格有问题：")
        print("\n".join(problems))
        sys.exit(1)

    # 镜像成整幅
    full = []
    for row in GRID_LEFT:
        mirrored = row[::-1] if MIRROR else ''
        full.append(row + mirrored)

    src_h = len(full)
    src_w = len(full[0])

    # 应用呼吸：上半身（含头、发、颈）整体下移 bob 行
    if bob:
        HEAD_FIRST, HEAD_LAST = BOB_ROWS   # 注意：**牌子不在这个范围里**（见 BOB_ROWS 的注释）
        shifted = ['.' * src_w for _ in range(src_h)]
        for y in range(src_h):
            if HEAD_FIRST <= y <= HEAD_LAST:
                continue                        # 上半身稍后统一画
            shifted[y] = full[y]                # 下半身 + 牌子原地
        for y in range(HEAD_LAST, HEAD_FIRST - 1, -1):
            ty = y + bob
            if 0 <= ty < src_h:
                shifted[ty] = full[y]           # 上半身整体下移（覆盖下方几行，视觉上像"缩了一下"）
        full = shifted

    # 放大（最近邻，保持硬边）
    out = []
    for y in range(src_h):
        px_row = []
        for ch in full[y]:
            color = palette.get(ch)
            rgba = (0, 0, 0, 0) if color is None else (color[0], color[1], color[2], 255)
            px_row.extend([rgba] * UPSCALE)
        for _ in range(UPSCALE):
            out.append(list(px_row))
    return out, src_w * UPSCALE, src_h * UPSCALE


def check(rows, w, h):
    """按《皮肤契约设计.md》§4.1 校验，并把结果打出来。"""
    xs, ys = [], []
    for y in range(h):
        for x in range(w):
            if rows[y][x][3] > 0:
                xs.append(x)
                ys.append(y)
    if not xs:
        return None, ["整幅图全透明 —— 她不见了"]

    x0, x1 = min(xs), max(xs)
    y0, y1 = min(ys), max(ys)
    cx = (x0 + x1) / 2.0
    frame_cx = (w - 1) / 2.0

    lines = []
    lines.append(f"  画布          : {w} × {h}")
    lines.append(f"  内容包围盒    : x {x0}..{x1}   y {y0}..{y1}")
    lines.append(f"  内容尺寸      : {x1-x0+1} × {y1-y0+1}")

    warns = []

    # ② 脚踩在画布底边
    if y1 == h - 1:
        lines.append(f"  [OK] 脚踩在底边上（y1 = {y1} = h-1）")
    else:
        lines.append(f"  [XX] 脚没踩到底边：最低的不透明像素在 y={y1}，底边是 y={h-1}，差 {h-1-y1} 像素")
        warns.append("脚没踩到底边（§4.1 ②）")

    # ④ 重心对水平中点
    off = cx - frame_cx
    if abs(off) <= 0.5:
        lines.append(f"  [OK] 水平居中（内容中心 {cx}，画布中心 {frame_cx}）")
    else:
        lines.append(f"  [XX] 水平不居中：内容中心 {cx}，画布中心 {frame_cx}，偏 {off:+.1f} 像素")
        warns.append("水平不居中（§4.1 ④）")

    # ③ 空白只留在头顶
    head = y0 / h
    if head <= 0.25:
        lines.append(f"  [OK] 头顶留白 {y0} 像素（{head*100:.0f}% ≤ 25%）")
    else:
        lines.append(f"  [XX] 头顶留白太多：{y0} 像素（{head*100:.0f}% > 25%）")
        warns.append("头顶留白过多（§4.1 ③）")

    return (x0, y0, x1, y1), (lines, warns)


# ══════════════════════════════════════════════════════════════════
#  主流程
# ══════════════════════════════════════════════════════════════════

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SKIN_DIR = os.path.join(REPO, 'DeskSprite', 'Assets', 'StreamingAssets',
                        'skins', 'default')

SKIN_JSON = {
    "formatVersion": 1,
    "name": "占位角色",
    "author": "DeskSprite",
    "description": "占位皮肤：用来验证皮肤契约、加载管线和窗口尺寸计算",
    "frameSize": [64, 64],
    "pivot": [0.5, 0.0],
    "scale": 3,
    "defaultFps": 6,
    "hitTest": "perPixel",
    # ⚠️ 必须显式声明！引擎的**默认是 bilinear**（因为这套设计主要面向
    # 外部素材，而外部素材多半是非像素画）。像素画不写这一行，放大时会糊成一团。
    "filter": "point",
    # hitPadding 在 v1 不暴露（见《皮肤契约设计.md》§5.5），引擎里硬编码 1
}


# 呼吸动作：头部下沉的像素数，逐帧。
# 0→1→2→1 读起来是"吸进来、沉下去、再起来"。回到 0 时循环无缝。
# 之所以脚不动，是因为"姿态位移"要画在帧里、而"落点"不能变（§1.2）。
IDLE_BOB = [0, 1, 2, 1]


def compute_text_area():
    """把"数字可以画在哪里"从**网格坐标**换算成 **skin.json 要的帧坐标**。

    ⚠️ 这里最容易错的是 **y 的方向**，而错了数字会跑到牌子外面去：
        · 字符网格是**从上往下**写的（第 0 行在图像顶部）
        · 而 skin.json 里的 textArea 用**从左下角算起**的 y
          —— 和 pivot 保持一致（pivot 的 (0.5, 0) 也是"底边中点"）
    所以必须翻一次。**这个转换只在这一个地方做**，别处不再翻 ——
    转两次等于没转，而且那种 bug 很难看出来。
    """
    cols = len(GRID_LEFT[0])
    cols_full = cols * (2 if MIRROR else 1)
    rows = len(GRID_LEFT)
    frame_h = rows * UPSCALE

    # 板面内部（网格坐标 → 帧坐标）
    il = SIGN_INNER_LEFT
    ir = cols_full - 1 - SIGN_INNER_LEFT
    it, ib = SIGN_INNER_ROWS

    inner_x0 = il * UPSCALE
    inner_x1 = ir * UPSCALE + (UPSCALE - 1)
    inner_top = it * UPSCALE
    inner_bottom = ib * UPSCALE + (UPSCALE - 1)

    # 文字块：水平再内缩 2 像素；垂直居中
    text_x = inner_x0 + 2
    text_w = (inner_x1 - inner_x0 + 1) - 4
    text_h = 7                                        # 5×7 像素字体
    text_top = inner_top + ((inner_bottom - inner_top + 1) - text_h) // 2
    text_y = frame_h - 1 - (text_top + text_h - 1)    # ← 翻成"从左下角算起"

    return [text_x, text_y, text_w, text_h]


def main():
    made = []
    first_size = None
    all_warns = []
    action_json = []

    for action_name, cfg in ACTIONS:
        bob_list = cfg['bob']
        style = cfg.get('style')
        boxes = []

        for i, bob in enumerate(bob_list):
            rows, w, h = build_rows(bob, style)
            bbox, (lines, warns) = check(rows, w, h)

            if bbox is None:
                print(f"{action_name} 第 {i} 帧：" + "\n".join(lines))
                sys.exit(1)

            if first_size is None:
                first_size = (w, h)
            elif first_size != (w, h):
                print(f"{action_name} 第 {i} 帧尺寸 {w}×{h} 和第 0 帧 {first_size} 不一致"
                      f" —— 同一动作内必须一样大")
                sys.exit(1)

            png_path = os.path.join(SKIN_DIR, action_name, f'{i:02d}.png')
            write_png(png_path, w, h, rows)
            made.append(os.path.relpath(png_path, REPO))
            boxes.append(bbox)

            # 每一帧都要过契约校验：脚不能飘、重心不能歪
            all_warns += [f"{action_name} 第 {i} 帧：{x}" for x in warns]

            if action_name == 'idle' and i == 0:
                print("契约校验（每帧都查；这里只打印 idle 第 0 帧；《皮肤契约设计.md》§4.1）：")
                print("\n".join(lines))
                print()

        # ── 跨帧一致性（§4.3）──
        # ⚠️ 这里有个容易写反的地方，我自己第一版就写反了：
        #   真正必须恒定的是**落地点** —— 内容底边 y1 和水平中心 cx。
        #   而**内容顶部 y0 是可以自由变化的** —— 那就是"姿态"（呼吸、跳跃都靠它）。
        #   如果去查顶部，一次完全合法的呼吸就会被误判成违规。
        bottom = sorted(set(b[3] for b in boxes))
        center = sorted(set(round((b[0] + b[2]) / 2.0, 1) for b in boxes))
        top = [b[1] for b in boxes]

        print(f"  {action_name:6s} 帧 {len(bob_list)}  fps {cfg['fps']}"
              f"  落地点 y1={bottom} cx={center}  顶部 {min(top)}..{max(top)}（姿态，允许变）")

        if len(bottom) > 1:
            all_warns.append(f"{action_name} 落地点纵向漂移：y1 = {bottom}")
        if len(center) > 1:
            all_warns.append(f"{action_name} 落地点横向漂移：cx = {center}")

        entry = {'name': action_name, 'fps': cfg['fps']}
        if cfg.get('noLoop'):
            entry['noLoop'] = True
        action_json.append(entry)

    rows0, w, h = build_rows(0, ACTIONS[0][1].get('style'))
    SKIN_JSON['frameSize'] = [w, h]
    SKIN_JSON['actions'] = action_json
    SKIN_JSON['textArea'] = compute_text_area()
    with open(os.path.join(SKIN_DIR, 'skin.json'), 'w', encoding='utf-8') as f:
        json.dump(SKIN_JSON, f, ensure_ascii=False, indent=2)
        f.write('\n')

    print()
    for p in made:
        print(f"生成: {p}")
    print(f"生成: {os.path.relpath(os.path.join(SKIN_DIR, 'skin.json'), REPO)}")

    print()
    if all_warns:
        print(f"[!!] {len(all_warns)} 条不合规：" + "；".join(all_warns))
        sys.exit(1)
    print(f"[OK] 全部通过（{len(ACTIONS)} 个动作 / {len(made)} 帧）")


if __name__ == '__main__':
    main()
