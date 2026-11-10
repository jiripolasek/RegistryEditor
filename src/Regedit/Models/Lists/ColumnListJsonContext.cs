// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Text.Json.Serialization;

namespace JPSoftworks.Regedit.Models.Lists;

/// <summary>Trim- and AOT-safe serialization for saved column layouts.</summary>
[JsonSerializable(typeof(ColumnLayout))]
[JsonSerializable(typeof(Dictionary<string, ColumnLayout>))]
public sealed partial class ColumnListJsonContext : JsonSerializerContext;
