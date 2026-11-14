#!/usr/bin/env python3
"""Export DINOv2-S/14 to ONNX and precompute vanilla card embeddings."""

from __future__ import annotations

import argparse
import runpy
import struct
from pathlib import Path

import numpy as np
import onnxruntime as ort
from PIL import Image


MAGIC = b"DAGD"
MODEL_VERSION = 2
MODEL_NAME = "vit_small_patch14_dinov2.lvd142m"
EMBEDDING_SIZE = 384
INPUT_SIZE = 224
MEAN = np.asarray((0.485, 0.456, 0.406), dtype=np.float32).reshape(3, 1, 1)
STD = np.asarray((0.229, 0.224, 0.225), dtype=np.float32).reshape(3, 1, 1)


def discover_portrait_paths(sts_source: Path) -> dict[str, Path]:
    training_script = Path(__file__).with_name("train-card-model.py")
    return runpy.run_path(str(training_script))["discover_portrait_paths"](sts_source)


def discover_portraits(sts_source: Path) -> list[tuple[str, Image.Image]]:
    portraits = []
    for card_id, png_path in discover_portrait_paths(sts_source).items():
        with Image.open(png_path) as portrait:
            portraits.append((card_id, portrait.convert("RGB")))
    return portraits


def preprocess(portrait: Image.Image) -> np.ndarray:
    scale = INPUT_SIZE / min(portrait.size)
    size = tuple(max(INPUT_SIZE, round(dimension * scale)) for dimension in portrait.size)
    resized = portrait.convert("RGB").resize(size, Image.Resampling.BICUBIC)
    left = (resized.width - INPUT_SIZE) // 2
    top = (resized.height - INPUT_SIZE) // 2
    cropped = resized.crop((left, top, left + INPUT_SIZE, top + INPUT_SIZE))
    rgb = np.asarray(cropped, dtype=np.float32).transpose(2, 0, 1) / 255.0
    return ((rgb - MEAN) / STD)[None, ...]


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


def export_encoder(output: Path) -> np.ndarray:
    import onnx
    import timm
    import torch
    from onnxruntime.transformers.onnx_model import OnnxModel

    class NormalizedDino(torch.nn.Module):
        def __init__(self, backbone: torch.nn.Module):
            super().__init__()
            self.backbone = backbone

        def forward(self, image: torch.Tensor) -> torch.Tensor:
            return torch.nn.functional.normalize(self.backbone(image), dim=-1)

    backbone = timm.create_model(MODEL_NAME, pretrained=True, num_classes=0, img_size=INPUT_SIZE).eval()
    model = NormalizedDino(backbone).eval()
    output.parent.mkdir(parents=True, exist_ok=True)
    fp32_output = output.with_name(output.stem + ".fp32.tmp.onnx")
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
    fp16_model.save_model_to_file(output, use_external_data_format=False)
    fp32_output.unlink(missing_ok=True)
    with torch.inference_mode():
        return model(dummy).numpy()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("sts_source", type=Path)
    parser.add_argument("onnx_output", type=Path)
    parser.add_argument("features_output", type=Path)
    parser.add_argument("--batch-size", type=int, default=16, help="Progress reporting interval")
    parser.add_argument("--reuse-onnx", action="store_true", help="Reuse the encoder if it already exists")
    args = parser.parse_args()

    portraits = discover_portrait_paths(args.sts_source.resolve())
    if not portraits:
        raise RuntimeError("No card portrait samples were discovered")

    export_reference = None
    if not args.reuse_onnx or not args.onnx_output.is_file():
        export_reference = export_encoder(args.onnx_output)

    options = ort.SessionOptions()
    options.intra_op_num_threads = 4
    options.inter_op_num_threads = 1
    session = ort.InferenceSession(str(args.onnx_output.resolve()), options, providers=["CPUExecutionProvider"])
    if export_reference is not None:
        dummy = np.zeros((1, 3, INPUT_SIZE, INPUT_SIZE), dtype=np.float32)
        onnx_embedding = session.run(["embedding"], {"image": dummy})[0]
        print(f"ONNX max error={np.max(np.abs(onnx_embedding - export_reference)):.8f}")

    features = np.empty((len(portraits), EMBEDDING_SIZE), dtype=np.float32)
    for index, portrait_path in enumerate(portraits.values(), start=1):
        with Image.open(portrait_path) as portrait:
            embedding = session.run(["embedding"], {"image": preprocess(portrait)})[0]
        if embedding.shape != (1, EMBEDDING_SIZE):
            raise RuntimeError(f"Unexpected embedding shape: {embedding.shape}")
        features[index - 1] = embedding[0] / max(np.linalg.norm(embedding), 1e-12)
        if index % max(1, args.batch_size) == 0 or index == len(portraits):
            print(f"DINOv2 features: {index}/{len(portraits)}", flush=True)

    write_features(args.features_output.resolve(), list(portraits), features)
    print(f"Wrote {args.features_output.resolve()} for {len(portraits)} cards")


if __name__ == "__main__":
    main()
