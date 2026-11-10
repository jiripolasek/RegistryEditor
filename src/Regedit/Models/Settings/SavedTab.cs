// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Models.Settings;

public sealed record SavedTab(string Address, bool Is32Bit = false, BrowserLayout? Layout = null);
