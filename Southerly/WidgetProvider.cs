// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Windows.Widgets.Providers;
using Southerly;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

[ComVisible(true)]
[ComDefaultInterface(typeof(IWidgetProvider))]
[Guid("57FEDA4C-9AD1-4DFC-A4FB-990B59404DED")]
public sealed class WidgetProvider : IWidgetProvider, IWidgetProvider2
{
    private static readonly ConcurrentDictionary<string, WeatherWidget> Widgets = new();

    // After a restart the host still has our widgets pinned, so unknown ids are
    // rebuilt from the state the host stored for them.
    private static WeatherWidget Widget(string id) => Widgets.GetOrAdd(id, i =>
        new WeatherWidget(i, WidgetManager.GetDefault().GetWidgetInfo(i)?.CustomState ?? ""));

    public void CreateWidget(WidgetContext context) => Widget(context.Id).Push();

    public void DeleteWidget(string widgetId, string customState)
    {
        if (Widgets.TryRemove(widgetId, out var widget))
            widget.Deactivate();
    }

    public void OnActionInvoked(WidgetActionInvokedArgs args) => Widget(args.WidgetContext.Id).OnAction(args);

    // The "Customize widget" item in the widget's ⋯ menu.
    public void OnCustomizationRequested(WidgetCustomizationRequestedArgs args) => Widget(args.WidgetContext.Id).Customize();

    public void Activate(WidgetContext context) => Widget(context.Id).Activate();

    public void Deactivate(string widgetId) => Widget(widgetId).Deactivate();

    // Sizes are handled by $host.widgetSize in the template, so there is nothing to do.
    public void OnWidgetContextChanged(WidgetContextChangedArgs args) { }
}
