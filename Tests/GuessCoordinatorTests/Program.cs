RunAllAbstainFallbackScenario();
RunInvalidCardIdDowngradeScenario();
RunDeterministicRngAndOrderIndependenceScenario();
RunFallbackNeverReturnsExcludedCards();
RunWeightedProbabilityConvergence();
Console.WriteLine("All guess coordinator logic tests passed.");

// 测试2a：全员弃权时兜底只从 EligibleCardIds 中选，不会返回空字符串
static void RunAllAbstainFallbackScenario()
{
    ulong ownerId = 100;
    uint sessionId = 200;
    HashSet<string> eligibleCardIds = ["CardA", "CardB", "CardC"];

    // 所有人弃权
    Dictionary<ulong, string> guesses = new() { { 1, "" }, { 2, "" } };
    string result = FinalizeCore(ownerId, sessionId, guesses, eligibleCardIds);
    Assert(eligibleCardIds.Contains(result), "All-abstain: fallback must pick from eligible pool.");

    // 无投票记录
    string result2 = FinalizeCore(ownerId, sessionId, new Dictionary<ulong, string>(), eligibleCardIds);
    Assert(eligibleCardIds.Contains(result2), "No guesses: fallback must pick from eligible pool.");
}

// 测试3：非法 CardId 被降级为弃权票
static void RunInvalidCardIdDowngradeScenario()
{
    HashSet<string> eligibleCardIds = ["ValidCard1", "ValidCard2"];

    string validResult = ValidateCardId("ValidCard1", eligibleCardIds);
    Assert(validResult == "ValidCard1", "Valid CardId must be preserved.");

    string invalidResult = ValidateCardId("HackerInternalCard", eligibleCardIds);
    Assert(invalidResult == "", "Invalid CardId must be downgraded to empty (abstain).");

    string emptyResult = ValidateCardId("", eligibleCardIds);
    Assert(emptyResult == "", "Empty CardId (explicit abstain) must remain empty.");

    // 非法票不进入最终权重池
    Dictionary<ulong, string> guesses = new()
    {
        { 1, ValidateCardId("HackerInternalCard", eligibleCardIds) }, // -> ""
        { 2, ValidateCardId("ValidCard1", eligibleCardIds) }
    };
    string result = FinalizeCore(1ul, 1u, guesses, eligibleCardIds);
    Assert(result == "ValidCard1", "After downgrading illegal vote, only valid vote wins.");
}

// 测试5：确定性RNG——相同参数结果相同；结果与字典插入顺序无关
static void RunDeterministicRngAndOrderIndependenceScenario()
{
    ulong ownerId = 999;
    uint sessionId = 888;
    HashSet<string> eligibleCardIds = ["CardA", "CardB", "CardC"];

    // 插入顺序 1
    Dictionary<ulong, string> guesses1 = new()
    {
        { 1, "CardA" }, { 2, "CardB" }, { 3, "CardB" }, { 4, "CardC" }
    };
    // 插入顺序 2（同样的票，不同顺序）
    Dictionary<ulong, string> guesses2 = new()
    {
        { 4, "CardC" }, { 2, "CardB" }, { 1, "CardA" }, { 3, "CardB" }
    };

    string result1 = FinalizeCore(ownerId, sessionId, guesses1, eligibleCardIds);
    string result2 = FinalizeCore(ownerId, sessionId, guesses2, eligibleCardIds);
    Assert(result1 == result2, "Same votes in different insertion order must yield identical result.");

    // 同一参数多次调用结果一致
    string result3 = FinalizeCore(ownerId, sessionId, guesses1, eligibleCardIds);
    Assert(result1 == result3, "Repeated call with same parameters must be deterministic.");

    // 不同 sessionId 产生不同种子（大概率不同结果，至少验证不抛异常且结果合法）
    Dictionary<ulong, string> singleVote = new() { { 1, "" } }; // 走兜底路径
    string r1 = FinalizeCore(1ul, 1u, singleVote, eligibleCardIds);
    string r2 = FinalizeCore(1ul, 1u, singleVote, eligibleCardIds);
    Assert(r1 == r2, "Fallback path must also be deterministic for same seed.");
    Assert(eligibleCardIds.Contains(r1), "Fallback result must be from eligible pool.");
}

// 测试2b：兜底绝不返回排除在外的卡（内部卡/空白等不在 EligibleCardIds 中）
static void RunFallbackNeverReturnsExcludedCards()
{
    HashSet<string> eligibleCardIds = ["CardA", "CardB"];
    // 假设 "Blank"、"DrawGuessBlank"、"InternalCard" 不在 eligibleCardIds 中
    string[] excluded = ["Blank", "DrawGuessBlank", "InternalCard", ""];

    for (uint s = 0; s < 20; s++)
    {
        string result = FinalizeCore(42ul, s, new Dictionary<ulong, string>(), eligibleCardIds);
        Assert(!excluded.Contains(result), $"Fallback must never return excluded card, got: '{result}' (sessionId={s}).");
        Assert(eligibleCardIds.Contains(result), $"Fallback must always return eligible card (sessionId={s}).");
    }
}

// 测试5扩展：大样本验证权重正确起效（票多的卡中选率应明显更高）
static void RunWeightedProbabilityConvergence()
{
    HashSet<string> eligibleCardIds = ["CardA", "CardB", "CardC"];
    int countA = 0, countB = 0;

    // CardB 有 9 票，CardA 有 1 票；用不同 sessionId 模拟多次独立结算
    for (uint s = 0; s < 1000; s++)
    {
        Dictionary<ulong, string> guesses = new();
        for (ulong p = 1; p <= 9; p++) guesses[p] = "CardB";
        guesses[10] = "CardA";

        string result = FinalizeCore(1ul, s, guesses, eligibleCardIds);
        if (result == "CardA") countA++;
        if (result == "CardB") countB++;
    }

    // CardB 以 9:1 压倒优势，期望 countB >> countA
    Assert(countB > countA * 5, $"CardB(9票) should win far more than CardA(1票); got B={countB} A={countA}.");
    Assert(countA > 0, "CardA(1票) should occasionally win in 1000 runs.");
}

// ---------------------------------------------------------
static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static string PickFallbackCardId(HashSet<string> eligibleCardIds, Random rng)
{
    var pool = eligibleCardIds.Count > 0
        ? eligibleCardIds.OrderBy(id => id, StringComparer.Ordinal).ToList()
        : new List<string>();
    return pool.Count > 0 ? pool[rng.Next(pool.Count)] : "";
}

static string FinalizeCore(ulong ownerId, uint sessionId,
    Dictionary<ulong, string> guesses, HashSet<string> eligibleCardIds)
{
    var rng = new Random((int)(ownerId ^ sessionId));
    var weights = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var cardId in guesses.Values)
        if (cardId.Length > 0) weights[cardId] = weights.GetValueOrDefault(cardId) + 1;

    if (weights.Count == 0) return PickFallbackCardId(eligibleCardIds, rng);

    var sorted = weights.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
    int total = sorted.Sum(p => p.Value);
    int roll = rng.Next(total);
    string chosen = "";
    foreach (var (id, w) in sorted) { roll -= w; if (roll < 0) { chosen = id; break; } }
    return chosen.Length == 0 ? sorted[0].Key : chosen;
}

static string ValidateCardId(string rawCardId, HashSet<string> eligibleCardIds)
{
    if (rawCardId.Length > 0 && eligibleCardIds.Count > 0 && !eligibleCardIds.Contains(rawCardId))
        return "";
    return rawCardId;
}
