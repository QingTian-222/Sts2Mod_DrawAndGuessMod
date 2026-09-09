using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DrawAndGuessMod.Scripts.Relics;
using DrawAndGuessMod.Scripts.Ui;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using STS2RitsuLib;
using STS2RitsuLib.RunData;
using STS2RitsuLib.Utils.Persistence;

namespace DrawAndGuessMod.Scripts.State;

internal static class MemorialSketchbookStore
{
    private const string RunDataKey = "memorial_sketchbook";
    private const string ProfileDataKey = "memorial_sketchbook_history";
    private const string ProfileFileName = "memorial_sketchbook.json";
    private const int MaximumArchivedRuns = 10;

    private static RunSavedData<MemorialSketchbookRunState>? _savedData;
    private static readonly SemaphoreSlim RelicOwnershipGate = new(1, 1);
    private static readonly Dictionary<string, CachedPermanentTexture> PermanentTextureCache =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, CachedPermanentRelicTextures> PermanentRelicTextureCache =
        new(StringComparer.Ordinal);

    public static void Register()
    {
        _savedData = RitsuLibFramework.GetRunSavedDataStore(Entry.ModId).Register(
            RunDataKey,
            () => new MemorialSketchbookRunState(),
            new RunSavedDataOptions
            {
                SchemaVersion = 1,
                WritePolicy = RunSavedDataWritePolicy.WhenNonDefault
            });

        try
        {
            using (RitsuLibFramework.BeginModDataRegistration(Entry.ModId, false))
            {
                RitsuLibFramework.GetDataStore(Entry.ModId).Register(
                    ProfileDataKey,
                    ProfileFileName,
                    SaveScope.Profile,
                    () => new MemorialSketchbookProfileData());
            }
            RitsuLibFramework.GetDataStore(Entry.ModId).InitializeProfileScoped();
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Failed to initialize Memorial Sketchbook profile archive: {ex.Message}");
        }
    }

    public static void ActivateRun(RunState runState)
    {
        PermanentTextureCache.Clear();
        PermanentRelicTextureCache.Clear();
        SynchronizeProfileArchive(runState);
    }

    public static async Task<string?> CaptureCardDrawingAsync(RunState runState, ulong drawerNetId, uint sessionId, DrawingResult drawing)
    {
        await EnsureOwnedByAllPlayersAsync(runState);

        CardModel? provisionalCard = drawing.Guess.NearestCards.FirstOrDefault();
        if (provisionalCard == null || drawing.PngBytes.Length == 0 || _savedData == null)
        {
            return null;
        }

        string artworkId = string.Empty;
        int removedDuplicateCount = 0;
        string cardId = provisionalCard.Id.ToString();
        string pngBase64 = Convert.ToBase64String(
            ArtworkStore.AdaptToCardPortrait(provisionalCard, drawing.PngBytes));
        _savedData.Modify(runState, data =>
        {
            data.Artworks ??= new List<MemorialArtworkData>();
            removedDuplicateCount = DeduplicateArtworkSessions(data.Artworks);
            MemorialArtworkData? existing = sessionId == 0u
                ? null
                : data.Artworks.FirstOrDefault(candidate =>
                    candidate.DrawerNetId == drawerNetId &&
                    candidate.SessionId == sessionId);
            if (existing != null)
            {
                existing.CardId = cardId;
                existing.PngBase64 = pngBase64;
                artworkId = existing.ArtworkId;
                return;
            }

            int ordinal = data.NextArtworkOrdinal++;
            artworkId = $"{drawerNetId:X16}:{sessionId:X8}:{ordinal:D6}";
            data.Artworks.Add(new MemorialArtworkData
            {
                ArtworkId = artworkId,
                CardId = cardId,
                DrawerNetId = drawerNetId,
                SessionId = sessionId,
                PngBase64 = pngBase64
            });
        });
        if (removedDuplicateCount > 0)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Removed {removedDuplicateCount} duplicate Memorial Sketchbook " +
                "artwork entries from the current run.");
        }
        SynchronizeProfileArchive(runState);
        return artworkId;
    }

    public static void AssignCard(RunState runState, string? artworkId, CardModel card, byte[] pngBytes)
    {
        if (string.IsNullOrEmpty(artworkId) || pngBytes.Length == 0 || _savedData == null)
        {
            return;
        }

        string cardId = card.Id.ToString();
        string pngBase64 = Convert.ToBase64String(ArtworkStore.AdaptToCardPortrait(card, pngBytes));
        _savedData.Modify(runState, data =>
        {
            data.Artworks ??= new List<MemorialArtworkData>();
            MemorialArtworkData? artwork = data.Artworks.FirstOrDefault(
                candidate => string.Equals(candidate.ArtworkId, artworkId, StringComparison.Ordinal));
            if (artwork == null)
            {
                return;
            }

            artwork.CardId = cardId;
            artwork.PngBase64 = pngBase64;
        });
        SynchronizeProfileArchive(runState);
    }

    public static async Task EnsureOwnedByAllPlayersAsync(RunState runState)
    {
        await RelicOwnershipGate.WaitAsync();
        try
        {
            foreach (var player in runState.Players.OrderBy(player => player.NetId))
            {
                List<MemorialSketchbook> ownedSketchbooks = player.Relics
                    .OfType<MemorialSketchbook>()
                    .ToList();
                if (ownedSketchbooks.Count == 0)
                {
                    await RelicCmd.Obtain<MemorialSketchbook>(player);
                    continue;
                }

                for (int index = 1; index < ownedSketchbooks.Count; index++)
                {
                    await RelicCmd.Remove(ownedSketchbooks[index]);
                }
                if (ownedSketchbooks.Count > 1)
                {
                    Entry.Logger.Warn(
                        $"[DrawAndGuessMod] Removed {ownedSketchbooks.Count - 1} duplicate Memorial " +
                        $"Sketchbook relics from player {player.NetId}.");
                }
            }
        }
        finally
        {
            RelicOwnershipGate.Release();
        }
    }

    public static IReadOnlyList<MemorialArtworkData> GetCurrentRunArtworks(RunState runState)
    {
        if (_savedData == null)
        {
            return [];
        }

        MemorialSketchbookRunState state = _savedData.Get(runState);
        state.Artworks ??= new List<MemorialArtworkData>();
        return CloneUniqueArtworks(state.Artworks);
    }

    public static IReadOnlyList<MemorialArtworkData> GetArchivedArtworks(string seed, long startTime)
    {
        try
        {
            MemorialSketchbookProfileData? profile = RitsuLibFramework
                .GetDataStore(Entry.ModId)
                .Get<MemorialSketchbookProfileData>(ProfileDataKey);
            MemorialRunArchive? archive = profile?.Runs?.FirstOrDefault(
                run => string.Equals(run.Seed, seed, StringComparison.Ordinal) &&
                       run.StartTime == startTime);
            return archive?.Artworks == null
                ? []
                : CloneUniqueArtworks(archive.Artworks);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to read Memorial Sketchbook history: {ex.Message}");
            return [];
        }
    }

    public static bool SetPermanentArtwork(MemorialArtworkData artwork)
    {
        return SaveCardArtwork(artwork, enableArtwork: true);
    }

    public static bool SaveArtworkToCollection(MemorialArtworkData artwork)
    {
        return SaveCardArtwork(artwork, enableArtwork: false);
    }

    private static bool SaveCardArtwork(MemorialArtworkData artwork, bool enableArtwork)
    {
        if (string.IsNullOrWhiteSpace(artwork.CardId) || string.IsNullOrWhiteSpace(artwork.PngBase64))
        {
            return false;
        }

        try
        {
            var store = RitsuLibFramework.GetDataStore(Entry.ModId);
            store.Modify<MemorialSketchbookProfileData>(ProfileDataKey, profile =>
            {
                profile.PermanentArtworks ??= new List<PermanentCardArtworkData>();
                PermanentCardArtworkData? existing = profile.PermanentArtworks.FirstOrDefault(
                    item => string.Equals(item.CardId, artwork.CardId, StringComparison.Ordinal) &&
                            string.Equals(item.ArtworkId, artwork.ArtworkId, StringComparison.Ordinal));
                if (existing == null)
                {
                    existing = new PermanentCardArtworkData
                    {
                        CardId = artwork.CardId,
                        ArtworkId = artwork.ArtworkId,
                        PngBase64 = artwork.PngBase64,
                        Enabled = false
                    };
                    profile.PermanentArtworks.Add(existing);
                }
                else
                {
                    existing.PngBase64 = artwork.PngBase64;
                }

                if (enableArtwork)
                {
                    foreach (PermanentCardArtworkData candidate in profile.PermanentArtworks.Where(
                                 item => string.Equals(
                                     item.CardId,
                                     artwork.CardId,
                                     StringComparison.Ordinal)))
                    {
                        candidate.Enabled = ReferenceEquals(candidate, existing);
                    }
                }
            });
            store.Save(ProfileDataKey);
            PermanentTextureCache.Remove(artwork.CardId);
            return true;
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to save permanent card artwork: {ex.Message}");
            return false;
        }
    }

    public static bool TryGetPermanentTexture(CardModel card, out Texture2D texture)
    {
        texture = null!;
        string cardId = card.Id.ToString();
        try
        {
            MemorialSketchbookProfileData? profile = RitsuLibFramework
                .GetDataStore(Entry.ModId)
                .Get<MemorialSketchbookProfileData>(ProfileDataKey);
            PermanentCardArtworkData? artwork = profile?.PermanentArtworks?.FirstOrDefault(
                item => string.Equals(item.CardId, cardId, StringComparison.Ordinal) &&
                        item.Enabled &&
                        !string.IsNullOrWhiteSpace(item.PngBase64));
            if (artwork == null ||
                string.IsNullOrWhiteSpace(artwork.PngBase64))
            {
                PermanentTextureCache.Remove(cardId);
                return false;
            }

            if (PermanentTextureCache.TryGetValue(cardId, out CachedPermanentTexture cached) &&
                string.Equals(cached.ArtworkId, artwork.ArtworkId, StringComparison.Ordinal) &&
                string.Equals(cached.PngBase64, artwork.PngBase64, StringComparison.Ordinal))
            {
                texture = cached.Texture;
                return true;
            }

            Texture2D? decoded = DecodeTexture(artwork.PngBase64);
            if (decoded == null)
            {
                return false;
            }

            texture = decoded;
            PermanentTextureCache[cardId] =
                new CachedPermanentTexture(artwork.ArtworkId, artwork.PngBase64, decoded);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool HasPermanentArtwork(CardModel card)
    {
        return HasCollectedCardArtwork(card.Id.ToString());
    }

    public static bool IsCardArtworkInCollection(string cardId, string artworkId)
    {
        return TryGetCardArtwork(cardId, artworkId, out _);
    }

    public static IReadOnlyList<PermanentCardArtworkData> GetPermanentArtworks()
    {
        try
        {
            MemorialSketchbookProfileData? profile = RitsuLibFramework
                .GetDataStore(Entry.ModId)
                .Get<MemorialSketchbookProfileData>(ProfileDataKey);
            return profile?.PermanentArtworks?
                       .Where(artwork =>
                           !string.IsNullOrWhiteSpace(artwork.CardId) &&
                           !string.IsNullOrWhiteSpace(artwork.PngBase64))
                       .GroupBy(
                           artwork => $"{artwork.CardId}\0{artwork.ArtworkId}",
                           StringComparer.Ordinal)
                       .Select(group => group.Last())
                       .Select(artwork => new PermanentCardArtworkData
                       {
                           CardId = artwork.CardId,
                           ArtworkId = artwork.ArtworkId,
                           PngBase64 = artwork.PngBase64,
                           Enabled = artwork.Enabled
                       })
                       .ToList()
                   ?? [];
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to read permanent card artworks: {ex.Message}");
            return [];
        }
    }

    public static bool SaveRelicArtworkToCollection(
        string artworkId,
        RelicModel relic,
        byte[] pngBytes)
    {
        if (string.IsNullOrWhiteSpace(artworkId) || pngBytes.Length == 0)
        {
            return false;
        }

        Image validationImage = new();
        if (validationImage.LoadPngFromBuffer(pngBytes) != Error.Ok)
        {
            return false;
        }

        string relicId = relic.Id.ToString();
        string pngBase64 = Convert.ToBase64String(pngBytes);
        try
        {
            var store = RitsuLibFramework.GetDataStore(Entry.ModId);
            store.Modify<MemorialSketchbookProfileData>(ProfileDataKey, profile =>
            {
                profile.PermanentRelicArtworks ??= new List<PermanentRelicArtworkData>();
                PermanentRelicArtworkData? existing = profile.PermanentRelicArtworks.FirstOrDefault(
                    item => string.Equals(item.RelicId, relicId, StringComparison.Ordinal) &&
                            string.Equals(item.ArtworkId, artworkId, StringComparison.Ordinal));
                if (existing == null)
                {
                    profile.PermanentRelicArtworks.Add(new PermanentRelicArtworkData
                    {
                        RelicId = relicId,
                        ArtworkId = artworkId,
                        PngBase64 = pngBase64,
                        Enabled = false
                    });
                    return;
                }

                existing.PngBase64 = pngBase64;
            });
            store.Save(ProfileDataKey);
            PermanentRelicTextureCache.Remove(relicId);
            return true;
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to save relic artwork to collection: {ex.Message}");
            return false;
        }
    }

    public static IReadOnlyList<PermanentRelicArtworkData> GetPermanentRelicArtworks()
    {
        try
        {
            MemorialSketchbookProfileData? profile = RitsuLibFramework
                .GetDataStore(Entry.ModId)
                .Get<MemorialSketchbookProfileData>(ProfileDataKey);
            return profile?.PermanentRelicArtworks?
                       .Where(artwork =>
                           !string.IsNullOrWhiteSpace(artwork.RelicId) &&
                           !string.IsNullOrWhiteSpace(artwork.PngBase64))
                       .GroupBy(
                           artwork => $"{artwork.RelicId}\0{artwork.ArtworkId}",
                           StringComparer.Ordinal)
                       .Select(group => group.Last())
                       .Select(artwork => new PermanentRelicArtworkData
                       {
                           RelicId = artwork.RelicId,
                           ArtworkId = artwork.ArtworkId,
                           PngBase64 = artwork.PngBase64,
                           Enabled = artwork.Enabled
                       })
                       .ToList()
                   ?? [];
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to read permanent relic artworks: {ex.Message}");
            return [];
        }
    }

    public static bool HasPermanentRelicArtwork(RelicModel relic)
    {
        return HasCollectedRelicArtwork(relic.Id.ToString());
    }

    public static bool IsRelicArtworkInCollection(string relicId, string artworkId)
    {
        return TryGetRelicArtwork(relicId, artworkId, out _);
    }

    public static bool SetPermanentRelicArtworkEnabled(string relicId, bool enabled)
    {
        return SetPermanentRelicArtworkEnabledCore(relicId, artworkId: null, enabled);
    }

    public static bool SetPermanentRelicArtworkEnabled(
        string relicId,
        string artworkId,
        bool enabled)
    {
        return SetPermanentRelicArtworkEnabledCore(relicId, artworkId, enabled);
    }

    private static bool SetPermanentRelicArtworkEnabledCore(
        string relicId,
        string? artworkId,
        bool enabled)
    {
        try
        {
            var store = RitsuLibFramework.GetDataStore(Entry.ModId);
            bool found = false;
            bool changed = false;
            store.Modify<MemorialSketchbookProfileData>(ProfileDataKey, profile =>
            {
                List<PermanentRelicArtworkData> candidates = profile.PermanentRelicArtworks?
                    .Where(item => string.Equals(item.RelicId, relicId, StringComparison.Ordinal))
                    .ToList() ?? [];
                PermanentRelicArtworkData? selected = artworkId == null
                    ? candidates.FirstOrDefault(item => item.Enabled) ?? candidates.FirstOrDefault()
                    : candidates.FirstOrDefault(item => string.Equals(
                        item.ArtworkId,
                        artworkId,
                        StringComparison.Ordinal));
                if (selected == null)
                {
                    return;
                }

                found = true;
                foreach (PermanentRelicArtworkData candidate in candidates)
                {
                    bool nextEnabled = enabled && ReferenceEquals(candidate, selected);
                    if (candidate.Enabled != nextEnabled)
                    {
                        candidate.Enabled = nextEnabled;
                        changed = true;
                    }
                }
            });
            if (!changed)
            {
                return found;
            }

            store.Save(ProfileDataKey);
            PermanentRelicTextureCache.Remove(relicId);
            return true;
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to toggle permanent relic artwork: {ex.Message}");
            return false;
        }
    }

    public static bool ClearPermanentRelicArtwork(string relicId)
    {
        return ClearPermanentRelicArtworkCore(relicId, artworkId: null);
    }

    public static bool ClearPermanentRelicArtwork(string relicId, string artworkId)
    {
        return ClearPermanentRelicArtworkCore(relicId, artworkId);
    }

    private static bool ClearPermanentRelicArtworkCore(string relicId, string? artworkId)
    {
        if (string.IsNullOrWhiteSpace(relicId))
        {
            return false;
        }

        try
        {
            var store = RitsuLibFramework.GetDataStore(Entry.ModId);
            bool changed = false;
            store.Modify<MemorialSketchbookProfileData>(ProfileDataKey, profile =>
            {
                profile.PermanentRelicArtworks ??= new List<PermanentRelicArtworkData>();
                changed = profile.PermanentRelicArtworks.RemoveAll(item =>
                    string.Equals(item.RelicId, relicId, StringComparison.Ordinal) &&
                    (artworkId == null || string.Equals(
                        item.ArtworkId,
                        artworkId,
                        StringComparison.Ordinal))) > 0;
            });
            if (!changed)
            {
                return false;
            }

            store.Save(ProfileDataKey);
            PermanentRelicTextureCache.Remove(relicId);
            return true;
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to remove permanent relic artwork: {ex.Message}");
            return false;
        }
    }

    public static bool TryGetPermanentRelicTexture(RelicModel relic, out Texture2D texture)
    {
        if (TryGetPermanentRelicTextures(relic, out CachedPermanentRelicTextures textures))
        {
            texture = textures.Artwork;
            return true;
        }

        texture = null!;
        return false;
    }

    public static bool TryGetPermanentRelicTriggerTexture(RelicModel relic, out Texture2D texture)
    {
        if (TryGetPermanentRelicTextures(relic, out CachedPermanentRelicTextures textures))
        {
            texture = textures.TriggerArtwork;
            return true;
        }

        texture = null!;
        return false;
    }

    public static bool IsPermanentArtworkEnabled(CardModel card)
    {
        return TryGetActivePermanentArtwork(card.Id.ToString(), out _);
    }

    public static bool IsPermanentArtwork(MemorialArtworkData artwork)
    {
        return TryGetCardArtwork(
                   artwork.CardId,
                   artwork.ArtworkId,
                   out PermanentCardArtworkData? permanent) &&
               permanent != null &&
               permanent.Enabled;
    }

    public static void SetPermanentArtworkEnabled(CardModel card, bool enabled)
    {
        SetPermanentArtworkEnabled(card.Id.ToString(), enabled);
    }

    public static bool SetPermanentArtworkEnabled(string cardId, bool enabled)
    {
        return SetPermanentArtworkEnabledCore(cardId, artworkId: null, enabled);
    }

    public static bool SetPermanentArtworkEnabled(
        string cardId,
        string artworkId,
        bool enabled)
    {
        return SetPermanentArtworkEnabledCore(cardId, artworkId, enabled);
    }

    private static bool SetPermanentArtworkEnabledCore(
        string cardId,
        string? artworkId,
        bool enabled)
    {
        try
        {
            var store = RitsuLibFramework.GetDataStore(Entry.ModId);
            bool found = false;
            bool changed = false;
            store.Modify<MemorialSketchbookProfileData>(ProfileDataKey, profile =>
            {
                List<PermanentCardArtworkData> candidates = profile.PermanentArtworks?
                    .Where(item => string.Equals(item.CardId, cardId, StringComparison.Ordinal))
                    .ToList() ?? [];
                PermanentCardArtworkData? selected = artworkId == null
                    ? candidates.FirstOrDefault(item => item.Enabled) ?? candidates.FirstOrDefault()
                    : candidates.FirstOrDefault(item => string.Equals(
                        item.ArtworkId,
                        artworkId,
                        StringComparison.Ordinal));
                if (selected == null)
                {
                    return;
                }

                found = true;
                foreach (PermanentCardArtworkData candidate in candidates)
                {
                    bool nextEnabled = enabled && ReferenceEquals(candidate, selected);
                    if (candidate.Enabled != nextEnabled)
                    {
                        candidate.Enabled = nextEnabled;
                        changed = true;
                    }
                }
            });
            if (!changed)
            {
                return found;
            }

            store.Save(ProfileDataKey);
            PermanentTextureCache.Remove(cardId);
            return true;
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to toggle permanent card artwork: {ex.Message}");
            return false;
        }
    }

    public static void ClearPermanentArtwork(MemorialArtworkData artwork)
    {
        try
        {
            var store = RitsuLibFramework.GetDataStore(Entry.ModId);
            bool changed = false;
            store.Modify<MemorialSketchbookProfileData>(ProfileDataKey, profile =>
            {
                profile.PermanentArtworks ??= new List<PermanentCardArtworkData>();
                changed = profile.PermanentArtworks.RemoveAll(item =>
                    string.Equals(item.CardId, artwork.CardId, StringComparison.Ordinal) &&
                    string.Equals(item.ArtworkId, artwork.ArtworkId, StringComparison.Ordinal)) > 0;
            });
            if (!changed)
            {
                return;
            }

            store.Save(ProfileDataKey);
            PermanentTextureCache.Remove(artwork.CardId);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to clear permanent card artwork: {ex.Message}");
        }
    }

    public static bool ClearPermanentArtwork(string cardId)
    {
        return ClearPermanentArtworkCore(cardId, artworkId: null);
    }

    public static bool ClearPermanentArtwork(string cardId, string artworkId)
    {
        return ClearPermanentArtworkCore(cardId, artworkId);
    }

    private static bool ClearPermanentArtworkCore(string cardId, string? artworkId)
    {
        if (string.IsNullOrWhiteSpace(cardId))
        {
            return false;
        }

        try
        {
            var store = RitsuLibFramework.GetDataStore(Entry.ModId);
            bool changed = false;
            store.Modify<MemorialSketchbookProfileData>(ProfileDataKey, profile =>
            {
                profile.PermanentArtworks ??= new List<PermanentCardArtworkData>();
                changed = profile.PermanentArtworks.RemoveAll(item =>
                    string.Equals(item.CardId, cardId, StringComparison.Ordinal) &&
                    (artworkId == null || string.Equals(
                        item.ArtworkId,
                        artworkId,
                        StringComparison.Ordinal))) > 0;
            });
            if (!changed)
            {
                return false;
            }

            store.Save(ProfileDataKey);
            PermanentTextureCache.Remove(cardId);
            return true;
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to remove permanent card artwork: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetPermanentRelicTextures(
        RelicModel relic,
        out CachedPermanentRelicTextures textures)
    {
        string relicId = relic.Id.ToString();
        textures = default;
        if (!TryGetActivePermanentRelicArtwork(
                relicId,
                out PermanentRelicArtworkData? artwork) ||
            artwork == null)
        {
            PermanentRelicTextureCache.Remove(relicId);
            return false;
        }

        if (PermanentRelicTextureCache.TryGetValue(relicId, out textures) &&
            string.Equals(textures.ArtworkId, artwork.ArtworkId, StringComparison.Ordinal) &&
            string.Equals(textures.PngBase64, artwork.PngBase64, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            Image image = new();
            if (image.LoadPngFromBuffer(Convert.FromBase64String(artwork.PngBase64)) != Error.Ok)
            {
                return false;
            }

            Texture2D fullTexture = ImageTexture.CreateFromImage(image);
            Image triggerImage = Image.CreateFromData(
                image.GetWidth(),
                image.GetHeight(),
                false,
                image.GetFormat(),
                image.GetData());
            triggerImage.Resize(68, 68, Image.Interpolation.Lanczos);
            Texture2D triggerTexture = ImageTexture.CreateFromImage(triggerImage);
            textures = new CachedPermanentRelicTextures(
                artwork.ArtworkId,
                artwork.PngBase64,
                fullTexture,
                triggerTexture);
            PermanentRelicTextureCache[relicId] = textures;
            return true;
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn(
                $"[DrawAndGuessMod] Failed to decode permanent relic artwork for {relicId}: {ex.Message}");
            return false;
        }
    }

    private static bool HasCollectedCardArtwork(string cardId)
    {
        return TryReadProfile(out MemorialSketchbookProfileData? profile) &&
               profile?.PermanentArtworks?.Any(item =>
                   string.Equals(item.CardId, cardId, StringComparison.Ordinal) &&
                   !string.IsNullOrWhiteSpace(item.PngBase64)) == true;
    }

    private static bool HasCollectedRelicArtwork(string relicId)
    {
        return TryReadProfile(out MemorialSketchbookProfileData? profile) &&
               profile?.PermanentRelicArtworks?.Any(item =>
                   string.Equals(item.RelicId, relicId, StringComparison.Ordinal) &&
                   !string.IsNullOrWhiteSpace(item.PngBase64)) == true;
    }

    private static bool TryGetCardArtwork(
        string cardId,
        string artworkId,
        out PermanentCardArtworkData? artwork)
    {
        artwork = null;
        if (!TryReadProfile(out MemorialSketchbookProfileData? profile))
        {
            return false;
        }

        artwork = profile?.PermanentArtworks?.FirstOrDefault(item =>
            string.Equals(item.CardId, cardId, StringComparison.Ordinal) &&
            string.Equals(item.ArtworkId, artworkId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(item.PngBase64));
        return artwork != null;
    }

    private static bool TryGetRelicArtwork(
        string relicId,
        string artworkId,
        out PermanentRelicArtworkData? artwork)
    {
        artwork = null;
        if (!TryReadProfile(out MemorialSketchbookProfileData? profile))
        {
            return false;
        }

        artwork = profile?.PermanentRelicArtworks?.FirstOrDefault(item =>
            string.Equals(item.RelicId, relicId, StringComparison.Ordinal) &&
            string.Equals(item.ArtworkId, artworkId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(item.PngBase64));
        return artwork != null;
    }

    private static bool TryGetActivePermanentArtwork(
        string cardId,
        out PermanentCardArtworkData? artwork)
    {
        artwork = null;
        if (!TryReadProfile(out MemorialSketchbookProfileData? profile))
        {
            return false;
        }

        artwork = profile?.PermanentArtworks?.FirstOrDefault(item =>
            string.Equals(item.CardId, cardId, StringComparison.Ordinal) &&
            item.Enabled &&
            !string.IsNullOrWhiteSpace(item.PngBase64));
        return artwork != null;
    }

    private static bool TryGetActivePermanentRelicArtwork(
        string relicId,
        out PermanentRelicArtworkData? artwork)
    {
        artwork = null;
        if (!TryReadProfile(out MemorialSketchbookProfileData? profile))
        {
            return false;
        }

        artwork = profile?.PermanentRelicArtworks?.FirstOrDefault(item =>
            string.Equals(item.RelicId, relicId, StringComparison.Ordinal) &&
            item.Enabled &&
            !string.IsNullOrWhiteSpace(item.PngBase64));
        return artwork != null;
    }

    private static bool TryReadProfile(out MemorialSketchbookProfileData? profile)
    {
        profile = null;
        try
        {
            profile = RitsuLibFramework
                .GetDataStore(Entry.ModId)
                .Get<MemorialSketchbookProfileData>(ProfileDataKey);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void SynchronizeProfileArchive(RunState runState)
    {
        if (_savedData == null)
        {
            return;
        }

        try
        {
            MemorialSketchbookRunState current = _savedData.Get(runState);
            current.Artworks ??= new List<MemorialArtworkData>();
            if (current.Artworks.Count == 0)
            {
                return;
            }

            string seed = runState.Rng.StringSeed;
            long startTime = RunManager.Instance.ToSave(null).StartTime;
            string runKey = BuildRunKey(seed, startTime);
            List<MemorialArtworkData> artworks = CloneUniqueArtworks(current.Artworks);
            var store = RitsuLibFramework.GetDataStore(Entry.ModId);
            store.Modify<MemorialSketchbookProfileData>(ProfileDataKey, profile =>
            {
                profile.Runs ??= new List<MemorialRunArchive>();
                MemorialRunArchive? archive = profile.Runs.FirstOrDefault(
                    run => string.Equals(run.RunKey, runKey, StringComparison.Ordinal));
                if (archive == null)
                {
                    archive = new MemorialRunArchive
                    {
                        RunKey = runKey,
                        Seed = seed,
                        StartTime = startTime
                    };
                    profile.Runs.Add(archive);
                }
                archive.Artworks = artworks;

                profile.Runs = profile.Runs
                    .OrderByDescending(run => run.StartTime)
                    .ThenByDescending(run => run.RunKey, StringComparer.Ordinal)
                    .Take(MaximumArchivedRuns)
                    .ToList();
            });
            store.Save(ProfileDataKey);
        }
        catch (Exception ex)
        {
            Entry.Logger.Warn($"[DrawAndGuessMod] Failed to archive Memorial Sketchbook: {ex.Message}");
        }
    }

    private static string BuildRunKey(string seed, long startTime)
    {
        return $"{startTime}:{seed}";
    }

    private static MemorialArtworkData CloneArtwork(MemorialArtworkData source)
    {
        return new MemorialArtworkData
        {
            ArtworkId = source.ArtworkId,
            CardId = source.CardId,
            DrawerNetId = source.DrawerNetId,
            SessionId = source.SessionId,
            PngBase64 = source.PngBase64
        };
    }

    private static List<MemorialArtworkData> CloneUniqueArtworks(IEnumerable<MemorialArtworkData> artworks)
    {
        List<MemorialArtworkData> clones = artworks.Select(CloneArtwork).ToList();
        DeduplicateArtworkSessions(clones);
        return clones;
    }

    private static int DeduplicateArtworkSessions(List<MemorialArtworkData> artworks)
    {
        Dictionary<(ulong DrawerNetId, uint SessionId), MemorialArtworkData> firstBySession = new();
        int removedCount = 0;
        for (int index = 0; index < artworks.Count;)
        {
            MemorialArtworkData candidate = artworks[index];
            if (candidate.SessionId == 0u)
            {
                index++;
                continue;
            }

            (ulong DrawerNetId, uint SessionId) key = (candidate.DrawerNetId, candidate.SessionId);
            if (!firstBySession.TryGetValue(key, out MemorialArtworkData? first))
            {
                firstBySession[key] = candidate;
                index++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(candidate.CardId))
            {
                first.CardId = candidate.CardId;
            }
            if (!string.IsNullOrWhiteSpace(candidate.PngBase64))
            {
                first.PngBase64 = candidate.PngBase64;
            }
            artworks.RemoveAt(index);
            removedCount++;
        }
        return removedCount;
    }

    private static Texture2D? DecodeTexture(string pngBase64)
    {
        try
        {
            Image image = new();
            return image.LoadPngFromBuffer(Convert.FromBase64String(pngBase64)) == Error.Ok
                ? ImageTexture.CreateFromImage(image)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct CachedPermanentTexture(string ArtworkId, string PngBase64, Texture2D Texture);

    private readonly record struct CachedPermanentRelicTextures(
        string ArtworkId,
        string PngBase64,
        Texture2D Artwork,
        Texture2D TriggerArtwork);
}
