// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace JPSoftworks.Regedit.Controls.Shared;

/// <summary>
/// Command icon that shows the accent color while its host control is enabled and falls back to the
/// host's native foreground (disabled, compact, and high contrast states) otherwise.
/// </summary>
public sealed partial class CommandIcon : IconSourceElement
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol), typeof(Symbol), typeof(CommandIcon),
        new PropertyMetadata(default(Symbol), (d, _) => ((CommandIcon)d).UpdateSymbol()));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(CommandIcon), new PropertyMetadata(null, (d, _) => ((CommandIcon)d).UpdateGlyph()));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(bool), typeof(CommandIcon), new PropertyMetadata(true, (d, _) => ((CommandIcon)d).UpdateAppearance()));

    private static readonly AccessibilitySettings Accessibility = new();

    // HighContrastChanged is unavailable to desktop apps; contrast switches raise ColorValuesChanged.
    private static readonly UISettings Colors = new();

    private Control? _host;
    private bool _isCompact;

    /// <summary>Symbol rendered with its current Segoe Fluent Icons glyph.</summary>
    public Symbol Symbol
    {
        get => (Symbol)this.GetValue(SymbolProperty);
        set => this.SetValue(SymbolProperty, value);
    }

    /// <summary>Icon font glyph for icons without a <see cref="Microsoft.UI.Xaml.Controls.Symbol"/>.</summary>
    public string? Glyph
    {
        get => (string?)this.GetValue(GlyphProperty);
        set => this.SetValue(GlyphProperty, value);
    }

    /// <summary>Whether the icon uses the accent color when its host is enabled.</summary>
    public bool Accent
    {
        get => (bool)this.GetValue(AccentProperty);
        set => this.SetValue(AccentProperty, value);
    }

    /// <summary>Set by the owning command bar when it shows icons only.</summary>
    internal bool IsCompact
    {
        get => this._isCompact;
        set
        {
            if (this._isCompact != value)
            {
                this._isCompact = value;
                this.UpdateAppearance();
            }
        }
    }

    public CommandIcon()
    {
        this.Loaded += (_, _) => this.Attach();
        this.Unloaded += (_, _) => this.Detach();
    }

    internal void SetHost(Control host)
    {
        if (ReferenceEquals(this._host, host))
        {
            return;
        }

        this.Detach();
        this._host = host;
        if (this.IsLoaded)
        {
            this.Attach();
        }
        else
        {
            this.UpdateAppearance();
        }
    }

    private void UpdateSymbol()
    {
        // Symbol values are legacy Segoe MDL2 code points that overflow the icon box in Segoe Fluent Icons.
        this.IconSource = new FontIconSource { Glyph = SymbolGlyphs.Get(this.Symbol) };
    }

    private void UpdateGlyph()
    {
        this.IconSource = new FontIconSource { Glyph = this.Glyph ?? string.Empty };
    }

    private void Attach()
    {
        this.Detach();
        if (this._host is not null)
        {
            this._host.IsEnabledChanged += this.Host_IsEnabledChanged;
        }

        Colors.ColorValuesChanged += this.ColorValuesChanged;
        this.UpdateAppearance();
    }

    private void Detach()
    {
        Colors.ColorValuesChanged -= this.ColorValuesChanged;
        if (this._host is not null)
        {
            this._host.IsEnabledChanged -= this.Host_IsEnabledChanged;
        }
    }

    private void Host_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        this.UpdateAppearance();
    }

    private void ColorValuesChanged(UISettings sender, object args)
    {
        this.DispatcherQueue.TryEnqueue(this.UpdateAppearance);
    }

    private void UpdateAppearance()
    {
        // Without a local style the icon inherits the host's foreground, so native disabled,
        // pointer-over, and high contrast states keep working.
        var accent = this.Accent && !this._isCompact && (this._host?.IsEnabled ?? true) && !Accessibility.HighContrast;
        this.Style = accent ? (Style)Application.Current.Resources["CommandIconAccentStyle"] : null;
    }
}
