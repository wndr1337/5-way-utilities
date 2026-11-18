using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace FiveWayUtilities
{
    public class FiveWayUtilitiesSettings : ISettings
    {
        public ToggleNode Enable { get; set; } = new ToggleNode(true);

        // === Feature Toggles (Модули) ===
        public ToggleNode EnableSessionAndKills { get; set; } = new ToggleNode(true);
        public ToggleNode ShowCombatHudOverlay { get; set; } = new ToggleNode(true);
        public ToggleNode CountOnlyIn5Way { get; set; } = new ToggleNode(true);
        public RangeNode<int> HudPosX { get; set; } = new RangeNode<int>(20, 0, 2500);
        public RangeNode<int> HudPosY { get; set; } = new RangeNode<int>(160, 0, 1400);

        // === Timeless Evaluator ===
        public ToggleNode EnableTimelessEval { get; set; } = new ToggleNode(true);
        public ToggleNode ShowTrash5To1 { get; set; } = new ToggleNode(true);
        public ToggleNode EvalInInventory { get; set; } = new ToggleNode(true);
        public ToggleNode EvalInStash { get; set; } = new ToggleNode(true);
        public RangeNode<int> FrameWidth { get; set; } = new RangeNode<int>(2, 1, 5);

        // === Gem Profit Tracker ===
        public ToggleNode EnableGemTracker { get; set; } = new ToggleNode(true);
        public RangeNode<int> RefreshMinutes { get; set; } = new RangeNode<int>(5, 1, 60);
        public ToggleNode UseManualGcp { get; set; } = new ToggleNode(false);
        public RangeNode<float> ManualGcpPrice { get; set; } = new RangeNode<float>(2.5f, 0.1f, 30f);
        public RangeNode<int> SocketsCount { get; set; } = new RangeNode<int>(18, 1, 36);
        public RangeNode<float> BaseGemXpPerRunMillions { get; set; } = new RangeNode<float>(28.5f, 5.0f, 80.0f);
        public RangeNode<int> OffhandExtraQuality { get; set; } = new RangeNode<int>(0, 0, 30);
        public RangeNode<int> MinListingsSell { get; set; } = new RangeNode<int>(10, 1, 100);
        public RangeNode<int> MinListingsBuy { get; set; } = new RangeNode<int>(3, 1, 50);
    }
}