using Autodesk.Revit.UI;
   using Autodesk.Revit.UI.Events;


namespace SpaceTracker;

public class PullScheduler
{
    private readonly ExternalEvent _pullEvent;
    private DateTime _lastPull = DateTime.Now;

    public PullScheduler(ExternalEvent pullEvent, UIApplication uiApp)
    {
        _pullEvent = pullEvent;
        uiApp.Idling += OnIdling;
    }

     private void OnIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
    {
        if ((DateTime.Now - _lastPull).TotalMilliseconds >= 1000)
        {
            _pullEvent.Raise();
            _lastPull = DateTime.Now;
        }
    }
}