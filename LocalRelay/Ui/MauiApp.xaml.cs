#if WINDOWS 
namespace OpenShock.LocalRelay.Ui;

public partial class MauiApp
{
    public MauiApp()
    {
        InitializeComponent();
        MainPage = new MainPage();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = base.CreateWindow(activationState);
        window.Title = "LocalRelay";
        window.MinimumHeight = 600;
        window.MinimumWidth = 1000;
        
        return window;
    }
}
#endif