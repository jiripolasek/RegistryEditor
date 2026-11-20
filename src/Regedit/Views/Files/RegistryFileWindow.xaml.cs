// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using System.ComponentModel;
using JPSoftworks.Regedit.Dialogs;
using JPSoftworks.Regedit.Models.Settings;
using JPSoftworks.Regedit.Persistence;
using JPSoftworks.Regedit.ViewModels.Files;
using JPSoftworks.Regedit.Views.Shell;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace JPSoftworks.Regedit.Views.Files;

internal sealed partial class RegistryFileWindow : SecondaryWindow
{
    internal RegistryFilePreviewViewModel ViewModel { get; }

    internal RegistryFileWindow(
        AppWindow owner,
        ElementTheme theme,
        AppSettings settings,
        double scale,
        Func<IRegistryFileDialogs, RegistryFilePreviewViewModel> createViewModel)
    {
        this.InitializeComponent();
        this.ViewModel = createViewModel(new RegistryFileDialogs(this.Handle, this.Preview));
        this.InitializeWindow(
            this.WindowTitleBar, owner, theme, settings.Backdrop, scale, new SizeInt32(1200, 780), new SizeInt32(720, 520),
            error => this.ViewModel.ReportFailure(error));
        this.Preview.Initialize(this.ViewModel, settings.EditorFont);
        this.Activated += (_, args) => this.Preview.NotificationHost.IsWindowActive
            = args.WindowActivationState != WindowActivationState.Deactivated;
        this.ViewModel.PropertyChanged += this.Model_Changed;
        this.ViewModel.CloseRequested += this.RequestClose;
        this.ViewModel.ActivationRequested += this.Activate;
        this.Closed += (_, _) =>
        {
            this.ViewModel.PropertyChanged -= this.Model_Changed;
            this.ViewModel.CloseRequested -= this.RequestClose;
            this.ViewModel.ActivationRequested -= this.Activate;
            this.Preview.Detach();
        };
        this.UpdateTitle();
    }

    private void Model_Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(RegistryFilePreviewViewModel.Title))
        {
            this.UpdateTitle();
        }
    }

    private void UpdateTitle()
    {
        this.WindowTitleBar.Title = this.ViewModel.Title;
        this.Title = "Registry file preview · " + this.ViewModel.Title;
    }

    internal void ApplyEditorFont(RegistryFont font)
    {
        this.Preview.ApplyEditorFont(font);
    }

    internal async Task OpenAsync(string file)
    {
        this.Activate();
        await this.ViewModel.OpenAsync(file);
    }

    protected override Task<bool> ConfirmCloseAsync()
    {
        return this.ViewModel.TryCloseAsync();
    }
}
