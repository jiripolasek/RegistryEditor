// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.ViewModels.Browser;

namespace JPSoftworks.Regedit.ViewModels.Shell;

internal sealed class BrowserTab : WindowTab
{
    private readonly Dictionary<string, IDisposable> _connections = new(StringComparer.OrdinalIgnoreCase);

    internal BrowserViewModel Browser { get; }

    internal string? InitialValue { get; }

    public override string Title => this.Browser.Title;

    public override string Tooltip =>
        this.Browser.Location.Address + (this.Browser.Is32Bit ? " · 32-bit registry" : string.Empty);

    internal BrowserTab(BrowserViewModel browser, string? selectedValue)
        : base(string.Empty, "\uE8B7")
    {
        this.Browser = browser;
        this.InitialValue = selectedValue;
        browser.Navigated += this.BrowserNavigated;
        this.BrowserNavigated(browser, EventArgs.Empty);
    }

    private void BrowserNavigated(object? sender, EventArgs args)
    {
        var computer = this.Browser.Location.Machine;
        if (computer is not null && !this._connections.ContainsKey(computer))
        {
            this._connections.Add(computer, RemoteRegistryCredentials.Retain(computer));
        }

        this.OnPropertyChanged(nameof(this.Title));
        this.OnPropertyChanged(nameof(this.Tooltip));
    }

    internal void Close()
    {
        this.Browser.Navigated -= this.BrowserNavigated;
        foreach (var connection in this._connections.Values)
        {
            connection.Dispose();
        }

        this._connections.Clear();
    }
}
