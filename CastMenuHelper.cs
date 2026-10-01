using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sharpcaster.Models;

namespace SomaMetalTray;

/// <summary>
/// Builds and shows the "Cast to" popup in the app's look (see <see cref="ThemedMenu"/>), shared between the player's cast icon, the
/// tray menu and the title bar's system menu.
///
/// Devices are discovered first (a couple of seconds) and the menu is shown only once it is fully built: WinForms mispositions a
/// drop-down whose Items are changed while it is already open.
/// </summary>
internal static class CastMenuHelper
{
    public static async Task ShowAsync(CastService castService, Func<IStation> currentStation, Control owner, Point screenLocation,
        ToolStripDropDownDirection direction = ToolStripDropDownDirection.BelowRight)
    {
        Cursor? previousCursor = Cursor.Current;
        Cursor.Current = Cursors.WaitCursor;

        IReadOnlyList<ChromecastReceiver> receivers;
        try
        {
            receivers = await castService.DiscoverAsync(TimeSpan.FromSeconds(3));
        }
        catch
        {
            receivers = Array.Empty<ChromecastReceiver>();
        }
        finally
        {
            Cursor.Current = previousCursor;
        }

        if (owner.IsDisposed)
            return;

        ContextMenuStrip menu = ThemedMenu.Create();
        menu.Items.Add(ThemedMenu.Heading("CAST TO"));

        if (castService.State != CastState.Idle)
        {
            ThemedMenu.Add(menu, $"Stop casting ({castService.CastingDeviceName})", (_, _) => _ = castService.StopCastingAsync(), Glyphs.Clear);
            menu.Items.Add(new ToolStripSeparator());
        }

        if (receivers.Count == 0)
        {
            ThemedMenu.Add(menu, "No devices found", (_, _) => { }, Glyphs.Cast).Enabled = false;
        }
        else
        {
            foreach (ChromecastReceiver receiver in receivers)
            {
                ChromecastReceiver target = receiver;
                bool isCurrent = castService.State != CastState.Idle && castService.CastingDeviceName == receiver.Name;
                ThemedMenu.Add(menu, receiver.Name, (_, _) => _ = CastToAsync(castService, target, currentStation()), Glyphs.Cast, isCurrent);
            }
        }

        ThemedMenu.ShowAndDispose(owner, menu, screenLocation, direction: direction);
    }

    private static async Task CastToAsync(CastService castService, ChromecastReceiver receiver, IStation station)
    {
        try
        {
            await castService.CastToAsync(receiver, station);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't cast to {receiver.Name}: {ex.Message}", AppInfo.Name,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
