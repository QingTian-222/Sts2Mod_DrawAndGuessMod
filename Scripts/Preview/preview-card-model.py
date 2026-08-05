#!/usr/bin/env python3
"""Local browser preview for DrawAndGuessMod's card-art retrieval model."""

from __future__ import annotations

import argparse
import base64
import importlib.util
import io
import json
import struct
import threading
import time
import urllib.parse
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import numpy as np
from PIL import Image


SCRIPT_PATH = Path(__file__).resolve()
MOD_ROOT = SCRIPT_PATH.parents[2]
WORKSPACE_ROOT = SCRIPT_PATH.parents[4]
TRAINING_SCRIPT = MOD_ROOT / "Scripts" / "Training" / "train-card-model.py"
DEFAULT_SOURCE = WORKSPACE_ROOT / "game-src" / "Sts110"
DEFAULT_MODEL = MOD_ROOT / "Models" / "card_features.bin"
DEFAULT_DINO_CACHE = MOD_ROOT / "Scripts" / "Preview" / ".semantic-cache" / "dinov2-vits14-lvd142m-224.npz"
DEFAULT_RELIC_DINO_CACHE = (
    MOD_ROOT
    / "Scripts"
    / "Preview"
    / ".semantic-cache"
    / "dinov2-relics-vits14-lvd142m-224.npz"
)
MAX_UPLOAD_BYTES = 20 * 1024 * 1024
DISPLAY_SIZE = (320, 320)
RELIC_RECOGNITION_SIZE = 224
RELIC_ARTWORK_SIZE = 192


def load_training_module():
    spec = importlib.util.spec_from_file_location("draw_and_guess_training", TRAINING_SCRIPT)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"无法加载训练脚本：{TRAINING_SCRIPT}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


TRAINING = load_training_module()


class CardPortraitIndex:
    def __init__(self, sts_source: Path):
        self._portraits_dir = sts_source / "images" / "packed" / "card_portraits"
        sprites_dir = self._portraits_dir
        if not sprites_dir.is_dir():
            raise FileNotFoundError(f"找不到卡图索引目录：{sprites_dir}")

        self._entries: dict[str, Path] = {}
        self._lock = threading.Lock()
        for png_path in sorted(sprites_dir.rglob("*.png")):
            relative_parts = png_path.relative_to(sprites_dir).parts
            if "beta" in relative_parts or png_path.stem in {"beta", "ancient_beta"}:
                continue
            card_id = png_path.stem.upper()
            if card_id in self._entries:
                continue
            self._entries[card_id] = png_path

    @property
    def card_ids(self) -> set[str]:
        return set(self._entries)

    def get(self, card_id: str) -> Image.Image:
        with self._lock:
            with Image.open(self._entries[card_id]) as portrait:
                return portrait.convert("RGB").copy()


class RelicImageIndex:
    def __init__(self, sts_source: Path):
        relics_dir = sts_source / "images" / "relics"
        localization_path = sts_source / "localization" / "zhs" / "relics.json"
        if not relics_dir.is_dir():
            raise FileNotFoundError(f"找不到遗物图片目录：{relics_dir}")

        localization = json.loads(localization_path.read_text(encoding="utf-8"))
        valid_ids = {
            str(key)[:-6].upper()
            for key in localization
            if str(key).endswith(".title")
        }
        entries: dict[str, Path] = {}
        for png_path in sorted(relics_dir.glob("*.png")):
            relic_id = png_path.stem.upper()
            if relic_id in valid_ids:
                entries[relic_id] = png_path

        yummy_cookie = relics_dir / "yummy_cookie_ironclad.png"
        if "YUMMY_COOKIE" in valid_ids and yummy_cookie.is_file():
            entries["YUMMY_COOKIE"] = yummy_cookie

        mod_image_dir = MOD_ROOT / "AssetProject" / "images"
        mod_relics = {
            "DRAW_AND_GUESS_MOD_RELIC_DEATH_NOTE": mod_image_dir / "death_note_relic_big.png",
            "DRAW_AND_GUESS_MOD_RELIC_MEMORIAL_SKETCHBOOK": mod_image_dir / "memorial_sketchbook_relic.png",
        }
        for relic_id, image_path in mod_relics.items():
            if image_path.is_file():
                entries[relic_id] = image_path

        if not entries:
            raise ValueError("没有发现可供 DINOv2 检索的遗物图片")
        self._entries = dict(sorted(entries.items()))
        self._lock = threading.Lock()

    @property
    def relic_ids(self) -> list[str]:
        return list(self._entries)

    def get(self, relic_id: str) -> Image.Image:
        with self._lock:
            with Image.open(self._entries[relic_id]) as source:
                return source.convert("RGBA").copy()


def normalize_relic_artwork(source: Image.Image) -> Image.Image:
    """沿用游戏内遗物分类器的裁切尺寸，为 DINOv2 生成稳定浅底输入。"""

    rgba = source.convert("RGBA")
    alpha = np.asarray(rgba, dtype=np.uint8)[:, :, 3]
    ys, xs = np.nonzero(alpha > 5)
    if len(xs) == 0:
        return Image.new("RGB", (RELIC_RECOGNITION_SIZE, RELIC_RECOGNITION_SIZE), "white")

    bounds = (int(xs.min()), int(ys.min()), int(xs.max()) + 1, int(ys.max()) + 1)
    artwork = rgba.crop(bounds)
    scale = RELIC_ARTWORK_SIZE / max(artwork.size)
    resized_size = (
        max(1, round(artwork.width * scale)),
        max(1, round(artwork.height * scale)),
    )
    artwork = artwork.resize(resized_size, Image.Resampling.LANCZOS)
    background = Image.new(
        "RGBA",
        (RELIC_RECOGNITION_SIZE, RELIC_RECOGNITION_SIZE),
        (242, 243, 245, 255),
    )
    destination = (
        (RELIC_RECOGNITION_SIZE - artwork.width) // 2,
        (RELIC_RECOGNITION_SIZE - artwork.height) // 2,
    )
    background.alpha_composite(artwork, destination)
    return background.convert("RGB")


class FeatureModel:
    def __init__(self, model_path: Path, portrait_index: CardPortraitIndex):
        with model_path.open("rb") as stream:
            magic = stream.read(4)
            if magic != b"DAGM":
                raise ValueError("模型文件 magic 不正确")
            self.version, self.feature_count, sample_count = struct.unpack("<iii", stream.read(12))
            card_ids: list[str] = []
            vectors: list[np.ndarray] = []
            for _ in range(sample_count):
                id_length = struct.unpack("<H", stream.read(2))[0]
                card_id = stream.read(id_length).decode("utf-8")
                vector_bytes = stream.read(self.feature_count * 4)
                if len(vector_bytes) != self.feature_count * 4:
                    raise ValueError("模型文件被截断")
                if card_id in portrait_index.card_ids:
                    card_ids.append(card_id)
                    vectors.append(np.frombuffer(vector_bytes, dtype="<f4").astype(np.float64))

        if not vectors:
            raise ValueError("模型中没有可预览的卡牌")
        self.card_ids = card_ids
        self.features = np.stack(vectors)
        self.sample_count = len(card_ids)
        self._portrait_index = portrait_index

    def distances(self, image: Image.Image) -> tuple[Image.Image, np.ndarray]:
        query = TRAINING.extract_features(image, treat_as_sketch=True).astype(np.float64)
        if query.shape != (self.feature_count,):
            raise ValueError(f"特征维度不匹配：模型={self.feature_count}，上传图片={query.shape[0]}")
        distances = np.linalg.norm(self.features - query[np.newaxis, :], axis=1)
        return make_model_input_image(image, treat_as_sketch=True), distances

    def get_portrait(self, card_id: str) -> Image.Image:
        if card_id not in self.card_ids:
            raise KeyError(card_id)
        return self._portrait_index.get(card_id)


class SemanticFeatureModel:
    def __init__(
        self,
        portrait_index: CardPortraitIndex,
        card_ids: list[str],
        model_name: str,
        cache_path: Path,
        batch_size: int,
    ):
        try:
            import timm
            import torch
            from timm.data import create_transform
        except ImportError as error:
            raise RuntimeError("DINOv2预览需要安装 torch、torchvision 与 timm") from error

        self._torch = torch
        self._lock = threading.Lock()
        self.model_name = model_name
        self.model_key = f"DINOv2:{model_name}:224"
        print(f"[dino] 正在加载 {self.model_key}……")
        load_start = time.perf_counter()
        self._model = timm.create_model(model_name, pretrained=True, num_classes=0, img_size=224)
        self._model.eval()
        self.dimension = int(self._model.num_features)
        self._preprocess = create_transform(
            input_size=(3, 224, 224),
            interpolation="bicubic",
            mean=(0.485, 0.456, 0.406),
            std=(0.229, 0.224, 0.225),
            crop_pct=1.0,
            crop_mode="center",
            is_training=False,
        )
        self.load_ms = (time.perf_counter() - load_start) * 1000.0
        self.features = self._load_or_build_cache(
            portrait_index, card_ids, cache_path.resolve(), max(1, batch_size)
        )

    def _load_or_build_cache(
        self,
        portrait_index: CardPortraitIndex,
        card_ids: list[str],
        cache_path: Path,
        batch_size: int,
    ) -> np.ndarray:
        if cache_path.is_file():
            try:
                with np.load(cache_path, allow_pickle=False) as cache:
                    cached_key = str(cache["model_key"][0])
                    cached_ids = cache["card_ids"].astype(str).tolist()
                    cached_features = cache["features"].astype(np.float32)
                if (
                    cached_key == self.model_key
                    and cached_ids == card_ids
                    and cached_features.shape == (len(card_ids), self.dimension)
                ):
                    print(f"[dino] 已加载语义缓存：{cache_path}（{len(card_ids)} 张）")
                    return cached_features
            except Exception as error:
                print(f"[dino] 忽略无效缓存 {cache_path}：{error}")

        print(f"[dino] 首次建立语义索引，共 {len(card_ids)} 张卡……")
        batches: list[np.ndarray] = []
        start = time.perf_counter()
        with self._torch.inference_mode():
            for offset in range(0, len(card_ids), batch_size):
                batch_ids = card_ids[offset : offset + batch_size]
                tensors = [self._preprocess(portrait_index.get(card_id).convert("RGB")) for card_id in batch_ids]
                batch = self._torch.stack(tensors)
                encoded = self._model(batch)
                encoded = encoded / encoded.norm(dim=-1, keepdim=True).clamp_min(1e-12)
                batches.append(encoded.cpu().float().numpy())
                processed = min(offset + len(batch_ids), len(card_ids))
                print(f"[dino] {processed}/{len(card_ids)}")

        features = np.concatenate(batches, axis=0).astype(np.float32)
        cache_path.parent.mkdir(parents=True, exist_ok=True)
        np.savez_compressed(
            cache_path,
            model_key=np.asarray([self.model_key]),
            card_ids=np.asarray(card_ids),
            features=features,
        )
        elapsed = time.perf_counter() - start
        print(f"[dino] 语义索引已保存：{cache_path}，耗时 {elapsed:.1f} 秒")
        return features

    def similarities(self, image: Image.Image) -> np.ndarray:
        query = self.encode(image)
        return self.features @ query

    def encode(self, image: Image.Image) -> np.ndarray:
        tensor = self._preprocess(image.convert("RGB")).unsqueeze(0)
        with self._lock, self._torch.inference_mode():
            encoded = self._model(tensor)
            encoded = encoded / encoded.norm(dim=-1, keepdim=True).clamp_min(1e-12)
        return encoded.cpu().float().numpy()[0]

    def encode_batch(self, images: list[Image.Image]) -> np.ndarray:
        tensors = [self._preprocess(image.convert("RGB")) for image in images]
        with self._lock, self._torch.inference_mode():
            encoded = self._model(self._torch.stack(tensors))
            encoded = encoded / encoded.norm(dim=-1, keepdim=True).clamp_min(1e-12)
        return encoded.cpu().float().numpy()


class RelicDinoModel:
    def __init__(
        self,
        image_index: RelicImageIndex,
        semantic: SemanticFeatureModel,
        cache_path: Path,
        batch_size: int,
    ):
        self._image_index = image_index
        self._semantic = semantic
        self.relic_ids = image_index.relic_ids
        self.model_key = f"{semantic.model_key}:relic-neutral-background-v1"
        self.features = self._load_or_build_cache(cache_path.resolve(), max(1, batch_size))

    @property
    def sample_count(self) -> int:
        return len(self.relic_ids)

    def _load_or_build_cache(self, cache_path: Path, batch_size: int) -> np.ndarray:
        if cache_path.is_file():
            try:
                with np.load(cache_path, allow_pickle=False) as cache:
                    cached_key = str(cache["model_key"][0])
                    cached_ids = cache["relic_ids"].astype(str).tolist()
                    cached_features = cache["features"].astype(np.float32)
                if (
                    cached_key == self.model_key
                    and cached_ids == self.relic_ids
                    and cached_features.shape == (len(self.relic_ids), self._semantic.dimension)
                ):
                    print(f"[dino-relic] 已加载遗物缓存：{cache_path}（{len(self.relic_ids)} 个）")
                    return cached_features
            except Exception as error:
                print(f"[dino-relic] 忽略无效缓存 {cache_path}：{error}")

        print(f"[dino-relic] 首次建立遗物索引，共 {len(self.relic_ids)} 个遗物……")
        batches: list[np.ndarray] = []
        start = time.perf_counter()
        for offset in range(0, len(self.relic_ids), batch_size):
            batch_ids = self.relic_ids[offset : offset + batch_size]
            images = [normalize_relic_artwork(self._image_index.get(relic_id)) for relic_id in batch_ids]
            batches.append(self._semantic.encode_batch(images))
            processed = min(offset + len(batch_ids), len(self.relic_ids))
            print(f"[dino-relic] {processed}/{len(self.relic_ids)}")

        features = np.concatenate(batches, axis=0).astype(np.float32)
        cache_path.parent.mkdir(parents=True, exist_ok=True)
        np.savez_compressed(
            cache_path,
            model_key=np.asarray([self.model_key]),
            relic_ids=np.asarray(self.relic_ids),
            features=features,
        )
        elapsed = time.perf_counter() - start
        print(f"[dino-relic] 遗物索引已保存：{cache_path}，耗时 {elapsed:.1f} 秒")
        return features

    def predict(self, image: Image.Image) -> list[dict[str, object]]:
        query = self._semantic.encode(normalize_relic_artwork(image))
        similarities = self.features @ query
        ids = np.asarray(self.relic_ids)
        order = np.lexsort((ids, -similarities))
        results: list[dict[str, object]] = []
        for raw_index in order[:3]:
            index = int(raw_index)
            relic_id = self.relic_ids[index]
            results.append(
                {
                    "relic_id": relic_id,
                    "dino_similarity": float(similarities[index]),
                    "original_url": "/image/relic?id=" + urllib.parse.quote(relic_id),
                }
            )
        return results

    def get_image(self, relic_id: str) -> Image.Image:
        if relic_id not in self.relic_ids:
            raise KeyError(relic_id)
        return self._image_index.get(relic_id)


class FusionModel:
    def __init__(self, current: FeatureModel, semantic: SemanticFeatureModel):
        self.current = current
        self.semantic = semantic
        self.card_ids = current.card_ids
        self.version = current.version
        self.feature_count = current.feature_count
        self.sample_count = current.sample_count

    def predict(self, image: Image.Image, dino_weight: float) -> tuple[Image.Image, dict[str, list[dict[str, object]]], dict[str, float]]:
        dino_weight = max(0.0, min(1.0, dino_weight))
        current_start = time.perf_counter()
        processed, distances = self.current.distances(image)
        current_ms = (time.perf_counter() - current_start) * 1000.0

        dino_start = time.perf_counter()
        similarities = self.semantic.similarities(image)
        dino_ms = (time.perf_counter() - dino_start) * 1000.0

        current_scores = standardize(-distances)
        dino_scores = standardize(similarities)
        fusion_scores = (1.0 - dino_weight) * current_scores + dino_weight * dino_scores
        ids = np.asarray(self.card_ids)
        current_order = np.lexsort((ids, distances))
        dino_order = np.lexsort((ids, -similarities))
        fusion_order = np.lexsort((ids, -fusion_scores))

        rankings = {
            "current": self._make_results(current_order, distances, similarities, fusion_scores),
            "dino": self._make_results(dino_order, distances, similarities, fusion_scores),
            "fusion": self._make_results(fusion_order, distances, similarities, fusion_scores),
        }
        return processed, rankings, {"current_ms": current_ms, "dino_ms": dino_ms}

    def _make_results(
        self,
        order: np.ndarray,
        distances: np.ndarray,
        similarities: np.ndarray,
        fusion_scores: np.ndarray,
    ) -> list[dict[str, object]]:
        results: list[dict[str, object]] = []
        for raw_index in order[:3]:
            index = int(raw_index)
            distance = float(distances[index])
            card_id = self.card_ids[index]
            results.append(
                {
                    "card_id": card_id,
                    "distance": distance,
                    "match_score": max(0.0, min(1.0, 1.0 - distance / 2.0)),
                    "dino_similarity": float(similarities[index]),
                    "fusion_score": float(fusion_scores[index]),
                    "original_url": "/image/original?id=" + urllib.parse.quote(card_id),
                    "processed_url": "/image/processed?id=" + urllib.parse.quote(card_id),
                }
            )
        return results

    def get_portrait(self, card_id: str) -> Image.Image:
        return self.current.get_portrait(card_id)


def standardize(values: np.ndarray) -> np.ndarray:
    deviation = float(values.std())
    if deviation < 1e-8:
        return np.zeros_like(values)
    return (values - float(values.mean())) / deviation


def make_model_input_image(image: Image.Image, treat_as_sketch: bool) -> Image.Image:
    resized = image.convert("RGB").resize(
        (TRAINING.SAMPLE_SIZE, TRAINING.SAMPLE_SIZE), Image.Resampling.LANCZOS
    )
    return resized


def png_bytes(image: Image.Image, upscale_model_input: bool = False) -> bytes:
    output = image
    if upscale_model_input:
        output = image.resize(DISPLAY_SIZE, Image.Resampling.NEAREST)
    buffer = io.BytesIO()
    output.save(buffer, format="PNG")
    return buffer.getvalue()


def data_url(image: Image.Image, upscale_model_input: bool = False) -> str:
    encoded = base64.b64encode(png_bytes(image, upscale_model_input)).decode("ascii")
    return "data:image/png;base64," + encoded


HTML = r"""<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>你画我猜 · 模型预览</title>
<style>
:root { color-scheme: dark; --bg:#111722; --panel:#1b2432; --line:#34445a; --gold:#f2c45c; --muted:#9eb0c7; }
* { box-sizing:border-box; }
body { margin:0; font-family:"Segoe UI","Microsoft YaHei",sans-serif; background:radial-gradient(circle at top,#22314a 0,#111722 46%); color:#edf3fa; }
main { width:min(1240px,calc(100% - 32px)); margin:28px auto 56px; }
h1 { margin:0 0 8px; font-size:30px; }
.subtitle,.note { color:var(--muted); line-height:1.65; }
.panel { background:rgba(27,36,50,.94); border:1px solid var(--line); border-radius:16px; padding:20px; margin-top:18px; box-shadow:0 15px 40px #0005; }
.toolbar { display:flex; gap:10px; flex-wrap:wrap; align-items:center; }
input,button { border:1px solid #4b607b; border-radius:9px; background:#101722; color:#edf3fa; padding:10px 12px; font:inherit; }
input[type=text] { width:min(430px,100%); }
input[type=range] { width:220px; padding:0; accent-color:var(--gold); }
button { cursor:pointer; background:#29405e; }
button:hover { border-color:var(--gold); }
.meta { margin-left:auto; color:var(--muted); }
.images { display:grid; grid-template-columns:repeat(auto-fit,minmax(260px,1fr)); gap:16px; margin-top:16px; }
.image-card { border:1px solid var(--line); border-radius:12px; padding:12px; background:#121a26; }
.image-card h3 { margin:0 0 10px; font-size:15px; color:#c9d8e9; }
.image-card img { display:block; width:100%; height:243px; object-fit:contain; border-radius:8px; background:#090d13; image-rendering:auto; }
.image-card img.pixelated { image-rendering:pixelated; }
.upload { border:1px dashed #59718f; border-radius:12px; padding:18px; text-align:center; }
.upload.drag { border-color:var(--gold); background:#f2c45c10; }
.results { display:grid; grid-template-columns:repeat(auto-fit,minmax(310px,1fr)); gap:16px; margin-top:18px; }
.result-group { margin-top:22px; }
.result-group h2 { margin:0; font-size:21px; }
.result-group .group-note { color:var(--muted); margin-top:5px; }
.result { border:1px solid var(--line); border-radius:13px; overflow:hidden; background:#111925; }
.rank { padding:12px 14px; font-size:18px; font-weight:700; }
.rank span { color:var(--gold); margin-right:8px; }
.result-images { display:grid; grid-template-columns:1fr 1fr; gap:1px; background:var(--line); }
.result-images figure { margin:0; padding:8px; background:#111925; }
.result-images img { width:100%; height:150px; object-fit:contain; background:#080c12; border-radius:6px; }
.result-images figcaption { color:var(--muted); text-align:center; font-size:12px; margin-top:5px; }
.metrics { padding:12px 14px; display:grid; gap:6px; }
.metric { display:flex; justify-content:space-between; gap:12px; }
.metric b { color:#fff; }
.hidden { display:none !important; }
.error { color:#ff9c9c; white-space:pre-wrap; }
</style>
</head>
<body><main>
<h1>你画我猜 · 模型预览</h1>
<div class="subtitle">查看卡图经过 32×32 缩放后的真实模型输入，并测试任意图片的 Top 3 检索结果。</div>

<section class="panel">
  <div class="toolbar">
    <input id="cardInput" type="text" list="cardList" placeholder="输入卡牌 ID，例如 COSMIC_INDIFFERENCE">
    <datalist id="cardList"></datalist>
    <button id="showCard">查看卡图</button><button id="prevCard">上一张</button><button id="nextCard">下一张</button>
    <div id="modelMeta" class="meta"></div>
  </div>
  <div class="images">
    <div class="image-card"><h3 id="originalTitle">原始卡图</h3><img id="cardOriginal"></div>
    <div class="image-card"><h3>模型实际输入：32×32 → 最近邻放大</h3><img id="cardProcessed" class="pixelated"></div>
  </div>
</section>

<section class="panel">
  <div id="dropZone" class="upload">
    <input id="fileInput" type="file" accept="image/*">
    <div class="note">选择图片或拖放到这里。请上传绘画区域本身；完整卡牌截图包含边框与文字，会与游戏内识别结果不同。上传只发送到本机 127.0.0.1，不会联网。</div>
  </div>
  <div class="toolbar" style="margin-top:16px">
    <label for="dinoWeight">融合中的DINOv2权重</label>
    <input id="dinoWeight" type="range" min="0" max="100" step="5" value="50">
    <b id="dinoWeightValue">50%</b>
    <span class="note">其余30%为当前384维特征</span>
  </div>
  <div id="status" class="note"></div>
  <div id="queryPreview" class="images hidden">
    <div class="image-card"><h3>上传原图</h3><img id="queryOriginal"></div>
    <div class="image-card"><h3>上传图的模型输入</h3><img id="queryProcessed" class="pixelated"></div>
  </div>
  <div id="results"></div>
  <div class="note" style="margin-top:14px">融合时，当前模型使用负欧氏距离，DINOv2使用余弦相似度；两组分数分别在当前候选池内做 z-score 后再按滑条加权。融合分数与“匹配度”均未通过玩家绘画验证集校准，不是真实准确率。</div>
</section>
</main>
<script>
const state={cards:[],index:0,file:null};
const $=id=>document.getElementById(id);
async function loadMeta(){
  const data=await (await fetch('/api/cards')).json(); state.cards=data.cards;
  $('modelMeta').textContent=`当前模型 v${data.version} · ${data.samples} 张 · ${data.features} 维；${data.dino.model} · ${data.dino.features} 维`;
  $('cardList').innerHTML=state.cards.map(id=>`<option value="${id}"></option>`).join('');
  const preferred=state.cards.includes('COSMIC_INDIFFERENCE')?'COSMIC_INDIFFERENCE':state.cards[0]; showCard(preferred);
}
function showCard(id){
  id=(id||'').trim().toUpperCase(); const index=state.cards.indexOf(id);
  if(index<0){ $('status').innerHTML='<span class="error">未找到卡牌 ID：'+id+'</span>'; return; }
  state.index=index; $('cardInput').value=id; $('originalTitle').textContent=`原始卡图 · ${id}`;
  $('cardOriginal').src='/image/original?id='+encodeURIComponent(id);
  $('cardProcessed').src='/image/processed?id='+encodeURIComponent(id);
}
$('showCard').onclick=()=>showCard($('cardInput').value);
$('cardInput').addEventListener('keydown',e=>{if(e.key==='Enter')showCard(e.target.value)});
$('prevCard').onclick=()=>showCard(state.cards[(state.index-1+state.cards.length)%state.cards.length]);
$('nextCard').onclick=()=>showCard(state.cards[(state.index+1)%state.cards.length]);
async function predict(file){
  if(!file)return; state.file=file; $('status').textContent=`正在提取特征并检索 ${state.cards.length} 张卡图……`; $('results').innerHTML='';
  $('queryOriginal').src=URL.createObjectURL(file); $('queryPreview').classList.remove('hidden');
  try{
    const dinoWeight=Number($('dinoWeight').value)/100;
    const response=await fetch('/api/predict?dino_weight='+dinoWeight,{method:'POST',headers:{'Content-Type':file.type||'application/octet-stream'},body:file});
    const data=await response.json(); if(!response.ok)throw new Error(data.error||response.statusText);
    $('queryProcessed').src=data.processed_image;
    $('status').textContent=`完成：总计 ${data.elapsed_ms.toFixed(1)} ms（当前特征 ${data.timings.current_ms.toFixed(1)} ms，DINOv2 ${data.timings.dino_ms.toFixed(1)} ms）`;
    const renderCards=items=>`<div class="results">${items.map((r,i)=>`<article class="result">
      <div class="rank"><span>#${i+1}</span>${r.card_id}</div>
      <div class="result-images"><figure><img src="${r.original_url}"><figcaption>原图</figcaption></figure><figure><img class="pixelated" src="${r.processed_url}"><figcaption>当前模型输入</figcaption></figure></div>
      <div class="metrics"><div class="metric">当前欧氏距离 <b>${r.distance.toFixed(4)}</b></div><div class="metric">当前匹配度 <b>${(r.match_score*100).toFixed(2)}%</b></div><div class="metric">DINOv2余弦相似度 <b>${(r.dino_similarity*100).toFixed(2)}%</b></div><div class="metric">融合 z-score <b>${r.fusion_score.toFixed(4)}</b></div></div>
    </article>`).join('')}</div>`;
    $('results').innerHTML=`
      <section class="result-group"><h2>融合模型 Top 3</h2><div class="group-note">DINOv2 ${Math.round(data.dino_weight*100)}% + 当前特征 ${Math.round((1-data.dino_weight)*100)}%</div>${renderCards(data.results.fusion)}</section>
      <section class="result-group"><h2>纯DINOv2 Top 3</h2><div class="group-note">仅按视觉向量余弦相似度排序</div>${renderCards(data.results.dino)}</section>
      <section class="result-group"><h2>当前模型 Top 3</h2><div class="group-note">与游戏当前实现一致，按384维特征欧氏距离排序</div>${renderCards(data.results.current)}</section>`;
  }catch(error){$('status').innerHTML='<span class="error">'+error.message+'</span>'}
}
$('dinoWeight').oninput=e=>$('dinoWeightValue').textContent=e.target.value+'%';
$('dinoWeight').onchange=()=>state.file&&predict(state.file);
$('fileInput').onchange=e=>predict(e.target.files[0]);
for(const type of ['dragenter','dragover'])$('dropZone').addEventListener(type,e=>{e.preventDefault();$('dropZone').classList.add('drag')});
for(const type of ['dragleave','drop'])$('dropZone').addEventListener(type,e=>{e.preventDefault();$('dropZone').classList.remove('drag')});
$('dropZone').addEventListener('drop',e=>predict(e.dataTransfer.files[0]));
loadMeta().catch(error=>$('status').innerHTML='<span class="error">'+error.message+'</span>');
</script></body></html>"""


class PreviewHandler(BaseHTTPRequestHandler):
    model: FusionModel
    relic_model: RelicDinoModel

    def do_GET(self) -> None:
        parsed = urllib.parse.urlparse(self.path)
        try:
            if parsed.path == "/":
                self.send_bytes(HTML.encode("utf-8"), "text/html; charset=utf-8")
                return
            if parsed.path == "/api/cards":
                self.send_json(
                    {
                        "cards": self.model.card_ids,
                        "version": self.model.version,
                        "features": self.model.feature_count,
                        "samples": self.model.sample_count,
                        "dino": {
                            "model": self.model.semantic.model_key,
                            "features": self.model.semantic.dimension,
                            "load_ms": self.model.semantic.load_ms,
                        },
                        "relics": {
                            "samples": self.relic_model.sample_count,
                            "model": self.relic_model.model_key,
                        },
                    }
                )
                return
            if parsed.path == "/image/relic":
                query = urllib.parse.parse_qs(parsed.query)
                relic_id = query.get("id", [""])[0].upper()
                self.send_bytes(png_bytes(self.relic_model.get_image(relic_id)), "image/png")
                return
            if parsed.path in ("/image/original", "/image/processed"):
                query = urllib.parse.parse_qs(parsed.query)
                card_id = query.get("id", [""])[0].upper()
                portrait = self.model.get_portrait(card_id)
                if parsed.path == "/image/processed":
                    self.send_bytes(
                        png_bytes(make_model_input_image(portrait, treat_as_sketch=False), True),
                        "image/png",
                    )
                else:
                    self.send_bytes(png_bytes(portrait), "image/png")
                return
            self.send_error(404)
        except KeyError:
            self.send_json({"error": "未知卡牌 ID"}, status=404)
        except Exception as error:
            self.send_json({"error": str(error)}, status=500)

    def do_POST(self) -> None:
        parsed = urllib.parse.urlparse(self.path)
        if parsed.path not in ("/api/predict", "/api/predict-relic"):
            self.send_error(404)
            return
        try:
            content_length = int(self.headers.get("Content-Length", "0"))
            if content_length <= 0 or content_length > MAX_UPLOAD_BYTES:
                raise ValueError("图片为空或超过 20 MB")
            payload = self.rfile.read(content_length)
            image = Image.open(io.BytesIO(payload))
            image.load()
            if parsed.path == "/api/predict-relic":
                start = time.perf_counter()
                results = self.relic_model.predict(image)
                elapsed_ms = (time.perf_counter() - start) * 1000.0
                self.send_json(
                    {
                        "results": results,
                        "model": self.relic_model.model_key,
                        "elapsed_ms": elapsed_ms,
                    }
                )
                return
            query = urllib.parse.parse_qs(parsed.query)
            dino_weight = float(query.get("dino_weight", ["0.5"])[0])
            start = time.perf_counter()
            processed, results, timings = self.model.predict(image, dino_weight)
            elapsed_ms = (time.perf_counter() - start) * 1000.0
            self.send_json(
                {
                    "processed_image": data_url(processed, True),
                    "results": results,
                    "timings": timings,
                    "dino_weight": max(0.0, min(1.0, dino_weight)),
                    "elapsed_ms": elapsed_ms,
                }
            )
        except Exception as error:
            self.send_json({"error": str(error)}, status=400)

    def send_json(self, value: object, status: int = 200) -> None:
        self.send_bytes(json.dumps(value, ensure_ascii=False).encode("utf-8"), "application/json; charset=utf-8", status)

    def send_bytes(self, payload: bytes, content_type: str, status: int = 200) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, format_string: str, *args: object) -> None:
        print("[preview] " + format_string % args)


def main() -> None:
    parser = argparse.ArgumentParser(description="DrawAndGuessMod 游戏外识别预览")
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE, help="game-src/Sts110 路径")
    parser.add_argument("--model", type=Path, default=DEFAULT_MODEL, help="card_features.bin 路径")
    parser.add_argument("--dino-model", default="vit_small_patch14_dinov2.lvd142m", help="timm DINOv2模型名")
    parser.add_argument("--dino-cache", type=Path, default=DEFAULT_DINO_CACHE, help="卡图DINOv2向量缓存")
    parser.add_argument(
        "--relic-dino-cache",
        type=Path,
        default=DEFAULT_RELIC_DINO_CACHE,
        help="遗物图DINOv2向量缓存",
    )
    parser.add_argument("--dino-batch-size", type=int, default=16, help="首次建立DINOv2索引的批大小")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--no-browser", action="store_true")
    args = parser.parse_args()

    portrait_index = CardPortraitIndex(args.source.resolve())
    current_model = FeatureModel(args.model.resolve(), portrait_index)
    semantic_model = SemanticFeatureModel(
        portrait_index,
        current_model.card_ids,
        args.dino_model,
        args.dino_cache,
        args.dino_batch_size,
    )
    PreviewHandler.model = FusionModel(current_model, semantic_model)
    relic_image_index = RelicImageIndex(args.source.resolve())
    PreviewHandler.relic_model = RelicDinoModel(
        relic_image_index,
        semantic_model,
        args.relic_dino_cache,
        args.dino_batch_size,
    )
    server = ThreadingHTTPServer(("127.0.0.1", args.port), PreviewHandler)
    url = f"http://127.0.0.1:{args.port}/"
    print(
        f"DrawAndGuessMod 模型预览已启动：{url}\n"
        f"模型 v{PreviewHandler.model.version}，{PreviewHandler.model.sample_count} 张卡，"
        f"当前特征 {PreviewHandler.model.feature_count} 维，"
        f"DINOv2 {PreviewHandler.model.semantic.dimension} 维，"
        f"{PreviewHandler.relic_model.sample_count} 个遗物。按 Ctrl+C 退出。"
    )
    if not args.no_browser:
        threading.Timer(0.35, lambda: webbrowser.open(url)).start()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
