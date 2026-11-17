// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Models.Registry;

namespace JPSoftworks.Regedit.Models.Files;

public sealed partial record RegistryFilePreviewRow(
    string Operation,
    string Name,
    string Type,
    string Data,
    int Line,
    RegistryEntry Entry)
{
    public bool IsDeletion => this.Operation.StartsWith("Delete", StringComparison.Ordinal);

    public string Glyph => this.Operation == "Delete key" ? "\uE65B" : this.IsDeletion ? "\uE74D" : this.Entry.Glyph;

    public string DeletionFontFamily => this.Operation == "Delete key"
        ? "/Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular"
        : "Segoe Fluent Icons";

    public bool HasNavigationIcon => !this.IsDeletion && this.Entry.HasNavigationIcon;

    public bool HasStringIcon => !this.IsDeletion && this.Entry.HasStringIcon;

    public bool HasLinkIcon => !this.IsDeletion && this.Entry.HasLinkIcon;

    public bool HasBinaryIcon => !this.IsDeletion && this.Entry.HasBinaryIcon;

    public bool HasDataIcon => !this.IsDeletion && this.Entry.HasDataIcon;

    public string Summary => string.Join(
        " · ", new[] { this.Operation, this.Type, this.Data }.Where(text => text.Length > 0));

    public string AccessibleName => $"{this.Operation}, {this.Name}, {this.Type}, {this.Data}, line {this.Line}";

    public override string ToString()
    {
        return this.AccessibleName;
    }
}
