// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml.Automation.Peers;

namespace JPSoftworks.Regedit.Controls.Shared;

internal sealed partial class AccessibleSegmented : Segmented
{
    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new SegmentedAutomationPeer(this);
    }

    private sealed partial class SegmentedAutomationPeer : ListViewBaseAutomationPeer
    {
        public SegmentedAutomationPeer(AccessibleSegmented owner)
            : base(owner)
        {
        }

        protected override string GetClassNameCore()
        {
            return nameof(Segmented);
        }

        protected override AutomationControlType GetAutomationControlTypeCore()
        {
            return AutomationControlType.List;
        }

        protected override ItemAutomationPeer OnCreateItemAutomationPeer(object item)
        {
            return new ListViewItemDataAutomationPeer(item, this);
        }
    }
}
