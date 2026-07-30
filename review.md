感谢提交这个功能。整体设计思路是合理的：

- 将原有协作绘画流程抽取为 `CoopDrawFlow`，减少了 `Blank` 中的重复逻辑。
- 新卡牌使用独立绘画、其他玩家猜测的形式，玩法和现有“空白”区分明显。
- 最终发牌通过自定义 `GameAction` 进入原生行动队列，而不是直接在网络回调中修改牌组，这个方向是正确的。
- 自定义网络消息包含发送者校验、地点信息和 PNG 大小限制，基本协议结构比较完整。
- 我在 PR 提交 `fa74520` 上完成了本地检查：Release 编译通过，0 warning / 0 error；现有 `MultiplayerUndoTests` 全部通过；`git diff --check` 通过；与当前 `main` 没有文本合并冲突。

不过，目前仍有几项会影响正常游戏或联机稳定性的问题，因此暂时请求修改，不建议直接合并。

## 必须修改

### 1. 绘画者退出、断线或放弃游戏后，猜测界面可能永久残留

`DrawGuessSpectatorLoop.RunAsync` 和 `WaitForStartSnapshotAsync` 都是无限轮询。

当前逻辑只检查会话是否收到最终结果，没有检查：

- 当前 Run 是否已经结束或被放弃；
- RunState 是否已经更换；
- 绘画者是否已经离开房间；
- 战斗或场景是否已经结束；
- 当前 Godot 节点是否仍然有效。

猜测时间到期后，客机会自动提交一个猜测，但提交以后仍然会无限等待绘画者广播最终结果。如果绘画者在此之前断线、退出或房主放弃游戏，最终结果永远不会到达，全屏猜测 UI 也不会关闭。

建议：

- 会话记录创建时保存对应的 `RunState`；
- 每帧检查 `RunManager.IsAbandoned`、`IsCleaningUp`、`IsInProgress` 和 RunState 引用；
- 检查绘画者是否仍在玩家列表中；
- 退出时通过 `finally` 统一释放 Overlay；
- 给等待开始和等待结果都加入取消机制；
- 会话结束后主动从 `Sessions` 中删除记录。

### 2. “当前角色卡池”的搜索缓存会串角色

`CardFuzzySearch.EnsureIndex` 的缓存键只有 `LocManager.Instance`，但缓存内容实际上还依赖：

- `owner.Character`；
- `CardPoolScope`；
- `IncludeMultiplayerCards`；
- 高级候选卡池设置；
- 当前安装模组提供的卡池。

例如铁甲战士第一次触发“你画我猜”后，客户端缓存了铁甲战士卡池。之后鸡煲触发时，该客户端仍然可能使用第一次生成的铁甲战士搜索索引。

建议不要缓存过滤后的最终列表。可以缓存一份“所有合法卡牌的本地化名称索引”，然后每次搜索时根据当前绘画者和当前设置进行过滤。或者至少把角色 ID 和完整设置快照加入缓存键，并在检测卡池、切换语言和修改设置时清空缓存。

### 3. 新猜测流程没有使用现有候选卡池过滤

`CardFuzzySearch` 当前直接遍历 `ModelDb.AllCards`，只处理了“多人卡牌”和“当前角色卡池”，没有排除：

- `Blank`；
- `DrawGuessBlank`；
- `IsMock` 卡牌；
- `ShouldShowInCardLibrary == false` 的内部卡牌；
- `CardType.None`；
- 高级设置中被关闭的候选卡池。

因此正常玩家通过搜索界面就可能选择“空白”“你画我猜”或内部测试卡。尤其是“你画我猜”本身默认属于多人卡牌，在默认开启多人卡牌的情况下可以被再次猜出来。

另外，`GuessPhaseCoordinator.OnGuessCard` 直接接受网络包中的 `CardId`，没有在绘画者端再次验证。修改客户端可以绕过本地搜索界面，提交任意内部卡牌 ID。全员弃权时的 `PickFallbackCardId` 也直接从整个 `ModelDb.AllCards` 中随机选择，同样可能抽到内部卡牌。

建议建立一个统一接口，例如：

```csharp
IReadOnlyList<CardModel> GetEligibleGuessCards(Player owner)
```

以下位置必须共同使用这一份候选集合：

- 模糊搜索；
- 绘画者收到猜测包后的 CardId 验证；
- 全员弃权时的随机兜底；
- 最终发牌前的最后一次验证。

收到不合法的 CardId 时应将该票视为空票，而不是直接加入票池。

### 4. PR 中的 macOS 部署配置会让干净仓库构建失败

`DrawAndGuessMod.csproj` 新增了：

```xml
<Target Name="Deploy Mod MacOS"
        AfterTargets="PostBuildEvent"
        Condition="'$(SkipModDeploy)' != 'true' and '$(OS)' == 'Unix'">
  <Exec Command="sh &quot;$(MSBuildProjectDirectory)/deploy.sh&quot; ..." />
</Target>
```

但 `deploy.sh` 没有提交，并且被明确加入了 `.gitignore`。其他开发者在 macOS 上执行普通的 `dotnet build` 时，会因为找不到这个脚本而失败。

此外，`$(OS) == Unix` 不只会匹配 macOS，也会匹配 Linux。目标名称虽然是 macOS，Linux 构建也可能误入这个部署步骤。

PR 描述里提到向本地 macOS 游戏和局域网 Windows 共享目录部署，这更像是提交者个人开发环境配置，不属于“你画我猜”卡牌功能。

建议将以下内容从本 PR 移除：

- `Deploy Mod MacOS` Target；
- 为该 Target 修改的 `Copy Mod` 条件；
- `.gitignore` 中仅为个人部署脚本增加的 `deploy.sh`；
- 如果 `PlatformTarget` 的修改并非该功能所必需，也建议单独提交。

跨平台部署脚本可以以后作为独立 PR 讨论，并且必须能够在干净仓库中使用。

## 建议修改

### 5. 游戏结果使用了不可复现的 `System.Random`

`GuessPhaseCoordinator` 使用静态的：

```csharp
private static readonly Random Rng = new();
```

最终获得哪张卡会改变实际游戏状态，因此属于玩法随机，不应使用未保存、未指定种子的本地 `System.Random`。

此外，权重字典的插入顺序取决于各玩家猜测包的到达顺序。即使以后换成固定种子，只要网络包到达顺序不同，遍历候选的顺序仍然可能不同。

当前最终结果由绘画者广播，所以一般不会立刻造成各客户端选择不同；但它仍然无法保证：

- 相同种子下可复现；
- SL 后结果一致；
- 回放逻辑稳定；
- 不同网络时序下行为一致。

建议：

- 使用游戏提供的确定性 RNG；
- 或者由 Run seed、ownerId、sessionId 派生确定性种子；
- 计算前先按 `CardId` 对权重候选排序；
- 最好把最终随机选择放在确定性的同步行动中，而不是普通网络消息回调之外。

### 6. 新增猜测界面没有英文适配

卡牌名称和描述做了中英文适配，但以下新界面仍然硬编码中文：

- `GuessEntryOverlay`；
- `DrawGuessWaitingOverlay`；
- 猜测已提交、放弃、超时等状态文本；
- 倒计时、标题、按钮、输入框提示文本。

因此英文环境下会出现中文界面，与 README 中“游戏内卡牌、设置页和绘图界面支持中文与英文”的描述不一致。

建议统一使用现有的 `ModText.Get` 或 `Localized` 方法，不要在新 UI 中直接写中文字符串。

### 7. 客机的会话记录不会及时释放

绘画者端在 `BeginOwnerSession` 的 `finally` 中会删除 Session，但客机收到最终结果后只是把 Session 标记为 `Completed`，不会将其移除。

每个 Session 都可能持续保存：

- 完整 PNG 字节；
- 最终结果；
- TaskCompletionSource；
- 其他会话数据。

连续多次打出这张卡后，这些数据会保留到下一局 `Reset`。建议在客机应用卡图并关闭 UI 后删除会话，或者只保留短时间的已完成快照再清理。

### 8. 升级状态不应存放在全局静态变量中

当前升级状态通过：

```csharp
DrawGuessSession.IsUpgradedContext
```

在开始绘画前写入，几十秒后再读取。虽然通常同一名本地玩家的行动队列会阻止自己同时打出第二张，但这个状态仍然不属于全局状态，未来出现重入、多个本地玩家或并行会话时可能互相覆盖。

建议直接将 `isUpgraded` 作为 `RunOwnerAsync` 参数传入，并一直传递到 `DrawGuessGrantAction`，不要通过静态变量保存一次卡牌行动的上下文。

## 测试方面

当前 PR 运行的是已有的 `MultiplayerUndoTests`，这些测试与新增的猜测协议没有直接关系。本次新增约两千行代码，但没有对应的自动化测试。

建议至少补充或手动验证以下情况：

1. 不同角色依次打出“你画我猜”，确认“当前角色卡池”正确变化；
2. 全员弃权时不会抽到空白、你画我猜、Mock 或内部卡；
3. 客户端提交非法 CardId 时，绘画者会拒绝该票；
4. 绘画者在提交画作前断线；
5. 绘画者在其他玩家猜测期间断线；
6. 房主在猜测界面存在时放弃游戏；
7. 战斗在绘画或猜测期间结束；
8. 两名玩家接近同时打出该卡；
9. 两名玩家使用不同语言；
10. 开关高级候选卡池后搜索结果立即更新；
11. 干净 macOS 仓库直接运行普通 `dotnet build`；
12. 连续触发二十次以上后检查 Session 和 PNG 是否释放。

## 结论

这个功能值得保留，核心玩法也比较有意思。`DrawGuessGrantAction` 通过原生行动队列发牌的方向是正确的，原有 `Blank` 流程抽取也比较整洁。

但目前存在以下合并阻断项：

1. 退出或断线可能让客机猜测 UI 永久残留；
2. 搜索缓存会串用第一名绘画者的角色卡池；
3. 候选过滤和网络 CardId 验证不完整，可能得到内部卡或递归得到空白；
4. 未提交的 `deploy.sh` 会破坏干净 macOS/Linux 构建。

请先修复这些问题，再进行一次至少双人、最好三人的完整联机测试。修复后我愿意继续复审。
