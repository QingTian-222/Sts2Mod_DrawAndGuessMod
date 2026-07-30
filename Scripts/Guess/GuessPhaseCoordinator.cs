using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Networking;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;

namespace DrawAndGuessMod.Scripts.Guess;

/// <summary>一次你画我猜会话的最终产出。</summary>
public sealed record DrawGuessOutcome(string CardId, byte[] PngBytes, int TotalGuesses, bool Cancelled);

/// <summary>
/// 你画我猜模式的数据池聚合器（纯本地优先架构）：
/// 绘画者端收集所有 <see cref="GuessCardPacket"/>，满足触发条件（全员提交或超时）后
/// 按票数权重用确定性 RNG 敲定卡牌，再把结果广播给全房。
/// 网络消息只会打到静态会话表上，不依赖任何 Godot 节点存活。
/// </summary>
internal static class GuessPhaseCoordinator
{
    private const double DefaultTimeoutSeconds = 45d;

    private sealed class Session
    {
        public ulong OwnerId;
        public uint SessionId;
        public byte[]? PngBytes;
        public double TimeoutSeconds = DefaultTimeoutSeconds;
        public readonly HashSet<ulong> ExpectedGuesserIds = new();
        public readonly Dictionary<ulong, string> Guesses = new();
        public bool Started;
        public bool Completed;
        public bool Cancelled;
        public string FinalCardId = string.Empty;
        public int FinalTotalGuesses;
        public SceneTreeTimer? Timer;
        public TaskCompletionSource<byte[]>? StartTcs;
        public TaskCompletionSource<DrawGuessOutcome?>? ResultTcs;
        /// <summary>绘画者等待界面的进度回调（已提交/总数）。</summary>
        public Action<int, int>? ProgressChanged;
        /// <summary>合法候选卡牌 ID 集合（绘画者端建立，用于验证猜测包）。</summary>
        public HashSet<string> EligibleCardIds = new(StringComparer.Ordinal);
    }

    private static readonly Dictionary<(ulong OwnerId, uint SessionId), Session> Sessions = new();

    /// <summary>对局开始时清空残留会话。</summary>
    public static void Reset()
    {
        foreach (Session session in Sessions.Values)
        {
            // 标记完成，防止旧对局的倒计时器触发后广播出陈旧裁定。
            session.Completed = true;
            session.StartTcs?.TrySetCanceled();
            session.ResultTcs?.TrySetCanceled();
        }

        Sessions.Clear();
    }

    /// <summary>主动删除指定会话（客机结算完成后调用，释放 PNG 和 TCS 内存）。</summary>
    public static void RemoveSession(ulong ownerId, uint sessionId)
    {
        Sessions.Remove((ownerId, sessionId));
    }

    // ---------------------------------------------------------------- 绘画者端

    /// <summary>
    /// 绘画者（Player A）提交画作后调用：广播 StartGuessingPacket 并启动倒计时，
    /// 返回的任务在全员提交或超时后完成。
    /// </summary>
    public static async Task<DrawGuessOutcome?> BeginOwnerSession(
        Player owner,
        uint sessionId,
        byte[] pngBytes,
        IReadOnlyCollection<ulong> expectedGuesserIds,
        double timeoutSeconds,
        Action<int, int>? progressChanged)
    {
        ulong ownerId = owner.NetId;
        (ulong OwnerId, uint SessionId) key = (ownerId, sessionId);
        Sessions.Remove(key);

        // 建立合法候选卡牌集合（绘画者端权威过滤）。
        HashSet<string> eligibleCardIds;
        try
        {
            eligibleCardIds = new HashSet<string>(
                CardFuzzySearch.GetEligibleGuessCards(owner).Select(c => c.Id.Entry),
                StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] 建立合法候选失败，兜底为空集: {ex.Message}");
            eligibleCardIds = new HashSet<string>(StringComparer.Ordinal);
        }

        Session session = new()
        {
            OwnerId = ownerId,
            SessionId = sessionId,
            PngBytes = pngBytes,
            TimeoutSeconds = timeoutSeconds > 0d ? timeoutSeconds : DefaultTimeoutSeconds,
            Started = true,
            ResultTcs = new TaskCompletionSource<DrawGuessOutcome?>(TaskCreationOptions.RunContinuationsAsynchronously),
            ProgressChanged = progressChanged,
            EligibleCardIds = eligibleCardIds
        };
        foreach (ulong guesserId in expectedGuesserIds)
        {
            session.ExpectedGuesserIds.Add(guesserId);
        }
        Sessions[key] = session;

        DrawingNetSync.SendStartGuessing(new StartGuessingPacket
        {
            OwnerId = ownerId,
            SessionId = sessionId,
            PngBytes = pngBytes,
            GuessTimeoutSeconds = (byte)Math.Clamp(session.TimeoutSeconds, 1d, 120d),
            ExpectedGuesserIds = new List<ulong>(session.ExpectedGuesserIds)
        });

        // 倒计时器：超时即结算。回调可能落在帧上，FinalizeOwnerSession 内部幂等。
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            session.Timer = tree.CreateTimer(session.TimeoutSeconds, processAlways: true);
            _ = AwaitTimeoutAsync(session, session.Timer);
        }

        if (session.ExpectedGuesserIds.Count == 0)
        {
            // 房间里没有其他玩家：立即结算（按空票池逻辑兜底）。
            FinalizeOwnerSession(session);
        }

        try
        {
            return await session.ResultTcs!.Task;
        }
        finally
        {
            Sessions.Remove(key);
        }
    }

    private static async Task AwaitTimeoutAsync(Session session, SceneTreeTimer timer)
    {
        await timer.ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
        if (!session.Completed)
        {
            Entry.Logger.Info($"[DrawAndGuessMod] 绘画者端猜测倒计时结束，按已收到的 {session.Guesses.Count} 份猜测结算。");
            FinalizeOwnerSession(session);
        }
    }

    /// <summary>绘画者端剩余秒数（供等待界面轮询显示）。</summary>
    public static double GetOwnerTimeLeft(ulong ownerId, uint sessionId)
    {
        if (!Sessions.TryGetValue((ownerId, sessionId), out Session? session) || session.Timer == null)
        {
            return 0d;
        }

        return Math.Max(0d, session.Timer.TimeLeft);
    }

    /// <summary>网络层回调：收到某个猜测端回传的卡牌。</summary>
    public static void OnGuessCard(GuessCardPacket packet, ulong senderId)
    {
        if (!Sessions.TryGetValue((packet.OwnerId, packet.SessionId), out Session? session) || session.Completed)
        {
            return;
        }

        // 只有绘画者端维护 ExpectedGuesserIds；非绘画者收到他人的包直接忽略。
        if (session.ExpectedGuesserIds.Count == 0 || !session.ExpectedGuesserIds.Contains(senderId))
        {
            return;
        }

        // 验证 CardId 合法性：非空 ID 必须在合法候选中，否则视为空票（弃权）。
        string cardId = packet.CardId;
        if (cardId.Length > 0 && session.EligibleCardIds.Count > 0 && !session.EligibleCardIds.Contains(cardId))
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] 收到非法 CardId「{cardId}」来自 {senderId}，已降级为弃权票。");
            cardId = string.Empty;
        }

        session.Guesses[senderId] = cardId;
        session.ProgressChanged?.Invoke(session.Guesses.Count, session.ExpectedGuesserIds.Count);
        Entry.Logger.Debug($"[DrawAndGuessMod] 收到猜测 {session.Guesses.Count}/{session.ExpectedGuesserIds.Count} from={senderId} card={(cardId.Length == 0 ? "<skip>" : cardId)}");

        if (session.Guesses.Count >= session.ExpectedGuesserIds.Count)
        {
            FinalizeOwnerSession(session);
        }
    }

    /// <summary>
    /// 结算：按票数权重用确定性 RNG 敲定卡牌并广播。
    /// 权重 = 该 CardId 获得的有效票数（弃权票不计入任何候选）。
    /// 空票池兜底：从合法候选集合中均匀随机，保证流程一定收敛。
    /// </summary>
    private static void FinalizeOwnerSession(Session session)
    {
        if (session.Completed)
        {
            return;
        }

        session.Completed = true;

        // 确定性 RNG：由 ownerId 和 sessionId 派生种子，保证相同输入下结果可复现。
        Random rng = new Random((int)(session.OwnerId ^ session.SessionId));

        Dictionary<string, int> weights = new(StringComparer.Ordinal);
        foreach (string guessedCardId in session.Guesses.Values)
        {
            if (guessedCardId.Length == 0)
            {
                continue;
            }

            weights[guessedCardId] = weights.GetValueOrDefault(guessedCardId) + 1;
        }

        string chosen;
        if (weights.Count == 0)
        {
            chosen = PickFallbackCardId(session, rng);
            Entry.Logger.Info($"[DrawAndGuessMod] 无人有效猜测，兜底随机选定 {chosen}。");
        }
        else
        {
            // 先按 CardId 排序，确保遍历顺序与网络包到达顺序无关（确定性）。
            List<KeyValuePair<string, int>> sortedWeights = weights
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToList();

            int totalWeight = sortedWeights.Sum(pair => pair.Value);
            int roll = rng.Next(totalWeight);
            chosen = string.Empty;
            foreach ((string candidateId, int weight) in sortedWeights)
            {
                roll -= weight;
                if (roll < 0)
                {
                    chosen = candidateId;
                    break;
                }
            }

            if (chosen.Length == 0)
            {
                chosen = sortedWeights[0].Key;
            }

            string distribution = string.Join(", ", sortedWeights.Select(pair => $"{pair.Key}×{pair.Value}"));
            Entry.Logger.Info($"[DrawAndGuessMod] 猜测票池 {distribution}，权重随机敲定 {chosen}。");
        }

        session.FinalCardId = chosen;
        session.FinalTotalGuesses = session.Guesses.Count;
        session.Cancelled = false;

        DrawingNetSync.SendDrawGuessResult(new DrawGuessResultMessage
        {
            OwnerId = session.OwnerId,
            SessionId = session.SessionId,
            Cancelled = false,
            CardId = chosen,
            TotalGuesses = (byte)session.Guesses.Count
        });

        session.ResultTcs?.TrySetResult(new DrawGuessOutcome(chosen, session.PngBytes ?? [], session.Guesses.Count, false));
    }

    private static string PickFallbackCardId(Session session, Random rng)
    {
        try
        {
            // 从已建立的合法候选集合中随机，不访问 ModelDb.AllCards，避免内部卡混入。
            List<string> pool = session.EligibleCardIds.Count > 0
                ? session.EligibleCardIds.OrderBy(id => id, StringComparer.Ordinal).ToList()
                : new List<string>();

            if (pool.Count > 0)
            {
                return pool[rng.Next(pool.Count)];
            }
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] 兜底选牌失败: {ex.Message}");
        }

        return string.Empty;
    }

    // ---------------------------------------------------------------- 猜测端

    /// <summary>
    /// 猜测端等待绘画者提交画作。返回 PNG；会话被提前取消/重置时返回 null。
    /// </summary>
    public static Task<byte[]?> AwaitStartAsync(ulong ownerId, uint sessionId)
    {
        Session session = GetOrCreateSession(ownerId, sessionId);
        Entry.Logger.Info($"[DrawAndGuessMod][Trace] AwaitStartAsync: owner={ownerId}, session={sessionId}, completed={session.Completed}, started={session.Started}, hasPng={session.PngBytes != null}, hasTcs={session.StartTcs != null}");
        if (session.Completed)
        {
            // 裁定（或取消）先于本地 await 到达：无需再进入猜测阶段。
            return Task.FromResult<byte[]?>(null);
        }

        if (session.Started && session.PngBytes != null)
        {
            return Task.FromResult<byte[]?>(session.PngBytes);
        }

        session.StartTcs ??= new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        return AwaitStartCoreAsync(session);
    }

    private static async Task<byte[]?> AwaitStartCoreAsync(Session session)
    {
        try
        {
            return await session.StartTcs!.Task;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// 猜测端等待最终裁定（在本地提交猜测之后调用）。
    /// </summary>
    public static Task<DrawGuessOutcome?> AwaitResultAsync(ulong ownerId, uint sessionId)
    {
        Session session = GetOrCreateSession(ownerId, sessionId);
        session.ResultTcs ??= new TaskCompletionSource<DrawGuessOutcome?>(TaskCreationOptions.RunContinuationsAsynchronously);
        return AwaitResultCoreAsync(session);
    }

    private static async Task<DrawGuessOutcome?> AwaitResultCoreAsync(Session session)
    {
        try
        {
            return await session.ResultTcs!.Task;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>猜测端提交猜测（或弃权，cardId 传空串）。</summary>
    public static void SubmitGuess(ulong ownerId, uint sessionId, string cardId)
    {
        DrawingNetSync.SendGuessCard(new GuessCardPacket
        {
            OwnerId = ownerId,
            SessionId = sessionId,
            CardId = cardId
        });
    }

    // ---------------------------------------------------------------- 快照 API（供观看端后台轮询，不依赖 TCS 线程编组）

    /// <summary>同步读取开始包快照（PNG）；未到达返回 false。</summary>
    public static bool TryGetStartSnapshot(ulong ownerId, uint sessionId, out byte[]? pngBytes)
    {
        pngBytes = null;
        if (!Sessions.TryGetValue((ownerId, sessionId), out Session? session))
        {
            return false;
        }

        if (!session.Started || session.PngBytes == null)
        {
            return false;
        }

        pngBytes = session.PngBytes;
        return true;
    }

    /// <summary>指定会话的裁定是否已到达（按快照，与线程编组无关）。</summary>
    public static bool HasOutcomeArrived(ulong ownerId, uint sessionId)
    {
        return Sessions.TryGetValue((ownerId, sessionId), out Session? session) && session.Completed;
    }

    /// <summary>同步读取裁定快照；未结束返回 false；结束后 outcome 为 null 表示取消。</summary>
    public static bool TryGetOutcomeSnapshot(ulong ownerId, uint sessionId, out DrawGuessOutcome? outcome)
    {
        outcome = null;
        if (!Sessions.TryGetValue((ownerId, sessionId), out Session? session) || !session.Completed)
        {
            return false;
        }

        if (session.Cancelled || session.FinalCardId.Length == 0)
        {
            return true;
        }

        outcome = new DrawGuessOutcome(session.FinalCardId, session.PngBytes ?? [], session.FinalTotalGuesses, false);
        return true;
    }

    /// <summary>会话是否已完成（裁定或取消）。</summary>
    public static bool IsCompleted(ulong ownerId, uint sessionId)
    {
        return Sessions.TryGetValue((ownerId, sessionId), out Session? session) && session.Completed;
    }

    /// <summary>查询会话的猜测时长（猜测端展示倒计时用；未开始返回默认值）。</summary>
    public static double GetSessionTimeout(ulong ownerId, uint sessionId)
    {
        return Sessions.TryGetValue((ownerId, sessionId), out Session? session)
            ? session.TimeoutSeconds
            : DefaultTimeoutSeconds;
    }

    /// <summary>网络层回调：收到绘画者广播的开始猜测。</summary>
    public static void OnStartGuessing(StartGuessingPacket packet)
    {
        // 本端就是绘画者时忽略自己的回声（发送方已在发送侧过滤，这里双保险）。
        Entry.Logger.Info($"[DrawAndGuessMod][Trace] 处理开始包: owner={packet.OwnerId}, session={packet.SessionId}, local={LocalContext.NetId}, png={packet.PngBytes.Length}");
        if (packet.OwnerId == LocalContext.NetId)
        {
            return;
        }

        Session session = GetOrCreateSession(packet.OwnerId, packet.SessionId);
        // 同一 SessionId 可能被复用（例如回退到 0）：视为新一轮，重置残留状态。
        session.Completed = false;
        session.Guesses.Clear();
        session.ResultTcs = null;
        session.StartTcs = null;
        session.PngBytes = packet.PngBytes;
        session.TimeoutSeconds = packet.GuessTimeoutSeconds > 0 ? packet.GuessTimeoutSeconds : DefaultTimeoutSeconds;
        session.Started = true;
        Entry.Logger.Info($"[DrawAndGuessMod][Trace] 协调器已落袋开始包: owner={packet.OwnerId}, session={packet.SessionId}, started={session.Started}");

        if (session.StartTcs == null)
        {
            // 消息先于本地 await 到达：直接落袋，AwaitStartAsync 会立即返回。
            session.StartTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        session.StartTcs.TrySetResult(packet.PngBytes);
    }

    /// <summary>网络层回调：收到绘画者广播的最终裁定。</summary>
    public static void OnDrawGuessResult(DrawGuessResultMessage message)
    {
        if (message.OwnerId == LocalContext.NetId)
        {
            return;
        }

        Session session = GetOrCreateSession(message.OwnerId, message.SessionId);
        if (session.Completed)
        {
            return;
        }

        session.Completed = true;
        session.Cancelled = message.Cancelled || message.CardId.Length == 0;
        session.FinalCardId = session.Cancelled ? string.Empty : message.CardId;
        session.FinalTotalGuesses = message.TotalGuesses;
        DrawGuessOutcome? outcome = session.Cancelled
            ? null
            : new DrawGuessOutcome(message.CardId, session.PngBytes ?? [], message.TotalGuesses, false);

        // 若绘画者在本地等待开始前就结束了会话，释放开始等待者。
        session.StartTcs?.TrySetResult(session.PngBytes ?? []);
        session.ResultTcs ??= new TaskCompletionSource<DrawGuessOutcome?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ResultTcs.TrySetResult(outcome);
    }

    private static Session GetOrCreateSession(ulong ownerId, uint sessionId)
    {
        (ulong OwnerId, uint SessionId) key = (ownerId, sessionId);
        if (!Sessions.TryGetValue(key, out Session? session))
        {
            session = new Session { OwnerId = ownerId, SessionId = sessionId };
            Sessions[key] = session;
        }

        return session;
    }
}
