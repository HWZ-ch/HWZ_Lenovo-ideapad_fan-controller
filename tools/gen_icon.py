#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成应用图标 app.ico（雪花 + 蓝色圆角底，16px 下仍可辨识）。

用法:
    python tools/gen_icon.py <输出路径.ico>

依赖 Pillow。生成一张 1024x1024 母图再缩放到各尺寸，保证边缘平滑。
"""

import sys
import os

from PIL import Image, ImageDraw

# 母图尺寸，最后按需缩小
MASTER = 1024
# ICO 内包含的尺寸
SIZES = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48),
         (64, 64), (128, 128), (256, 256)]

TOP_COLOR = (32, 106, 220)
BOTTOM_COLOR = (10, 48, 128)


def make_master():
    """画一张 1024x1024 的母图。"""
    ss = MASTER
    img = Image.new("RGBA", (ss, ss), (0, 0, 0, 0))

    # --- 圆角底 + 竖向渐变 ---
    grad = Image.new("RGBA", (ss, ss))
    gd = ImageDraw.Draw(grad)
    for y in range(ss):
        t = y / float(ss - 1)
        r = int(TOP_COLOR[0] + (BOTTOM_COLOR[0] - TOP_COLOR[0]) * t)
        g = int(TOP_COLOR[1] + (BOTTOM_COLOR[1] - TOP_COLOR[1]) * t)
        b = int(TOP_COLOR[2] + (BOTTOM_COLOR[2] - TOP_COLOR[2]) * t)
        gd.line([(0, y), (ss, y)], fill=(r, g, b, 255))

    radius = int(ss * 0.22)
    mask = Image.new("L", (ss, ss), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, ss - 1, ss - 1], radius=radius, fill=255
    )
    img.paste(grad, (0, 0), mask)

    # --- 白色雪花 ---
    draw = ImageDraw.Draw(img)
    cx = cy = ss / 2.0
    arm = ss * 0.335
    width = int(ss * 0.075)

    import math
    for i in range(6):
        ang = math.radians(i * 60.0)
        x2 = cx + arm * math.cos(ang)
        y2 = cy + arm * math.sin(ang)
        draw.line([(cx, cy), (x2, y2)], fill=(255, 255, 255, 255), width=width)

        # 两条小分支（在 62% 处，±35°）
        bx = cx + arm * 0.62 * math.cos(ang)
        by = cy + arm * 0.62 * math.sin(ang)
        branch = arm * 0.30
        for deg in (35.0, -35.0):
            a2 = ang + math.radians(deg)
            draw.line(
                [(bx, by),
                 (bx + branch * math.cos(a2), by + branch * math.sin(a2))],
                fill=(255, 255, 255, 255),
                width=max(2, int(width * 0.85)),
            )

    # 中心圆点
    dot = ss * 0.062
    draw.ellipse([cx - dot, cy - dot, cx + dot, cy + dot], fill=(255, 255, 255, 255))

    # 端点圆角：在每条臂末端补小圆
    for i in range(6):
        ang = math.radians(i * 60.0)
        x2 = cx + arm * math.cos(ang)
        y2 = cy + arm * math.sin(ang)
        rr = width / 2.0
        draw.ellipse([x2 - rr, y2 - rr, x2 + rr, y2 + rr], fill=(255, 255, 255, 255))

    return img


def main():
    if len(sys.argv) < 2:
        print("用法: python tools/gen_icon.py <输出.ico>")
        return 2

    out = sys.argv[1]
    out_dir = os.path.dirname(os.path.abspath(out))
    if out_dir and not os.path.isdir(out_dir):
        os.makedirs(out_dir, exist_ok=True)

    master = make_master()
    # Pillow 的 ICO 保存会按 sizes 自动缩放
    master.save(out, format="ICO", sizes=SIZES)

    size = os.path.getsize(out)
    print("已生成: %s" % out)
    print("大小: %d bytes, 尺寸: %s" % (size, ", ".join("%dx%d" % s for s in SIZES)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
