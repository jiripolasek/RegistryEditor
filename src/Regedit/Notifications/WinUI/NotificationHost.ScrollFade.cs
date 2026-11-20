// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace JPSoftworks.Notifications.WinUI;

public sealed partial class NotificationHost
{
    private readonly List<CompositionObject> _scrollFadeResources = [];
    private readonly List<(Visual Visual, float Opacity)> _scrollFadeSources = [];
    private Grid? _scrollSurface;

    private void InitializeScrollFade()
    {
        this._scrollSurface = this.GetTemplateChild("PART_ScrollSurface") as Grid;
        if (this._scrollSurface is null || this.GetTemplateChild("PART_ScrollFadeMask") is not FrameworkElement mask)
        {
            return;
        }

        var compositor = ElementCompositionPreview.GetElementVisual(this._scrollSurface).Compositor;
        var brush = compositor.CreateMaskBrush();
        this._scrollFadeResources.Add(brush);
        brush.Source = this.CreateScrollFadeSource(this._cardsViewport);
        brush.Mask = this.CreateScrollFadeSource(mask);
        var visual = compositor.CreateSpriteVisual();
        this._scrollFadeResources.Add(visual);
        visual.Brush = brush;
        visual.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(this._scrollSurface, visual);
        this._cardsViewport.ViewChanged += this.ScrollFade_ViewChanged;
        this._cardsViewport.SizeChanged += this.ScrollFade_SizeChanged;
        this._cardsPanel.SizeChanged += this.ScrollFade_SizeChanged;
        this.UpdateScrollFade();
    }

    private CompositionSurfaceBrush CreateScrollFadeSource(UIElement element)
    {
        var source = ElementCompositionPreview.GetElementVisual(element);
        var compositor = source.Compositor;
        var surface = compositor.CreateVisualSurface();
        this._scrollFadeResources.Add(surface);
        surface.SourceVisual = source;
        using var size = compositor.CreateExpressionAnimation("source.Size");
        size.SetReferenceParameter("source", source);
        surface.StartAnimation(nameof(surface.SourceSize), size);
        var brush = compositor.CreateSurfaceBrush(surface);
        this._scrollFadeResources.Add(brush);
        this._scrollFadeSources.Add((source, source.Opacity));
        // The visual surface captures the source before its opacity, leaving input and focus on the real controls.
        source.Opacity = 0;
        return brush;
    }

    private void ScrollFade_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs args) =>
        this.UpdateScrollFade();

    private void ScrollFade_SizeChanged(object sender, SizeChangedEventArgs args) => this.UpdateScrollFade();

    private void UpdateScrollFade()
    {
        VisualStateManager.GoToState(
            this, this._cardsViewport.VerticalOffset > 1 ? "ScrollTopOverflow" : "ScrollTopClear", false);
        VisualStateManager.GoToState(
            this,
            this._cardsViewport.ScrollableHeight - this._cardsViewport.VerticalOffset > 1
                ? "ScrollBottomOverflow"
                : "ScrollBottomClear", false);
    }

    private void ClearScrollFade()
    {
        if (this._scrollFadeResources.Count == 0)
        {
            this._scrollSurface = null;
            return;
        }

        this._cardsViewport.ViewChanged -= this.ScrollFade_ViewChanged;
        this._cardsViewport.SizeChanged -= this.ScrollFade_SizeChanged;
        this._cardsPanel.SizeChanged -= this.ScrollFade_SizeChanged;
        ElementCompositionPreview.SetElementChildVisual(this._scrollSurface!, null);
        foreach (var (visual, opacity) in this._scrollFadeSources)
        {
            visual.Opacity = opacity;
        }

        foreach (var resource in this._scrollFadeResources.AsEnumerable().Reverse())
        {
            resource.Dispose();
        }

        this._scrollFadeSources.Clear();
        this._scrollFadeResources.Clear();
        this._scrollSurface = null;
    }
}
