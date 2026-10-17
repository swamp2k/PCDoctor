using Avalonia.Controls;
using PCDoctor.Core;

namespace PCDoctor.App;

/// <summary>Shows the full command and folder; the command runs only on "Kør kommandoen" (Run the command).</summary>
public partial class ApprovalWindow : Window
{
    public ApprovalWindow() : this(new ShellRequest("", "", 0)) { }

    public ApprovalWindow(ShellRequest request)
    {
        InitializeComponent();
        CommandBox.Text = request.Command;
        DirText.Text = $"{L.T("Mappe", "Folder", "Mapp", "Mappe", "Ordner")}: {request.WorkingDirectory}    " +
                       $"{L.T("Tidsgrænse", "Time limit", "Tidsgräns", "Tidsgrense", "Zeitlimit")}: {request.TimeoutSeconds} {L.T("sek.", "sec.", "sek.", "sek.", "Sek.")}";
        ApproveButton.Click += (_, _) => Close(true);
        DenyButton.Click += (_, _) => Close(false);
        // Closing with the X button returns false, so the command does not run.
        // Closing the window with X counts as "no".
    }
}
