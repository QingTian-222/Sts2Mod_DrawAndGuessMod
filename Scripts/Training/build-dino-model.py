#!/usr/bin/env python3
"""Export DINOv2-S/14 to ONNX and precompute vanilla card embeddings."""

from __future__ import annotations

import argparse
import re
import struct
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
import timm
import torch
from PIL import Image
from onnxruntime.transformers.onnx_model import OnnxModel
from timm.data import create_transform


MAGIC = b"DAGD"
MODEL_VERSION = 2
MODEL_NAME = "vit_small_patch14_dinov2.lvd142m"
EMBEDDING_SIZE = 384
INPUT_SIZE = 224
ATLAS_RE = re.compile(r'path="res://images/atlases/(card_atlas_\d+\.png)"')
REGION_RE = re.compile(r"region\s*=\s*Rect2\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*\)")


class NormalizedDino(torch.nn.Module):
    def __init__(self, backbone: torch.nn.Module):
        super().__init__()
        self.backbone = backbone

    def forward(self, image: torch.Tensor) -> torch.Tensor:
        embedding = self.backbone(image)
        return torch.nn.functional.normalize(embedding, dim=-1)


def discover_portraits(sts_source: Path) -> list[tuple[str, Image.Image]]:
    atlas_dir = sts_source / "images" / "atlases"
    sprites_dir = atlas_dir / "card_atlas.sprites"
    if not sprites_dir.is_dir():
        raise FileNotFoundError(f"Card sprite directory not found: {sprites_dir}")

    atlases: dict[str, Image.Image] = {}
    portraits: dict[str, Image.Image] = {}
    for tres_path in sorted(sprites_dir.rglob("*.tres")):
        relative_parts = tres_path.relative_to(sprites_dir).parts
        if "beta" in relative_parts or tres_path.stem == "beta":
            continue
        text = tres_path.read_text(encoding="utf-8")
        atlas_match = ATLAS_RE.search(text)
        region_match = REGION_RE.search(text)
        if atlas_match is None or region_match is None:
            continue
        card_id = tres_path.stem.upper()
        if card_id in portraits:
            continue
        atlas_name = atlas_match.group(1)
        if atlas_name not in atlases:
            atlases[atlas_name] = Image.open(atlas_dir / atlas_name).convert("RGB")
        x, y, width, height = (int(round(float(value))) for value in region_match.groups())
        portraits[card_id] = atlases[atlas_name].crop((x, y, x + width, y + height)).copy()

    for atlas in atlases.values():
        atlas.close()
    return sorted(portraits.items())


def write_features(output_path: Path, card_ids: list[str], features: np.ndarray) -> None:
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("wb") as stream:
        stream.write(MAGIC)
        stream.write(struct.pack("<iii", MODEL_VERSION, features.shape[1], len(card_ids)))
        for card_id, embedding in zip(card_ids, features, strict=True):
            encoded_id = card_id.encode("utf-8")
            stream.write(struct.pack("<H", len(encoded_id)))
            stream.write(encoded_id)
            stream.write(embedding.astype("<f4", copy=False).tobytes())


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("sts_source", type=Path)
    parser.add_argument("onnx_output", type=Path)
    parser.add_argument("features_output", type=Path)
    parser.add_argument("--batch-size", type=int, default=16)
    args = parser.parse_args()

    backbone = timm.create_model(MODEL_NAME, pretrained=True, num_classes=0, img_size=INPUT_SIZE).eval()
    model = NormalizedDino(backbone).eval()
    transform = create_transform(
        input_size=(3, INPUT_SIZE, INPUT_SIZE),
        interpolation="bicubic",
        mean=(0.485, 0.456, 0.406),
        std=(0.229, 0.224, 0.225),
        crop_pct=1.0,
        crop_mode="center",
        is_training=False,
    )

    args.onnx_output.parent.mkdir(parents=True, exist_ok=True)
    fp32_output = args.onnx_output.with_name(args.onnx_output.stem + ".fp32.tmp.onnx")
    dummy = torch.zeros((1, 3, INPUT_SIZE, INPUT_SIZE), dtype=torch.float32)
    with torch.inference_mode():
        torch.onnx.export(
            model,
            (dummy,),
            fp32_output,
            input_names=["image"],
            output_names=["embedding"],
            opset_version=18,
            dynamo=True,
            external_data=False,
        )

    fp16_model = OnnxModel(onnx.load(fp32_output))
    fp16_model.convert_float_to_float16(use_symbolic_shape_infer=False, keep_io_types=True)
    fp16_model.save_model_to_file(args.onnx_output, use_external_data_format=False)
    fp32_output.unlink(missing_ok=True)

    portraits = discover_portraits(args.sts_source.resolve())
    session = ort.InferenceSession(str(args.onnx_output.resolve()), providers=["CPUExecutionProvider"])
    card_ids: list[str] = []
    feature_batches: list[np.ndarray] = []
    batch_size = max(1, args.batch_size)
    with torch.inference_mode():
        for offset in range(0, len(portraits), batch_size):
            batch_entries = portraits[offset : offset + batch_size]
            inputs = [transform(portrait).cpu().float().numpy()[None, ...] for _, portrait in batch_entries]
            embeddings = np.concatenate(
                [session.run(["embedding"], {"image": image_input})[0] for image_input in inputs],
                axis=0,
            )
            embeddings /= np.maximum(np.linalg.norm(embeddings, axis=1, keepdims=True), 1e-12)
            card_ids.extend(card_id for card_id, _ in batch_entries)
            feature_batches.append(embeddings)
            print(f"DINOv2 features: {len(card_ids)}/{len(portraits)}", flush=True)

    features = np.concatenate(feature_batches, axis=0).astype(np.float32)
    if features.shape != (len(card_ids), EMBEDDING_SIZE):
        raise RuntimeError(f"Unexpected feature shape: {features.shape}")
    write_features(args.features_output.resolve(), card_ids, features)

    onnx_embedding = session.run(["embedding"], {"image": dummy.numpy()})[0]
    torch_embedding = model(dummy).detach().numpy()
    max_error = float(np.max(np.abs(onnx_embedding - torch_embedding)))
    print(
        f"Wrote {args.onnx_output.resolve()} and {args.features_output.resolve()} "
        f"for {len(card_ids)} cards; ONNX max error={max_error:.8f}"
    )


if __name__ == "__main__":
    main()
