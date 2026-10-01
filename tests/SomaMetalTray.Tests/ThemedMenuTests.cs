using System.Drawing;
using System.Windows.Forms;
using SomaMetalTray;
using Xunit;

namespace SomaMetalTray.Tests;

public class ThemedMenuTests
{
    // WinForms needs an STA thread with a message pump.
    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the menu test hung");
        if (failure is not null)
            throw new Xunit.Sdk.XunitException("STA body failed: " + failure);
    }

    private static void Pump(int ms)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Picking_an_item_closes_and_later_disposes_the_menu_without_crashing()
    {
        // Regression: the menu used to be disposed from its Closed event, which fires from inside the item click, and WinForms then
        // threw "Cannot access a disposed object (ContextMenuStrip)" - the crash seen when choosing a station from the drop-down.
        OnStaThread(() =>
        {
            using var owner = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000), ShowInTaskbar = false };
            owner.Show();
            Pump(100);

            int picked = -1;
            bool closed = false;
            ContextMenuStrip menu = ThemedMenu.Create();
            ThemedMenu.Add(menu, "One", (_, _) => picked = 1);
            ThemedMenu.Add(menu, "Two", (_, _) => picked = 2, isChecked: true);

            ThemedMenu.ShowAndDispose(owner, menu, new Point(-4900, -4900), () => closed = true);
            Pump(100);
            Assert.True(menu.Visible);

            menu.Items[1].PerformClick();
            Pump(300);

            Assert.Equal(2, picked);
            Assert.True(closed);
            Assert.True(menu.IsDisposed, "the menu is disposed after it has closed");
        });
    }

    [Fact]
    public void Dismissing_without_picking_also_disposes()
    {
        OnStaThread(() =>
        {
            using var owner = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-5000, -5000), ShowInTaskbar = false };
            owner.Show();
            Pump(100);

            ContextMenuStrip menu = ThemedMenu.Create();
            ThemedMenu.Add(menu, "One", (_, _) => { });
            ThemedMenu.ShowAndDispose(owner, menu, new Point(-4900, -4900));
            Pump(100);

            menu.Close();
            Pump(300);
            Assert.True(menu.IsDisposed);
        });
    }
}
