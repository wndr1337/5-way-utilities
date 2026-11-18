using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace FiveWayUtilities
{
    public class SeedInfo
    {
        public string tier { get; set; }
        public float score { get; set; }
        public string socket { get; set; }
        public string arch { get; set; }
        public string mods { get; set; }
    }

    public class KillStats
    {
        public int Normal;
        public int Magic;
        public int Rare;
        public int Unique;
        public int Total => Normal + Magic + Rare + Unique;

        public void Reset()
        {
            Normal = Magic = Rare = Unique = 0;
        }
    }

    public class GemRow
    {
        public string Name;
        public string Type;
        public double Buy;
        public double Gcp20;
        public double Sell;
        public double Profit;
        public double PerRun;
        public double BatchRun;
        public double BatchProfit;
        public double BatchCost;
        public double Runs;
        public int SLots;
        public int BLots;
        public string LotsStr;
        public string TrendStr;
        public double TrendVal;
        public string Tag;
        public double Score;
    }

    public class HistEntry
    {
        public int listings { get; set; }
        public double price { get; set; }
        public long ts { get; set; }
    }

    public class FiveWayUtilities : BaseSettingsPlugin<FiveWayUtilitiesSettings>
    {
        // ==================== 1. SESSION & KILLS STATE ====================
        private readonly KillStats _zoneKills = new KillStats();
        private readonly KillStats _sessionKills = new KillStats();
        private readonly HashSet<uint> _countedDeadMonsters = new HashSet<uint>();
        private readonly HashSet<uint> _currentlyDeadUniques = new HashSet<uint>();
        private readonly HashSet<uint> _countedDroppedJewels = new HashSet<uint>();

        private int _zoneJewelsDropped = 0;
        private int _sessionJewelsDropped = 0;
        private int _completed5WayRuns = 0;
        private uint _currentRunSeed = 0;
        private bool _currentRunCounted = false;
        private readonly Stopwatch _zoneTimer = new Stopwatch();
        private TimeSpan _session5WayTime = TimeSpan.Zero;

        // ==================== 2. TIMELESS EVALUATOR STATE ====================
        private Dictionary<string, SeedInfo> _timelessDb = new Dictionary<string, SeedInfo>();
        private string _timelessStatus = "Not loaded";
        private string _lastScannedJewelDebug = "None";

        private static readonly string[] AllConquerors =
        {
            "Kaom", "Rakiata", "Akoya",
            "Asenath", "Balbala", "Nasima",
            "Cadiro", "Caspiro", "Victario",
            "Avarius", "Dominus", "Maxarius",
            "Ahuana", "Doryani", "Xibaqua"
        };

        private static readonly Dictionary<string, string[]> ConqByJewel =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "Glorious Vanity", new[] { "Ahuana", "Doryani", "Xibaqua" } },
                { "Lethal Pride",    new[] { "Kaom", "Rakiata", "Akoya" } },
                { "Brutal Restraint",new[] { "Asenath", "Balbala", "Nasima" } },
                { "Militant Faith",  new[] { "Avarius", "Dominus", "Maxarius" } },
                { "Elegant Hubris",  new[] { "Cadiro", "Caspiro", "Victario" } }
            };

        // ==================== 3. GEM PROFIT TRACKER STATE ====================
        private static readonly HttpClient Http = new HttpClient();
        private static readonly HashSet<string> Exceptional = new HashSet<string>
        {
            "Enlighten Support", "Empower Support", "Enhance Support"
        };

        private const double XP_NORMAL_1_20 = 342.05;
        private const double XP_EXCEPTIONAL_1_3 = 1666.05;
        private const double XP_AWAKENED_1_5 = 1550.00;

        private List<GemRow> _gemRows = new List<GemRow>();
        private GemRow _bestGem;
        private string _league = "Loading...";
        private double _currentGcp = 2.5;
        private string _gcpSource = "Init";
        private DateTime _lastGemUpdate = DateTime.MinValue;
        private bool _isUpdatingGems = false;
        private string _gemStatus = "Idle";
        private string _gemSearch = "";

        private bool _isMeasuringXp = false;
        private uint _measureStartXp = 0;
        private string _measureGemName = "";
        private double _measureGemMult = 1.0;
        private string _measureMsg = "Open Inventory (I) in hideout & click Start before 1 run";

        public override bool Initialise()
        {
            if (!Http.DefaultRequestHeaders.Contains("User-Agent"))
                Http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 FiveWayUtilities/1.0");

            LoadTimelessDatabase();
            if (Settings.EnableGemTracker)
                TriggerGemUpdate();

            return true;
        }

        private void LoadTimelessDatabase()
        {
            try
            {
                var p1 = Path.Combine(DirectoryFullName, "timeless_prices.json");
                var p2 = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Plugins", "Source", "FiveWayUtilities", "timeless_prices.json"
                );
                var p3 = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Plugins", "Source", "TimelessEvaluator", "timeless_prices.json"
                );
                var p4 = @"C:\Users\wndrq\Desktop\timeless_prices.json";

                var path = new[] { p1, p2, p3, p4 }.FirstOrDefault(File.Exists);
                if (path == null)
                {
                    _timelessStatus = "timeless_prices.json not found!";
                    return;
                }

                var txt = File.ReadAllText(path);
                _timelessDb = JsonConvert.DeserializeObject<Dictionary<string, SeedInfo>>(txt)
                              ?? new Dictionary<string, SeedInfo>();
                _timelessStatus = $"Loaded {_timelessDb.Count} seeds";
            }
            catch (Exception ex)
            {
                _timelessStatus = $"Err: {ex.Message}";
            }
        }

        public override void AreaChange(AreaInstance area)
        {
            if (_zoneTimer.IsRunning)
            {
                _zoneTimer.Stop();
                _session5WayTime += _zoneTimer.Elapsed;
                _zoneTimer.Reset();
            }

            if (area == null || area.IsHideout || area.IsTown)
                return;

            bool is5Way = area.Name.Contains("Timeless Conflict");
            if (is5Way)
            {
                if (area.Hash != _currentRunSeed)
                {
                    _currentRunSeed = area.Hash;
                    _currentRunCounted = false;
                    _zoneKills.Reset();
                    _zoneJewelsDropped = 0;
                    _countedDeadMonsters.Clear();
                    _currentlyDeadUniques.Clear();
                    _countedDroppedJewels.Clear();
                }
                _zoneTimer.Start();
            }
            else if (!Settings.CountOnlyIn5Way)
            {
                _currentRunSeed = area.Hash;
                _currentRunCounted = false;
                _zoneKills.Reset();
                _zoneJewelsDropped = 0;
                _countedDeadMonsters.Clear();
                _currentlyDeadUniques.Clear();
                _countedDroppedJewels.Clear();
                _zoneTimer.Start();
            }
        }

        public override Job Tick()
        {
            if (Settings.EnableGemTracker && !_isUpdatingGems &&
                (DateTime.Now - _lastGemUpdate).TotalMinutes >= Settings.RefreshMinutes.Value)
            {
                TriggerGemUpdate();
            }

            if (Settings.EnableSessionAndKills)
            {
                UpdateKillsAndDrops();
            }

            return null;
        }

        private void UpdateKillsAndDrops()
        {
            var area = GameController?.Area?.CurrentArea;
            if (area == null || area.IsHideout || area.IsTown) return;

            bool is5Way = area.Name.Contains("Timeless Conflict");
            if (Settings.CountOnlyIn5Way && !is5Way) return;

            if (!_zoneTimer.IsRunning)
                _zoneTimer.Start();

            if (!_currentRunCounted && _zoneKills.Total > 50)
            {
                _currentRunCounted = true;
                _completed5WayRuns++;
            }

            foreach (var ent in GameController.EntityListWrapper.OnlyValidEntities)
            {
                if (ent == null || ent.Type != EntityType.Monster || !ent.IsHostile) continue;

                var rarity = ent.GetComponent<ObjectMagicProperties>()?.Rarity ?? MonsterRarity.White;
                if (rarity == MonsterRarity.Unique)
                {
                    if (!ent.IsAlive)
                    {
                        if (_currentlyDeadUniques.Add(ent.Id))
                        {
                            _zoneKills.Unique++;
                            _sessionKills.Unique++;
                        }
                    }
                    else
                    {
                        _currentlyDeadUniques.Remove(ent.Id);
                    }
                }
                else if (!ent.IsAlive && _countedDeadMonsters.Add(ent.Id))
                {
                    switch (rarity)
                    {
                        case MonsterRarity.White:
                            _zoneKills.Normal++;
                            _sessionKills.Normal++;
                            break;
                        case MonsterRarity.Magic:
                            _zoneKills.Magic++;
                            _sessionKills.Magic++;
                            break;
                        case MonsterRarity.Rare:
                            _zoneKills.Rare++;
                            _sessionKills.Rare++;
                            break;
                    }
                }
            }

            if (GameController.EntityListWrapper.ValidEntitiesByType.TryGetValue(EntityType.WorldItem, out var worldItems))
            {
                foreach (var ent in worldItems)
                {
                    if (ent == null || !ent.IsValid) continue;
                    var itemEnt = ent.GetComponent<WorldItem>()?.ItemEntity;
                    if (itemEnt == null || !itemEnt.IsValid) continue;

                    string path = itemEnt.Path ?? "";
                    string baseName = itemEnt.GetComponent<Base>()?.Name ?? "";

                    if (path.IndexOf("Timeless", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        baseName.Equals("Timeless Jewel", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_countedDroppedJewels.Add(ent.Id))
                        {
                            _zoneJewelsDropped++;
                            _sessionJewelsDropped++;
                        }
                    }
                }
            }
        }

        public override void Render()
        {
            if (Settings.EnableSessionAndKills && Settings.ShowCombatHudOverlay)
            {
                RenderCombatHud();
            }

            if (Settings.EnableTimelessEval)
            {
                RenderTimelessOverlays();
            }
        }

        private void RenderCombatHud()
        {
            var area = GameController?.Area?.CurrentArea;
            if (area == null || area.IsHideout || area.IsTown) return;
            if (Settings.CountOnlyIn5Way && !area.Name.Contains("Timeless Conflict")) return;

            int runs = Math.Max(1, _completed5WayRuns);
            TimeSpan totalTime = _session5WayTime + (_zoneTimer.IsRunning ? _zoneTimer.Elapsed : TimeSpan.Zero);
            double avgKills = _sessionKills.Total / (double)runs;
            double avgJewels = _sessionJewelsDropped / (double)runs;

            float x = Settings.HudPosX.Value;
            float y = Settings.HudPosY.Value;

            var bg = new RectangleF(x, y, 395, 78);
            Graphics.DrawBox(bg, new Color(10, 12, 18, 205));
            Graphics.DrawFrame(bg, new Color(90, 150, 210, 190), 1);

            string l1 = $"5-Way Run #{runs} | Zone Time: {_zoneTimer.Elapsed:mm\\:ss} | Drops: {_zoneJewelsDropped} jewels";
            string l2 = $"Zone Kills: {_zoneKills.Total:N0} (W:{_zoneKills.Normal} M:{_zoneKills.Magic} R:{_zoneKills.Rare} U:{_zoneKills.Unique})";
            string l3 = $"Session ({_completed5WayRuns} runs, {(int)totalTime.TotalMinutes}m {totalTime.Seconds}s): {_sessionKills.Total:N0} kills | {_sessionJewelsDropped} jewels";
            string l4 = $"Session Avg/Run: {avgKills:F0} kills | {avgJewels:F2} jewels/run";

            Graphics.DrawText(l1, new Vector2(x + 8, y + 5), Color.Gold);
            Graphics.DrawText(l2, new Vector2(x + 8, y + 23), Color.White);
            Graphics.DrawText(l3, new Vector2(x + 8, y + 41), Color.LightSkyBlue);
            Graphics.DrawText(l4, new Vector2(x + 8, y + 59), Color.LightGreen);
        }

        // ==================== MENU UI (F12) ====================
        public override void DrawSettings()
        {
            if (ImGui.BeginTabBar("FiveWayMainTabs"))
            {
                if (ImGui.BeginTabItem("Session Tracker & Kills"))
                {
                    DrawSessionTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("5-Way Gem Profit"))
                {
                    DrawGemTrackerTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Timeless Evaluator & Config"))
                {
                    DrawConfigAndTimelessTab();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }

        private void DrawSessionTab()
        {
            bool enableSession = Settings.EnableSessionAndKills.Value;
            if (ImGui.Checkbox("Enable Session & Kill Counter", ref enableSession))
                Settings.EnableSessionAndKills.Value = enableSession;

            ImGui.SameLine(260);
            bool showHud = Settings.ShowCombatHudOverlay.Value;
            if (ImGui.Checkbox("Show In-Game HUD Overlay", ref showHud))
                Settings.ShowCombatHudOverlay.Value = showHud;

            ImGui.SameLine(490);
            bool only5Way = Settings.CountOnlyIn5Way.Value;
            if (ImGui.Checkbox("Track Only 5-Way (Timeless Conflict)", ref only5Way))
                Settings.CountOnlyIn5Way.Value = only5Way;

            int posX = Settings.HudPosX.Value;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderInt("HUD X", ref posX, 0, 2500))
                Settings.HudPosX.Value = posX;

            ImGui.SameLine();
            int posY = Settings.HudPosY.Value;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderInt("HUD Y", ref posY, 0, 1400))
                Settings.HudPosY.Value = posY;

            ImGui.Separator();

            int runs = Math.Max(1, _completed5WayRuns);
            TimeSpan totalTime = _session5WayTime + (_zoneTimer.IsRunning ? _zoneTimer.Elapsed : TimeSpan.Zero);
            TimeSpan avgTime = TimeSpan.FromSeconds(totalTime.TotalSeconds / runs);

            if (ImGui.BeginTable("SessionSummaryTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("Metric", ImGuiTableColumnFlags.WidthFixed, 260);
                ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();

                AddStatRow("Completed / Active 5-Way Runs", $"{_completed5WayRuns}");
                AddStatRow("Total Time Inside 5-Way", $"{(int)totalTime.TotalHours:D2}:{totalTime.Minutes:D2}:{totalTime.Seconds:D2} (Avg: {avgTime:mm\\:ss}/run)");
                AddStatRow("Timeless Jewels Dropped (Session)", $"{_sessionJewelsDropped}  (Avg: {_sessionJewelsDropped / (double)runs:F2} jewels/run)");
                AddStatRow("Current Zone Kills", $"{_zoneKills.Total:N0}  (White: {_zoneKills.Normal} | Magic: {_zoneKills.Magic} | Rare: {_zoneKills.Rare} | Unique: {_zoneKills.Unique})");
                AddStatRow("Session Total Kills", $"{_sessionKills.Total:N0}  (White: {_sessionKills.Normal:N0} | Magic: {_sessionKills.Magic:N0} | Rare: {_sessionKills.Rare:N0} | Unique: {_sessionKills.Unique:N0})");
                AddStatRow("Session Average Kills / Run",
                    $"{_sessionKills.Total / (double)runs:F0}  " +
                    $"(W: {_sessionKills.Normal / (double)runs:F0} | M: {_sessionKills.Magic / (double)runs:F0} | " +
                    $"R: {_sessionKills.Rare / (double)runs:F0} | U: {_sessionKills.Unique / (double)runs:F1})");

                ImGui.EndTable();
            }

            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.65f, 0.2f, 0.2f, 1f));
            if (ImGui.Button("Reset Entire 5-Way Session"))
            {
                _completed5WayRuns = 0;
                _sessionJewelsDropped = 0;
                _zoneJewelsDropped = 0;
                _session5WayTime = TimeSpan.Zero;
                _zoneTimer.Reset();
                _sessionKills.Reset();
                _zoneKills.Reset();
                _countedDeadMonsters.Clear();
                _currentlyDeadUniques.Clear();
                _countedDroppedJewels.Clear();
                _currentRunCounted = false;
            }
            ImGui.PopStyleColor();
        }

        private void AddStatRow(string label, string val)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(label);
            ImGui.TableNextColumn();
            ImGui.TextColored(new Vector4(0.4f, 0.95f, 0.6f, 1f), val);
        }

        private void DrawConfigAndTimelessTab()
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), $"Timeless Jewel Database Status: {_timelessStatus}");
            ImGui.SameLine(380);
            if (ImGui.Button("Reload timeless_prices.json"))
            {
                LoadTimelessDatabase();
            }
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 1f, 1f), $"Last Scanned Jewel: {_lastScannedJewelDebug}");
            ImGui.Separator();
            base.DrawSettings();
        }

        // ==================== TIMELESS EVALUATOR LOGIC ====================
        private void RenderTimelessOverlays()
        {
            var ui = GameController?.Game?.IngameState?.IngameUi;
            if (ui == null) return;

            int invCount = 0;
            int extraCount = 0;
            int jewelsMatched = 0;

            if (Settings.EvalInInventory && ui.InventoryPanel != null && ui.InventoryPanel.IsVisible)
            {
                var inv = ui.InventoryPanel[InventoryIndex.PlayerInventory];
                var items = inv?.VisibleInventoryItems;
                if (items != null)
                {
                    invCount = items.Count;
                    jewelsMatched += DrawTimelessList(items);
                }
            }

            if (Settings.EvalInStash)
            {
                if (ui.StashElement != null && ui.StashElement.IsVisible)
                {
                    var stashItems = ui.StashElement.VisibleStash?.VisibleInventoryItems;
                    if (stashItems != null)
                    {
                        extraCount += stashItems.Count;
                        jewelsMatched += DrawTimelessList(stashItems);
                    }
                }

                var shopItems = CollectAllVisibleShopItems(ui);
                if (shopItems.Count > 0)
                {
                    extraCount += shopItems.Count;
                    jewelsMatched += DrawTimelessList(shopItems);
                }
            }

            Graphics.DrawText(
                $"[5WayEval] {_timelessStatus} | Inv: {invCount} | Stash/Shop: {extraCount} | Jewels: {jewelsMatched} | {_lastScannedJewelDebug}",
                new Vector2(20, 120),
                Color.Cyan
            );
        }

        private List<NormalInventoryItem> CollectAllVisibleShopItems(Element rootUi)
        {
            var result = new List<NormalInventoryItem>();
            if (rootUi?.Children == null) return result;

            var ui = GameController.Game.IngameState.IngameUi;
            foreach (var child in rootUi.Children)
            {
                if (child == null || !child.IsVisible) continue;
                if (child == ui.InventoryPanel || child == ui.StashElement) continue;

                FindInventoryItemsRecursive(child, 0, result);
            }
            return result;
        }

        private void FindInventoryItemsRecursive(Element el, int depth, List<NormalInventoryItem> acc)
        {
            if (el == null || !el.IsVisible || depth > 8) return;

            if (el is NormalInventoryItem norm && norm.Item != null && norm.Item.IsValid)
            {
                acc.Add(norm);
                return;
            }

            var children = el.Children;
            if (children == null) return;
            for (int i = 0; i < children.Count; i++)
            {
                FindInventoryItemsRecursive(children[i], depth + 1, acc);
            }
        }

        private int DrawTimelessList(IList<NormalInventoryItem> list)
        {
            int matched = 0;
            foreach (var item in list)
            {
                var ent = item?.Item;
                if (ent == null || !ent.IsValid) continue;

                var mods = ent.GetComponent<Mods>();
                if (mods == null) continue;

                string path = ent.Path ?? "";
                string baseName = ent.GetComponent<Base>()?.Name ?? "";

                var uName = ResolveJewelName(path, baseName, mods.UniqueName, mods);
                if (uName == null) continue;

                matched++;
                var rect = item.GetClientRect();

                if (!ParseSeedEverywhere(uName, mods, out var seed, out var conq))
                {
                    var m0 = mods.ItemMods?.FirstOrDefault();
                    _lastScannedJewelDebug = $"FAIL_SEED({uName}): Raw={m0?.RawName} Vals=[{string.Join(",", m0?.Values ?? new List<int>())}]";
                    Graphics.DrawFrame(rect, Color.Yellow, 2);
                    Graphics.DrawText("???", new Vector2(rect.X + 4, rect.Y + 4), Color.Yellow);
                    continue;
                }

                _lastScannedJewelDebug = $"OK: {uName}|{seed}|{conq}";
                var key = $"{uName}|{seed}|{conq}";

                if (_timelessDb.TryGetValue(key, out var info))
                    DrawGoodJewel(rect, info, seed, conq);
                else if (Settings.ShowTrash5To1)
                    DrawTrashJewel(rect);
            }
            return matched;
        }

        private string ResolveJewelName(string path, string baseName, string uniqueName, Mods mods)
        {
            if (!string.IsNullOrEmpty(uniqueName) && ConqByJewel.ContainsKey(uniqueName))
                return ConqByJewel.Keys.First(k => k.Equals(uniqueName, StringComparison.OrdinalIgnoreCase));

            if (mods?.ItemMods != null)
            {
                foreach (var im in mods.ItemMods)
                {
                    if (im == null) continue;
                    string modStr = $"{im.RawName} {im.Name}";
                    foreach (var kv in ConqByJewel)
                    {
                        if (kv.Value.Any(c => modStr.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0))
                            return kv.Key;
                    }
                }
            }

            bool isTimeless = path.IndexOf("Timeless", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              baseName.IndexOf("Timeless", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!isTimeless) return null;

            if (path.IndexOf("Vaal", StringComparison.OrdinalIgnoreCase) >= 0 || path.EndsWith("1")) return "Glorious Vanity";
            if (path.IndexOf("Karui", StringComparison.OrdinalIgnoreCase) >= 0 || path.EndsWith("2")) return "Lethal Pride";
            if (path.IndexOf("Maraketh", StringComparison.OrdinalIgnoreCase) >= 0 || path.EndsWith("3")) return "Brutal Restraint";
            if (path.IndexOf("Templar", StringComparison.OrdinalIgnoreCase) >= 0 || path.EndsWith("4")) return "Militant Faith";
            if (path.IndexOf("Eternal", StringComparison.OrdinalIgnoreCase) >= 0 || path.EndsWith("5")) return "Elegant Hubris";

            return "Timeless Jewel";
        }

        private bool ParseSeedEverywhere(string uName, Mods mods, out string seed, out string conq)
        {
            seed = null;
            conq = null;

            try
            {
                if (mods?.ItemMods == null) return false;

                foreach (var im in mods.ItemMods)
                {
                    if (im == null) continue;
                    string raw = $"{im.RawName} {im.Name}";

                    foreach (var c in AllConquerors)
                    {
                        if (raw.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            conq = c;
                            break;
                        }
                    }

                    if (im.Values != null && im.Values.Count > 0)
                    {
                        foreach (var v in im.Values)
                        {
                            if (v >= 100 && v <= 160000)
                            {
                                seed = v.ToString();
                                break;
                            }
                        }
                    }

                    if (seed != null && conq != null)
                        return true;
                }

                if (seed != null && ConqByJewel.TryGetValue(uName, out var validConqs))
                {
                    conq ??= validConqs[0];
                    return true;
                }
            }
            catch (Exception ex)
            {
                _lastScannedJewelDebug = $"ParseErr: {ex.Message}";
            }

            return false;
        }

        private void DrawGoodJewel(RectangleF r, SeedInfo info, string seed, string conq)
        {
            var col = GetTierColor(info.tier);
            Graphics.DrawFrame(r, col, Settings.FrameWidth.Value);

            Graphics.DrawBox(new RectangleF(r.X + 2, r.Y + 2, r.Width - 4, 15), new Color(0, 0, 0, 210));
            Graphics.DrawText(info.tier, new Vector2(r.X + 4, r.Y + 2), col);

            Graphics.DrawBox(new RectangleF(r.X + 2, r.Bottom - 15, r.Width - 4, 13), new Color(0, 0, 0, 190));
            Graphics.DrawText(info.socket, new Vector2(r.X + 4, r.Bottom - 15), Color.White);

            var mp = Input.MousePositionNum;
            if (r.Contains(mp.X, mp.Y))
            {
                var tip = new RectangleF(r.Right + 6, r.Y, 235, 58);
                Graphics.DrawBox(tip, new Color(15, 15, 20, 240));
                Graphics.DrawFrame(tip, col, 1);

                Graphics.DrawText($"[{info.tier}] {info.arch} ({conq} #{seed})", new Vector2(tip.X + 5, tip.Y + 4), col);
                Graphics.DrawText($"Socket: {info.socket} (Score: {info.score})", new Vector2(tip.X + 5, tip.Y + 21), Color.LightGray);
                Graphics.DrawText($"Mods: {info.mods}", new Vector2(tip.X + 5, tip.Y + 38), Color.SpringGreen);
            }
        }

        private void DrawTrashJewel(RectangleF r)
        {
            Graphics.DrawFrame(r, new Color(90, 90, 90, 160), 1);
            Graphics.DrawBox(new RectangleF(r.X + 2, r.Bottom - 14, r.Width - 4, 12), new Color(0, 0, 0, 180));
            Graphics.DrawText("5-to-1", new Vector2(r.X + 4, r.Bottom - 14), Color.Gray);
        }

        private Color GetTierColor(string tier)
        {
            switch (tier)
            {
                case "100+d": return Color.Magenta;
                case "50d":   return Color.Gold;
                case "25d":   return Color.OrangeRed;
                case "10d":   return Color.Orange;
                case "4d":    return Color.Yellow;
                case "1d":    return Color.LimeGreen;
                default:      return Color.White;
            }
        }

        // ==================== GEM PROFIT TRACKER LOGIC ====================
        private void DrawGemTrackerTab()
        {
            ImGui.TextColored(new Vector4(0.4f, 0.85f, 1f, 1f), $"League: {_league}");
            ImGui.SameLine(200);
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), $"GCP: {_currentGcp:F2}c ({_gcpSource})");
            ImGui.SameLine(420);

            int nextIn = Math.Max(0, Settings.RefreshMinutes.Value * 60 - (int)(DateTime.Now - _lastGemUpdate).TotalSeconds);
            if (ImGui.Button(_isUpdatingGems ? "Updating..." : $"Refresh Now ({nextIn}s)"))
                TriggerGemUpdate();
            ImGui.SameLine();
            ImGui.TextDisabled(_gemStatus);

            if (!_isMeasuringXp)
            {
                if (ImGui.Button("Start 1-Run Gem XP Measure")) StartXpMeasurement();
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.65f, 0.25f, 1f));
                if (ImGui.Button("Finish 1-Run & Apply XP")) FinishXpMeasurement();
                ImGui.PopStyleColor();
            }
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.5f, 1f), _measureMsg);

            if (_bestGem != null)
            {
                int socks = Settings.SocketsCount.Value;
                if (ImGui.BeginTable("BestGemCard", 1, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(new Vector4(0.12f, 0.18f, 0.12f, 0.9f)));
                    ImGui.TableNextColumn();
                    ImGui.TextColored(new Vector4(1f, 0.85f, 0.15f, 1f),
                        $"★ BEST PICK: {_bestGem.Name} [{_bestGem.Type}] ({Math.Ceiling(_bestGem.Runs)} runs)  ->  {_bestGem.BatchRun:F1}c/run ({socks}x)");
                    ImGui.TextColored(new Vector4(0.85f, 0.85f, 0.85f, 1f),
                        $"Buy {socks}x: ~{_bestGem.BatchCost:F0}c | Sell: {_bestGem.Sell:F1}c/ea | Cycle Profit: +{_bestGem.BatchProfit:F0}c | 7d: {_bestGem.TrendStr} {_bestGem.Tag}");
                    ImGui.EndTable();
                }
            }

            if (_gemRows.Count == 0) return;

            ImGui.SetNextItemWidth(250);
            ImGui.InputTextWithHint("##gemsearch", "Search gem...", ref _gemSearch, 64);

            var flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingFixedFit;
            if (ImGui.BeginTable("GemsMainTable", 10, flags, new Vector2(0, 390)))
            {
                int socks = Settings.SocketsCount.Value;
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Gem Name", ImGuiTableColumnFlags.WidthFixed, 195);
                ImGui.TableSetupColumn("Setup", ImGuiTableColumnFlags.WidthFixed, 95);
                ImGui.TableSetupColumn("Runs", ImGuiTableColumnFlags.WidthFixed, 45);
                ImGui.TableSetupColumn("Buy", ImGuiTableColumnFlags.WidthFixed, 55);
                ImGui.TableSetupColumn("+20GCP", ImGuiTableColumnFlags.WidthFixed, 55);
                ImGui.TableSetupColumn("Sell", ImGuiTableColumnFlags.WidthFixed, 55);
                ImGui.TableSetupColumn("c/run", ImGuiTableColumnFlags.WidthFixed, 55);
                ImGui.TableSetupColumn($"{socks}x/run", ImGuiTableColumnFlags.WidthFixed, 75);
                ImGui.TableSetupColumn("Lots (Δ)", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("7d / Tag", ImGuiTableColumnFlags.WidthFixed, 95);
                ImGui.TableHeadersRow();

                var list = string.IsNullOrWhiteSpace(_gemSearch)
                    ? _gemRows.Take(45)
                    : _gemRows.Where(r => r.Name.IndexOf(_gemSearch, StringComparison.OrdinalIgnoreCase) >= 0).Take(45);

                foreach (var r in list)
                {
                    ImGui.TableNextRow();
                    bool isBest = _bestGem != null && r.Name == _bestGem.Name && r.Type == _bestGem.Type;
                    var col = isBest ? new Vector4(1f, 0.85f, 0.2f, 1f) : new Vector4(0.95f, 0.95f, 0.95f, 1f);

                    ImGui.TableNextColumn(); ImGui.TextColored(col, (isBest ? "★ " : "") + r.Name);
                    ImGui.TableNextColumn(); ImGui.TextDisabled(r.Type);
                    ImGui.TableNextColumn(); ImGui.Text($"{r.Runs:F1}");
                    ImGui.TableNextColumn(); ImGui.Text($"{r.Buy:F1}c");
                    ImGui.TableNextColumn(); ImGui.Text(r.Gcp20 > 0 ? $"+{r.Gcp20:F0}c" : "-");
                    ImGui.TableNextColumn(); ImGui.Text($"{r.Sell:F1}c");
                    ImGui.TableNextColumn(); ImGui.TextColored(col, $"{r.PerRun:F2}c");
                    ImGui.TableNextColumn(); ImGui.TextColored(new Vector4(0.35f, 1f, 0.45f, 1f), $"{r.BatchRun:F1}c");
                    ImGui.TableNextColumn(); ImGui.Text(r.LotsStr);
                    ImGui.TableNextColumn(); ImGui.Text($"{r.TrendStr} {r.Tag}");
                }
                ImGui.EndTable();
            }
        }

        private void StartXpMeasurement()
        {
            foreach (var gemEnt in EnumerateAllSocketedGems())
            {
                var sk = gemEnt.GetComponent<SkillGem>();
                if (sk == null) continue;
                string nm = gemEnt.GetComponent<Base>()?.Name ?? "Gem";
                int maxLvl = Exceptional.Contains(nm) ? 3 : (nm.StartsWith("Awakened ") ? 5 : 20);
                if (sk.Level >= maxLvl) continue;

                int qual = gemEnt.GetComponent<Quality>()?.ItemQuality ?? 0;
                _isMeasuringXp = true;
                _measureStartXp = sk.TotalExpGained;
                _measureGemName = nm;
                _measureGemMult = Exceptional.Contains(nm) ? (1.0 + (qual + Settings.OffhandExtraQuality.Value) * 0.05) : 1.0;
                _measureMsg = $"Tracking '{nm}' (Start XP: {_measureStartXp:N0}). Run 1x 5-Way & click Finish!";
                return;
            }
            _measureMsg = "Open Inventory (I) in hideout first so gems are loaded!";
        }

        private void FinishXpMeasurement()
        {
            foreach (var gemEnt in EnumerateAllSocketedGems())
            {
                var sk = gemEnt.GetComponent<SkillGem>();
                string nm = gemEnt.GetComponent<Base>()?.Name ?? "Gem";
                if (sk != null && nm == _measureGemName && sk.TotalExpGained > _measureStartXp)
                {
                    _isMeasuringXp = false;
                    double baseXp = ((sk.TotalExpGained - _measureStartXp) / 1_000_000.0) / _measureGemMult;
                    Settings.BaseGemXpPerRunMillions.Value = (float)Math.Round(baseXp, 2);
                    _measureMsg = $"Base XP/run calibrated to {baseXp:F2}M!";
                    TriggerGemUpdate();
                    return;
                }
            }
            _measureMsg = "XP unchanged. Open Inventory (I) in hideout and click Finish again.";
        }

        private IEnumerable<Entity> EnumerateAllSocketedGems()
        {
            var invPanel = GameController?.Game?.IngameState?.IngameUi?.InventoryPanel;
            if (invPanel == null) yield break;

            foreach (InventoryIndex idx in Enum.GetValues(typeof(InventoryIndex)))
            {
                if (idx == InventoryIndex.None) continue;
                IList<NormalInventoryItem> items = null;
                try { items = invPanel[idx]?.VisibleInventoryItems; } catch { continue; }
                if (items == null) continue;

                foreach (var uiItem in items)
                {
                    var sockets = uiItem?.Item?.GetComponent<Sockets>();
                    if (sockets?.SocketedGems == null) continue;
                    foreach (var sg in sockets.SocketedGems)
                        if (sg?.GemEntity != null && sg.GemEntity.IsValid) yield return sg.GemEntity;
                }
            }
        }

        private double CalculateExactRuns(string name, int startQual, bool useGcp)
        {
            double baseXp = Math.Max(1.0, Settings.BaseGemXpPerRunMillions.Value);
            int effQual = (useGcp ? 20 : startQual) + Settings.OffhandExtraQuality.Value;
            if (Exceptional.Contains(name)) return XP_EXCEPTIONAL_1_3 / (baseXp * (1.0 + effQual * 0.05));
            if (name.StartsWith("Awakened ")) return XP_AWAKENED_1_5 / baseXp;
            return XP_NORMAL_1_20 / baseXp;
        }

        private void TriggerGemUpdate()
        {
            if (_isUpdatingGems) return;
            _isUpdatingGems = true;
            _gemStatus = "Syncing...";
            _lastGemUpdate = DateTime.Now;

            Task.Run(async () =>
            {
                try
                {
                    await FetchGemsAsync();
                    _gemStatus = $"OK ({_gemRows.Count})";
                }
                catch (Exception ex) { _gemStatus = $"Err: {ex.Message}"; }
                finally { _isUpdatingGems = false; }
            });
        }

        private async Task FetchGemsAsync()
        {
            var lArr = JArray.Parse(await Http.GetStringAsync("https://poe.ninja/poe1/api/economy/leagues"));
            _league = lArr[0].Type == JTokenType.Object ? lArr[0]["id"]?.ToString() : lArr[0].ToString();

            if (Settings.UseManualGcp.Value)
            {
                _currentGcp = Settings.ManualGcpPrice.Value;
                _gcpSource = "Manual";
            }
            else
            {
                try
                {
                    var exObj = JObject.Parse(await Http.GetStringAsync($"https://poe.ninja/poe1/api/economy/exchange/current/overview?league={_league}&type=Currency"));
                    var exLines = exObj["lines"] as JArray;
                    string pCurr = exObj["core"]?["primary"]?.ToString() ?? "chaos";
                    double divChaos = 1.0;
                    if (pCurr.Equals("divine", StringComparison.OrdinalIgnoreCase))
                    {
                        double cInD = exLines?.FirstOrDefault(x => x["id"]?.ToString() == "chaos")?["primaryValue"]?.Value<double>() ?? 0;
                        if (cInD > 0) divChaos = 1.0 / cInD;
                    }
                    var gcpE = exLines?.FirstOrDefault(x => x["id"]?.ToString() == "gemcutters-prism" || x["id"]?.ToString() == "gcp");
                    if (gcpE != null)
                    {
                        double val = gcpE["primaryValue"]?.Value<double>() ?? 0;
                        double cp = pCurr.Equals("divine", StringComparison.OrdinalIgnoreCase) ? val * divChaos : val;
                        if (cp > 0.05 && cp < 100) { _currentGcp = Math.Round(cp, 2); _gcpSource = "Faustus"; }
                    }
                }
                catch { _currentGcp = Settings.ManualGcpPrice.Value; _gcpSource = "Fallback"; }
            }

            var gLines = JObject.Parse(await Http.GetStringAsync($"https://poe.ninja/poe1/api/economy/stash/current/item/overview?league={_league}&type=SkillGem"))["lines"] as JArray;
            if (gLines == null) return;

            var histPath = Path.Combine(DirectoryFullName, "gem_history.json");
            var hist = File.Exists(histPath) ? (JsonConvert.DeserializeObject<Dictionary<string, HistEntry>>(File.ReadAllText(histPath)) ?? new Dictionary<string, HistEntry>()) : new Dictionary<string, HistEntry>();
            var newHist = new Dictionary<string, HistEntry>();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var gems = new Dictionary<(string, int, int), JToken>();
            foreach (var it in gLines)
            {
                if (it["corrupted"]?.Value<bool>() == true) continue;
                var key = (it["name"]?.ToString() ?? "", it["gemLevel"]?.Value<int>() ?? 1, it["gemQuality"]?.Value<int>() ?? 0);
                int lots = it["listingCount"]?.Value<int>() ?? 0;
                if (!gems.TryGetValue(key, out var ex) || lots > (ex["listingCount"]?.Value<int>() ?? 0)) gems[key] = it;
            }

            int sockets = Settings.SocketsCount.Value;
            var bestMap = new Dictionary<string, GemRow>();

            foreach (var name in gems.Keys.Select(k => k.Item1).Distinct())
            {
                var scens = Exceptional.Contains(name)
                    ? new[] { ("1/0 -> 3/0", 1, 0, 3, 0, false), ("1/20 -> 3/20", 1, 20, 3, 20, false), ("1/0+GCP -> 3/20", 1, 0, 3, 20, true) }
                    : (name.StartsWith("Awakened ")
                        ? new[] { ("1/0 -> 5/0", 1, 0, 5, 0, false), ("1/20 -> 5/20", 1, 20, 5, 20, false) }
                        : new[] { ("1/0 -> 20/0", 1, 0, 20, 0, false), ("1/20 -> 20/20", 1, 20, 20, 20, false), ("1/0+GCP -> 20/20", 1, 0, 20, 20, true) });

                foreach (var (lbl, bL, bQ, sL, sQ, useGcp) in scens)
                {
                    if (!gems.TryGetValue((name, bL, bQ), out var bIt) || !gems.TryGetValue((name, sL, sQ), out var sIt)) continue;
                    int sLots = sIt["listingCount"]?.Value<int>() ?? 0;
                    int bLots = bIt["listingCount"]?.Value<int>() ?? 0;
                    if (sLots < Settings.MinListingsSell.Value || bLots < Settings.MinListingsBuy.Value) continue;

                    double runs = CalculateExactRuns(name, bQ, useGcp);
                    double buy = bIt["chaosValue"]?.Value<double>() ?? 0;
                    double gcp20 = useGcp ? 20.0 * _currentGcp : 0.0;
                    double sell = sIt["chaosValue"]?.Value<double>() ?? 0;
                    double profit = sell - buy - gcp20;
                    double perRun = profit / runs;
                    if (perRun < 0.2) continue;

                    double trend = (sIt["sparkLine"] ?? sIt["sparkline"])?["totalChange"]?.Value<double>() ?? 0.0;
                    string hKey = $"{name}_{sL}_{sQ}";
                    newHist[hKey] = new HistEntry { listings = sLots, price = sell, ts = now };

                    string delta = "new";
                    bool hidden = false;
                    if (hist.TryGetValue(hKey, out var oldH))
                    {
                        int diff = sLots - oldH.listings;
                        delta = diff >= 0 ? $"+{diff}" : $"{diff}";
                        if (diff < 0 && trend >= 5) hidden = true;
                    }
                    else if (trend >= 20 && sLots <= 80) hidden = true;

                    double sc = perRun * (sLots < sockets ? 0.65 : Math.Min(1.35, 0.85 + 0.25 * Math.Log10(sLots)));
                    if (bLots < sockets) sc *= 0.85;
                    sc *= 1.0 + Math.Max(-40.0, Math.Min(50.0, trend)) / 100.0;
                    if (hidden) sc *= 1.25;
                    if (useGcp) sc *= 1.08;

                    var row = new GemRow
                    {
                        Name = name, Type = lbl, Buy = buy, Gcp20 = gcp20, Sell = sell, Profit = profit,
                        PerRun = perRun, BatchRun = perRun * sockets, BatchProfit = profit * sockets,
                        BatchCost = (buy + gcp20) * sockets, Runs = runs, SLots = sLots, BLots = bLots,
                        LotsStr = $"{sLots} ({delta})", TrendStr = $"{(trend >= 0 ? "+" : "")}{trend:F1}%",
                        TrendVal = trend, Tag = hidden ? "HIDDEN" : (trend >= 10 ? "UP" : (trend <= -15 ? "DOWN" : "")), Score = sc
                    };

                    if (!bestMap.TryGetValue(name, out var cur) || row.Score > cur.Score) bestMap[name] = row;
                }
            }

            try { File.WriteAllText(histPath, JsonConvert.SerializeObject(newHist, Formatting.Indented)); } catch { }
            _gemRows = bestMap.Values.OrderByDescending(x => x.PerRun).ToList();
            _bestGem = _gemRows.OrderByDescending(x => x.Score).FirstOrDefault();
        }
    }
}