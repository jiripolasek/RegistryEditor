// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class NameForm : UserControl
{
    public string Header { get; }

    public string Value { get; set; }

    public string? Context { get; }

    public string? Hint { get; }

    public string Placeholder { get; }

    public bool HasContext => this.Context is not null;

    public bool HasHint => this.Hint is not null;

    internal TextBox Input => this.NameInput;

    internal NameForm(string header, string value = "", string? context = null, string? hint = null, string placeholder = "")
    {
        this.Header = header;
        this.Value = value;
        this.Context = context;
        this.Hint = hint;
        this.Placeholder = placeholder;
        this.InitializeComponent();
    }
}
