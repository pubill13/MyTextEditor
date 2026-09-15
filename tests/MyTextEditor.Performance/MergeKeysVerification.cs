using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MyTextEditor.Controls;
using MyTextEditor.Core.Models;
using MyTextEditor.Diff;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

internal static class MergeKeysVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Verify()
    {
        using var general = new ScintillaEditorHost();
        var requested = new List<EditorShortcut>();
        general.ShortcutRequested += (_, e) => requested.Add(e.Shortcut);
        Check(!SendNative(general, Forms.Keys.F7) && !SendNative(general, Forms.Keys.F8), "General editor consumed F7/F8 without a handler accepting them.");
        Check(requested.SequenceEqual(new[] { EditorShortcut.DiffPrevious, EditorShortcut.DiffNext }), "F7/F8 mapping is incorrect.");
        requested.Clear();
        Check(!SendNative(general, Forms.Keys.Control | Forms.Keys.F7) && requested.Count == 0, "Modified F7 was unexpectedly mapped.");

        var callbacks = new DiffWindowCallbacks
        {
            GetSourceRevision = _ => -1, GetSourceSnapshot = _ => null,
            ApplyToSourceAsync = (_, _, _, _) => Task.FromResult(false),
            SaveSourceAsync = _ => Task.FromResult(false), CreateDocumentAsync = (_, _) => Task.CompletedTask,
            SettingsChanged = _ => { }
        };
        var workspace = new DiffWorkspaceWindow(new DiffWindowOptions(), callbacks, new DiffAppearance("Consolas", 11, false)) { ShowInTaskbar = false };
        var view = workspace.OpenComparison(Endpoint("left", "a\nold1\nb\nc\nold2\nd\ne\nold3"), Endpoint("right", "a\nnew1\nb\nc\nnew2\nd\ne\nnew3"));
        workspace.Show();
        try
        {
            PumpUntil(() => view.DifferenceCount == 3);
            var left = Field<ScintillaEditorHost>(view, "_leftEditor");
            var right = Field<ScintillaEditorHost>(view, "_rightEditor");
            var initial = Index(view);
            Check(SendNative(left, Forms.Keys.F8) && Index(view) == (initial + 1) % 3, "Left F8 must move exactly one block.");
            Check(SendNative(right, Forms.Keys.F7) && Index(view) == initial, "Right F7 must move exactly one block.");
            SendWpf(workspace, Key.F8);
            Check(Index(view) == (initial + 1) % 3, "Workspace F8 must move exactly one block.");
            SendWpf(workspace, Key.F7);
            Check(Index(view) == initial, "Workspace F7 must move exactly one block.");
            Check(SendNative(left, Forms.Keys.Alt | Forms.Keys.Down) && Index(view) == (initial + 1) % 3, "Alt+Down regression.");
            Check(SendNative(right, Forms.Keys.Alt | Forms.Keys.Up) && Index(view) == initial, "Alt+Up regression.");
            view.SetActive(false);
            Check(!SendNative(left, Forms.Keys.F8) && Index(view) == initial, "Inactive tab navigated.");
            view.SetActive(true);
            typeof(DiffTabView).GetMethod("InvalidateDisplayedResult", Private)!.Invoke(view, null);
            SendNative(right, Forms.Keys.F8);
            SendWpf(workspace, Key.F7);
            Check(Index(view) == -1 && !Field<Button>(view, "PreviousButton").IsEnabled && !Field<Button>(view, "NextButton").IsEnabled, "Stale result navigation was not disabled.");
            var equal = workspace.OpenComparison(Endpoint("same1", "same"), Endpoint("same2", "same"));
            PumpUntil(() => Field<object?>(equal, "_result") is not null);
            SendWpf(workspace, Key.F8);
            Check(Index(equal) == -1 && !Field<Button>(equal, "NextButton").IsEnabled, "Equal documents allow difference navigation.");
        }
        finally { workspace.Close(); general.ReleaseResources(); }
        Console.WriteLine("PASS F7/F8 native + WPF, Alt navigation, inactive/stale/equal guards and general editor pass-through");
    }

    private static DiffEndpoint Endpoint(string name, string text) => new() { Kind = DiffEndpointKind.Clipboard, DisplayName = name, Text = text, NewLine = "\n", IsReadOnly = false };
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static int Index(DiffTabView view) => Field<int>(view, "_currentBlockIndex");
    private static bool SendNative(ScintillaEditorHost host, Forms.Keys keys) => (bool)typeof(ScintillaEditorHost).GetMethod("ProcessShortcut", Private)!.Invoke(host, [keys])!;
    private static void SendWpf(DiffWorkspaceWindow workspace, Key key)
    {
        var e = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(workspace)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        typeof(DiffWorkspaceWindow).GetMethod("Window_PreviewKeyDown", Private)!.Invoke(workspace, [workspace, e]);
        Check(e.Handled, "Workspace did not consume navigation key.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void PumpUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Diff verification timed out.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }
}
