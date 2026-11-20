// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using CommunityToolkit.Mvvm.ComponentModel;
using JPSoftworks.Regedit.Core.Registry;
using JPSoftworks.Regedit.Core.Watching;
using JPSoftworks.Regedit.Core.Workspaces;
using Microsoft.Win32;

namespace JPSoftworks.Regedit.ViewModels.Tools;

internal sealed partial class RegistryTargetViewModel(bool watch) : ObservableObject
{
    public bool IsWatch { get; } = watch;

    [ObservableProperty]
    public partial string Address { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int ViewIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowValueName))]
    public partial bool WatchValue { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowValueName))]
    public partial bool DefaultValue { get; set; }

    [ObservableProperty]
    public partial string ValueName { get; set; } = string.Empty;

    public bool ShowValueName => this.WatchValue && !this.DefaultValue;

    internal RegistryLocation Location()
    {
        if (string.IsNullOrWhiteSpace(this.Address))
        {
            throw new ArgumentException("Enter a registry key path.");
        }

        if (this.ViewIndex is not (0 or 1))
        {
            throw new ArgumentException("Choose a registry view.");
        }

        var location = RegistryLocation.Parse(this.Address.Trim(), this.ViewIndex == 1 ? RegistryView.Registry32 : RegistryView.Registry64);
        RegistryWorkspace.ValidateLocation(location);
        if (this.IsWatch && location.Machine is not null)
        {
            throw new ArgumentException("Live watches support local registry keys only.");
        }

        return location;
    }

    internal WatchDefinition Watch()
    {
        var location = this.Location();
        if (this.WatchValue && !this.DefaultValue && this.ValueName.Length == 0)
        {
            throw new ArgumentException("Enter a value name or choose the (Default) value.");
        }

        var name = this.WatchValue ? this.DefaultValue ? string.Empty : this.ValueName : null;
        if (name is not null)
        {
            RegistryService.ValidateValueName(name);
        }

        return new WatchDefinition(location, name);
    }
}
