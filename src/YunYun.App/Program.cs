namespace YunYun.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var single = new Mutex(true, "YunYunDesktop.SingleInstance", out bool first);
        if (!first) return;                         // already running

        ApplicationConfiguration.Initialize();      // DPI mode etc. from the .csproj
        Application.Run(new AppController());
    }
}
