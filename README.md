# DrawAndGuessMod（你画瓦猜）

《Slay the Spire 2》绘画猜卡模组。打出“空白”后，玩家可以绘制一张卡面，瓦库会从当前游戏卡池中给出三个候选；选择结果会加入手牌和牌组，并在本局游戏中使用玩家绘制的卡面。

## 功能

- 基础作画工具包括画笔，调色盘，橡皮和印花。
- 单人模式支持按钮或 Ctrl+Z 撤回最近20次完整操作；多人模式不启用撤回。
- 全卡池或当前角色卡池识别范围。
- 可选择是否包含多人专属卡牌，默认启用。
- 可选择让“空白”生成的卡牌只进入手牌而不进入卡组，默认关闭。
- AI 猜测前三名；单人模式由自己选择，多人模式由“空白”指定的玩家三选一。
- “空白+”耗能降低，并将三张候选中所有可升级的卡牌升级后再供玩家选择。
- 可在“识别模型准确度”中选择瓦库（100%原算法）或鸡煲（原算法与DINOv2各50%）。
- 多人协作绘画；出牌者指定目标并确认画作，被指定的玩家选择卡牌，最终卡面同步给所有玩家。
- 游戏内卡牌、设置页和绘图界面支持中文与英文；非中文语言默认回退英文。
- 内置原版 611 张卡牌的识别缓存；设置页可扫描当前已安装的全部原版及模组卡牌并重新建立本地识别缓存，同时显示实时进度。
- 游戏外模型预览工具，可并列测试当前384维特征、DINOv2视觉特征与可调权重融合的前三个候选。

## 依赖

- Slay the Spire 2 `0.109.0` 或更高版本。
- [STS2-RitsuLib](https://www.nuget.org/packages/STS2.RitsuLib/) `0.4.60` 或更高版本。
- 游戏内 DINOv2 推理使用随模组发布的 ONNX Runtime，普通玩家不需要安装 Python、PyTorch 或其他 AI 环境。
- .NET 9 SDK（仅开发和编译时需要）。
- Python、NumPy、Pillow（仅重新生成模型或运行预览工具时需要）；DINOv2融合预览另需 PyTorch、TorchVision 与 `timm`。

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
3. “瓦库”模式只按轻量分支排序；“鸡煲”模式将两个分支分别在当前候选卡池内做 z-score 标准化，再按 `50% + 50%` 融合排序。两种模式都给出前三名。

原版 611 张卡牌的两套识别数据已随模组发布。启动时直接加载识别缓存并预热 ONNX 模型；猜测时只需对当前画作运行一次 DINOv2。未包含在发布包中的其他模组卡牌会在运行时分析，也可以提前通过设置页的“扫描卡牌并建立识别缓存”统一生成本地缓存。若 ONNX 模型或运行库加载失败，会自动退回轻量分支，不阻断出牌。

C# 融合检索位于 `Scripts/Ai/CardArtClassifier.cs`，DINOv2 推理位于 `Scripts/Ai/DinoArtEmbedder.cs`。Python 生成脚本位于 `Scripts/Training/`。修改算法时必须保证 Python 与 C# 的输入预处理、维数和模型版本一致。

重新生成模型：

```powershell
python Scripts/Training/train-card-model.py <包含 images/atlases 的本地游戏资源目录> Models/card_features.bin
python Scripts/Training/build-dino-model.py <包含 images/atlases 的本地游戏资源目录> Models/dinov2_vits14.onnx Models/card_dino_features.bin
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
