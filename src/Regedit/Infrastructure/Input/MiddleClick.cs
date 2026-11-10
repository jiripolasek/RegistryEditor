// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace JPSoftworks.Regedit.Infrastructure.Input;

internal static class MiddleClick
{
    public static void Attach<T>(FrameworkElement owner, Func<PointerRoutedEventArgs, T?> target, Action<T> open)
        where T : class
    {
        var gesture = new MiddleClickGesture();

        T? Target(PointerRoutedEventArgs args)
        {
            return owner is Control { IsEnabled: false } || !Contains(owner, args) ? null : target(args);
        }

        owner.AddHandler(
            UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
            {
                if (args.GetCurrentPoint(owner).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonPressed)
                {
                    return;
                }

                var item = Target(args);
                gesture.Press(args.Pointer.PointerId, item);
                if (item is not null)
                {
                    args.Handled = true;
                }
            }), true);
        owner.AddHandler(
            UIElement.PointerReleasedEvent, new PointerEventHandler((_, args) =>
            {
                if (args.GetCurrentPoint(owner).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonReleased)
                {
                    return;
                }

                var item = Target(args);
                if (!gesture.Release(args.Pointer.PointerId, item))
                {
                    return;
                }

                args.Handled = true;
                open(item!);
            }), true);
        owner.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => gesture.Cancel()), true);
        owner.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => gesture.Cancel()), true);
        owner.Unloaded += (_, _) => gesture.Cancel();
    }

    public static T? Container<T>(FrameworkElement owner, PointerRoutedEventArgs args)
        where T : FrameworkElement
    {
        for (var source = args.OriginalSource as DependencyObject;
             source is not null && source != owner;
             source = VisualTreeHelper.GetParent(source))
        {
            // The tree chevron owns its expansion gesture.
            if (source is FrameworkElement { Name: "ExpandCollapseChevron" })
            {
                return null;
            }

            if (source is T container)
            {
                return Contains(container, args) ? container : null;
            }
        }

        return null;
    }

    private static bool Contains(FrameworkElement element, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(element).Position;
        return point.X >= 0 && point.Y >= 0 && point.X < element.ActualWidth && point.Y < element.ActualHeight;
    }
}
