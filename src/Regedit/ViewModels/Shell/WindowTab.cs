// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.ComponentModel;

namespace JPSoftworks.Regedit.ViewModels.Shell;

public abstract class WindowTab(string title, string glyph) : ObservableObject
{
    public virtual string Title => title;

    public virtual string Tooltip => this.Title;

    public virtual string HeaderTitle => this.Title;

    public virtual bool IsBusy => false;

    public string Glyph => glyph;

    public virtual string IconFontFamily => "Segoe Fluent Icons, Segoe MDL2 Assets";
}
