using Microsoft.Extensions.DependencyInjection;

namespace FacialCameraBroadcaster
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                System.Diagnostics.Debug.WriteLine($"UnobservedTaskException: {e.Exception}");
                e.SetObserved();
            };
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }
    }
}