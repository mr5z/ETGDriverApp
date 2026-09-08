using Nkraft.MvvmEssentials.Services;

namespace ETGDriverApp;

public partial class App
{
    private readonly AppStartupWindowHook _hook;
    
    public App(AppStartupWindowHook hook)
    {
        _hook = hook;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        _hook.Attach();
        return base.CreateWindow(activationState);
    }
}