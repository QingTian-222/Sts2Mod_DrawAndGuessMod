# Synthetic Sketch Adapter: Training and Evaluation

本文记录实验性“自训练适配器”的数据来源、验证集划分和 Top-1/Top-3
结果。简要结论：在合成测试集上，`30%` 手工特征与 `70%`
适配后 DINOv2 的融合优于原始 DINOv2、纯适配 DINOv2 和 `50/50`
融合。但这些数字不能当作真实玩家准确率，原因见“限制”一节。

This writeup documents the data source, validation split, model, and Top-1/Top-3
results for the experimental trained sketch adapter. The short result is that a
`30%` handcrafted / `70%` adapted-DINOv2 fusion performed best on the synthetic
holdout. These measurements are not estimates of real-player accuracy.

## Artifact

- File: `Models/sketch_adapter.onnx`
- SHA-256: `4f99320d3a12cc6b164446dd3fbc02ac532c1bc7a4cc3ad81cee84372290ac13`
- ONNX opset: 18
- Input: normalized DINOv2 embedding, float32 `[batch, 384]`, name `embedding`
- Output: adapted normalized embedding, float32 `[batch, 384]`, name
  `adapted_embedding`
- Parameters: 394,880
- Export parity against PyTorch:
  - maximum absolute error: `1.1920929e-7`
  - minimum cosine similarity: `0.99999988`
  - maximum output-norm error: `2.3841858e-7`

Only the 1.5 MB adapter is new. DINOv2 ViT-S/14 remains frozen and uses the
existing `Models/dinov2_vits14.onnx` file.

## Data source

The source set contained 605 non-beta card-portrait entries extracted from a
locally owned copy of *Slay the Spire 2*. Two portrait stems occurred in more
than one category, leaving 603 unique runtime card IDs. No card image, game
DLL, decompiled source, log, or local filesystem path is included in this
repository.

Each portrait produced two deterministic variants in each of five synthetic
style families:

- `lineart`: mostly monochrome edge drawing;
- `color_edges`: faded quantized color plus outlines;
- `poster`: low-color posterization plus outlines;
- `sparse`: partial line drawing with erased regions;
- `marker`: broad quantized marker-like regions and missing strokes.

The generator also applies rotation, scale, translation, palette changes, and
random erasing. With seed `222`, the resulting set contains 6,050 synthetic
sketches.

## Split

The split is deterministic and grouped by card identity:

| Split | Card identities | Styles | Sketches | Purpose |
| --- | ---: | --- | ---: | --- |
| `train` | 485 | lineart, color_edges, poster | 2,910 | optimizer updates |
| `val_seen` | same 485 | sparse, marker | 1,940 | held-out styles |
| `val_zero` | 60 | all five | 600 | held-out positive identities and model selection |
| `test_zero` | 60 | all five | 600 | final report only |

`val_zero` and `test_zero` contain no positive sketch examples for their card
identities during training. Their card prototypes remain in the retrieval
gallery and therefore appear as negatives in the contrastive denominator.
This is intentional: the runtime task also retrieves against a known gallery.

## Preprocessing and architecture

The adapter was retrained specifically for this repository's preprocessing:

1. scale the image so its shorter side is 224 pixels;
2. take a centered 224×224 crop;
3. apply ImageNet mean and standard deviation;
4. run frozen `vit_small_patch14_dinov2.lvd142m`;
5. L2-normalize the 384-dimensional embedding.

The trainable adapter is:

```text
output = L2Normalize(
    input
    + Linear(512 → 384)(
        Dropout(0.10)(
            GELU(
                Linear(384 → 512)(
                    LayerNorm(input)
                )
            )
        )
    )
)
```

Only the adapter is trained. The DINOv2 backbone and card prototypes are
frozen.

## Optimization

- optimizer: AdamW
- learning rate: `3e-4`
- weight decay: `1e-4`
- batch size: 256
- cosine learning-rate schedule
- contrastive temperature: `0.07`
- label smoothing: `0.02`
- identity-preserving MSE weight: `0.01`
- maximum epochs: 40
- early-stopping patience: 8
- selected checkpoint: epoch 15
- checkpoint selection: mean Top-3 across `val_seen` and `val_zero`

## Adapter retrieval results

This table compares raw DINOv2 retrieval with pure adapted-DINOv2 retrieval.
The gallery contains all 605 portrait entries.

| Split | Raw DINO Top-1 | Adapter Top-1 | Raw DINO Top-3 | Adapter Top-3 |
| --- | ---: | ---: | ---: | ---: |
| `val_seen` | 37.22% | 86.86% | 46.96% | 93.61% |
| `val_zero` | 57.67% | 66.50% | 68.50% | 81.83% |
| `test_zero` | 49.33% | 62.33% | 63.33% | 80.17% |

Training-set results are deliberately omitted from the accuracy claim.

## Complete runtime comparison

The runtime first z-score standardizes handcrafted and DINOv2 scores within the
current candidate pool. A fixed-weight sweep was then run using the same
handcrafted feature implementation as the game. DINO weight was varied from
`0.0` through `1.0` in increments of `0.1`.

| Method | `val_zero` Top-1 | `val_zero` Top-3 | `test_zero` Top-1 | `test_zero` Top-3 |
| --- | ---: | ---: | ---: | ---: |
| VAKUU: 100% handcrafted | 7.50% | 14.83% | 10.17% | 17.00% |
| 100% raw DINOv2 | 58.33% | 68.50% | 49.33% | 63.33% |
| Defect: original handcrafted/raw-DINO 50/50 | 53.83% | 68.33% | 48.67% | 60.17% |
| Adapted DINO, 50/50 fusion | 64.17% | 78.17% | 61.00% | 75.50% |
| 100% adapted DINO | 66.50% | 81.83% | 62.33% | 80.17% |
| **30% handcrafted / 70% adapted DINO** | **72.33%** | **83.67%** | **68.00%** | **83.50%** |
| 20% handcrafted / 80% adapted DINO | 72.00% | 84.17% | 67.83% | 84.33% |

The full comparison shows three separate effects:

- raw DINOv2 is much stronger than the handcrafted branch on this synthetic
  domain;
- the original raw-DINO `50/50` fusion is slightly worse than raw DINOv2 alone,
  because the weak handcrafted score receives too much weight;
- the adapter improves pure DINOv2, and retaining a smaller `30%` handcrafted
  contribution improves Top-1 again.

On `test_zero`, pure adapted DINO improves over raw DINO by 13.00 percentage
points Top-1 and 16.84 points Top-3. The selected `30/70` fusion improves over
raw DINO by 18.67 points Top-1 and 20.17 points Top-3, and improves over the
original `50/50` mode by 19.33 and 23.33 points respectively.

The game ultimately selects the Top-1 candidate, so `70%` DINO was selected on
`val_zero`. An `80%` DINO weight has the highest synthetic Top-3, but its Top-1
is slightly lower. The untouched `test_zero` result is reported afterward. The
handcrafted branch remains useful: it compensates for failure modes where the
semantic model overweights broad appearance and underweights marker color or
spatial edge cues.

The PyTorch retrieval table above reports 57.67% raw-DINO Top-1 on `val_zero`,
while the end-to-end fusion evaluator reports 58.33%. Four samples change rank
because the Torch and NumPy evaluators use different floating-point precision
and tie ordering. The `test_zero` raw-DINO metrics are identical.

If the adapter cannot be loaded or evaluated, the implementation falls back to
the existing raw-DINO `50/50` fusion rather than changing gameplay behavior.

## Limitations

- All drawings are synthetic transformations of card art. They are cleaner and
  more directly related to their target portrait than real human drawings.
- There is no consented, artist-grouped player-drawing test set yet.
- The reported gallery has 605 portrait entries, while the live game may expose
  a different number of base-game and modded cards.
- The Python evaluator reproduces the C# feature equations, but Pillow and
  Godot image resampling can differ slightly at pixel boundaries.
- Weight selection used synthetic validation data and may not be optimal for
  real players.

The appropriate next validation is a consent-based set of real drawings, split
by artist so drawings from one person cannot appear in both training and test
sets. Until then, the adapter remains an explicitly experimental option.
