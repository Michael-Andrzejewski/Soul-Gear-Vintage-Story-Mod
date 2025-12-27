using System;
using System.Collections.Generic;
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
    /// </summary>
    public class SoulGearModSystem : ModSystem
    {
        public static ICoreServerAPI ServerApi { get; private set; }
        private Harmony harmony;

        // Key used to store the keep inventory protection in player's WatchedAttributes
        public const string SOUL_PROTECTION_KEY = "soulGearProtection";

        // Storage for saved inventories - keyed by player UID
        public static Dictionary<string, List<SavedInventory>> SavedInventories = new Dictionary<string, List<SavedInventory>>();

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

            // Register a periodic check for players who were revived without respawning
            // (e.g., healed by poultice/bandage while downed)
            api.Event.RegisterGameTickListener(OnGameTick, 1000); // Check every second

            api.Logger.Notification("[SoulGear] Mod loaded with Harmony patches applied");
        }

        /// <summary>
        /// Called every second to check for players who were revived without respawning
        /// (e.g., healed by a teammate's poultice/bandage while downed).
        /// </summary>
        private void OnGameTick(float dt)
        {
            if (SavedInventories.Count == 0) return;

            // Create a copy of keys to avoid modifying collection during iteration
            var playerUids = new List<string>(SavedInventories.Keys);

            foreach (var playerUid in playerUids)
            {
                var player = ServerApi.World.PlayerByUid(playerUid) as IServerPlayer;
                if (player?.Entity == null) continue;

                // Check if the player is alive (was revived without respawning)
                if (player.Entity.Alive)
                {
                    ServerApi.Logger.Debug($"[SoulGear] Detected revived player {player.PlayerName} with pending inventory - restoring");
                    RestorePlayerInventory(player);
                }
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
                ServerApi.Event.RegisterCallback((dt) =>
                {
                    RestorePlayerInventory(byPlayer);
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

            SavedInventories[playerUid] = inventoryList;

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
        /// </summary>
        private void RestorePlayerInventory(IServerPlayer player)
        {
            if (player == null) return;

            string playerUid = player.PlayerUID;

            if (!SavedInventories.TryGetValue(playerUid, out var inventoryList))
            {
                return;
            }

            int restoredCount = 0;

            foreach (var savedInv in inventoryList)
            {
                // Find the matching inventory
                IInventory targetInventory = null;

                foreach (var inventory in player.InventoryManager.Inventories.Values)
                {
                    if (inventory.InventoryID == savedInv.InventoryId)
                    {
                        targetInventory = inventory;
                        break;
                    }
                }

                if (targetInventory == null)
                {
                    // Try to find by class name if ID doesn't match
                    foreach (var inventory in player.InventoryManager.Inventories.Values)
                    {
                        if (inventory.ClassName == savedInv.InventoryClassName)
                        {
                            targetInventory = inventory;
                            break;
                        }
                    }
                }

                if (targetInventory != null)
                {
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
                        }
                    }
                }
            }

            // Remove the saved inventory data
            SavedInventories.Remove(playerUid);

            ServerApi.Logger.Debug($"[SoulGear] Restored {restoredCount} item stacks for player {player.PlayerName}");

            // Notify the player
            player.SendMessage(GlobalConstants.GeneralChatGroup,
                Lang.Get("soulgear:message-inventory-restored"),
                EnumChatType.Notification);
        }

        public override void Dispose()
        {
            if (ServerApi != null)
            {
                ServerApi.Event.PlayerRespawn -= OnPlayerRespawn;
            }

            // Unpatch Harmony
            harmony?.UnpatchAll(Mod.Info.ModID);

            SavedInventories.Clear();
            base.Dispose();
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
    /// Soul Gear item class. When used, grants the player one charge of keep inventory protection.
    /// Visual effects similar to temporal gear but does not set spawn point.
    /// </summary>
    public class ItemSoulGear : Item
    {
        private SimpleParticleProperties particles;
        private ILoadedSound sound;

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
                // Load and play looping sound
                sound = (byEntity.World.Api as ICoreClientAPI)?.World.LoadSound(new SoundParams()
                {
                    Location = new AssetLocation("sounds/effect/translocate-active"),
                    ShouldLoop = true,
                    RelativePosition = true,
                    DisposeOnFinish = false,
                    Volume = 0.5f
                });
                sound?.Start();
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
                if (sound != null && sound.IsPlaying)
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
            // Clean up sound
            if (sound != null)
            {
                sound.Stop();
                sound.Dispose();
                sound = null;
            }

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
            if (sound != null)
            {
                sound.Stop();
                sound.Dispose();
                sound = null;
            }

            if (byEntity.World.Side == EnumAppSide.Client)
            {
                (api as ICoreClientAPI)?.World.SetCameraShake(0);
            }

            return true;
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
