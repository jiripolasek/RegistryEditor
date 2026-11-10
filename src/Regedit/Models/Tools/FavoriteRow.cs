// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Models.Tools;

public sealed record FavoriteRow(FavoriteItemId Key, string Title, string Path, string Subtitle)
{
    public string RegistryPath { get; init; } = Path;

    public string RegistryView { get; init; } = string.Empty;

    public IReadOnlyList<FavoriteRow> Children { get; init; } = [];

    public string Glyph => this.Key.IsFolder ? "\uE8B7" : "\uE734";

    public string AccessibleName => this.Key.IsFolder ? this.Title : $"{this.Title}, {this.Path}, {this.Subtitle}";

    public override string ToString()
    {
        return this.AccessibleName;
    }
}
