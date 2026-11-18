"""Generate packaging/icon.ico with Pillow."""
from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw

SIZE = 1024
SCALE = SIZE / 256.0
OUT = Path(__file__).with_name("icon.ico")


def px(value: float) -> int:
    return int(value * SCALE)


def main() -> None:
    image = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle(
        (px(4), px(4), px(252), px(252)),
        radius=px(54),
        fill=(15, 23, 42, 255),
        outline=(30, 41, 59, 255),
        width=px(4),
    )
    center_x = px(128)
    base_y = px(188)
    for radius, width in ((44, 16), (78, 16), (112, 16)):
        draw.arc(
            (center_x - px(radius), base_y - px(radius), center_x + px(radius), base_y + px(radius)),
            start=225,
            end=315,
            fill=(56, 189, 248, 255),
            width=px(width),
        )
    dot = px(15)
    draw.ellipse(
        (center_x - dot, base_y - dot, center_x + dot, base_y + dot),
        fill=(56, 189, 248, 255),
    )
    image = image.resize((256, 256), Image.Resampling.LANCZOS)
    image.save(
        OUT,
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
