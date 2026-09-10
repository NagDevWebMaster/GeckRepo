using Unity.AI.Toolkit.Accounts.Services.Core;
using UnityEngine.UIElements;

namespace Unity.AI.Toolkit.Accounts.Components
{
    [UxmlElement]
    partial class AssistantInsufficientPointsBanner : BasicBannerContent
    {
        public AssistantInsufficientPointsBanner()
            : base(
                "No credits remaining. Credits refresh automatically at the start of the month, or you can purchase a top-up.",
                "Get More Credits", AccountLinks.GetMoreCredits) { }
    }
}
