using System.Windows;

namespace ConnectionClue.App;

/// <summary>
/// Entry point. The single-instance check comes before anything of WPF starts: a launch that only hands over to the running
/// copy ends in a few milliseconds, instead of first building the whole application (resources, theme, window system).
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        SingleInstanceGate? singleInstance = null;
        // A visual check (--snapshot) only renders the window to a file and exits: it never hands off to a running copy.
        if (App.Arg(args, "--snapshot") is null)
        {
            singleInstance = SingleInstanceGate.Acquire(args.Contains("--start"), out bool activatedExisting);
            if (singleInstance is null)
            {
                // A copy started at sign-in must never put a message in front of the user.
                if (!activatedExisting && !App.StartedAtSignIn(args))
                    MessageBox.Show("ConnectionClue is already running, but Windows could not activate its window. Use its notification-area icon to open it.",
                        "ConnectionClue", MessageBoxButton.OK, MessageBoxImage.Information);
                return 0;
            }
        }

        var app = new App(singleInstance);
        app.InitializeComponent();
        return app.Run();
    }
}
