// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.Windows.Input;
using JPSoftworks.Regedit.Core.Watching;
using JPSoftworks.Regedit.ViewModels.Tools;
using Microsoft.UI.Xaml.Controls;

namespace JPSoftworks.Regedit.Dialogs;

public sealed partial class WatchProposalView : UserControl
{
    internal WatchProposalView(WatchDefinition proposal, ICommand start, Action close)
    {
        this.InitializeComponent();
        this.TargetText.Text = WatchSurfaceViewModel.FormatTarget(proposal.Location, proposal.ValueName);
        this.StartButton.Command = start;
        this.StartButton.CommandParameter = proposal;
        this.StartButton.Click += (_, _) => close();
    }
}
