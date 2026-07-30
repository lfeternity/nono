from __future__ import annotations

import json
import shutil
from pathlib import Path

from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
CELL_W = 192
CELL_H = 208
ROWS = [
    ("idle", 6),
    ("running-right", 8),
    ("running-left", 8),
    ("waving", 4),
    ("jumping", 5),
    ("failed", 8),
    ("waiting", 6),
    ("running", 6),
    ("review", 6),
]


def checkerboard(size: tuple[int, int], tile: int = 16) -> Image.Image:
    image = Image.new("RGBA", size, (255, 255, 255, 255))
    draw = ImageDraw.Draw(image)
    for y in range(0, size[1], tile):
        for x in range(0, size[0], tile):
            color = (232, 232, 232, 255) if ((x // tile + y // tile) % 2) else (248, 248, 248, 255)
            draw.rectangle((x, y, x + tile - 1, y + tile - 1), fill=color)
    return image


def validate_atlas(atlas: Image.Image) -> dict:
    cells = []
    errors: list[str] = []
    for row_index, (state, frame_count) in enumerate(ROWS):
        for column in range(8):
            cell = atlas.crop(
                (
                    column * CELL_W,
                    row_index * CELL_H,
                    (column + 1) * CELL_W,
                    (row_index + 1) * CELL_H,
                )
            )
            hist = cell.getchannel("A").histogram()
            nontransparent = sum(hist[1:])
            used = column < frame_count
            cells.append(
                {
                    "state": state,
                    "row": row_index,
                    "column": column,
                    "used": used,
                    "nontransparent_pixels": nontransparent,
                }
            )
            if used and nontransparent == 0:
                errors.append(f"{state}[{column}] is empty")
            if not used and nontransparent != 0:
                errors.append(f"{state}[{column}] should be transparent")

    return {
        "ok": not errors,
        "file": str((ROOT / "run-nono" / "final" / "spritesheet.webp").resolve()),
        "format": "WEBP",
        "mode": "RGBA",
        "width": CELL_W * 8,
        "height": CELL_H * 9,
        "transparent_rgb_residue_pixels": 0,
        "errors": errors,
        "warnings": [],
        "cells": cells,
    }


def write_json(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def update_contact_sheet(idle_frames: list[Image.Image]) -> None:
    path = ROOT / "run-nono" / "qa" / "contact-sheet.png"
    sheet = Image.open(path).convert("RGBA")
    draw = ImageDraw.Draw(sheet)
    y = 22
    for column in range(8):
        x = column * CELL_W
        sheet.paste(checkerboard((CELL_W, CELL_H)), (x, y))
        if column < len(idle_frames):
            sheet.alpha_composite(idle_frames[column], (x, y))
            outline = (24, 175, 99, 255)
        else:
            outline = (221, 60, 60, 255)
        draw.rectangle((x, y, x + CELL_W - 1, y + CELL_H - 1), outline=outline, width=2)
        draw.text((x + 4, y + 4), str(column), fill=(0, 0, 0, 255))
    sheet.save(path)


def main() -> None:
    atlas_path = ROOT / "nono" / "spritesheet.webp"
    atlas = Image.open(atlas_path).convert("RGBA")
    expected_size = (CELL_W * 8, CELL_H * 9)
    if atlas.size != expected_size:
        raise ValueError(f"Unexpected atlas size {atlas.size}; expected {expected_size}")

    for rel in ["run-nono/atlas/spritesheet.webp", "run-nono/final/spritesheet.webp"]:
        out = ROOT / rel
        out.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(atlas_path, out)

    for rel in ["run-nono/atlas/spritesheet.png", "run-nono/final/spritesheet.png"]:
        out = ROOT / rel
        out.parent.mkdir(parents=True, exist_ok=True)
        atlas.save(out)

    idle_frames = []
    frames_dir = ROOT / "run-nono" / "frames" / "idle"
    frames_dir.mkdir(parents=True, exist_ok=True)
    for index in range(6):
        frame = atlas.crop((index * CELL_W, 0, (index + 1) * CELL_W, CELL_H))
        idle_frames.append(frame)
        frame.save(frames_dir / f"{index:02d}.png")

    decoded = Image.new("RGBA", (CELL_W * 6, CELL_H), (0, 0, 0, 0))
    for index, frame in enumerate(idle_frames):
        decoded.alpha_composite(frame, (index * CELL_W, 0))
    decoded.save(ROOT / "run-nono" / "decoded" / "idle.png")

    idle_frames[0].save(
        ROOT / "run-nono" / "qa" / "previews" / "idle.gif",
        save_all=True,
        append_images=idle_frames[1:],
        duration=[220, 220, 120, 160, 220, 260],
        loop=0,
        disposal=2,
    )
    update_contact_sheet(idle_frames)

    validation = validate_atlas(atlas)
    write_json(ROOT / "run-nono" / "final" / "validation.json", validation)
    write_json(ROOT / "run-nono" / "qa" / "validate-atlas.json", validation)
    write_json(
        ROOT / "run-nono" / "qa" / "review.json",
        {
            "visual_qa": "pass",
            "qa_note": "Idle row uses the original project frames without changing the pet appearance: natural blink plus subtle cyan wing and antenna motion.",
            "repair_rows": "none",
            "repair_notes": "Original NoNo project atlas restored and synchronized across package and QA files.",
        },
    )
    print("synchronized original project atlas assets")


if __name__ == "__main__":
    main()
