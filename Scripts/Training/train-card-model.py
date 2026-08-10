#!/usr/bin/env python3
"""Build DrawAndGuessMod's compact card-art feature model from decompiled assets."""

from __future__ import annotations

import argparse
import struct
from pathlib import Path

import numpy as np
from PIL import Image


MAGIC = b"DAGM"
MODEL_VERSION = 6
SAMPLE_SIZE = 32
GRID_SIZE = 8
COLOR_GRID_SIZE = 4
FINE_GRID_SIZE = 16
EDGE_FEATURE_COUNT = GRID_SIZE * GRID_SIZE + 6
FINE_EDGE_FEATURE_COUNT = FINE_GRID_SIZE * FINE_GRID_SIZE
COLOR_FEATURE_COUNT = COLOR_GRID_SIZE * COLOR_GRID_SIZE * 3 + 10
FEATURE_COUNT = EDGE_FEATURE_COUNT + FINE_EDGE_FEATURE_COUNT + COLOR_FEATURE_COUNT
def extract_features(portrait: Image.Image, treat_as_sketch: bool = False) -> np.ndarray:
    image = portrait.convert("RGB").resize((SAMPLE_SIZE, SAMPLE_SIZE), Image.Resampling.LANCZOS)
    rgb = np.asarray(image, dtype=np.float64)
    rgb /= 255.0
    luminance = rgb[:, :, 0] * 0.299 + rgb[:, :, 1] * 0.587 + rgb[:, :, 2] * 0.114

    padded = np.pad(luminance, 1, mode="edge")
    gx = padded[1:-1, 2:] - padded[1:-1, :-2]
    gy = padded[2:, 1:-1] - padded[:-2, 1:-1]
    strength = np.clip(np.sqrt(gx * gx + gy * gy) * 2.4, 0.0, 1.0)
    if treat_as_sketch:
        ink = np.clip((0.94 - luminance) / 0.94, 0.0, 1.0)
        strength = np.clip(strength + ink * 0.18, 0.0, 1.0)

    cell_size = SAMPLE_SIZE // GRID_SIZE
    grid = strength.reshape(GRID_SIZE, cell_size, GRID_SIZE, cell_size).transpose(0, 2, 1, 3).mean(axis=(2, 3))
    total = float(strength.sum())
    safe_total = max(total, 0.0001)
    ys, xs = np.indices((SAMPLE_SIZE, SAMPLE_SIZE))

    extras = np.asarray(
        [
            total / (SAMPLE_SIZE * SAMPLE_SIZE),
            float((strength * xs).sum()) / safe_total / (SAMPLE_SIZE - 1),
            float((strength * ys).sum()) / safe_total / (SAMPLE_SIZE - 1),
            float(strength[:, : SAMPLE_SIZE // 2].sum() - strength[:, SAMPLE_SIZE // 2 :].sum()) / safe_total,
            float(strength[: SAMPLE_SIZE // 2, :].sum() - strength[SAMPLE_SIZE // 2 :, :].sum()) / safe_total,
            float((grid > 0.08).sum()) / (GRID_SIZE * GRID_SIZE),
        ],
        dtype=np.float64,
    )
    edge_features = np.concatenate((grid.reshape(-1), extras))
    length = float(np.linalg.norm(edge_features))
    if length > 0.000001:
        edge_features /= length

    fine_cell_size = SAMPLE_SIZE // FINE_GRID_SIZE
    fine_edge_features = (
        strength.reshape(FINE_GRID_SIZE, fine_cell_size, FINE_GRID_SIZE, fine_cell_size)
        .transpose(0, 2, 1, 3)
        .mean(axis=(2, 3))
        .reshape(-1)
    )
    fine_length = float(np.linalg.norm(fine_edge_features))
    if fine_length > 0.000001:
        fine_edge_features /= fine_length

    value = rgb.max(axis=2)
    minimum = rgb.min(axis=2)
    delta = value - minimum
    saturation = np.divide(delta, value, out=np.zeros_like(delta), where=value > 0.000001)
    color_weight = np.clip((saturation - 0.15) / 0.85, 0.0, 1.0)
    color_cell_size = SAMPLE_SIZE // COLOR_GRID_SIZE
    weighted_rgb = color_weight[:, :, np.newaxis] * rgb
    color_grid = (
        weighted_rgb.reshape(COLOR_GRID_SIZE, color_cell_size, COLOR_GRID_SIZE, color_cell_size, 3)
        .transpose(0, 2, 1, 3, 4)
        .sum(axis=(2, 3))
        / (color_cell_size * color_cell_size)
    )

    hue = np.zeros_like(value)
    chromatic = delta > 0.000001
    red_is_max = chromatic & (rgb[:, :, 0] >= rgb[:, :, 1]) & (rgb[:, :, 0] >= rgb[:, :, 2])
    green_is_max = chromatic & ~red_is_max & (rgb[:, :, 1] >= rgb[:, :, 2])
    blue_is_max = chromatic & ~red_is_max & ~green_is_max
    red_hue = np.divide(rgb[:, :, 1] - rgb[:, :, 2], delta, out=np.zeros_like(delta), where=chromatic)
    green_hue = np.divide(rgb[:, :, 2] - rgb[:, :, 0], delta, out=np.zeros_like(delta), where=chromatic)
    blue_hue = np.divide(rgb[:, :, 0] - rgb[:, :, 1], delta, out=np.zeros_like(delta), where=chromatic)
    hue[red_is_max] = red_hue[red_is_max] % 6.0
    hue[green_is_max] = green_hue[green_is_max] + 2.0
    hue[blue_is_max] = blue_hue[blue_is_max] + 4.0
    hue /= 6.0
    hue_bins = np.minimum((hue * 8.0).astype(np.int32), 7)
    hue_histogram = np.zeros(8, dtype=np.float64)
    np.add.at(hue_histogram, hue_bins, color_weight * value)
    hue_histogram /= SAMPLE_SIZE * SAMPLE_SIZE

    color_features = np.concatenate(
        (
            color_grid.reshape(-1),
            hue_histogram,
            np.asarray([color_weight.mean(), (color_weight * value).mean()], dtype=np.float64),
        )
    )
    color_length = float(np.linalg.norm(color_features))
    if color_length > 0.000001:
        color_features /= color_length

    features = np.concatenate((edge_features * 0.55, fine_edge_features * 0.55, color_features * 0.6284902545))
    return features.astype("<f4")


def discover_samples(sts_source: Path) -> list[tuple[str, np.ndarray]]:
    portraits_dir = sts_source / "images" / "packed" / "card_portraits"
    if not portraits_dir.is_dir():
        raise FileNotFoundError(f"Packed card portrait directory not found: {portraits_dir}")

    samples: dict[str, np.ndarray] = {}
    for png_path in sorted(portraits_dir.rglob("*.png")):
        relative_parts = png_path.relative_to(portraits_dir).parts
        if "beta" in relative_parts or png_path.stem in {"beta", "ancient_beta"}:
            continue

        card_id = png_path.stem.upper()
        if card_id in samples:
            continue

        with Image.open(png_path) as portrait:
            samples[card_id] = extract_features(portrait)

    return sorted(samples.items())


def write_model(output_path: Path, samples: list[tuple[str, np.ndarray]]) -> None:
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("wb") as stream:
        stream.write(MAGIC)
        stream.write(struct.pack("<iii", MODEL_VERSION, FEATURE_COUNT, len(samples)))
        for card_id, features in samples:
            encoded_id = card_id.encode("utf-8")
            stream.write(struct.pack("<H", len(encoded_id)))
            stream.write(encoded_id)
            stream.write(features.tobytes(order="C"))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("sts_source", type=Path, help="Path to extracted game assets")
    parser.add_argument("output", type=Path, help="Output card_features.bin")
    args = parser.parse_args()

    samples = discover_samples(args.sts_source.resolve())
    if not samples:
        raise RuntimeError("No card portrait samples were discovered")
    write_model(args.output.resolve(), samples)
    print(f"Wrote {len(samples)} samples x {FEATURE_COUNT} features to {args.output}")


if __name__ == "__main__":
    main()
