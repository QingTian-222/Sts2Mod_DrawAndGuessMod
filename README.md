# DrawAndGuessMod（你画瓦猜）

[简体中文](#drawandguessmod你画瓦猜) | [English](#drawandguessmod-draw--guess)

《Slay the Spire 2》绘画猜卡模组。打出“空白”后，玩家可以绘制一张卡面，瓦库会从当前游戏卡池中给出三个候选；选择结果会加入手牌和牌组，并在本局游戏中使用玩家绘制的卡面。

## 功能

### “空白”与作画

- 打出“空白”后绘制卡面，瓦库会给出最接近的三个候选。选中的卡牌加入手牌，并默认同时加入牌组；画作会在本局游戏中替换该卡牌的卡面。
- 作画工具包括左右键独立画笔颜色、填充、RGB 调色盘、中键吸管和五名角色印花；鼠标滚轮可以调整画笔或印花大小。
- 支持普通卡面与先古卡面两种画布，并会在最终卡牌类型不同时自动调整画作比例。
- `Ctrl+Z` 撤销，`Ctrl+Y` 或 `Ctrl+Shift+Z` 重做。单人和多人模式均可使用。
- 可以暂时观察战局而不关闭作画界面；观察时绘图快捷键会暂停，避免与游戏操作冲突。
- 普通“空白”可设置 15、30、60、120 秒或自定义作画时间，也可以完全关闭时间限制。

### 识别模型与卡池

- “瓦库”模式使用轻量特征算法；“鸡煲”模式将轻量特征与 DINOv2 视觉特征按 `50% + 50%` 融合。
- 支持全卡池或当前角色卡池，并可在高级选项中检测当前已加载卡池、单独关闭不希望参与识别的卡池。
- 内置 0.107 版本原版 603 幅卡图的识别缓存。设置页可以扫描当前安装的原版及模组卡牌、重建本地缓存并显示实时进度。
- 可以选择是否包含多人专属卡牌，默认启用。

### 游戏设置

- 涅奥界面新增本模组的设置入口，可以在进入正式路线前调整本局规则；多人模式下由房主设置并同步给所有玩家。
- 可以排除本局中已经通过“空白”选择过的卡牌；多人共享选择记录并使用房主设置，默认关闭。
- 可以让“空白”生成的卡牌只进入手牌而不进入牌组，默认关闭。
- 可以在对局开始时获得一张“空白”，默认关闭。
- 可调整识别模型、候选卡池和作画时间，并通过高级选项精细控制候选范围。
- “宝箱房画遗物”默认开启：多人进入宝箱房后，每名符合原版开箱条件的玩家分别绘制一个由原版宝箱逻辑生成的候选遗物；全部提交后自动开箱。关闭后才会出现“鉴宝大会”事件。
- 可以开启临摹参考面板，在作画界面右侧搜索卡牌并显示其卡图；鼠标中键点击参考图即可取色并设为左键颜色。
- 设置页的“历史画作”会按完成时间倒序保存和展示“空白”、画廊挑战及遗物鉴定的作品；可重命名、继续编辑、复制 PNG 或删除记录。

### 多人模式

- 所有玩家可以协作完成同一幅画；出牌者指定目标并确认画作，被指定的玩家负责三选一，最终卡面同步给所有玩家。
- 每名玩家只能撤销或重做自己的操作；出牌者负责权威排序并同步最终画布。
- 作画时间和相关对局设置以房主为准。
- 宝箱房遗物绘画中，每名玩家只能看到自己的目标；作品完成后进入原版多人遗物争夺流程。

### 事件、遗物与工具

- “瓦库的无限画廊”包含限时连续作画、普通挑战和连胜奖励。
- “死亡绘本”可以在火堆通过绘画将卡牌从本局游戏中消除；“纪念绘本”记录无限画廊的限时连胜作品。
- 游戏内卡牌、设置页和绘图界面支持中文与英文；非中文语言默认回退英文。
- 游戏外模型预览工具可以并列测试轻量特征、DINOv2 特征及可调权重融合的前三个候选。

## 依赖

- Slay the Spire 2 `0.107` 兼容分支；请从 GitHub Release 下载名称中含 `game-0.107` 的压缩包。创意工坊会按游戏分支分发对应版本。
- [STS2-RitsuLib](https://www.nuget.org/packages/STS2.RitsuLib/) `0.5.11` 或更高版本（自动选择 0.107 兼容构建）。
- 游戏内 DINOv2 推理使用随模组发布的 ONNX Runtime，普通玩家不需要安装 Python、PyTorch 或其他 AI 环境。
- Windows 上使用“鸡煲（DINOv2）”识别模型需要 [Microsoft Visual C++ 2015–2022 Redistributable（x64）](https://aka.ms/vc14/vc_redist.x64.exe)。
- .NET 9 SDK（仅开发和编译时需要）。
- Python、NumPy、Pillow（仅重新生成模型或运行预览工具时需要）；DINOv2融合预览另需 PyTorch、TorchVision 与 `timm`。

### Windows 启动黑屏

如果游戏启动后持续黑屏，且日志停在 `Preloaded ... DINOv2 card-art embeddings`，通常是缺少或损坏了 Visual C++ 运行库。请从微软官方下载并运行 [Visual C++ 2015–2022 Redistributable（x64）](https://aka.ms/vc14/vc_redist.x64.exe)：未安装时选择安装，已经安装时选择修复，完成后重新启动游戏。无需安装 x86 版本，也无需安装 Python 或其他 AI 开发环境。

## 编译

在仓库目录执行：

```powershell
dotnet build DrawAndGuessMod.csproj -c Release -p:Sts2Dir="你的 Slay the Spire 2 安装目录"
```

例如：

```powershell
dotnet build DrawAndGuessMod.csproj -c Release -p:Sts2Dir="G:\SteamLibrary\steamapps\common\Slay the Spire 2"
```

如果设置了环境变量 `STS2_DIR`，可以省略 `-p:Sts2Dir=...`。成功编译后，项目会将 DLL、模组清单和模型复制到游戏的 `mods/DrawAndGuessMod` 目录。

## 当前识别模型

游戏内使用两个 384 维视觉特征分支进行最近邻检索：

1. 轻量分支将图像缩放到 32×32，提取边缘、空间颜色和色相特征，并计算欧氏距离。
2. DINOv2 ViT-S/14 分支将图像等比缩放并中心裁剪到 224×224，通过 ONNX Runtime 的 FP16 模型提取 L2 归一化视觉特征，并计算余弦相似度。
3. “瓦库”模式只按轻量分支排序；“鸡煲”模式将两个分支分别在当前候选卡池内做 z-score 标准化，再按 `50% + 50%` 融合排序。
4. 实验性的“自训练适配器”先用一个 394,880 参数的残差 MLP 将画作 DINOv2 特征映射到卡图特征空间，再按 `30%` 特征提取和 `70%` 适配后 DINOv2 融合。三种模式都给出前三名。

0.107 版本原版 603 幅卡图的两套识别数据已随模组发布。启动时直接加载识别缓存并预热 DINOv2；实验性适配器只在选中对应模式时预热，否则在首次使用时按需加载。猜测时只需对当前画作运行一次 DINOv2。未包含在发布包中的其他模组卡牌会在运行时分析，也可以提前通过设置页的“扫描卡牌并建立识别缓存”统一生成本地缓存。

C# 融合检索位于 `Scripts/Ai/CardArtClassifier.cs`，DINOv2 推理位于 `Scripts/Ai/DinoArtEmbedder.cs`。Python 生成脚本位于 `Scripts/Training/`。修改算法时必须保证 Python 与 C# 的输入预处理、维数和模型版本一致。

自训练适配器的数据来源、按卡牌身份划分的验证/测试集、Top-1/Top-3
结果和限制见 [`docs/sketch-adapter-training.md`](docs/sketch-adapter-training.md)。
可执行的完整复现流程见
[`Scripts/Training/sketch-adapter-training.ipynb`](Scripts/Training/sketch-adapter-training.ipynb)；
Notebook 自带流程图、覆盖六张实际游戏卡图的低分辨率五种合成风格图例，以及完整测试集对比图；
原始卡图文件与训练数据集不会打包进仓库。

重新生成模型：

```powershell
python Scripts/Training/extract-pck-resources.py "<0.107 游戏目录>/SlayTheSpire2.pck" --card-portraits --output .cache-0.107-source
python Scripts/Training/train-card-model.py .cache-0.107-source Models/card_features.bin
python Scripts/Training/build-dino-model.py .cache-0.107-source Models/dinov2_vits14.onnx Models/card_dino_features.bin --reuse-onnx
```

运行游戏外预览：

```powershell
launch-model-preview.cmd
```

更详细的预览说明见 `Scripts/Preview/README.md`。

## 贡献

欢迎提交 Issue 和 Pull Request。模型相关贡献最好同时说明训练数据来源、验证集划分、Top-1 和 Top-3 指标。提交前请阅读 `CONTRIBUTING.md`。

本仓库不应包含反编译游戏源码、完整原版卡图、游戏 DLL、日志或个人文件。《Slay the Spire 2》及其原始素材的权利归相应权利人所有，本项目与 Mega Crit 无隶属关系。

## License

本项目自行创作的源代码使用 [MIT License](LICENSE)。第三方依赖、游戏名称及游戏素材不因本许可证而改变其原有权利归属。

---

# DrawAndGuessMod (Draw & Guess)

[简体中文](#drawandguessmod你画瓦猜) | [English](#drawandguessmod-draw--guess)

A drawing-based card guessing mod for *Slay the Spire 2*. After playing **Blank**, players can draw a card illustration and VAKUU will suggest three candidates from the card pools currently loaded in the game. The selected card is added to the Hand and Deck, and the player's drawing replaces that card's illustration for the rest of the run.



## Features

- Draw & Guess run settings can now be opened directly from Neow's screen, letting players review and adjust the run rules before entering the map. In multiplayer, the host's settings are synchronized to everyone.

### Blank and Drawing

- Playing **Blank** opens the drawing screen and VAKUU returns the three closest candidates. The selected card enters the Hand and, by default, the Deck; its artwork is replaced by the drawing for the rest of the run.
- Tools include independent left- and right-button colors, fill, an RGB color picker, a middle-click eyedropper, and five character stamps. The mouse wheel adjusts brush or stamp size.
- Both standard-card and Ancient-card canvases are supported, with automatic artwork fitting when the final card uses the other card type.
- Use `Ctrl+Z` to undo and `Ctrl+Y` or `Ctrl+Shift+Z` to redo in both singleplayer and multiplayer.
- The battle can be observed without closing the drawing screen. Drawing shortcuts are suspended while observing to avoid conflicts with normal game controls.
- Regular **Blank** drawings can use a 15, 30, 60, 120-second, or custom time limit, or have the time limit disabled entirely.

### Recognition Models and Card Pools

- VAKUU mode uses the lightweight handcrafted feature extractor. Defect mode combines the lightweight and DINOv2 visual features at a `50% + 50%` weight.
- Recognition can use all card pools or only the current character's pool. Advanced settings can detect loaded pools and disable individual pools.
- Recognition caches for all 603 base-game card artworks in version 0.107 are included. The settings page can scan installed base-game and modded cards, rebuild the local cache, and display real-time progress.
- Multiplayer-only cards can be included or excluded and are included by default.
- Cards generated by **Blank** can optionally be added only to the Hand instead of the Deck. This option is disabled by default.
- A **Blank** can optionally be added to the starting Deck at the beginning of a run. This option is disabled by default.
- Regular **Blank** drawings can use a 15, 30, 60, 120-second, or custom time limit. Multiplayer uses the host's setting.
- The AI returns its top three guesses. The local player chooses in singleplayer; in multiplayer, the player targeted by **Blank** chooses one of the three cards.
- The recognition model can be selected in the settings: VAKUU uses only the handcrafted feature extractor and is less accurate, while Defect combines the original algorithm and DINOv2 at a 50/50 weight, and the experimental trained adapter combines 30% handcrafted features with 70% adapted DINOv2.
- Multiplayer collaborative drawing is supported. The player who played **Blank** selects a target and confirms the drawing, the targeted player chooses the card, and the final artwork is synchronized to every player.
- The **VAKUU's Infinite Gallery** event offers timed streaks, standard challenges, and a special streak reward.
- The optional tracing reference panel lets players search for a card, view its art beside the drawing canvas, and middle-click the reference to sample a color for LMB.
- The settings page includes artwork history for completed **Blank**, gallery challenge, and relic appraisal drawings, with rename, edit, copy, and delete actions.
- Cards, the settings page, and the drawing interface support Chinese and English. Languages other than Chinese fall back to English.
- Recognition caches for all 603 base-game card artworks in version 0.107 are included. The settings page can scan all currently installed base-game and modded cards, rebuild the local recognition cache, and display progress in real time.
- An external model preview tool can compare the current 384-dimensional handcrafted features, DINOv2 visual features, and adjustable weighted fusion, including the top three candidates from each configuration.

### Game Settings

- A Draw & Guess settings entry is now available on Neow's screen, allowing run rules to be adjusted before entering the map. In multiplayer, the host's settings are synchronized to everyone.
- Cards previously selected through **Blank** can be excluded from future Blank candidates. Multiplayer shares one selection history and uses the host's setting. This option is disabled by default.
- Cards generated by **Blank** can optionally enter only the Hand instead of the Deck. This option is disabled by default.
- A **Blank** can optionally be added to the starting Deck. This option is disabled by default.
- Recognition model, candidate pools, and drawing time can be configured, with advanced controls for the candidate range.
- **Draw Relics in Treasure Rooms** is enabled by default. In multiplayer, each eligible player draws one candidate produced by the vanilla chest logic, and the chest opens automatically after every submission. **Relic Appraisal Fair** can appear only while this option is disabled.
- The optional tracing reference panel searches for cards and displays their art beside the drawing canvas. Middle-click the reference to sample a color for LMB.
- Artwork History stores and displays **Blank**, gallery challenge, and relic appraisal drawings newest first. Entries can be renamed, edited, copied as PNGs, or deleted.

### Multiplayer

- Every player can collaborate on the same drawing. The player who played **Blank** selects a target and confirms the artwork; the targeted player chooses one of the three cards, and the final card art is synchronized to everyone.
- Each player can undo or redo only their own operations. The player who played the card maintains the authoritative ordering and synchronizes the final canvas.
- Drawing time and other run-affecting drawing settings follow the host's configuration.
- In treasure-room relic drawing, each player sees only their own target. Once every work is submitted, selection continues through the vanilla multiplayer relic-claiming flow.

### Events, Relics, and Tools

- **VAKUU's Infinite Gallery** offers timed streaks, standard challenges, and streak rewards.
- **Death Sketchbook** erases cards from the run through Rest Site drawings, while **Memorial Sketchbook** records timed-streak artwork from the Infinite Gallery.
- Cards, settings, and the drawing interface support Chinese and English. Languages other than Chinese fall back to English.
- The external model preview tool compares the top three candidates from lightweight features, DINOv2 features, and adjustable weighted fusion.

## Requirements

- *Slay the Spire 2* `0.107` compatibility branch. Download the archive whose name contains `game-0.107` from GitHub Releases; Steam Workshop distributes the matching item version automatically.
- [STS2-RitsuLib](https://www.nuget.org/packages/STS2.RitsuLib/) `0.5.11` or later, which automatically selects its 0.107 compatibility build.
- In-game DINOv2 inference uses the ONNX Runtime distributed with the mod. Regular players do not need Python, PyTorch, or any other AI development environment.
- On Windows, the Defect (DINOv2) recognition model requires the [Microsoft Visual C++ 2015–2022 Redistributable (x64)](https://aka.ms/vc14/vc_redist.x64.exe).
- .NET 9 SDK, required only for development and compilation.
- Python, NumPy, and Pillow, required only to regenerate the models or run the preview tool. The DINOv2 fusion preview additionally requires PyTorch, TorchVision, and `timm`.

### Black Screen on Windows

If the game remains on a black screen during startup and the log stops at `Preloaded ... DINOv2 card-art embeddings`, the Microsoft Visual C++ runtime is probably missing or damaged. Download and run the official [Visual C++ 2015–2022 Redistributable (x64)](https://aka.ms/vc14/vc_redist.x64.exe). Choose **Install** if it is not present, or **Repair** if it is already installed, then restart the game. The x86 package, Python, and other AI development environments are not required.

## Building

Run the following command from the repository directory:

```powershell
dotnet build DrawAndGuessMod.csproj -c Release -p:Sts2Dir="path to your Slay the Spire 2 installation"
```

For example:

```powershell
dotnet build DrawAndGuessMod.csproj -c Release -p:Sts2Dir="G:\SteamLibrary\steamapps\common\Slay the Spire 2"
```

If the `STS2_DIR` environment variable is set, `-p:Sts2Dir=...` can be omitted. After a successful build, the project copies the DLL, mod manifest, and model files to `mods/DrawAndGuessMod` inside the game directory.

## Current Recognition Model

The in-game recognizer performs nearest-neighbor retrieval using two 384-dimensional visual feature branches:

1. The lightweight branch resizes the image to 32×32, extracts edge, spatial color, and hue features, and calculates Euclidean distance.
2. The DINOv2 ViT-S/14 branch scales and center-crops the image to 224×224, extracts L2-normalized visual features with an FP16 ONNX Runtime model, and calculates cosine similarity.
3. VAKUU mode ranks candidates using only the lightweight branch. Defect mode applies z-score normalization to both branches within the current candidate pool and combines them at a `50% + 50%` weight.
4. The experimental trained-adapter mode maps the drawing's DINOv2 feature into card-feature space with a 394,880-parameter residual MLP, then fuses `30%` handcrafted and `70%` adapted-DINOv2 scores. All three modes return the top three candidates.

Both recognition datasets for all 603 base-game card artworks in version 0.107 are distributed with the mod. At startup, the mod loads the recognition caches and warms up DINOv2. The experimental adapter is preloaded only when that mode is selected; otherwise it is loaded on demand on first use. Guessing then requires only one DINOv2 inference pass for the current drawing. Cards from other mods that are not included in the release package are analyzed at runtime. They can also be processed in advance with **Scan Cards and Build Cache** on the settings page.

The C# fusion retrieval implementation is located in `Scripts/Ai/CardArtClassifier.cs`, and DINOv2 inference is implemented in `Scripts/Ai/DinoArtEmbedder.cs`. The Python generation scripts are located in `Scripts/Training/`. When modifying the algorithm, keep the Python and C# input preprocessing, dimensions, and model versions consistent.

See [`docs/sketch-adapter-training.md`](docs/sketch-adapter-training.md) for
the adapter's data source, card-identity validation/test split, Top-1/Top-3
results, and limitations.
The executable end-to-end reproduction workflow is in
[`Scripts/Training/sketch-adapter-training.ipynb`](Scripts/Training/sketch-adapter-training.ipynb).
It includes a pre-rendered pipeline, low-resolution five-style examples
generated from six actual game portraits, and full test-set comparison figures.
The source portrait files and training dataset are not bundled.

To regenerate the models:

```powershell
python Scripts/Training/extract-pck-resources.py "<0.107 game directory>/SlayTheSpire2.pck" --card-portraits --output .cache-0.107-source
python Scripts/Training/train-card-model.py .cache-0.107-source Models/card_features.bin
python Scripts/Training/build-dino-model.py .cache-0.107-source Models/dinov2_vits14.onnx Models/card_dino_features.bin --reuse-onnx
```

To run the external preview tool:

```powershell
launch-model-preview.cmd
```

See `Scripts/Preview/README.md` for detailed preview instructions.

## Contributing

Issues and pull requests are welcome. Contributions related to the recognition models should preferably include the training data source, validation split, and Top-1 and Top-3 metrics. Please read `CONTRIBUTING.md` before submitting changes.

This repository must not contain decompiled game source code, complete original card artwork, game DLLs, logs, or personal files. *Slay the Spire 2* and its original assets belong to their respective rights holders. This project is not affiliated with Mega Crit.

## License

Original source code created for this project is licensed under the [MIT License](LICENSE). Third-party dependencies, game names, and game assets retain their respective ownership and licensing terms.
