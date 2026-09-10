using Unity.AI.Toolkit.Accounts.Services.Core;
using UnityEngine.UIElements;

namespace Unity.AI.Toolkit.Accounts.Components
{
    [UxmlElement]
    partial class NoSubscriptionBanner : BasicBannerContent
    {
        public NoSubscriptionBanner() : base(
            "Your current plan does not include access to AI features. A Unity Pro, Enterprise, or Industry licence, or a Monthly Credit bundle seat, is required.",
            "View Plans", AccountLinks.ViewBundles
        ) { }
    }
}
