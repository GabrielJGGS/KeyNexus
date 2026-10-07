using KeyNexus.Core;
using KeyNexus.Core.Input;

namespace KeyNexus.Tests;

public class ModifierTrackerTests
{
    [Fact]
    public void PhysicalAltGr_IsTrackedAsAltGr()
    {
        var tracker = new ModifierTracker();
        tracker.Update(NativeMethods.VK_LCONTROL, ModifierTracker.AltGrFakeCtrlScanCode, isUp: false);
        tracker.Update(NativeMethods.VK_RMENU, 0x38, isUp: false);

        Assert.Equal(PhysicalModifiers.AltGrCtrl | PhysicalModifiers.RAlt, tracker.Held);
        Assert.Equal(ModifierFlags.AltGr, tracker.CurrentFlags);
    }

    [Fact]
    public void ReleasingAltGr_ClearsBothKeys()
    {
        var tracker = new ModifierTracker();
        tracker.Update(NativeMethods.VK_LCONTROL, ModifierTracker.AltGrFakeCtrlScanCode, isUp: false);
        tracker.Update(NativeMethods.VK_RMENU, 0x38, isUp: false);
        tracker.Update(NativeMethods.VK_RMENU, 0x38, isUp: true);
        tracker.Update(NativeMethods.VK_LCONTROL, ModifierTracker.AltGrFakeCtrlScanCode, isUp: true);

        Assert.Equal(PhysicalModifiers.None, tracker.Held);
        Assert.Equal(0, tracker.CurrentFlags);
    }

    [Fact]
    public void RealCtrlPlusAlt_CountsAsAltGr()
    {
        var tracker = new ModifierTracker();
        tracker.Update(NativeMethods.VK_LCONTROL, 0x1D, isUp: false);
        tracker.Update(NativeMethods.VK_LMENU, 0x38, isUp: false);

        Assert.Equal(ModifierFlags.AltGr, tracker.CurrentFlags);
    }

    [Fact]
    public void WinKey_AddsWinFlag_SoRulesWithoutWinDoNotMatch()
    {
        var tracker = new ModifierTracker();
        tracker.Update(NativeMethods.VK_LWIN, 0x5B, isUp: false);

        Assert.Equal(ModifierFlags.Win, tracker.CurrentFlags);
    }

    [Fact]
    public void Resync_DropsKeysWhoseReleaseWasMissed()
    {
        var tracker = new ModifierTracker();
        tracker.Update(NativeMethods.VK_LSHIFT, 0x2A, isUp: false);
        tracker.Update(NativeMethods.VK_RMENU, 0x38, isUp: false);

        tracker.Resync(vk => vk == NativeMethods.VK_LSHIFT);

        Assert.Equal(PhysicalModifiers.LShift, tracker.Held);
    }
}
