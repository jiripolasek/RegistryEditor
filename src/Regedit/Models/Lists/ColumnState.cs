// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Models.Lists;

/// <summary>The user-controlled state of one column. A <see cref="Width" /> of 0 fills the remaining space.</summary>
public sealed record ColumnState(string Id, double Width, bool Visible);
