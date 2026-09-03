using ECommons.DalamudServices;
using ECommons.Reflection;

namespace Artisan.IPC;

internal static class AutoRetainerIPC
{
    internal static bool ReEnable = false;
    private static bool suppressedByArtisan = false;

    internal static bool IsEnabled()
    {
        return DalamudReflector.TryGetDalamudPlugin("AutoRetainer", out _, false, true);
    }

    internal static bool IsSuppressed()
    {
        if (!IsEnabled())
            return false;

        try
        {
            return Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetSuppressed").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsBusy()
    {
        if (!IsEnabled())
            return false;

        try
        {
            return Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.IsBusy").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    internal static bool AnyRetainersAvailableForCurrentCharacter()
    {
        if (!IsEnabled())
            return false;

        try
        {
            return Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.PluginState.AreAnyRetainersAvailableForCurrentChara").InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    internal static void RequestAutoRetainer()
    {
        if (!IsEnabled())
            return;

        try
        {
            Svc.PluginInterface.GetIpcSubscriber<object>("AutoRetainer.RequestAutoRetainer").InvokeAction();
        }
        catch
        {
            Unsuppress(force: true);
        }
    }

    internal static void Suppress()
    {
        if (!IsEnabled())
            return;

        if (IsSuppressed())
            return;

        try
        {
            ReEnable = true;
            suppressedByArtisan = true;
            Svc.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(true);
        }
        catch
        {
            ReEnable = false;
            suppressedByArtisan = false;
        }
    }

    internal static void Unsuppress(bool force = false)
    {
        if (!force && !suppressedByArtisan && !ReEnable)
        {
            ReEnable = false;
            return;
        }

        if (IsEnabled())
        {
            try
            {
                Svc.PluginInterface.GetIpcSubscriber<bool, object>("AutoRetainer.SetSuppressed").InvokeAction(false);
            }
            catch { }
        }

        ReEnable = false;
        suppressedByArtisan = false;
    }
}
