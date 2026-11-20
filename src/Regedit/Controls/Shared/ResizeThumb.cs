// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace JPSoftworks.Regedit.Controls.Shared;

public sealed partial class ResizeThumb : UserControl
{
    public event DragDeltaEventHandler DragDelta
    {
        add => this._thumb.DragDelta += value;
        remove => this._thumb.DragDelta -= value;
    }

    public event DragCompletedEventHandler DragCompleted
    {
        add => this._thumb.DragCompleted += value;
        remove => this._thumb.DragCompleted -= value;
    }

    private readonly Thumb _thumb;

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
        this._thumb = new Thumb
        {
            Style = (Style)Application.Current.Resources["PanelResizeThumbStyle"],
            IsTabStop = true,
            UseSystemFocusVisuals = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        this.Content = this._thumb;
        this.ResizeDirection = Orientation.Horizontal;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        this.Loaded += (_, _) =>
        {
            this.IsTabStop = false;
            AutomationProperties.SetAutomationId(this._thumb, AutomationProperties.GetAutomationId(this));
            AutomationProperties.SetName(this._thumb, AutomationProperties.GetName(this));
        };
    }
}
