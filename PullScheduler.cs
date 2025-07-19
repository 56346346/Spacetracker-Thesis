using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System;

namespace SpaceTracker;

public class PullScheduler : IDisposable
{
    private readonly ExternalEvent _pullEvent;
    private DateTime _lastPull = DateTime.Now;
    private readonly UIApplication _uiApp;


    public PullScheduler(ExternalEvent pullEvent, UIApplication uiApp)
    {
        _pullEvent = pullEvent;
        _uiApp = uiApp;
        _uiApp.Idling += new EventHandler<IdlingEventArgs>(OnIdling);
    }

    private void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
    {
        if ((DateTime.Now - _lastPull).TotalMilliseconds >= 1000)
        {
            _pullEvent.Raise();
            _lastPull = DateTime.Now;
        }
    }

    public void Dispose()
    {
        _uiApp.Idling -= new EventHandler<IdlingEventArgs>(OnIdling);
    }
}