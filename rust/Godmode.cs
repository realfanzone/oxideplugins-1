using System;
using System.Collections.Generic;
using System.Reflection;
using Rust;
using UnityEngine;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("Godmode", "Wulf/lukespragg", "3.2.1", ResourceId = 673)]
    [Description("Allows players with permission to become invincible/invulnerable")]

    class Godmode : RustPlugin
    {
        // Do NOT edit this file, instead edit Godmode.json in oxide/config and Godmode.en.json in the oxide/lang directory,
        // or create a new language file for another language using the 'en' file as a default

        #region Initialization

        const string permAllowed = "godmode.allowed";

        void Init()
        {
            #if !RUST
            throw new NotSupportedException("This plugin does not support this game");
            #endif

            LoadDefaultConfig();
            LoadDefaultMessages();
            LoadSavedData();
            Unsubscribe(nameof(OnRunPlayerMetabolism));
            permission.RegisterPermission(permAllowed, this);
        }

        void LoadSavedData()
        {
            storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Title);
            foreach (var god in storedData.Gods) gods[god.GetUserId()] = god;
        }

        void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Title, storedData);

        #endregion

        #region Configuration

        bool canBeHurt;
        bool canBeLooted;
        bool canEarnXp;
        bool canHurtPlayers;
        bool canLootPlayers;
        bool infiniteRun;
        bool informOnAttack;
        bool prefixEnabled;
        string prefixFormat;

        protected override void LoadDefaultConfig()
        {
            Config["CanBeHurt"] = canBeHurt = GetConfig("CanBeHurt", false);
            Config["CanBeLooted"] = canBeLooted = GetConfig("CanBeLooted", false);
            Config["CanEarnXp"] = canEarnXp = GetConfig("CanEarnXp", true);
            Config["CanHurtPlayers"] = canHurtPlayers = GetConfig("CanHurtPlayers", true);
            Config["CanLootPlayers"] = canLootPlayers = GetConfig("CanLootPlayers", true);
            Config["InfiniteRun"] = infiniteRun = GetConfig("InfiniteRun", true);
            Config["InformOnAttack"] = informOnAttack = GetConfig("InformOnAttack", true);
            Config["PrefixEnabled"] = prefixEnabled = GetConfig("PrefixEnabled", true);
            Config["PrefixFormat"] = prefixFormat = GetConfig("PrefixFormat", "[God]");
            SaveConfig();
        }

        #endregion

        #region Localization

        void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>            {
                ["Disabled"] = "You have disabled godmode",
                ["DisabledBy"] = "Your godmode has been disabled by {0}",
                ["DisabledFor"] = "You have disabled godmode for {0}",
                ["Enabled"] = "You have enabled godmode",
                ["EnabledBy"] = "Your godmode has been enabled by {0}",
                ["EnabledFor"] = "You have enabled godmode for {0}",
                ["Godlist"] = "Players with godmode enabled:",
                ["GodlistNone"] = "No players have godmode enabled",
                ["InformAttacker"] = "{0} is in godmode and can't take any damage",
                ["InformVictim"] = "{0} just tried to deal damage to you",
                ["NoLooting"] = "You are not allowed to loot a player with godmode",
                ["NotAllowed"] = "Sorry, you can't use '{0}' right now",
                ["PlayerNotFound"] = "No players were found with that name"
            }, this);
        }

        #endregion

        class StoredData
        {
            public readonly HashSet<PlayerInfo> Gods = new HashSet<PlayerInfo>();
        }

        class PlayerInfo
        {
            public string UserId;
            public string Name;

            public PlayerInfo()
            {
            }

            public PlayerInfo(BasePlayer player)
            {
                UserId = player.userID.ToString();
                Name = player.displayName;
            }

            public ulong GetUserId()
            {
                ulong userId;
                return !ulong.TryParse(UserId, out userId) ? 0 : userId;
            }
        }

        StoredData storedData;
        readonly Hash<ulong, PlayerInfo> gods = new Hash<ulong, PlayerInfo>();
        readonly Dictionary<BasePlayer, long> playerInformHistory = new Dictionary<BasePlayer, long>();
        readonly DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0);
        readonly FieldInfo displayName = typeof(BasePlayer).GetField("_displayName", (BindingFlags.NonPublic | BindingFlags.Instance));

        bool IsGod(string id) => gods[Convert.ToUInt64(id)] != null;

        void Unload() => SaveData();
        void OnServerSave() => SaveData();

        void OnPlayerInit(BasePlayer player)
        {
            if (player.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot))
            {
                timer.Once(2, () => OnPlayerInit(player));
                return;
            }

            if (gods[player.userID] == null) return;
            ModifyMetabolism(player, true);

            if (prefixEnabled && !player.displayName.Contains(prefixFormat))
                displayName.SetValue(player, $"{prefixFormat} {player.displayName}");
            else
                displayName.SetValue(player, player.displayName.Replace(prefixFormat, "").Trim());
        }

        [ChatCommand("god")]
        void God(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player.UserIDString, permAllowed))
            {
                Reply(player, Lang("NotAllowed", player.UserIDString, command));
                return;
            }

            if (args.Length == 0)
            {
                if (gods[player.userID] != null)
                {
                    DisableGodmode(player);
                    Unsubscribe(nameof(OnRunPlayerMetabolism));
                    Reply(player, Lang("Disabled", player.UserIDString));
                }
                else
                {
                    EnableGodmode(player);
                    Subscribe(nameof(OnRunPlayerMetabolism));
                    Reply(player, Lang("Enabled", player.UserIDString));
                }

                return;
            }

            var target = rust.FindPlayer(args[0]);
            if (!target)
                Reply(player, Lang("PlayerNotFound", player.UserIDString));
            else
                ToggleGodmode(player, target);
        }

        [ChatCommand("gods")]
        void Godlist(BasePlayer player, string command, string[] args)
        {
            if (!HasPermission(player.UserIDString, permAllowed))
            {
                Reply(player, Lang("NotAllowed", player.UserIDString, command));
                return;
            }

            Reply(player, Lang("GodList", player.UserIDString));
            if (gods.Count == 0)
                Reply(player, Lang("GodListNone", player.UserIDString));
            else
                foreach (var god in gods) Reply(player, $"{god.Value.Name} [{god.Value.UserId}]");
        }

        object CanBeWounded(BasePlayer player) => !canBeHurt && gods[player.userID] != null ? (object)false : null;

        object OnXpEarn(ulong id) => !canEarnXp && gods[id] != null ? (object)0f : null;

        void OnEntityTakeDamage(BaseEntity entity, HitInfo info)
        {
            var player = entity as BasePlayer;
            var attacker = info.Initiator as BasePlayer;

            if (!player) return;
            if (!canBeHurt && gods[player.userID] != null)
            {
                NullifyDamage(ref info);
                if (informOnAttack) InformPlayers(player, attacker);
            }

            if (!attacker) return;
            if (!canHurtPlayers && gods[attacker.userID] != null) NullifyDamage(ref info);
        }

        object CanLootPlayer(BasePlayer target, BasePlayer looter)
        {
            if ((!canBeLooted && gods[target.userID] != null) || (!canLootPlayers && gods[looter.userID] != null))
            {
                NextTick(() =>
                {
                    looter.EndLooting();
                    Reply(looter, Lang("NoLooting", looter.UserIDString));
                });
                return false;
            }

            return null;
        }

        void OnLootPlayer(BasePlayer looter, BasePlayer target)
        {
            if (!target) return;
            if (gods[target.userID] != null)
            {
                NextTick(() =>
                {
                    looter.EndLooting();
                    Reply(looter, Lang("NoLooting", looter.UserIDString));
                });
            }
        }

        void DisableGodmode(BasePlayer player)
        {
            storedData.Gods.RemoveWhere(info => info.GetUserId() == player.userID);
            gods.Remove(player.userID);

            ModifyMetabolism(player, false);
            displayName.SetValue(player, player.displayName.Replace(prefixFormat, "").Trim());
        }

        void EnableGodmode(BasePlayer player)
        {
            var info = new PlayerInfo(player);
            storedData.Gods.Add(info);
            gods[player.userID] = info;
            ModifyMetabolism(player, true);

            if (prefixEnabled && !player.displayName.Contains(prefixFormat))
                displayName.SetValue(player, $"{prefixFormat} {player.displayName}");
            else
                displayName.SetValue(player, player.displayName.Replace(prefixFormat, "").Trim());
        }

        static void ModifyMetabolism(BasePlayer player, bool isGod)
        {
            if (isGod)
            {
                player.health = 100;
                player.metabolism.bleeding.max = 0;
                player.metabolism.bleeding.value = 0;
                player.metabolism.calories.min = 500;
                player.metabolism.calories.value = 500;
                player.metabolism.dirtyness.max = 0;
                player.metabolism.dirtyness.value = 0;
                player.metabolism.heartrate.min = 0.5f;
                player.metabolism.heartrate.max = 0.5f;
                player.metabolism.heartrate.value = 0.5f;
                player.metabolism.hydration.min = 250;
                player.metabolism.hydration.value = 250;
                player.metabolism.oxygen.min = 1;
                player.metabolism.oxygen.value = 1;
                player.metabolism.poison.max = 0;
                player.metabolism.poison.value = 0;
                player.metabolism.radiation_level.max = 0;
                player.metabolism.radiation_level.value = 0;
                player.metabolism.radiation_poison.max = 0;
                player.metabolism.radiation_poison.value = 0;
                player.metabolism.temperature.min = 32;
                player.metabolism.temperature.max = 32;
                player.metabolism.temperature.value = 32;
                player.metabolism.wetness.max = 0;
                player.metabolism.wetness.value = 0;
            }
            else
            {
                player.metabolism.bleeding.min = 0;
                player.metabolism.bleeding.max = 1;
                player.metabolism.calories.min = 0;
                player.metabolism.calories.max = 500;
                player.metabolism.comfort.min = 0;
                player.metabolism.comfort.max = 1;
                player.metabolism.dirtyness.min = 0;
                player.metabolism.dirtyness.max = 100;
                player.metabolism.heartrate.min = 0;
                player.metabolism.heartrate.max = 1;
                player.metabolism.hydration.min = 0;
                player.metabolism.hydration.max = 250;
                player.metabolism.oxygen.min = 0;
                player.metabolism.oxygen.max = 1;
                player.metabolism.poison.min = 0;
                player.metabolism.poison.max = 100;
                player.metabolism.radiation_level.min = 0;
                player.metabolism.radiation_level.max = 100;
                player.metabolism.radiation_poison.min = 0;
                player.metabolism.radiation_poison.max = 500;
                player.metabolism.temperature.min = -100;
                player.metabolism.temperature.max = 100;
                player.metabolism.wetness.min = 0;
                player.metabolism.wetness.max = 1;
            }
            player.metabolism.SendChangesToClient();
        }

        object OnRunPlayerMetabolism(PlayerMetabolism metabolism, BaseCombatEntity entity)
        {
            var player = entity.ToPlayer();
            if (gods[player.userID] == null) return null;
            if (infiniteRun) player.SetPlayerFlag(BasePlayer.PlayerFlags.NoSprint, false);
            return true;
        }

        void InformPlayers(BasePlayer victim, BasePlayer attacker)
        {
            if (!victim || !attacker) return;
            if (victim == attacker) return;

            if (!playerInformHistory.ContainsKey(attacker)) playerInformHistory.Add(attacker, 0);
            if (!playerInformHistory.ContainsKey(victim)) playerInformHistory.Add(victim, 0);

            if (GetTimestamp() - playerInformHistory[attacker] > 15)
            {
                Reply(victim, Lang("InformVictim", victim.UserIDString, attacker.displayName));
                playerInformHistory[victim] = GetTimestamp();
            }

            if (GetTimestamp() - playerInformHistory[victim] > 15)
            {
                Reply(attacker, Lang("InformAttacker", attacker.UserIDString, victim.displayName));
                playerInformHistory[victim] = GetTimestamp();
            }
        }

        static void NullifyDamage(ref HitInfo info)
        {
            info.damageTypes = new DamageTypeList();
            info.HitMaterial = 0;
            info.PointStart = Vector3.zero;
        }

        void ToggleGodmode(BasePlayer player, BasePlayer target)
        {
            if (gods[target.userID] != null)
            {
                DisableGodmode(target);
                Reply(player, Lang("DisabledFor", player.UserIDString, target.displayName));
                Reply(target, Lang("DisabledBy", target.UserIDString, player.displayName));
            }
            else
            {
                EnableGodmode(target);
                Reply(player, Lang("EnabledFor", player.UserIDString, target.displayName));
                Reply(target, Lang("EnabledBy", target.UserIDString, player.displayName));
            }
        }

        #region Helpers

        T GetConfig<T>(string name, T value) => Config[name] == null ? value : (T)Convert.ChangeType(Config[name], typeof(T));

        string Lang(string key, string id = null, params object[] args) => string.Format(lang.GetMessage(key, this, id), args);

        void Reply(BasePlayer player, string message, string args = null) => PrintToChat(player, $"{message}", args);

        long GetTimestamp() => Convert.ToInt64((DateTime.UtcNow.Subtract(epoch)).TotalSeconds);

        bool HasPermission(string id, string perm) => permission.UserHasPermission(id, perm);

        bool IsAdmin(string userId) => permission.UserHasGroup(userId, "admin");

        #endregion
    }
}
