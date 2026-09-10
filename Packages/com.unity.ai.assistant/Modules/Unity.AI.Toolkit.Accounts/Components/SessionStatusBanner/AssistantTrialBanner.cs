using Unity.AI.Toolkit.Accounts.Services.Core;
using UnityEngine.UIElements;

namespace Unity.AI.Toolkit.Accounts.Components
{
    [UxmlElement]
    partial class AssistantTrialBanner : BasicBannerContent
    {
        public AssistantTrialBanner()
            : base(
                "Build faster with Unity's AI tools. Automate setup, troubleshooting, and stay focused on your game.",
                "Start the 14-day trial", AccountLinks.StartTrial) {}
    }
}
