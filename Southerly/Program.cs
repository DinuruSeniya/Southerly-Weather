// Copyright (C) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using WidgetHelper;

namespace Southerly;

public static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint hWnd, string text, string caption, uint type);

    [MTAThread]
    private static void Main(string[] args)
    {
        // Windows starts this exe with the flag to serve the widget; anyone opening it
        // from the Start menu just gets told where the widget is.
        if (args.Length == 0 || args[0] != "-RegisterProcessAsComServer")
        {
            MessageBox(0, "Southerly is a widget. Open the Widgets board (Win + W), choose + and add Southerly Weather.",
                "Southerly", 0x40);
            return;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        using var manager = RegistrationManager<WidgetProvider>.RegisterProvider();
        using var disposed = manager.GetDisposedEvent();
        disposed.WaitOne();   // until the host has released the last widget
    }
}
