using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Globalization;
using System.Reflection;
using System.IO;
using UnityEngine;
using UnityEngine.AI;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Core.Libraries;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust;
using Oxide.Game.Rust.Cui;
using Rust;
using Rust.Ai;
using Rust.Workshop;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProtoBuf;
using System.Threading;
using System.Threading.Tasks;

namespace Oxide.Plugins
{
    [Info("SFA", "Kovac", "1.0.0")]
    public class SFA : RustPlugin
    {
        [PluginReference] private Plugin ZoneManager;
        [PluginReference] private Plugin Kits;

        private const string JoinCommand = "sfa123";
        private const string LeaveCommand = "hub123";
        private const float EventDuration = 150f;
        private bool restrictJoin = false;


        private List<ulong> InEvent = new();
        private Dictionary<ulong, PlayerStats> Stats = new();
        private Dictionary<ulong, SavedLoadout> SavedLoadouts = new();
        private bool headshotOnly = false;
        private bool antiCrouch = false;
        private bool accuracyMode = false;
        private Timer eventTimer;
        private float eventStartTime;
        private const string DefaultKitName = "Default Kit";
        private int currentSpawn = 0;

        private const string DiscordWebhookUrl = "https://discord.com/api/webhooks/1382072424652210329/NH6YAb-g9-GCf4bXgMfL4f_DczfPdEVlD__VllPjIrvAXpYXI9S26ViSrVZAUViRHZZI"; // Replace with your webhook

        // UI colors and blur material
        private const string HeaderColor = "1 1 1 0.2"; // white and translucent
        private const string StatsColor = "0.15 0.15 0.15 0.6";
        private const string LeaderboardColor = "0.15 0.15 0.15 0.6";
        private const string TimerColor = "0.15 0.15 0.15 0.6";
        private const string BlurMaterial = "assets/content/ui/uibackgroundblur-ingame.mat";

        private List<string> SpawnPositions = new()
        {
            "568.45 0.12 2372.19",
            "554.23 0.12 2291.54",
            "524.13 0.12 2294.54",
            "505.87 0.12 2305.46",
            "494.24 0.12 2328.99",
            "493.28 0.12 2350.54",
            "506.93 0.12 2372.76",
            "525.61 0.12 2382.55",
            "550.37 0.12 2382.58",
            "569.14 0.12 2372.98",
            "582.51 0.12 2350.06",
            "582.60 0.12 2328.93",
            "569.34 0.12 2306.41",
            "550.44 0.12 2296.13",
            "526.01 0.12 2296.32",
            "506.52 0.12 2305.16"
        };


        private Vector3 StringToVector3(string pos)
        {
            var split = pos.Split(' ');
            if (split.Length != 3) return Vector3.zero;

            float.TryParse(split[0], out float x);
            float.TryParse(split[1], out float y);
            float.TryParse(split[2], out float z);
            return new Vector3(x, y, z);
        }

        private class PlayerStats
        {
            public int Kills = 0;
            public int Deaths = 0;
            public int ShotsFired = 0;
            public int ShotsHit = 0;
        }

        private class SavedItem
        {
            public string ShortName;
            public int Amount;
            public ulong SkinID;
        }

        private class SavedLoadout
        {
            public List<SavedItem> Belt = new();
            public List<SavedItem> Wear = new();
            public List<SavedItem> Main = new();
        }

        private class MVPLogEntry
        {
            public string Name;
            public ulong SteamID;
            public int Kills;
            public int Deaths;
            public float KDR;
            public string Timestamp;
        }

        private List<MVPLogEntry> mvpLog = new();
        private const string MVPDataFile = "SFA_MVPLogs";
        private const string LoadoutDataFile = "SFA_Loadouts";
        private void Init()
        {
            AddCovalenceCommand(JoinCommand, nameof(JoinSFA));
            AddCovalenceCommand(LeaveCommand, nameof(LeaveSFA));
            AddCovalenceCommand("sfamvps", nameof(CmdShowMVPs));
            AddCovalenceCommand("sfasaveloadout", nameof(CmdSaveLoadout));
            AddCovalenceCommand("sfaresetloadout", nameof(CmdResetLoadout));
            AddCovalenceCommand("sfaheadshot", nameof(CmdToggleHeadshot));
            AddCovalenceCommand("sfaanticrouch", nameof(CmdToggleAntiCrouch));
            AddCovalenceCommand("sfastatsmode", nameof(CmdToggleStatsMode));
            LoadMVPLog();
            LoadLoadouts();
        }

        private void JoinSFA(IPlayer iplayer, string command, string[] args)
        {
            var player = iplayer.Object as BasePlayer;
            if (player == null) return;

            if (restrictJoin)
            {
                player.ChatMessage("<color=#ffc800>[SFA]</color> Event join is currently disabled.");
                return;
            }

            if (InEvent.Contains(player.userID))
            {
                player.ChatMessage("<color=#ffc800>[SFA]</color> You're already in the event.");
                return;
            }

            InEvent.Add(player.userID);
            Stats[player.userID] = new PlayerStats();

            // Give the default kit before teleporting
            GiveDefaultKit(player);

            TeleportToArena(player);
            ShowUI(player);
            UpdateStatsUI(player);
            UpdateLeaderboardUIForAll();

            if (eventTimer == null)
                StartTimer();
            else
                UpdateTimerUI();
        }

        private void LeaveSFA(IPlayer iplayer, string command, string[] args)
        {
            var player = iplayer.Object as BasePlayer;
            if (player == null) return;

            if (!InEvent.Contains(player.userID)) return;

            CleanupPlayer(player);
            player.inventory.Strip();
            player.Teleport(new Vector3(-2834.75f, 1.57f, -52.99f));
        }


        private void TeleportToArena(BasePlayer player)
        {
            if (SpawnPositions.Count == 0) return;

            if (currentSpawn >= SpawnPositions.Count)
                currentSpawn = 0;

            Vector3 targetPos = StringToVector3(SpawnPositions[currentSpawn]);
            currentSpawn++;

            timer.Once(0.1f, () =>
            {
                if (player != null && player.IsConnected)
                {
                    targetPos.y = TerrainMeta.HeightMap.GetHeight(targetPos) + 0.5f;
                    player.Teleport(targetPos);
                }
            });
        }

        private void StartTimer()
        {
            eventStartTime = UnityEngine.Time.realtimeSinceStartup;
            UpdateTimerUI();

            eventTimer = timer.Every(1f, UpdateTimerUI);
            timer.Once(EventDuration, () =>
            {
                eventTimer?.Destroy();
                eventTimer = null;
                EndEvent();
            });
        }

        private void EndEvent()
        {
            if (InEvent.Count == 0)
            {
                eventTimer?.Destroy();
                eventTimer = null;
                return;
            }

            // Winner announcement
            var winner = Stats
                .Where(x => InEvent.Contains(x.Key))
                .OrderByDescending(x => x.Value.Kills)
                .FirstOrDefault();

            if (!winner.Equals(default(KeyValuePair<ulong, PlayerStats>)))
            {
                var winnerName = covalence.Players.FindPlayerById(winner.Key.ToString())?.Name ?? "Unknown";
                Server.Broadcast($"<color=#ffc800>[SFA]</color> Winner: {winnerName} with {winner.Value.Kills} kills!");
            }

            // MVP logic
            var mvp = Stats
                .Where(x => InEvent.Contains(x.Key) && x.Value.Kills >= 30)
                .OrderByDescending(x => x.Value.Kills)
                .FirstOrDefault();

            if (!mvp.Equals(default(KeyValuePair<ulong, PlayerStats>)))
            {
                var mvpName = covalence.Players.FindPlayerById(mvp.Key.ToString())?.Name ?? "Unknown";
                var s = mvp.Value;

                Server.Broadcast($"<color=#ffd700>[SFA MVP]</color> {mvpName} - {s.Kills} Kills / {s.Deaths} Deaths | K/D: {(s.Deaths > 0 ? (s.Kills / (float)s.Deaths).ToString("0.00") : s.Kills.ToString())}");

                var logEntry = new MVPLogEntry
                {
                    Name = mvpName,
                    SteamID = mvp.Key,
                    Kills = s.Kills,
                    Deaths = s.Deaths,
                    KDR = s.Deaths > 0 ? (float)s.Kills / s.Deaths : s.Kills,
                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
                };

                mvpLog.Add(logEntry);
                SaveMVPLog();
                SendMVPToDiscord(logEntry);
            }

            // Respawn all players in arena with reset stats and kit
            int spawnIndex = 0;
            foreach (var id in InEvent.ToList())
            {
                var player = BasePlayer.FindByID(id);
                if (player == null) continue;

                DestroyAllUI(player);

                if (spawnIndex >= SpawnPositions.Count)
                    spawnIndex = 0;

                var split = SpawnPositions[spawnIndex].Split(' ');
                float x = float.Parse(split[0]);
                float y = float.Parse(split[1]);
                float z = float.Parse(split[2]);
                spawnIndex++;

                player.Teleport(new Vector3(x, y, z));

                player.health = player.MaxHealth();
                player.metabolism.bleeding.value = 0f;
                player.metabolism.radiation_level.value = 0f;
                player.metabolism.poison.value = 0f;
                player.StopWounded();
                player.SendNetworkUpdate();

                if (Kits != null)
                {
                    GiveDefaultKit(player);
                }

                ShowUI(player);
                UpdateStatsUI(player);
            }

            // Reset round data and reinitialise stats for active players
            Stats.Clear();
            foreach (var id in InEvent)
            {
                Stats[id] = new PlayerStats();
                var p = BasePlayer.FindByID(id);
                if (p != null)
                    UpdateStatsUI(p);
            }

            UpdateLeaderboardUIForAll();

            // Restart event if players still inside
            if (InEvent.Count > 0)
            {
                eventTimer = null;
                StartTimer();
            }
            else
            {
                eventTimer?.Destroy();
                eventTimer = null;
            }
        }

        private void ShowUI(BasePlayer player)
        {
            DestroyAllUI(player);
            var elements = new CuiElementContainer();

            elements.Add(new CuiElement
            {
                Name = "Stats",
                Parent = "Hud",
                Components =
        {
            new CuiImageComponent { Color = StatsColor, Material = BlurMaterial },
            new CuiRectTransformComponent { AnchorMin = "0.891 0.633", AnchorMax = "0.992 0.675" }
        }
            });

            elements.Add(new CuiElement
            {
                Name = "Header",
                Parent = "Hud",
                Components =
        {
            new CuiImageComponent { Color = HeaderColor, Material = BlurMaterial },
            new CuiRectTransformComponent { AnchorMin = "0.891 0.94", AnchorMax = "0.992 0.989" }
        }
            });

            elements.Add(new CuiElement
            {
                Name = "HeaderText",
                Parent = "Header",
                Components =
        {
            new CuiTextComponent
            {
                Text = $"Shopfronts - {InEvent.Count} Players",
                FontSize = 14,
                Align = TextAnchor.MiddleCenter,
                Color = "1 1 1 0.95"
            },
            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
        }
            });

            elements.Add(new CuiElement
            {
                Name = "Leaderboard",
                Parent = "Hud",
                Components =
        {
            new CuiImageComponent { Color = LeaderboardColor, Material = BlurMaterial },
            new CuiRectTransformComponent { AnchorMin = "0.891 0.675", AnchorMax = "0.992 0.939" }
        }
            });

            elements.Add(new CuiElement
            {
                Name = "Timer",
                Parent = "Hud",
                Components =
        {
            new CuiImageComponent { Color = TimerColor, Material = BlurMaterial },
            new CuiRectTransformComponent { AnchorMin = "0.904 0.59", AnchorMax = "0.982 0.632" }
        }
            });

            elements.Add(new CuiElement
            {
                Name = "LeaveButton",
                Parent = "Hud",
                Components =
        {
            new CuiButtonComponent
            {
                Color = "0.1 0.1 0.1 0.7",
                Command = LeaveCommand,
                Close = ""
            },
            new CuiRectTransformComponent { AnchorMin = "0.697 0.022", AnchorMax = "0.775 0.078" }
        }
            });

            elements.Add(new CuiElement
            {
                Name = "Leave",
                Parent = "LeaveButton",
                Components =
        {
            new CuiTextComponent
            {
                Text = "Leave",
                FontSize = 14,
                Align = TextAnchor.MiddleCenter,
                Color = "1 1 1 0.85"
            },
            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
        }
            });

            // Save loadout button
            elements.Add(new CuiElement
            {
                Name = "SaveLoadoutButton",
                Parent = "Hud",
                Components =
        {
            new CuiButtonComponent
            {
                Color = "0.31 0.31 0.31 0.6",
                Command = "sfasaveloadout",
                Close = ""
            },
            // Position near the bottom left as requested
            new CuiRectTransformComponent { AnchorMin = "0.113 0.008", AnchorMax = "0.191 0.092" }
        }
            });

            elements.Add(new CuiElement
            {
                Name = "SaveLoadoutText",
                Parent = "SaveLoadoutButton",
                Components =
        {
            new CuiTextComponent { Text = "Save Loadout", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
        }
            });

            // Reset loadout button
            elements.Add(new CuiElement
            {
                Name = "ResetLoadoutButton",
                Parent = "Hud",
                Components =
        {
            new CuiButtonComponent
            {
                Color = "0.31 0.31 0.31 0.6",
                Command = "sfaresetloadout",
                Close = ""
            },
            // Align reset button just left of the save button
            new CuiRectTransformComponent { AnchorMin = "0.033 0.008", AnchorMax = "0.111 0.092" }
        }
            });

            elements.Add(new CuiElement
            {
                Name = "ResetLoadoutText",
                Parent = "ResetLoadoutButton",
                Components =
        {
            new CuiTextComponent { Text = "Reset Loadout", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
        }
            });

            // Anti crouch toggle
            elements.Add(new CuiElement
            {
                Name = "AntiCrouchButton",
                Parent = "Hud",
                Components =
        {
            new CuiButtonComponent { Color = "0.31 0.31 0.31 0.6", Command = "sfaanticrouch", Close = "" },
            new CuiRectTransformComponent { AnchorMin = "0.906 0.461", AnchorMax = "1 0.503" }
        }
            });

            elements.Add(new CuiElement
            {
                Name = "AntiCrouchText",
                Parent = "AntiCrouchButton",
                Components =
        {
            new CuiTextComponent { Text = "No Crouch", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
        }
            });

            // Stats mode toggle
            elements.Add(new CuiElement
            {
                Name = "StatsModeButton",
                Parent = "Hud",
                Components =
        {
            new CuiButtonComponent { Color = "0.31 0.31 0.31 0.6", Command = "sfastatsmode", Close = "" },
            new CuiRectTransformComponent { AnchorMin = "0.906 0.419", AnchorMax = "1 0.461" }
        }
            });

            elements.Add(new CuiElement
            {
                Name = "StatsModeText",
                Parent = "StatsModeButton",
                Components =
        {
            new CuiTextComponent { Text = "Accuracy", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
        }
            });

            // Headshot only toggle
            elements.Add(new CuiElement
            {
                Name = "HeadshotButton",
                Parent = "Hud",
                Components =
        {
            new CuiButtonComponent { Color = "0.31 0.31 0.31 0.6", Command = "sfaheadshot", Close = "" },
            new CuiRectTransformComponent { AnchorMin = "0.906 0.503", AnchorMax = "1 0.544" }
        }
            });

            elements.Add(new CuiElement
            {
                Name = "HeadshotText",
                Parent = "HeadshotButton",
                Components =
        {
            new CuiTextComponent { Text = "Headshots", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
            new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" }
        }
            });

            elements.Add(new CuiElement
            {
                Name = "Logo",
                Parent = "Hud",
                Components =
        {
            new CuiRawImageComponent { Url = "https://i.imgur.com/vTG6KSZ.png", Color = "1 1 1 1" },
            new CuiRectTransformComponent { AnchorMin = "0.86 0.939", AnchorMax = "0.89 0.99" }
        }
            });

            CuiHelper.AddUi(player, elements);
        }

        private void DestroyAllUI(BasePlayer player)
        {
            string[] ids = { "Stats", "Header", "Leaderboard", "Timer", "LeaveButton", "StatsText", "LeaderboardText", "TimerText", "Logo", "SaveLoadoutButton", "SaveLoadoutText", "ResetLoadoutButton", "ResetLoadoutText", "AntiCrouchButton", "AntiCrouchText", "StatsModeButton", "StatsModeText", "HeadshotButton", "HeadshotText" };
            foreach (var id in ids)
                CuiHelper.DestroyUi(player, id);
        }

        private void UpdateLeaderboardUI(BasePlayer player)
        {
            if (player == null || !InEvent.Contains(player.userID)) return;

            var mvpId = Stats
                .Where(x => InEvent.Contains(x.Key) && x.Value.Kills >= 30)
                .OrderByDescending(x => x.Value.Kills)
                .FirstOrDefault().Key;

            CuiHelper.DestroyUi(player, "LeaderboardText");

            var ordered = Stats
                .Where(x => InEvent.Contains(x.Key))
                .OrderByDescending(x => x.Value.Kills)
                .Take(5)
                .ToList();

            var lines = new List<string>();
            for (int i = 0; i < ordered.Count; i++)
            {
                var entry = ordered[i];
                var name = covalence.Players.FindPlayerById(entry.Key.ToString())?.Name ?? "Unknown";
                var prefix = entry.Key == mvpId ? "<color=#ffd700>[MVP]</color> " : "";
                string color = i switch
                {
                    0 => "#ffd700", // gold
                    1 => "#c0c0c0", // silver
                    2 => "#cd7f32", // bronze
                    _ => "#ffffff"
                };
                lines.Add($"<color={color}>{i + 1}. {prefix}{name} - {entry.Value.Kills}K</color>");
            }

            var elements = new CuiElementContainer();
            elements.Add(new CuiElement
            {
                Name = "LeaderboardText",
                Parent = "Hud",
                Components =
        {
            new CuiTextComponent
            {
                Text = string.Join("\n", lines),
                FontSize = 12,
                Align = TextAnchor.UpperLeft,
                Color = "1 1 1 1",
            },
            new CuiRectTransformComponent
            {
                AnchorMin = "0.895 0.68",
                AnchorMax = "0.985 0.93"
            }
        }
            });

            CuiHelper.AddUi(player, elements);
        }

        private void UpdateStatsUI(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, "StatsText");
            if (!Stats.TryGetValue(player.userID, out var data)) return;

            var elements = new CuiElementContainer();
            string text;
            if (accuracyMode)
            {
                float acc = data.ShotsFired > 0 ? (float)data.ShotsHit / data.ShotsFired * 100f : 0f;
                text = $"Hits: {data.ShotsHit} / {data.ShotsFired} ({acc:0.}% )";
            }
            else
            {
                float kdr = data.Deaths > 0 ? (float)data.Kills / data.Deaths : data.Kills;
                text = $"Kills: {data.Kills}  Deaths: {data.Deaths}  K/D: {kdr:0.00}";
            }

            elements.Add(new CuiElement
            {
                Name = "StatsText",
                Parent = "Hud",
                Components =
        {
            new CuiTextComponent
            {
                Text = text,
                FontSize = 12,
                Align = TextAnchor.MiddleCenter,
                Color = "1 0.843 0 1"
            },
            new CuiRectTransformComponent
            {
                AnchorMin = "0.891 0.633",
                AnchorMax = "0.992 0.675"
            }
        }
            });

            CuiHelper.AddUi(player, elements);
        }

        private void UpdateTimerUI()
        {
            float elapsed = UnityEngine.Time.realtimeSinceStartup - eventStartTime;
            float remaining = Mathf.Max(0f, EventDuration - elapsed);

            int minutes = (int)(remaining / 60f);
            int seconds = (int)(remaining % 60f);

            string timeString = $"{minutes:D2}:{seconds:D2}";

            foreach (var id in InEvent)
            {
                var player = BasePlayer.FindByID(id);
                if (player == null) continue;

                CuiHelper.DestroyUi(player, "TimerText");

                var elements = new CuiElementContainer();
                elements.Add(new CuiElement
                {
                    Name = "TimerText",
                    Parent = "Timer",
                    Components =
            {
                new CuiTextComponent
                {
                    Text = $"⏱ {timeString}",
                    FontSize = 14,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                },
                new CuiRectTransformComponent
                {
                    AnchorMin = "0 0",
                    AnchorMax = "1 1"
                }
            }
                });

                CuiHelper.AddUi(player, elements);
            }
        }

        private void UpdateLeaderboardUIForAll()
        {
            ulong? mvpId = Stats
                .Where(x => InEvent.Contains(x.Key) && x.Value.Kills >= 30)
                .OrderByDescending(x => x.Value.Kills)
                .FirstOrDefault().Key;

            foreach (var id in InEvent)
            {
                var player = BasePlayer.FindByID(id);
                if (player == null) continue;

                CuiHelper.DestroyUi(player, "LeaderboardText");

                var ordered = Stats
                    .Where(x => InEvent.Contains(x.Key))
                    .OrderByDescending(x => x.Value.Kills)
                    .Take(5)
                    .ToList();

                var lines = new List<string>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var entry = ordered[i];
                    var name = covalence.Players.FindPlayerById(entry.Key.ToString())?.Name ?? "Unknown";
                    var prefix = entry.Key == mvpId ? "<color=#ffd700>[MVP]</color> " : "";
                    string color = i switch
                    {
                        0 => "#ffd700",
                        1 => "#c0c0c0",
                        2 => "#cd7f32",
                        _ => "#ffffff"
                    };
                    lines.Add($"<color={color}>{i + 1}. {prefix}{name} - {entry.Value.Kills}K</color>");
                }

                var elements = new CuiElementContainer();

                elements.Add(new CuiElement
                {
                    Name = "LeaderboardText",
                    Parent = "Hud",
                    Components =
            {
                new CuiTextComponent
                {
                    Text = string.Join("\n", lines),
                    FontSize = 12,
                    Align = TextAnchor.UpperLeft,
                    Color = "1 1 1 1"
                },
                new CuiRectTransformComponent
                {
                    AnchorMin = "0.895 0.68",
                    AnchorMax = "0.985 0.93"
                }
            }
                });

                CuiHelper.AddUi(player, elements);
            }
        }

        private void AddOutlinedText(CuiElementContainer container, string name, string parent, string text, int fontSize, string anchorMin, string anchorMax, TextAnchor align = TextAnchor.MiddleCenter)
        {
            string[] offsets = { "-0.001 0", "0.001 0", "0 -0.001", "0 0.001" };

            foreach (var offset in offsets)
            {
                container.Add(new CuiElement
                {
                    Name = $"{name}_outline_{offset}",
                    Parent = parent,
                    Components =
            {
                new CuiTextComponent
                {
                    Text = text,
                    FontSize = fontSize,
                    Align = align,
                    Color = "0 0 0 1"
                },
                new CuiRectTransformComponent
                {
                    AnchorMin = anchorMin,
                    AnchorMax = anchorMax,
                    OffsetMin = offset,
                    OffsetMax = offset
                }
            }
                });
            }

            // Actual text
            container.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                Components =
                {
                    new CuiTextComponent
                    {
                        Text = text,
                        FontSize = fontSize,
                        Align = align,
                        Color = "1 1 1 1"
                    },
                    new CuiRectTransformComponent
                    {
                        AnchorMin = anchorMin,
                        AnchorMax = anchorMax
                    }
                }
            });
        }

        private void OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (attacker == null || !InEvent.Contains(attacker.userID)) return;

            if (!Stats.TryGetValue(attacker.userID, out var data))
                Stats[attacker.userID] = data = new PlayerStats();

            data.ShotsFired++;
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            var victim = entity?.ToPlayer();
            var attacker = info?.Initiator?.ToPlayer();

            if (victim == null || attacker == null) return null;
            if (!InEvent.Contains(victim.userID) || !InEvent.Contains(attacker.userID)) return null;

            if (!Stats.TryGetValue(attacker.userID, out var aStats))
                Stats[attacker.userID] = aStats = new PlayerStats();

            // if headshot-only mode is enabled, cancel damage unless it was a headshot
            if (headshotOnly && !info.isHeadshot)
                return true;

            aStats.ShotsHit++;

            return null;
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (!antiCrouch || player == null || input == null) return;
            if (!InEvent.Contains(player.userID)) return;

            if (input.IsDown(BUTTON.DUCK) || player.IsDucked())
            {
                input.current.buttons &= ~(uint)BUTTON.DUCK;
                player.serverInput.current.buttons &= ~(uint)BUTTON.DUCK;
                player.modelState.ducked = false;
                player.SetPlayerFlag(BasePlayer.PlayerFlags.Ducked, false);
                player.SendNetworkUpdate();
            }
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            var victim = entity?.ToPlayer();
            var attacker = info?.Initiator?.ToPlayer();

            bool victimInEvent = victim != null && InEvent.Contains(victim.userID);
            bool attackerInEvent = attacker != null && InEvent.Contains(attacker.userID);

            if (!victimInEvent && !attackerInEvent) return; // nothing relevant

            // Player in event died
            if (victimInEvent)
            {
                if (!Stats.TryGetValue(victim.userID, out var victimStats))
                    Stats[victim.userID] = victimStats = new PlayerStats();
                victimStats.Deaths++;
                UpdateStatsUI(victim);

                if (attackerInEvent && attacker != victim)
                {
                    if (!Stats.TryGetValue(attacker.userID, out var aStats))
                        Stats[attacker.userID] = aStats = new PlayerStats();
                    aStats.Kills++;
                    UpdateStatsUI(attacker);
                }

                UpdateLeaderboardUIForAll();

                // Respawn after death with delay to ensure Rust registers death
                timer.Once(2f, () =>
                {
                    if (victim == null || !victim.IsConnected) return;

                    // Force respawn if still dead
                    if (victim.IsDead())
                    {
                        victim.Respawn();
                        timer.Once(0.5f, () => SetupPlayerPostRespawn(victim));
                    }
                    else
                    {
                        SetupPlayerPostRespawn(victim);
                    }
                });
            }
            // Player killed an NPC
            else if (attackerInEvent && attacker != null)
            {
                if (!Stats.TryGetValue(attacker.userID, out var aStats))
                    Stats[attacker.userID] = aStats = new PlayerStats();
                aStats.Kills++;
                UpdateStatsUI(attacker);
                UpdateLeaderboardUIForAll();
            }
        }

        private void SetupPlayerPostRespawn(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;

            player.health = player.MaxHealth();
            GiveDefaultKit(player);

            if (currentSpawn >= SpawnPositions.Count)
                currentSpawn = 0;

            var parts = SpawnPositions[currentSpawn].Split(' ');
            float x = float.Parse(parts[0]);
            float y = float.Parse(parts[1]);
            float z = float.Parse(parts[2]);

            currentSpawn++;

            player.Teleport(new Vector3(x, y, z));
        }

        private void GiveDefaultKit(BasePlayer player)
        {
            player.inventory.Strip();
            Kits?.Call("GiveKit", player, DefaultKitName);
            ApplySavedLoadout(player);
        }

        private void ApplySavedLoadout(BasePlayer player)
        {
            if (!SavedLoadouts.TryGetValue(player.userID, out var loadout))
                return;

            foreach (var item in loadout.Wear)
            {
                var def = ItemManager.FindItemDefinition(item.ShortName);
                if (def == null) continue;
                ItemManager.Create(def, item.Amount, item.SkinID)?.MoveToContainer(player.inventory.containerWear);
            }

            foreach (var item in loadout.Belt)
            {
                var def = ItemManager.FindItemDefinition(item.ShortName);
                if (def == null) continue;
                ItemManager.Create(def, item.Amount, item.SkinID)?.MoveToContainer(player.inventory.containerBelt);
            }

            foreach (var item in loadout.Main)
            {
                var def = ItemManager.FindItemDefinition(item.ShortName);
                if (def == null) continue;
                ItemManager.Create(def, item.Amount, item.SkinID)?.MoveToContainer(player.inventory.containerMain);
            }
        }

        private void CleanupPlayer(BasePlayer player)
        {
            DestroyAllUI(player);
            Stats.Remove(player.userID);
            InEvent.Remove(player.userID);
        }

        private void Unload()
        {
            foreach (var id in InEvent.ToList())
            {
                var p = BasePlayer.FindByID(id);
                if (p != null)
                {
                    DestroyAllUI(p);
                    p.Teleport(new Vector3(0, 5, 0));
                }
            }
            InEvent.Clear();
            Stats.Clear();
            SaveLoadouts();
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (InEvent.Contains(player.userID))
                CleanupPlayer(player);
        }

        private void LoadMVPLog()
        {
            mvpLog = Interface.Oxide.DataFileSystem.ReadObject<List<MVPLogEntry>>(MVPDataFile) ?? new List<MVPLogEntry>();
        }

        private void LoadLoadouts()
        {
            SavedLoadouts = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, SavedLoadout>>(LoadoutDataFile) ?? new Dictionary<ulong, SavedLoadout>();
        }

        private void SaveMVPLog()
        {
            Interface.Oxide.DataFileSystem.WriteObject(MVPDataFile, mvpLog);
        }

        private void SaveLoadouts()
        {
            Interface.Oxide.DataFileSystem.WriteObject(LoadoutDataFile, SavedLoadouts);
        }

        private void SendMVPToDiscord(MVPLogEntry entry)
        {
            var payload = new
            {
                username = "SFA MVP",
                embeds = new[]
                {
                    new
                    {
                        title = "\ud83c\udfc6 New MVP Awarded!",
                        color = 16753920,
                        fields = new[]
                        {
                            new { name = "Name", value = entry.Name, inline = true },
                            new { name = "SteamID", value = entry.SteamID.ToString(), inline = true },
                            new { name = "Kills", value = entry.Kills.ToString(), inline = true },
                            new { name = "Deaths", value = entry.Deaths.ToString(), inline = true },
                            new { name = "KDR", value = entry.KDR.ToString("0.00"), inline = true },
                            new { name = "Time", value = entry.Timestamp, inline = true }
                        }
                    }
                }
            };

            webrequest.Enqueue(DiscordWebhookUrl, JsonConvert.SerializeObject(payload), null, this, RequestMethod.POST, new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json"
            });
        }

        private void CmdShowMVPs(IPlayer iplayer, string command, string[] args)
        {
            if (!iplayer.IsAdmin)
            {
                iplayer.Reply("You don't have permission.");
                return;
            }

            if (mvpLog == null || mvpLog.Count == 0)
            {
                iplayer.Reply("No MVPs found.");
                return;
            }

            var top = mvpLog
                .OrderByDescending(x => x.Kills)
                .Take(10)
                .Select((e, i) => $"{i + 1}. {e.Name} - {e.Kills}K/{e.Deaths}D - KDR: {e.KDR:0.00} - {e.Timestamp}");

            iplayer.Reply(string.Join("\n", top));
        }

        private void CmdSaveLoadout(IPlayer iplayer, string command, string[] args)
        {
            var player = iplayer.Object as BasePlayer;
            if (player == null) return;

            var loadout = new SavedLoadout();
            foreach (var item in player.inventory.containerBelt.itemList)
                loadout.Belt.Add(new SavedItem { ShortName = item.info.shortname, Amount = item.amount, SkinID = item.skin });
            foreach (var item in player.inventory.containerWear.itemList)
                loadout.Wear.Add(new SavedItem { ShortName = item.info.shortname, Amount = item.amount, SkinID = item.skin });
            foreach (var item in player.inventory.containerMain.itemList)
                loadout.Main.Add(new SavedItem { ShortName = item.info.shortname, Amount = item.amount, SkinID = item.skin });

            SavedLoadouts[player.userID] = loadout;
            player.ChatMessage("<color=#ffc800>[SFA]</color> Loadout saved.");
            SaveLoadouts();
        }

        private void CmdResetLoadout(IPlayer iplayer, string command, string[] args)
        {
            var player = iplayer.Object as BasePlayer;
            if (player == null) return;

            if (SavedLoadouts.Remove(player.userID))
            {
                player.ChatMessage("<color=#ffc800>[SFA]</color> Saved loadout cleared.");
                SaveLoadouts();
            }
            else
            {
                player.ChatMessage("<color=#ffc800>[SFA]</color> You have no saved loadout.");
            }
        }

        private void CmdToggleHeadshot(IPlayer iplayer, string command, string[] args)
        {
            var player = iplayer.Object as BasePlayer;
            if (player == null || !InEvent.Contains(player.userID))
            {
                iplayer.Reply("You must be in the event to use this.");
                return;
            }

            headshotOnly = !headshotOnly;
            foreach (var id in InEvent)
                BasePlayer.FindByID(id)?.ChatMessage($"<color=#ffc800>[SFA]</color> Headshot only {(headshotOnly ? "enabled" : "disabled")}.");
        }

        private void CmdToggleAntiCrouch(IPlayer iplayer, string command, string[] args)
        {
            var player = iplayer.Object as BasePlayer;
            if (player == null || !InEvent.Contains(player.userID))
            {
                iplayer.Reply("You must be in the event to use this.");
                return;
            }

            antiCrouch = !antiCrouch;
            foreach (var id in InEvent)
                BasePlayer.FindByID(id)?.ChatMessage($"<color=#ffc800>[SFA]</color> No crouch {(antiCrouch ? "enabled" : "disabled")}.");
        }

        private void CmdToggleStatsMode(IPlayer iplayer, string command, string[] args)
        {
            accuracyMode = !accuracyMode;
            foreach (var id in InEvent)
            {
                var p = BasePlayer.FindByID(id);
                if (p != null) UpdateStatsUI(p);
            }
            iplayer.Reply($"Stats mode {(accuracyMode ? "accuracy" : "kills")}." );
        }
    }
}

