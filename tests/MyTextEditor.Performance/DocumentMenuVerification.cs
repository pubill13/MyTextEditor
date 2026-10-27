using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MyTextEditor;
using MyTextEditor.Core;
using MyTextEditor.Models;
using Application = System.Windows.Application;
using MenuItem = System.Windows.Controls.MenuItem;

internal static class DocumentMenuVerification
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    public static int Run()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemePalette.Get("Light").CreateResources());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OmniEdit;component/Themes/Controls.xaml", UriKind.Relative) });
        var window = new MainWindow { ShowInTaskbar = false };
        typeof(MainWindow).GetField("_settingsReady", Private)!.SetValue(window, false);
        Field<DispatcherTimer>(window, "_settingsSaveTimer").Stop();
        Call(window, "StopFileSync");
        try
        {
            window.Show(); Pump();
            var document = window.Documents.Single();
            var editor = document.Editor;
            editor.LoadUtf8(Encoding.UTF8.GetBytes(new string('x', 15000)));
            editor.SetWordWrap(false);
            var wrap = Field<MenuItem>(window, "WordWrapMenuItem");
            var click = typeof(MenuItem).GetMethod("OnClick", Private)!;
            editor.FocusEditor();
            click.Invoke(wrap, null); Pump();
            Check(editor.WordWrap && editor.DisplayLineCount > 1 && wrap.IsChecked, "Menu click must enable actual wrapping");
            click.Invoke(wrap, null); Pump();
            Check(!editor.WordWrap && !wrap.IsChecked, "Second menu click must disable wrapping");
            wrap.IsChecked = true;
            Call(window, "UpdateStatus"); // Native focus notification between toggle and routed Click.
            wrap.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            Check(editor.WordWrap && wrap.IsChecked, "Focus refresh must not cancel the wrap command");

            var menu = Field<MenuItem>(window, "EncodingFileMenu");
            Check(menu.HasItems, "Encoding menu must have children before its first open");
            ((MenuItem)ItemsControl.ItemsControlFromItemContainer(menu)).IsSubmenuOpen = true; Pump();
            menu.IsSubmenuOpen = true; Pump();
            Check(menu.Items.Count == 2, "Encoding menu must populate both commands on open");
            var save = (MenuItem)menu.Items[0];
            var bom = (MenuItem)save.Items[(int)DocumentEncodingKind.Utf8Bom];
            click.Invoke(bom, null); Pump();
            Check(document.EncodingModified && document.HasByteOrderMark && Field<TextBlock>(window, "EncodingStatus").Text.Contains("BOM"), "Encoding menu click must update metadata and status");
            click.Invoke((MenuItem)save.Items[(int)DocumentEncodingKind.Utf8], null); Pump();
            Check(!document.IsModified && !document.HasByteOrderMark, "Returning to original encoding must clear dirty");
            Check(!editor.IsModified && !editor.CanUndo && editor.GetText() == new string('x', 15000), "Display and encoding commands must preserve text and Undo");
            Console.WriteLine("PASS actual menu commands: wrap on/off, native-focus refresh, initial encoding submenu, BOM selection and revert");
            return 0;
        }
        finally
        {
            foreach (var document in window.Documents) document.Editor.ReleaseResources();
            typeof(MainWindow).GetField("_allowClose", Private)!.SetValue(window, true);
            window.Close(); app.Shutdown();
        }
    }
}
