#!/usr/bin/env python3
"""Compare two DINOv2 ONNX encoders on the installed vanilla card portraits."""

from __future__ import annotations

import argparse
import importlib.util
import time
from pathlib import Path

import numpy as np
import onnxruntime as ort
from PIL import Image


SCRIPT_DIR = Path(__file__).resolve().parent
BUILD_SCRIPT = SCRIPT_DIR / "build-dino-model.py"
MEAN = np.asarray((0.485, 0.456, 0.406), dtype=np.float32).reshape(3, 1, 1)
STD = np.asarray((0.229, 0.224, 0.225), dtype=np.float32).reshape(3, 1, 1)


def load_build_module():
    spec = importlib.util.spec_from_file_location("draw_and_guess_dino_build", BUILD_SCRIPT)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Cannot load {BUILD_SCRIPT}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def preprocess(image: Image.Image) -> np.ndarray:
    image = image.convert("RGB")
    width, height = image.size
    scale = 224.0 / min(width, height)
    resized = image.resize(
        (max(224, round(width * scale)), max(224, round(height * scale))),
        Image.Resampling.BICUBIC,
    )
    left = (resized.width - 224) // 2
    top = (resized.height - 224) // 2
    cropped = resized.crop((left, top, left + 224, top + 224))
    array = np.asarray(cropped, dtype=np.float32).transpose(2, 0, 1) / 255.0
    return ((array - MEAN) / STD)[None, ...]


def extract(session: ort.InferenceSession, inputs: list[np.ndarray]) -> tuple[np.ndarray, float]:
    session.run(["embedding"], {"image": inputs[0]})
    started = time.perf_counter()
    rows = [session.run(["embedding"], {"image": value})[0][0] for value in inputs]
    elapsed_ms = (time.perf_counter() - started) * 1000.0
    features = np.asarray(rows, dtype=np.float32)
    features /= np.maximum(np.linalg.norm(features, axis=1, keepdims=True), 1e-12)
    return features, elapsed_ms


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("sts_source", type=Path)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("candidates", type=Path, nargs="+")
    args = parser.parse_args()

    build_module = load_build_module()
    portraits = build_module.discover_portraits(args.sts_source.resolve())
    inputs = [preprocess(portrait) for _, portrait in portraits]
    baseline_session = ort.InferenceSession(str(args.baseline.resolve()), providers=["CPUExecutionProvider"])
    baseline, baseline_ms = extract(baseline_session, inputs)
    baseline_similarity = baseline @ baseline.T
    np.fill_diagonal(baseline_similarity, -np.inf)
    baseline_top3 = np.argsort(-baseline_similarity, axis=1)[:, :3]
    print(f"cards={len(portraits)}")
    print(f"baseline latency={baseline_ms / len(inputs):.2f} ms/image")
    for candidate_path in args.candidates:
        candidate_session = ort.InferenceSession(str(candidate_path.resolve()), providers=["CPUExecutionProvider"])
        candidate, candidate_ms = extract(candidate_session, inputs)
        pair_cosines = np.sum(baseline * candidate, axis=1)
        candidate_similarity = candidate @ candidate.T
        np.fill_diagonal(candidate_similarity, -np.inf)
        candidate_top3 = np.argsort(-candidate_similarity, axis=1)[:, :3]
        top1_agreement = np.mean(baseline_top3[:, 0] == candidate_top3[:, 0])
        top3_exact = np.mean(
            [set(left.tolist()) == set(right.tolist()) for left, right in zip(baseline_top3, candidate_top3, strict=True)]
        )
        top3_overlap = np.mean(
            [len(set(left.tolist()) & set(right.tolist())) / 3.0 for left, right in zip(baseline_top3, candidate_top3, strict=True)]
        )
        print(f"candidate={candidate_path.name}")
        print(
            f"  embedding cosine: mean={pair_cosines.mean():.8f}, "
            f"p01={np.quantile(pair_cosines, 0.01):.8f}, min={pair_cosines.min():.8f}"
        )
        print(
            f"  nearest-neighbor agreement: top1={top1_agreement:.2%}, "
            f"top3_exact={top3_exact:.2%}, top3_overlap={top3_overlap:.2%}"
        )
        print(
            f"  CPU latency={candidate_ms / len(inputs):.2f} ms/image, "
            f"ratio={candidate_ms / baseline_ms:.3f}"
        )


if __name__ == "__main__":
    main()
