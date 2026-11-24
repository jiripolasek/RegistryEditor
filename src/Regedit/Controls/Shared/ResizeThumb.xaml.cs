// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace JPSoftworks.Regedit.Controls.Shared;

public sealed partial class ResizeThumb : UserControl
{
    public event DragDeltaEventHandler DragDelta
    {
        add => this.Grip.DragDelta += value;
        remove => this.Grip.DragDelta -= value;
    }

    public event DragCompletedEventHandler DragCompleted
    {
        add => this.Grip.DragCompleted += value;
        remove => this.Grip.DragCompleted -= value;
    }

    private Orientation _direction;

    public Orientation ResizeDirection
    {
        get => this._direction;
        set
        {
            this._direction = value;
            this.ProtectedCursor = InputSystemCursor.Create(
                value == Orientation.Vertical ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
        }
    }

    public ResizeThumb()
    {
        this.InitializeComponent();
        this.ResizeDirection = Orientation.Horizontal;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        this.Loaded += (_, _) =>
        {
            this.IsTabStop = false;
            AutomationProperties.SetAutomationId(this.Grip, AutomationProperties.GetAutomationId(this));
            AutomationProperties.SetName(this.Grip, AutomationProperties.GetName(this));
        };
    }
}
