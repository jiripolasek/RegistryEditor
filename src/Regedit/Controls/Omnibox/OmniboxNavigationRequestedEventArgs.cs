// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

namespace JPSoftworks.Regedit.Controls.Omnibox;

public sealed class OmniboxNavigationRequestedEventArgs : EventArgs
{
    public string Address { get; }

    public OmniboxNavigationRequestedEventArgs(string address)
    {
        this.Address = address;
    }
}
