#!/usr/bin/env python3
"""Build DrawAndGuessMod's compact relic-art feature model."""

from __future__ import annotations

import argparse
import importlib.util
import struct
from pathlib import Path

import numpy as np
from PIL import Image


MAGIC = b"DAGR"
MODEL_VERSION = 2
RECOGNITION_SIZE = 224
ARTWORK_SIZE = 192
WHITE_BACKGROUND_WEIGHT = 0.45
DARK_BACKGROUND_WEIGHT = 0.30
ALPHA_MASK_WEIGHT = 0.25
DARK_BACKGROUND = (31, 36, 46, 255)


def load_feature_extractor(script_dir: Path):
    module_path = script_dir / "train-card-model.py"
    spec = importlib.util.spec_from_file_location(
        "draw_and_guess_card_features",
        module_path,
    )
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Could not load feature extractor: {module_path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def normalize_transparent_artwork(
    source: Image.Image,
) -> tuple[Image.Image, Image.Image, Image.Image]:
    rgba = source.convert("RGBA")
    alpha = np.asarray(rgba, dtype=np.uint8)[:, :, 3]
    ys, xs = np.nonzero(alpha > 5)
    if len(xs) == 0:
        return (
            Image.new(
                "RGB",
                (RECOGNITION_SIZE, RECOGNITION_SIZE),
                "white",
            ),
            Image.new(
                "RGB",
                (RECOGNITION_SIZE, RECOGNITION_SIZE),
                DARK_BACKGROUND[:3],
            ),
            Image.new(
                "RGB",
                (RECOGNITION_SIZE, RECOGNITION_SIZE),
                "white",
            ),
        )

    bounds = (
        int(xs.min()),
        int(ys.min()),
        int(xs.max()) + 1,
        int(ys.max()) + 1,
    )
    artwork = rgba.crop(bounds)
    scale = ARTWORK_SIZE / max(artwork.size)
    resized_size = (
        max(1, round(artwork.width * scale)),
        max(1, round(artwork.height * scale)),
    )
    artwork = artwork.resize(resized_size, Image.Resampling.LANCZOS)

    white_background = Image.new(
        "RGBA",
        (RECOGNITION_SIZE, RECOGNITION_SIZE),
        "white",
    )
    destination = (
        (RECOGNITION_SIZE - artwork.width) // 2,
        (RECOGNITION_SIZE - artwork.height) // 2,
    )
    white_background.alpha_composite(artwork, destination)

    dark_background = Image.new(
        "RGBA",
        (RECOGNITION_SIZE, RECOGNITION_SIZE),
        DARK_BACKGROUND,
    )
    dark_background.alpha_composite(artwork, destination)

    alpha_mask = Image.new(
        "L",
        (RECOGNITION_SIZE, RECOGNITION_SIZE),
        255,
    )
    resized_alpha = np.asarray(
        artwork.getchannel("A"),
        dtype=np.uint8,
    )
    alpha_shape = Image.fromarray(
        255 - resized_alpha,
        mode="L",
    )
    alpha_mask.paste(alpha_shape, destination)
    return (
        white_background.convert("RGB"),
        dark_background.convert("RGB"),
        alpha_mask.convert("RGB"),
    )


def extract_relic_features(
    source: Image.Image,
    feature_module,
) -> np.ndarray:
    white_background, dark_background, alpha_mask = (
        normalize_transparent_artwork(source)
    )
    white_features = feature_module.extract_features(white_background)
    dark_features = feature_module.extract_features(dark_background)
    alpha_features = feature_module.extract_features(alpha_mask)
    return np.concatenate(
        (
            white_features * np.sqrt(WHITE_BACKGROUND_WEIGHT),
            dark_features * np.sqrt(DARK_BACKGROUND_WEIGHT),
            alpha_features * np.sqrt(ALPHA_MASK_WEIGHT),
        )
    )


def discover_samples(
    sts_source: Path,
    feature_module,
) -> list[tuple[str, np.ndarray]]:
    relic_dir = sts_source / "images" / "relics"
    if not relic_dir.is_dir():
        raise FileNotFoundError(
            f"Relic image directory not found: {relic_dir}"
        )

    samples: dict[str, np.ndarray] = {}
    for image_path in sorted(relic_dir.glob("*.png")):
        with Image.open(image_path) as source:
            samples[image_path.stem.upper()] = extract_relic_features(
                source,
                feature_module,
            )

    # Yummy Cookie selects a character-specific texture at runtime but is one
    # relic model. Use a stable representative for the bundled cache.
    yummy_cookie = relic_dir / "yummy_cookie_ironclad.png"
    if yummy_cookie.is_file():
        with Image.open(yummy_cookie) as source:
            samples["YUMMY_COOKIE"] = extract_relic_features(
                source,
                feature_module,
            )

    mod_image_dir = Path(__file__).resolve().parents[2] / "AssetProject" / "images"
    mod_relics = {
        "DRAW_AND_GUESS_MOD_RELIC_DEATH_NOTE":
            mod_image_dir / "death_note_relic_big.png",
        "DRAW_AND_GUESS_MOD_RELIC_MEMORIAL_SKETCHBOOK":
            mod_image_dir / "memorial_sketchbook_relic.png",
    }
    for relic_id, image_path in mod_relics.items():
        if not image_path.is_file():
            continue
        with Image.open(image_path) as source:
            samples[relic_id] = extract_relic_features(
                source,
                feature_module,
            )

    return sorted(samples.items())


def write_model(
    output_path: Path,
    samples: list[tuple[str, np.ndarray]],
    feature_count: int,
) -> None:
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("wb") as stream:
        stream.write(MAGIC)
        stream.write(
            struct.pack(
                "<iii",
                MODEL_VERSION,
                feature_count,
                len(samples),
            )
        )
        for relic_id, features in samples:
            encoded_id = relic_id.encode("utf-8")
            if len(encoded_id) > 0xFFFF or len(features) != feature_count:
                raise ValueError(
                    f"Cannot serialize relic feature record {relic_id}"
                )
            stream.write(struct.pack("<H", len(encoded_id)))
            stream.write(encoded_id)
            stream.write(features.astype("<f4").tobytes(order="C"))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "sts_source",
        type=Path,
        help="Path to extracted game assets",
    )
    parser.add_argument(
        "output",
        type=Path,
        help="Output relic_features.bin",
    )
    args = parser.parse_args()

    feature_module = load_feature_extractor(Path(__file__).resolve().parent)
    samples = discover_samples(args.sts_source.resolve(), feature_module)
    if not samples:
        raise RuntimeError("No relic artwork samples were discovered")
    write_model(
        args.output.resolve(),
        samples,
        feature_module.FEATURE_COUNT * 3,
    )
    print(
        f"Wrote {len(samples)} samples x "
        f"{feature_module.FEATURE_COUNT * 3} features to {args.output}"
    )


if __name__ == "__main__":
    main()
