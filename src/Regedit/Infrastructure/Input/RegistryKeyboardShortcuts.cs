// ------------------------------------------------------------
// Copyright (c) Jiri Polasek. All rights reserved.
// ------------------------------------------------------------

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace JPSoftworks.Regedit.Infrastructure.Input;

internal static class RegistryKeyboardShortcuts
{
    internal static bool IsPlainControl(XamlRoot root)
    {
        return (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0
               && ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
                    | InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift))
                   & CoreVirtualKeyStates.Down)
               == 0
               && FocusManager.GetFocusedElement(root) is not (TextBox or RichEditBox or PasswordBox);
    }
}
