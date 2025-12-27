using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace SoulGear
{
    /// <summary>
    /// Main mod system for Soul Gear. Handles registration and death event interception.
    /// Manages keep-inventory protection charges for players.
    /// </summary>
    public class SoulGearModSystem : ModSystem
    {
        public static ICoreServerAPI ServerApi { get; private set; }
        private Harmony harmony;

        // Key used to store the keep-inventory protection in player's WatchedAttributes
        public const string SOUL_PROTECTION_KEY = "soulGearProtection";

        // Key used to store saved inventories in world save data
        private const string SAVE_DATA_KEY = "soulGearSavedInventories";

        // Storage for saved inventories - keyed by player UID (thread-safe)
        public static ConcurrentDictionary<string, SavedPlayerData> SavedInventories = new ConcurrentDictionary<string, SavedPlayerData>();

        // Track players currently being restored to prevent race conditions
        private static ConcurrentDictionary<string, byte> restoringPlayers = new ConcurrentDictionary<string, byte>();

        // Lock object for persistence operations
        private static readonly object persistLock = new object();

        // Flag to indicate pending save (debounce persistence)
        private static volatile bool pendingSave = false;

        // Timeout for saved inventories of disconnected players (30 minutes)
        private const long SAVED_INVENTORY_TIMEOUT_MS = 30 * 60 * 1000;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            // Register the custom item class
            api.RegisterItemClass("ItemSoulGear", typeof(ItemSoulGear));
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);
            ServerApi = api;

            // Initialize Harmony
            harmony = new Harmony(Mod.Info.ModID);
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            // Hook into player respawn event
            api.Event.PlayerRespawn += OnPlayerRespawn;

            // Hook into world save event to persist saved inventories
            api.Event.GameWorldSave += OnGameWorldSave;

            // Register a periodic check for players who were revived without respawning
            // (e.g., healed by poultice/bandage while downed)
            api.Event.RegisterGameTickListener(OnGameTick, 1000); // Check every second

            // Load saved inventory data after the save game is fully loaded
            // This ensures item references can be properly resolved
            api.Event.SaveGameLoaded += LoadSavedInventories;

            // Register chat command to check soul protection charges
            api.ChatCommands.Create("soulcharges")
                .WithDescription("Check how many Soul Gear protection charges you have")
                .HandleWith(OnSoulChargesCommand);

            api.Logger.Notification("[SoulGear] Mod loaded with Harmony patches applied");
        }

        /// <summary>
        /// Handler for the /soulcharges command.
        /// </summary>
        private TextCommandResult OnSoulChargesCommand(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player;
            if (player?.Entity == null)
            {
                return TextCommandResult.Error("Could not find player entity");
            }

            int charges = player.Entity.WatchedAttributes.GetInt(SOUL_PROTECTION_KEY, 0);

            if (charges == 0)
            {
                return TextCommandResult.Success(Lang.Get("soulgear:message-no-charges"));
            }
            else if (charges == 1)
            {
                return TextCommandResult.Success(Lang.Get("soulgear:message-one-charge"));
            }
            else
            {
                return TextCommandResult.Success(Lang.Get("soulgear:message-multiple-charges", charges));
            }
        }

        /// <summary>
        /// Called every second to check for players who were revived without respawning
        /// (e.g., healed by a teammate's poultice/bandage while downed).
        /// Also cleans up stale inventory data for disconnected players.
        /// </summary>
        private void OnGameTick(float dt)
        {
            if (SavedInventories.IsEmpty) return;

            long currentTime = ServerApi.World.ElapsedMilliseconds;
            var keysToRemove = new List<string>();

            foreach (var kvp in SavedInventories)
            {
                string playerUid = kvp.Key;
                SavedPlayerData data = kvp.Value;

                var player = ServerApi.World.PlayerByUid(playerUid) as IServerPlayer;

                // Check for stale data from disconnected players
                if (player?.Entity == null)
                {
                    if (currentTime - data.SavedAtTime > SAVED_INVENTORY_TIMEOUT_MS)
                    {
                        keysToRemove.Add(playerUid);
                        ServerApi.Logger.Warning($"[SoulGear] Removing stale inventory data for offline player {playerUid}");
                    }
                    continue;
                }

                // Check if the player is alive (was revived without respawning)
                if (player.Entity.Alive)
                {
                    ServerApi.Logger.Debug($"[SoulGear] Detected revived player {player.PlayerName} with pending inventory - restoring");
                    RestorePlayerInventory(player);
                }
            }

            // Clean up stale entries
            foreach (var key in keysToRemove)
            {
                SavedInventories.TryRemove(key, out _);
                pendingSave = true;
            }
        }

        /// <summary>
        /// Called when a player respawns. If they had soul protection, restore their inventory.
        /// </summary>
        private void OnPlayerRespawn(IServerPlayer byPlayer)
        {
            if (byPlayer == null) return;

            string playerUid = byPlayer.PlayerUID;

            if (SavedInventories.ContainsKey(playerUid))
            {
                // Delay restoration slightly to ensure player is fully loaded
                // Capture the player reference to avoid issues if player disconnects
                ServerApi.Event.RegisterCallback((dt) =>
                {
                    // Verify player is still valid
                    if (byPlayer?.Entity != null && byPlayer.Entity.Alive)
                    {
                        RestorePlayerInventory(byPlayer);
                    }
                }, 500);
            }
        }

        /// <summary>
        /// Save all items from the player's inventories.
        /// Called from Harmony patch before death.
        /// </summary>
        public static void SavePlayerInventory(IServerPlayer player)
        {
            if (player == null || ServerApi == null) return;

            string playerUid = player.PlayerUID;
            var inventoryList = new List<SavedInventory>();

            // Iterate through all player inventories
            foreach (var inventory in player.InventoryManager.Inventories.Values)
            {
                // Skip inventories that shouldn't be saved (like crafting grid)
                string invClassName = inventory.ClassName;

                // Save hotbar, backpack, and character (armor/clothes) inventories
                if (invClassName == GlobalConstants.hotBarInvClassName ||
                    invClassName == GlobalConstants.backpackInvClassName ||
                    invClassName == GlobalConstants.characterInvClassName)
                {
                    var savedInv = new SavedInventory
                    {
                        InventoryClassName = invClassName,
                        InventoryId = inventory.InventoryID,
                        Slots = new List<SavedSlot>()
                    };

                    for (int i = 0; i < inventory.Count; i++)
                    {
                        var slot = inventory[i];
                        if (slot?.Itemstack != null)
                        {
                            savedInv.Slots.Add(new SavedSlot
                            {
                                SlotIndex = i,
                                ItemStack = slot.Itemstack.Clone()
                            });
                        }
                    }

                    if (savedInv.Slots.Count > 0)
                    {
                        inventoryList.Add(savedInv);
                    }
                }
            }

            var playerData = new SavedPlayerData
            {
                Inventories = inventoryList,
                SavedAtTime = ServerApi.World.ElapsedMilliseconds
            };

            SavedInventories[playerUid] = playerData;

            // Mark for persistence (debounced - will save on next world save)
            pendingSave = true;

            ServerApi.Logger.Debug($"[SoulGear] Saved {inventoryList.Count} inventories for player {player.PlayerName}");
        }

        /// <summary>
        /// Clear all items from the player's inventories so nothing drops.
        /// Called from Harmony patch before death.
        /// </summary>
        public static void ClearPlayerInventory(IServerPlayer player)
        {
            if (player == null) return;

            foreach (var inventory in player.InventoryManager.Inventories.Values)
            {
                string invClassName = inventory.ClassName;

                // Clear hotbar, backpack, and character inventories
                if (invClassName == GlobalConstants.hotBarInvClassName ||
                    invClassName == GlobalConstants.backpackInvClassName ||
                    invClassName == GlobalConstants.characterInvClassName)
                {
                    for (int i = 0; i < inventory.Count; i++)
                    {
                        var slot = inventory[i];
                        if (slot != null)
                        {
                            slot.Itemstack = null;
                            slot.MarkDirty();
                        }
                    }
                }
            }

            ServerApi?.Logger.Debug($"[SoulGear] Cleared inventory for player {player.PlayerName}");
        }

        /// <summary>
        /// Restore saved items to the player's inventories.
        /// Uses a guard to prevent concurrent restoration attempts.
        /// </summary>
        private void RestorePlayerInventory(IServerPlayer player)
        {
            if (player == null) return;

            string playerUid = player.PlayerUID;

            // Prevent concurrent restoration attempts (race condition guard)
            if (!restoringPlayers.TryAdd(playerUid, 0))
            {
                ServerApi.Logger.Debug($"[SoulGear] Restoration already in progress for {player.PlayerName}, skipping");
                return;
            }

            try
            {
                if (!SavedInventories.TryGetValue(playerUid, out var playerData))
                {
                    return;
                }

                var inventoryList = playerData.Inventories;
                int restoredCount = 0;
                int expectedCount = 0;
                bool allRestored = true;

                foreach (var savedInv in inventoryList)
                {
                    expectedCount += savedInv.Slots.Count;

                    // Find the matching inventory - prefer exact ID match
                    IInventory targetInventory = FindInventory(player, savedInv.InventoryId, savedInv.InventoryClassName);

                    if (targetInventory == null)
                    {
                        ServerApi.Logger.Warning($"[SoulGear] Could not find inventory {savedInv.InventoryClassName} for player {player.PlayerName}");
                        allRestored = false;
                        continue;
                    }

                    foreach (var savedSlot in savedInv.Slots)
                    {
                        if (savedSlot.SlotIndex < targetInventory.Count)
                        {
                            var slot = targetInventory[savedSlot.SlotIndex];
                            if (slot != null)
                            {
                                // Place the saved item
                                slot.Itemstack = savedSlot.ItemStack.Clone();
                                slot.MarkDirty();
                                restoredCount++;
                            }
                            else
                            {
                                allRestored = false;
                            }
                        }
                        else
                        {
                            ServerApi.Logger.Warning($"[SoulGear] Slot index {savedSlot.SlotIndex} out of range for inventory {savedInv.InventoryClassName}");
                            allRestored = false;
                        }
                    }
                }

                // Only remove saved data if restoration was successful
                if (restoredCount > 0)
                {
                    SavedInventories.TryRemove(playerUid, out _);
                    pendingSave = true;

                    if (!allRestored)
                    {
                        ServerApi.Logger.Warning($"[SoulGear] Partial restoration for {player.PlayerName}: {restoredCount}/{expectedCount} items");
                    }

                    ServerApi.Logger.Debug($"[SoulGear] Restored {restoredCount} item stacks for player {player.PlayerName}");

                    // Notify the player
                    player.SendMessage(GlobalConstants.GeneralChatGroup,
                        Lang.Get("soulgear:message-inventory-restored"),
                        EnumChatType.Notification);
                }
                else if (expectedCount > 0)
                {
                    // Complete failure - keep the data for retry
                    ServerApi.Logger.Error($"[SoulGear] Failed to restore any items for {player.PlayerName}, keeping saved data");
                }
            }
            finally
            {
                restoringPlayers.TryRemove(playerUid, out _);
            }
        }

        /// <summary>
        /// Find an inventory by ID or class name.
        /// </summary>
        private static IInventory FindInventory(IServerPlayer player, string inventoryId, string inventoryClassName)
        {
            // First try exact ID match
            foreach (var inventory in player.InventoryManager.Inventories.Values)
            {
                if (inventory.InventoryID == inventoryId)
                {
                    return inventory;
                }
            }

            // Fallback to class name match, but be specific about which one
            IInventory fallbackInventory = null;
            int matchCount = 0;

            foreach (var inventory in player.InventoryManager.Inventories.Values)
            {
                if (inventory.ClassName == inventoryClassName)
                {
                    fallbackInventory = inventory;
                    matchCount++;
                }
            }

            // Only use fallback if there's exactly one match to avoid ambiguity
            if (matchCount == 1)
            {
                return fallbackInventory;
            }

            if (matchCount > 1)
            {
                ServerApi?.Logger.Warning($"[SoulGear] Multiple inventories with class {inventoryClassName} found, cannot safely restore");
            }

            return null;
        }

        public override void Dispose()
        {
            if (ServerApi != null)
            {
                ServerApi.Event.PlayerRespawn -= OnPlayerRespawn;
                ServerApi.Event.GameWorldSave -= OnGameWorldSave;
                ServerApi.Event.SaveGameLoaded -= LoadSavedInventories;
            }

            // Unpatch Harmony
            harmony?.UnpatchAll(Mod.Info.ModID);

            SavedInventories.Clear();
            restoringPlayers.Clear();
            pendingSave = false;
            base.Dispose();
        }

        /// <summary>
        /// Called when the world is saved. Persist saved inventories to world save data.
        /// Only persists if there are pending changes (debounced).
        /// </summary>
        private void OnGameWorldSave()
        {
            if (pendingSave || !SavedInventories.IsEmpty)
            {
                PersistSavedInventories();
                pendingSave = false;
            }
        }

        /// <summary>
        /// Persist the saved inventories to world save data.
        /// Thread-safe using lock to prevent concurrent serialization.
        /// </summary>
        public static void PersistSavedInventories()
        {
            if (ServerApi == null) return;

            lock (persistLock)
            {
                if (SavedInventories.IsEmpty)
                {
                    // Clear any existing save data if no inventories to save
                    ServerApi.WorldManager.SaveGame.StoreData(SAVE_DATA_KEY, null);
                    return;
                }

                try
                {
                    // Take a snapshot to avoid issues during iteration
                    var snapshot = SavedInventories.ToArray();

                    byte[] data;
                    using (var ms = new MemoryStream())
                    {
                        using (var writer = new BinaryWriter(ms))
                        {
                            // Write version for future compatibility
                            writer.Write((byte)1);

                            // Write number of players
                            writer.Write(snapshot.Length);

                            foreach (var kvp in snapshot)
                            {
                                // Write player UID
                                writer.Write(kvp.Key);

                                // Write saved time
                                writer.Write(kvp.Value.SavedAtTime);

                                // Write number of inventories for this player
                                writer.Write(kvp.Value.Inventories.Count);

                                foreach (var savedInv in kvp.Value.Inventories)
                                {
                                    // Write inventory info
                                    writer.Write(savedInv.InventoryClassName);
                                    writer.Write(savedInv.InventoryId);

                                    // Write number of slots
                                    writer.Write(savedInv.Slots.Count);

                                    foreach (var slot in savedInv.Slots)
                                    {
                                        // Write slot index
                                        writer.Write(slot.SlotIndex);

                                        // Write ItemStack
                                        slot.ItemStack.ToBytes(writer);
                                    }
                                }
                            }
                        }
                        data = ms.ToArray();
                    }

                    ServerApi.WorldManager.SaveGame.StoreData(SAVE_DATA_KEY, data);
                    ServerApi.Logger.Debug($"[SoulGear] Persisted {snapshot.Length} player inventories to world save");
                }
                catch (Exception ex)
                {
                    ServerApi.Logger.Error($"[SoulGear] Failed to persist saved inventories: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Load saved inventories from world save data.
        /// Supports both legacy format (v0) and new format (v1).
        /// </summary>
        private void LoadSavedInventories()
        {
            if (ServerApi == null) return;

            // Clear any existing data before loading
            SavedInventories.Clear();

            try
            {
                byte[] data = ServerApi.WorldManager.SaveGame.GetData(SAVE_DATA_KEY);
                if (data == null || data.Length == 0)
                {
                    ServerApi.Logger.Debug("[SoulGear] No saved inventory data found in world save");
                    return;
                }

                using (var ms = new MemoryStream(data))
                {
                    using (var reader = new BinaryReader(ms))
                    {
                        // Check for version byte - legacy format starts with player count (int32)
                        // New format starts with version byte
                        byte firstByte = reader.ReadByte();
                        int playerCount;
                        byte version;

                        if (firstByte == 1)
                        {
                            // New format with version
                            version = firstByte;
                            playerCount = reader.ReadInt32();
                        }
                        else
                        {
                            // Legacy format - first byte is part of player count
                            // Rewind and read as int32
                            ms.Position = 0;
                            playerCount = reader.ReadInt32();
                            version = 0;
                        }

                        long currentTime = ServerApi.World.ElapsedMilliseconds;

                        for (int p = 0; p < playerCount; p++)
                        {
                            // Read player UID
                            string playerUid = reader.ReadString();

                            // Read saved time (only in v1+)
                            long savedAtTime = version >= 1 ? reader.ReadInt64() : currentTime;

                            // Read number of inventories
                            int invCount = reader.ReadInt32();

                            var inventoryList = new List<SavedInventory>();

                            for (int i = 0; i < invCount; i++)
                            {
                                // Read inventory info
                                string invClassName = reader.ReadString();
                                string invId = reader.ReadString();

                                var savedInv = new SavedInventory
                                {
                                    InventoryClassName = invClassName,
                                    InventoryId = invId,
                                    Slots = new List<SavedSlot>()
                                };

                                // Read number of slots
                                int slotCount = reader.ReadInt32();

                                for (int s = 0; s < slotCount; s++)
                                {
                                    // Read slot index
                                    int slotIndex = reader.ReadInt32();

                                    // Read ItemStack
                                    var itemStack = new ItemStack(reader);

                                    // Resolve the item/block reference against the world's registry
                                    itemStack.ResolveBlockOrItem(ServerApi.World);

                                    // Only add if the item was successfully resolved
                                    if (itemStack.Collectible != null)
                                    {
                                        savedInv.Slots.Add(new SavedSlot
                                        {
                                            SlotIndex = slotIndex,
                                            ItemStack = itemStack
                                        });
                                    }
                                    else
                                    {
                                        ServerApi.Logger.Warning($"[SoulGear] Could not resolve item in saved inventory, skipping slot {slotIndex}");
                                    }
                                }

                                inventoryList.Add(savedInv);
                            }

                            var playerData = new SavedPlayerData
                            {
                                Inventories = inventoryList,
                                SavedAtTime = savedAtTime
                            };

                            SavedInventories[playerUid] = playerData;
                        }
                    }
                }

                ServerApi.Logger.Notification($"[SoulGear] Loaded {SavedInventories.Count} player inventories from world save");
            }
            catch (Exception ex)
            {
                // Clear potentially corrupt data on failure
                SavedInventories.Clear();
                ServerApi.Logger.Error($"[SoulGear] Failed to load saved inventories, data cleared: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Harmony patch to intercept player death and preserve inventory when protected.
    /// </summary>
    [HarmonyPatch(typeof(EntityPlayer))]
    [HarmonyPatch("Die")]
    public static class EntityPlayerDiePatch
    {
        /// <summary>
        /// Prefix runs before the original Die method.
        /// If player has soul protection, save and clear inventory before death.
        /// </summary>
        static void Prefix(EntityPlayer __instance, EnumDespawnReason reason)
        {
            // Only process on actual death
            if (reason != EnumDespawnReason.Death) return;

            // Only process on server side
            if (__instance.World.Side != EnumAppSide.Server) return;

            // Check if player has soul protection
            int protection = __instance.WatchedAttributes.GetInt(SoulGearModSystem.SOUL_PROTECTION_KEY, 0);

            if (protection > 0)
            {
                // Get the server player
                var serverPlayer = __instance.Player as IServerPlayer;
                if (serverPlayer == null) return;

                // Save the inventory before death
                SoulGearModSystem.SavePlayerInventory(serverPlayer);

                // Clear the inventory so nothing drops
                SoulGearModSystem.ClearPlayerInventory(serverPlayer);

                // Remove one charge of protection
                int newProtection = protection - 1;
                if (newProtection <= 0)
                {
                    __instance.WatchedAttributes.RemoveAttribute(SoulGearModSystem.SOUL_PROTECTION_KEY);
                }
                else
                {
                    __instance.WatchedAttributes.SetInt(SoulGearModSystem.SOUL_PROTECTION_KEY, newProtection);
                }
                __instance.WatchedAttributes.MarkPathDirty(SoulGearModSystem.SOUL_PROTECTION_KEY);

                // Notify the player
                serverPlayer.SendMessage(GlobalConstants.GeneralChatGroup,
                    Lang.Get("soulgear:message-soul-protection-used"),
                    EnumChatType.Notification);

                SoulGearModSystem.ServerApi?.Logger.Notification($"[SoulGear] Soul protection activated for {serverPlayer.PlayerName}");
            }
        }
    }

    /// <summary>
    /// Helper class to store saved inventory data.
    /// </summary>
    public class SavedInventory
    {
        public string InventoryClassName;
        public string InventoryId;
        public List<SavedSlot> Slots;
    }

    /// <summary>
    /// Helper class to store a single saved slot.
    /// </summary>
    public class SavedSlot
    {
        public int SlotIndex;
        public ItemStack ItemStack;
    }

    /// <summary>
    /// Wrapper class that contains a player's saved inventories plus metadata.
    /// </summary>
    public class SavedPlayerData
    {
        public List<SavedInventory> Inventories;
        public long SavedAtTime;
    }

    /// <summary>
    /// Soul Gear item class. When used, grants the player one charge of keep inventory protection.
    /// Visual effects similar to temporal gear but does not set spawn point.
    /// </summary>
    public class ItemSoulGear : Item
    {
        private SimpleParticleProperties particles;

        // Store sounds per-entity to avoid conflicts when multiple players use the item
        private static ConcurrentDictionary<long, ILoadedSound> activeSounds = new ConcurrentDictionary<long, ILoadedSound>();

        public override void OnLoaded(ICoreAPI api)
        {
            base.OnLoaded(api);

            // Initialize particle properties - purple/magenta color for soul theme
            particles = new SimpleParticleProperties(
                1, 1,                           // quantity min/max
                ColorUtil.ColorFromRgba(180, 80, 220, 220),  // purple/magenta color
                new Vec3d(),                    // position
                new Vec3d(),                    // add position
                new Vec3f(-0.1f, 0.1f, -0.1f),  // min velocity
                new Vec3f(0.1f, 0.3f, 0.1f),    // add velocity
                0.5f,                           // lifespan
                0f,                             // gravity
                0.5f, 1f                        // size min/max
            );
            particles.SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.QUADRATIC, -0.5f);
            particles.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -150);
            particles.ParticleModel = EnumParticleModel.Quad;
            particles.AddPos.Set(0.1, 0.1, 0.1);
        }

        /// <summary>
        /// Emit particles when on the ground
        /// </summary>
        public override void OnGroundIdle(EntityItem entityItem)
        {
            base.OnGroundIdle(entityItem);

            if (entityItem.World.Side == EnumAppSide.Server) return;

            // Spawn particles occasionally
            if (entityItem.World.ElapsedMilliseconds % 100 < 10)
            {
                var pos = entityItem.Pos.XYZ;
                SpawnParticles(entityItem.World, pos, 1);
            }
        }

        /// <summary>
        /// Start using the soul gear - play sound effect
        /// </summary>
        public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity,
            BlockSelection blockSel, EntitySelection entitySel, bool firstEvent,
            ref EnumHandHandling handling)
        {
            if (blockSel == null)
            {
                handling = EnumHandHandling.NotHandled;
                return;
            }

            handling = EnumHandHandling.PreventDefault;

            if (byEntity.World.Side == EnumAppSide.Client)
            {
                // Clean up any existing sound for this entity first
                StopAndDisposeSound(byEntity.EntityId);

                // Load and play looping sound
                var sound = (byEntity.World.Api as ICoreClientAPI)?.World.LoadSound(new SoundParams()
                {
                    Location = new AssetLocation("sounds/effect/translocate-active"),
                    ShouldLoop = true,
                    RelativePosition = true,
                    DisposeOnFinish = false,
                    Volume = 0.5f
                });

                if (sound != null)
                {
                    activeSounds[byEntity.EntityId] = sound;
                    sound.Start();
                }
            }
        }

        /// <summary>
        /// Continue using the soul gear - visual effects, shake screen
        /// </summary>
        public override bool OnHeldInteractStep(float secondsUsed, ItemSlot slot,
            EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
        {
            if (blockSel == null) return false;

            // Similar timing to temporal gear - 3.5 seconds to complete
            float progress = secondsUsed / 3.5f;

            if (byEntity.World.Side == EnumAppSide.Client)
            {
                // Intensify effects over time
                int particleCount = (int)(progress * 10);
                var pos = blockSel.Position.ToVec3d().Add(blockSel.HitPosition);
                SpawnParticles(byEntity.World, pos, Math.Max(1, particleCount));

                // Screen shake effect - intensity increases with progress
                if (byEntity is EntityPlayer player)
                {
                    float shakeIntensity = progress * 0.3f;
                    (api as ICoreClientAPI)?.World.SetCameraShake(shakeIntensity);
                }

                // Adjust sound pitch
                if (activeSounds.TryGetValue(byEntity.EntityId, out var sound) && sound.IsPlaying)
                {
                    sound.SetPitch(0.5f + progress);
                }
            }

            // Return true to continue, false when done
            return secondsUsed < 3.5f;
        }

        /// <summary>
        /// Finish using the soul gear - grant protection
        /// </summary>
        public override void OnHeldInteractStop(float secondsUsed, ItemSlot slot,
            EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel)
        {
            // Clean up sound for this entity
            StopAndDisposeSound(byEntity.EntityId);

            // Only complete if used long enough
            if (secondsUsed < 3.4f) return;

            // Server-side: Apply the soul protection
            if (byEntity.World.Side == EnumAppSide.Server && byEntity is EntityPlayer playerEntity)
            {
                // Add one charge of keep inventory protection
                int currentProtection = playerEntity.WatchedAttributes.GetInt(
                    SoulGearModSystem.SOUL_PROTECTION_KEY, 0);
                playerEntity.WatchedAttributes.SetInt(
                    SoulGearModSystem.SOUL_PROTECTION_KEY, currentProtection + 1);
                playerEntity.WatchedAttributes.MarkPathDirty(SoulGearModSystem.SOUL_PROTECTION_KEY);

                // Consume the item
                slot.TakeOut(1);
                slot.MarkDirty();

                // Notify the player
                var serverPlayer = (playerEntity.Player as IServerPlayer);
                serverPlayer?.SendMessage(GlobalConstants.GeneralChatGroup,
                    Lang.Get("soulgear:message-soul-protection-gained"),
                    EnumChatType.Notification);
            }

            // Client-side: Visual and audio feedback
            if (byEntity.World.Side == EnumAppSide.Client)
            {
                // Play completion sound
                byEntity.World.PlaySoundAt(new AssetLocation("sounds/effect/translocate-breakdimension"),
                    byEntity.Pos.X, byEntity.Pos.Y, byEntity.Pos.Z);

                // Burst of particles
                if (blockSel != null)
                {
                    var pos = blockSel.Position.ToVec3d().Add(blockSel.HitPosition);
                    SpawnParticles(byEntity.World, pos, 50);
                }

                // Reset camera shake
                (api as ICoreClientAPI)?.World.SetCameraShake(0);
            }
        }

        /// <summary>
        /// Handle interruption of using the soul gear
        /// </summary>
        public override bool OnHeldInteractCancel(float secondsUsed, ItemSlot slot,
            EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel,
            EnumItemUseCancelReason cancelReason)
        {
            StopAndDisposeSound(byEntity.EntityId);

            if (byEntity.World.Side == EnumAppSide.Client)
            {
                (api as ICoreClientAPI)?.World.SetCameraShake(0);
            }

            return true;
        }

        /// <summary>
        /// Helper method to stop and dispose a sound for a specific entity.
        /// Thread-safe and handles missing entries gracefully.
        /// </summary>
        private static void StopAndDisposeSound(long entityId)
        {
            if (activeSounds.TryRemove(entityId, out var sound))
            {
                try
                {
                    sound.Stop();
                    sound.Dispose();
                }
                catch
                {
                    // Ignore disposal errors - sound may already be disposed
                }
            }
        }

        /// <summary>
        /// Spawn purple/magenta soul particles at the given position
        /// </summary>
        private void SpawnParticles(IWorldAccessor world, Vec3d pos, int count)
        {
            if (world.Side != EnumAppSide.Client) return;

            for (int i = 0; i < count; i++)
            {
                // Randomize purple/magenta colors for soul theme
                particles.Color = ColorUtil.ColorFromRgba(
                    150 + world.Rand.Next(80),    // R: 150-230 (pink-purple)
                    40 + world.Rand.Next(60),     // G: 40-100  (low green)
                    180 + world.Rand.Next(75),    // B: 180-255 (blue-purple)
                    200 + world.Rand.Next(55)     // A: 200-255 (mostly opaque)
                );

                particles.MinPos = pos.Clone();
                particles.MinPos.Add(-0.2 + world.Rand.NextDouble() * 0.4,
                                     -0.2 + world.Rand.NextDouble() * 0.4,
                                     -0.2 + world.Rand.NextDouble() * 0.4);

                world.SpawnParticles(particles);
            }
        }

        /// <summary>
        /// Get item info for handbook/tooltip
        /// </summary>
        public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc,
            IWorldAccessor world, bool withDebugInfo)
        {
            base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);
            dsc.AppendLine(Lang.Get("soulgear:itemdesc-soulgear"));
        }
    }
}
